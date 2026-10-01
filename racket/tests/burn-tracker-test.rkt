#lang racket/base

;; Ports BrainFuel.Tests/QuotaBurnTrackerTests.cs + SeverityTests.

(require rackunit
         "../brainfuel/burn-tracker.rkt")

(define T0 (* 1780000000000)) ; 2026-09-26T10:00:00Z, unix ms
(define minute (* 60 1000))

;; ---- QuotaBurnTrackerTests -------------------------------------------------

;; +10 pct in 0.5 h -> 20 %/h; 50% used at 20%/h -> 2.5 h to empty.
(define t (make-burn-tracker))
(burn-add-sample! t T0 40)
(burn-add-sample! t (+ T0 (* 30 minute)) 50)
(check-true (burn-has-current-rate? t (+ T0 (* 30 minute))))
(check-equal? (burn-tracker-rate-pct-per-hour t) 20.0)
(check-equal? (burn-project-hours-to-exhaustion t 50 (+ T0 (* 30 minute))) 2.5)

;; Under the 5-minute noise floor: no rate.
(define t2 (make-burn-tracker))
(burn-add-sample! t2 T0 40)
(burn-add-sample! t2 (+ T0 (* 2 minute)) 45)
(check-false (burn-has-current-rate? t2 (+ T0 (* 2 minute))))
(check-false (burn-project-hours-to-exhaustion t2 45 (+ T0 (* 2 minute))))

;; Window reset drops samples AND the stale rate; the new window needs its
;; own two spaced samples: 5 -> 10 pct over 14 min -> 21.43 %/h.
(define t3 (make-burn-tracker))
(burn-add-sample! t3 T0 60)
(burn-add-sample! t3 (+ T0 (* 30 minute)) 70)
(check-true (burn-has-current-rate? t3 (+ T0 (* 30 minute))))
(burn-add-sample! t3 (+ T0 (* 31 minute)) 5)
(check-false (burn-has-current-rate? t3 (+ T0 (* 31 minute))))
(burn-add-sample! t3 (+ T0 (* 45 minute)) 10)
(check-true (burn-has-current-rate? t3 (+ T0 (* 45 minute))))
(check-equal? (burn-tracker-rate-pct-per-hour t3) 21.428571428571427)

;; Flat usage produces no rate.
(define t4 (make-burn-tracker))
(burn-add-sample! t4 T0 40)
(burn-add-sample! t4 (+ T0 (* 30 minute)) 40)
(check-false (burn-has-current-rate? t4 (+ T0 (* 30 minute))))

;; Rates expire after 2 h.
(define t5 (make-burn-tracker))
(burn-add-sample! t5 T0 40)
(burn-add-sample! t5 (+ T0 (* 30 minute)) 50)
(check-false (burn-has-current-rate? t5 (+ T0 (* 3 60 minute))))
(check-false (burn-project-hours-to-exhaustion t5 50 (+ T0 (* 3 60 minute))))

;; An exhausted window projects 0 hours.
(check-equal? (burn-project-hours-to-exhaustion t5 100 (+ T0 (* 30 minute))) 0.0)

;; Only the last 4 samples are kept.
(define t6 (make-burn-tracker))
(for ([i (in-range 8)])
  (burn-add-sample! t6 (+ T0 (* i 10 minute)) (+ 10.0 i)))
(check-equal? (length (burn-tracker-samples t6)) 4)
(check-equal? (cdr (car (burn-tracker-samples t6))) 14.0)

;; ---- SeverityTests -----------------------------------------------------------

(define severity-cases
  '((0.0 calm) (74.9 calm) (75.0 amber) (89.9 amber) (90.0 red) (100.0 red)))
(for ([c (in-list severity-cases)])
  (check-equal? (severity-from-used-pct (car c)) (cadr c)
                (format "severity ~a" (car c))))
