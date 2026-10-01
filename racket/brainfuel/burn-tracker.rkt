#lang racket/base

(require racket/list)

;; Burn-rate estimation, ported from Services/QuotaBurnTracker.cs:
;; two consecutive samples at least 5 minutes apart and increasing produce a
;; rate in used-% per hour; the rate stays valid for 2 h; a decreasing
;; percentage means the window reset, so samples AND the stale rate are
;; dropped. Severity thresholds (calm/amber/red) live here too.

(provide (struct-out burn-tracker)
         make-burn-tracker
         burn-add-sample!
         burn-tracker-rate-pct-per-hour
         burn-has-current-rate?
         burn-project-hours-to-exhaustion
         severity-from-used-pct)

(struct burn-tracker (samples rate-pct-per-hour rate-at-ms) #:transparent #:mutable)
;; samples: list of (cons at-ms used-pct), oldest first.

(define min-sample-span-ms (* 5 60 1000))    ; below this, noise dominates
(define rate-valid-ms (* 2 60 60 1000))      ; older estimates are stale

(define (make-burn-tracker)
  (burn-tracker '() #f #f))

(define (clamp-pct v) (min 100.0 (max 0.0 v)))

(define (burn-add-sample! tracker at-ms used-pct)
  (define pct (clamp-pct used-pct))
  (define samples (burn-tracker-samples tracker))
  (when (and (pair? samples)
             (< pct (cdr (car (last-pair samples)))))
    ;; Window reset (or plan upgrade): old-window history would mislead.
    (set-burn-tracker-samples! tracker '())
    (set-burn-tracker-rate-pct-per-hour! tracker #f)
    (set-burn-tracker-rate-at-ms! tracker #f))

  (define updated (append (burn-tracker-samples tracker) (list (cons at-ms pct))))
  (when (> (length updated) 4)
    (set! updated (list-tail updated (- (length updated) 4))))
  (set-burn-tracker-samples! tracker updated)

  (define first-sample (car updated))
  (define last-sample (last-pair updated))
  (define span-ms (- (car (car last-sample)) (car first-sample)))
  (when (and (>= span-ms min-sample-span-ms)
             (> (cdr (car last-sample)) (cdr first-sample)))
    (set-burn-tracker-rate-pct-per-hour!
     tracker
     (/ (- (cdr (car last-sample)) (cdr first-sample))
        (/ span-ms 3600000.0)))
    (set-burn-tracker-rate-at-ms! tracker at-ms))
  (void))

(define (burn-has-current-rate? tracker now-ms)
  (define rate (burn-tracker-rate-pct-per-hour tracker))
  (define at (burn-tracker-rate-at-ms tracker))
  (and rate (> rate 0)
       at (<= (- now-ms at) rate-valid-ms)))

;; Hours until the window is fully used at the current burn rate.
(define (burn-project-hours-to-exhaustion tracker used-pct now-ms)
  (and (burn-has-current-rate? tracker now-ms)
       (let ([rate (burn-tracker-rate-pct-per-hour tracker)])
         (and rate
              (let ([hours (/ (- 100.0 (clamp-pct used-pct)) rate)])
                (and (>= hours 0) hours))))))

;; Visual urgency derived from the used percentage.
(define (severity-from-used-pct used-pct)
  (cond
    [(>= used-pct 90) 'red]
    [(>= used-pct 75) 'amber]
    [else 'calm]))
