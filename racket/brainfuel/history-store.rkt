#lang racket/base

;; Rolling local usage history backing the detail graphs, ported from
;; Services/UsageHistoryStore.cs. One JSON file per account next to
;; settings.json: [{at,h,w}] with percentages only (absent windows omitted),
;; 8-day retention, 3000-sample cap, corrupt file starts clean.

(require racket/file
         racket/path
         racket/string
         json
         "failure.rkt"
         "timestamps.rkt"
         "usage-models.rkt")

(provide (struct-out usage-sample)
         (struct-out history-store)
         history-retention-ms
         history-max-samples
         history-path-for-account
         load-history
         history-samples
         history-append!
         history-prune!
         history-save!
         average-burn-rate-24h)

(struct usage-sample (at-ms hourly-pct weekly-pct) #:transparent)
;; hourly/weekly pct: real or #f (window absent — the old NaN).

(struct history-store (path samples) #:transparent #:mutable)

(define history-retention-ms (* 8 24 60 60 1000))
(define history-max-samples 3000)

(define shared-history-filename "usage-history.json")

;; Per-account path; moves the pre-0.6 shared file into place for the
;; migrated default account on first use (best effort).
(define (history-path-for-account data-dir account-id)
  (define path (build-path data-dir (string-append "usage-history-" account-id ".json")))
  (when (and (equal? account-id "default")
             (not (file-exists? path))
             (file-exists? (build-path data-dir shared-history-filename)))
    (with-handlers ([exn:fail? void])
      (rename-file-or-directory
       (build-path data-dir shared-history-filename) path #f)))
  path)

(define (sample-from-json e)
  (define at (iso8601->ms (jstring e "at")))
  (and at
       (usage-sample
        at
        (let ([v (jreal e "h")]) (and v v))
        (let ([v (jreal e "w")]) (and v v)))))

(define (load-history path)
  (define samples
    (with-handlers
        ([exn:fail?
          (lambda (e)
            ;; A corrupt history must never break startup.
            (log-error (format "usage-history load failed, starting fresh: ~a"
                               (exn-message e)))
            '())])
      (if (not (file-exists? path))
          '()
          (let ([doc (with-input-from-file path read-json)])
            (if (list? doc)
                (sort (for/list ([e (in-list doc)]
                                 #:when (hash? e)
                                 #:do [(define s (sample-from-json e))]
                                 #:when s)
                        s)
                      <
                      #:key usage-sample-at-ms)
                '())))))
  (history-store path samples))

(define (history-samples store)
  (history-store-samples store))

;; Keeps the list time-ordered even if the clock jumps backwards. Appends at
;; the end of the (stable) merge sort so samples recorded within the same
;; millisecond (fast refreshes on a quick machine) keep their write order
;; instead of collapsing into a nondeterministic one.
(define (history-append! store sample)
  (set-history-store-samples!
   store
   (sort (append (history-store-samples store) (list sample))
         <
         #:key usage-sample-at-ms)))

(define (history-prune! store now-ms)
  (define kept
    (for/list ([s (in-list (history-store-samples store))]
               #:when (<= (- now-ms (usage-sample-at-ms s)) history-retention-ms))
      s))
  (set-history-store-samples!
   store
   (if (> (length kept) history-max-samples)
       (list-tail kept (- (length kept) history-max-samples))
       kept)))

(define (round-to-2 v)
  (/ (round (* v 100.0)) 100.0))

(define (sample->json s)
  (define base (hasheq 'at (ms->iso8601 (usage-sample-at-ms s))))
  (define base2
    (if (usage-sample-hourly-pct s)
        (hash-set base 'h (round-to-2 (usage-sample-hourly-pct s)))
        base))
  (if (usage-sample-weekly-pct s)
      (hash-set base2 'w (round-to-2 (usage-sample-weekly-pct s)))
      base2))

(define (history-save! store)
  (with-handlers
      ([exn:fail?
        (lambda (e)
          (log-error (format "usage-history save failed: ~a" (exn-message e))))])
    (define dir (path-only (history-store-path store)))
    (when dir (make-directory* dir))
    (with-output-to-file (history-store-path store) #:exists 'replace
      (lambda ()
        (write-json
         (for/list ([s (in-list (history-store-samples store))])
           (sample->json s)))))))

;; Average consumption speed of the hourly window over the last 24 h: the sum
;; of positive deltas (a drop means a window reset, not negative usage)
;; divided by 24. Returns #f when no comparable pair exists in range.
(define (average-burn-rate-24h samples now-ms)
  (define start (- now-ms (* 24 60 60 1000)))
  (for/fold ([total 0.0] [prev #f] [any-pair #f]
             #:result (and any-pair (/ total 24.0)))
            ([s (in-list samples)])
    (define at (usage-sample-at-ms s))
    (define v (usage-sample-hourly-pct s))
    (cond
      [(< at start) (values total prev any-pair)]
      ;; Absent window: the chain of comparable samples breaks.
      [(not v) (values total #f any-pair)]
      [prev (values (+ total (max 0.0 (- v prev))) v #t)]
      [else (values total v any-pair)])))
