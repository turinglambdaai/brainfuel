#lang racket/base

;; Failure taxonomy ported from Services/UsageFailure.cs plus the body
;; heuristics from the GLM client (authentication / missing-plan wording).
;; Kinds are plain symbols so the domain core stays free of Rivet types.

(require racket/string)

(provide usage-failure-kinds
         usage-failure?
         exn:usage?
         exn:usage-kind
         exn:usage-status-code
         raise-usage
         (struct-out transport-error)
         raise-transport
         transient-failure?
         save-anyway?
         contains-any?
         looks-like-auth-failure?
         looks-like-missing-plan?
         classify-http-status
         classify-error-envelope
         classify-transport-message
         log-error)

(define usage-failure-kinds
  '(authentication
    network
    tls
    proxy
    timeout
    rate-limited
    no-coding-plan
    service-unavailable
    invalid-response
    unknown))

(define (usage-failure? v) (and (memq v usage-failure-kinds) #t))

;; Raised by the quota clients. Mirrors UsageRequestException.
(struct exn:usage exn:fail (kind status-code) #:transparent)

(define (raise-usage kind message [status-code #f])
  (raise (exn:usage message (current-continuation-marks) kind status-code)))

;; Raised by an HTTP transport. `kind` is 'timeout or 'raw; the raw kind is
;; classified per provider (GLM distinguishes TLS/proxy, Codex/Claude do not).
(struct transport-error (kind message status-code) #:transparent)

(define (raise-transport message [status-code #f])
  (raise (transport-error 'raw message status-code)))

;; Failures that can heal on their own; the scheduler retries these after 45 s
;; instead of the configured interval.
(define (transient-failure? kind)
  (and (memq kind '(network tls proxy timeout rate-limited
                    service-unavailable invalid-response))
       #t))

;; Every failure except Authentication allows "save anyway".
(define (save-anyway? kind) (not (eq? kind 'authentication)))

(define (blank-string? v)
  (or (not v) (not (string? v)) (equal? "" (string-trim v))))

;; Case-insensitive substring test (C# string.Contains ordinal-ignore-case).
(define (contains-ci? value needle)
  (and (string? value)
       (string-contains? (string-downcase value) (string-downcase needle))))

(define (contains-any? value needles)
  (if (blank-string? value)
      #f
      (and (ormap (lambda (needle) (contains-ci? value needle)) needles)
           #t)))

(define (looks-like-auth-failure? body)
  (contains-any?
   body
   '("unauthorized" "invalid api key" "invalid key" "authorization"
     "authentication" "鉴权" "密钥无效" "key无效")))

(define (looks-like-missing-plan? body)
  (and (contains-any? body '("coding plan" "subscription" "套餐" "订阅"))
       (contains-any? body '("not found" "not subscribed" "no plan"
                             "not activated" "未开通" "未订阅" "不存在"))))

;; HTTP status -> failure kind (GlmUsageClient.ClassifyHttpFailure).
(define (classify-http-status code body)
  (cond
    [(= code 407) 'proxy]
    [(= code 429) 'rate-limited]
    [(or (= code 402) (looks-like-missing-plan? body)) 'no-coding-plan]
    [(or (= code 401) (= code 403) (looks-like-auth-failure? body))
     'authentication]
    [(>= code 500) 'service-unavailable]
    [else 'service-unavailable]))

;; GLM HTTP-200 error envelope -> failure kind.
(define (classify-error-envelope code msg body)
  (cond
    [(or (looks-like-auth-failure? msg)
         (looks-like-auth-failure? body)
         (member code '("401" "403" "1001")))
     'authentication]
    [(looks-like-missing-plan? body) 'no-coding-plan]
    [(equal? code "429") 'rate-limited]
    [else 'invalid-response]))

;; Transport exception message -> TLS / Proxy / Network.
(define (classify-transport-message message)
  (cond
    [(contains-any? message '("ssl" "tls" "certificate" "证书")) 'tls]
    [(contains-any? message '("proxy" "tunnel")) 'proxy]
    [else 'network]))

;; Minimal error log (the old app wrote brainfuel.log; hosts own file logging
;; now, so this only surfaces when BRAINFUEL_DEBUG is set).
(define (log-error message)
  (when (getenv "BRAINFUEL_DEBUG")
    (eprintf "brainfuel: ~a~n" message)))
