#lang racket/base

;; Normalized usage snapshot + GLM monitor-envelope parsing, ported from
;; Services/UsageModels.cs and GlmUsageClient.MapSnapshot. Percentages are
;; floats 0..100 inside the domain core; the backend converts to basis points
;; at the RPC boundary (RVT1 has no float).

(require json
         racket/port
         racket/string
         "failure.rkt")

(provide (struct-out usage-snapshot)
         severity-from-used-pct
         pct->bp
         round-int
         json-parse-safe
         jref jstring jreal jint jbool
         glm-error-code glm-error-msg glm-is-error-envelope?
         parse-glm-usage)

;; hourly-pct / weekly-pct: real or #f when the window was absent from the
;; response. reset-ms: unix millis or #f. plan-level: string or #f.
(struct usage-snapshot
  (hourly-pct weekly-pct hourly-reset-ms weekly-reset-ms plan-level fetched-at-ms)
  #:transparent)

;; Severity ports Severity.FromUsedPct (calm < 75 <= amber < 90 <= red).
(define (severity-from-used-pct used-pct)
  (cond
    [(>= used-pct 90) 'red]
    [(>= used-pct 75) 'amber]
    [else 'calm]))

;; Percentage -> basis points (1 bp = 0.01 %), clamped to the RVT1 range.
(define (pct->bp pct)
  (max 0 (min 10000 (round-int (* pct 100)))))

;; C# Math.Round is banker's rounding; Racket `round` matches. Results cross
;; the RPC boundary as Int64, so they are collapsed to exact integers.
(define (round-int x)
  (inexact->exact (round x)))

;; ---- JSON helpers (racket/json jsexpr values) -----------------------------

(define (json-parse-safe body)
  (with-handlers ([exn:fail? (λ (_) #f)])
    (with-input-from-string body read-json)))

(define (jref obj key)
  (and (hash? obj) (hash-ref obj (string->symbol key) #f)))

(define (jstring obj key)
  (define v (jref obj key))
  (and (string? v) v))

(define (jreal obj key)
  (define v (jref obj key))
  ;; exact->inexact, not (* 1.0 v): exact-zero annihilation would return the
  ;; exact 0 for "percentage": 0 and leak exact numbers into the snapshot.
  (and (real? v) (exact->inexact v)))

;; Ints tolerate whole-number floats; other shapes read as absent.
(define (jint obj key)
  (define v (jref obj key))
  (cond
    [(exact-integer? v) v]
    [(real? v) (inexact->exact (truncate v))]
    [else #f]))

(define (jbool obj key)
  (define v (jref obj key))
  (if (boolean? v) v #f))

;; ---- GLM envelope ----------------------------------------------------------

;; `code` appears as both number and string in the wild.
(define (glm-error-code doc)
  (define v (jref doc "code"))
  (cond
    [(exact-integer? v) (number->string v)]
    [(real? v) (number->string (inexact->exact (truncate v)))]
    [(string? v) v]
    [else #f]))

(define (glm-error-msg doc)
  (or (jstring doc "msg") (jstring doc "message")))

;; Both gateways report rejected keys with HTTP 200 +
;; {"code":…,"msg":…,"success":false}. Absent keys must NOT read as
;; "success: false" — in Racket #f is a boolean, so the key's presence is
;; checked explicitly (the C# JsonElement? is null when absent).
(define (glm-is-error-envelope? doc)
  (define success-present? (and (hash? doc) (hash-has-key? doc 'success)))
  (define success (jref doc "success"))
  (define code (glm-error-code doc))
  (or (and success-present? (boolean? success) (not success))
      (and code (not (member code '("0" "200"))))))

(define (top-level-fields body)
  (define doc (json-parse-safe body))
  (cond
    [(not doc) "<not json>"]
    [(hash? doc)
     (string-join (map symbol->string (hash-keys doc)) ", ")]
    [else "<not an object>"]))

;; RawLimit reset scan: prefer nextResetTime (unix ms), else any extra numeric
;; field whose key mentions RESET/EXPIRE/END/NEXT (TryFindReset).
(define known-limit-keys
  '(type unit number percentage currentValue usage remaining nextResetTime))

(define (glm-reset-ms lim)
  (or (let ([v (jref lim "nextResetTime")])
        (and (real? v) (> v 0) (inexact->exact (truncate v))))
      (let loop ([keys (if (hash? lim) (hash-keys lim) '())])
        (cond
          [(null? keys) #f]
          [else
           (define key (car keys))
           (define name (string-upcase (symbol->string key)))
           (define v (hash-ref lim key #f))
           (cond
             [(and (not (memq key known-limit-keys))
                   (or (string-contains? name "RESET")
                       (string-contains? name "EXPIRE")
                       (string-contains? name "END")
                       (string-contains? name "NEXT"))
                   (real? v) (> v 0))
              (inexact->exact (truncate v))]
             [else (loop (cdr keys))])]))))

;; Percentages may arrive on a 0..1 scale (fractional values with max <= 1.5)
;; or already as 0..100 integers (Lite reports plain "1" meaning 1 %).
(define (percentage-scale limits)
  (define-values (max-pct has-fractional?)
    (for/fold ([mx 0.0] [frac #f])
              ([lim (in-list limits)])
      (define p (jreal lim "percentage"))
      (if p
          (values (max mx p)
                  (or frac (and (> p 0) (not (= p (floor p))))))
          (values mx frac))))
  (if (and has-fractional? (<= max-pct 1.5)) 100.0 1.0))

;; Only TOKENS_LIMIT / CREDIT_LIMIT are handled; TIME_LIMIT (MCP monthly
;; quota) is ignored. number == 5 is the 5-hour window, the first other
;; token/credit limit is weekly.
(define (map-glm-limits limits level now-ms)
  (define scale (percentage-scale limits))
  (let loop ([rest limits]
             [hourly #f] [hourly-reset #f]
             [weekly #f] [weekly-reset #f])
    (if (null? rest)
        (usage-snapshot hourly weekly hourly-reset weekly-reset level now-ms)
        (let* ([lim (car rest)]
               [type (string-upcase (string-trim (or (jstring lim "type") "")))])
          (if (and (not (equal? type "TOKENS_LIMIT"))
                   (not (equal? type "CREDIT_LIMIT")))
              (loop (cdr rest) hourly hourly-reset weekly weekly-reset)
              (let ([pct (* (or (jreal lim "percentage") 0) scale)]
                    [reset (glm-reset-ms lim)])
                (cond
                  [(equal? (jint lim "number") 5)
                   (loop (cdr rest) pct reset weekly weekly-reset)]
                  [(not weekly)
                   (loop (cdr rest) hourly hourly-reset pct reset)]
                  [else
                   (loop (cdr rest) hourly hourly-reset weekly weekly-reset)])))))))

;; Parses a GLM monitor response body; raises exn:usage on every failure
;; shape, exactly like GlmUsageClient.GetUsageAsync.
(define (parse-glm-usage body now-ms)
  (define doc (json-parse-safe body))
  (unless doc
    (raise-usage 'invalid-response "GLM quota response was not valid JSON"))

  (when (glm-is-error-envelope? doc)
    (define code (glm-error-code doc))
    (define msg (glm-error-msg doc))
    (raise-usage
     (classify-error-envelope code msg body)
     (format "GLM error envelope (code ~a): ~a"
             (or code "?") (or msg "<no message>"))))

  (define data (jref doc "data"))
  (unless (hash? data)
    (raise-usage
     'invalid-response
     (format "GLM quota response did not contain a data object (top-level fields: ~a)"
             (top-level-fields body))))

  (define limits (or (jref data "limits") '()))
  (define snapshot (map-glm-limits limits (jstring data "level") now-ms))

  (unless (or (usage-snapshot-hourly-pct snapshot)
              (usage-snapshot-weekly-pct snapshot))
    (define structure
      (if (null? limits)
          "limits: none"
          (string-join
           (for/list ([lim (in-list limits)])
             (format "~a#~a"
                     (or (jstring lim "type") "?")
                     (or (let ([n (jint lim "number")])
                           (and n (number->string n)))
                         "-")))
           ", ")))
    (raise-usage
     'no-coding-plan
     (format "No Coding Plan token quota was present in the GLM response (~a)"
             structure)))

  snapshot)
