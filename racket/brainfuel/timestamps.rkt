#lang racket/base

;; Time helpers. Internal timestamps are unix milliseconds (exact integers).
;; ISO-8601 parsing/formatting is shared by the Claude client (resets_at) and
;; the usage-history store (drop-in compatible with the old app's "O"-format
;; round-trip strings).

(require racket/date
         racket/format
         racket/string)

(provide now-ms
         iso8601->ms
         ms->iso8601)

(define (now-ms)
  (inexact->exact (floor (current-inexact-milliseconds))))

;; Accepts "yyyy-MM-ddTHH:mm:ss(.fff)?(Z|±HH:MM|±HHMM)?". A missing zone is
;; treated as UTC (the old app assumed local there, but every file this
;; program ever wrote carries an explicit offset).
(define iso8601-regexp
  #px"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt ]([0-9]{2}):([0-9]{2})(?::([0-9]{2})(\\.[0-9]+)?)?(Z|z|[+-][0-9]{2}:?[0-9]{2})?$")

(define (atoi s) (and s (string->number s)))

(define (iso8601->ms text)
  (and (string? text)
       (let* ([m (regexp-match iso8601-regexp (string-trim text))])
         (and m
              (let ([year (atoi (list-ref m 1))]
                    [month (atoi (list-ref m 2))]
                    [day (atoi (list-ref m 3))]
                    [hour (atoi (list-ref m 4))]
                    [minute (atoi (list-ref m 5))]
                    [second (or (atoi (list-ref m 6)) 0)]
                    [frac (list-ref m 7)]
                    [zone (list-ref m 8)])
                (with-handlers ([exn:fail? (λ (_) #f)])
                  (define epoch
                    (find-seconds second minute hour day month year #f)) ;; local-time? #f = inputs are UTC
                  (define offset-seconds
                    (cond [(not zone) 0]
                          [(member zone '("Z" "z")) 0]
                          [else
                           (define sign
                             (if (equal? (substring zone 0 1) "-") -1 1))
                           (define rest
                             (string-replace (substring zone 1) ":" ""))
                           (define hh (string->number (substring rest 0 2)))
                           (define mm (string->number (substring rest 2)))
                           (if (and hh mm)
                               (* sign (+ (* hh 3600) (* mm 60)))
                               0)]))
                  (define fraction-ms
                    (if frac
                        (string->number
                         (substring (string-append (substring frac 1) "000") 0 3))
                        0))
                  (+ (* (+ epoch (- offset-seconds)) 1000) fraction-ms)))))))

;; UTC "round-trip"-style output: 7 fractional digits plus +00:00, matching
;; what DateTimeOffset wrote historically. Old builds parse it unchanged.
(define (ms->iso8601 ms)
  (define seconds (quotient ms 1000))
  (define millis (modulo ms 1000))
  (define d (seconds->date seconds #f)) ; UTC
  (format "~a-~a-~aT~a:~a:~a.~a000+00:00"
          (~r (date-year d) #:min-width 4 #:pad-string "0")
          (~r (date-month d) #:min-width 2 #:pad-string "0")
          (~r (date-day d) #:min-width 2 #:pad-string "0")
          (~r (date-hour d) #:min-width 2 #:pad-string "0")
          (~r (date-minute d) #:min-width 2 #:pad-string "0")
          (~r (date-second d) #:min-width 2 #:pad-string "0")
          (~r millis #:min-width 3 #:pad-string "0")))
