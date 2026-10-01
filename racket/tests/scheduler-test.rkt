#lang racket/base

;; Application-service tests: refresh single-flight, alerts with hysteresis,
;; burn tracking, retry intervals, multi-account switching, credential-state
;; transitions and settings normalization. Ports the MainViewModel behavior.

(require racket/file
         racket/string
         rackunit
         "../brainfuel/credentials.rkt"
         "../brainfuel/failure.rkt"
         "../brainfuel/scheduler.rkt"
         "../brainfuel/settings.rkt"
         "../brainfuel/usage-models.rkt")

(define minute (* 60 1000))
(define T0 1780000000000) ; 2026-09-26T10:00:00Z

(define (glm-body hourly weekly)
  (format "{\"data\":{\"level\":\"Max\",\"limits\":[
             {\"type\":\"TOKENS_LIMIT\",\"number\":5,\"percentage\":~a},
             {\"type\":\"TOKENS_LIMIT\",\"number\":1,\"percentage\":~a}]}}"
          hourly weekly))

;; A service whose fetch serves bodies from a box, with a controllable clock.
(define (make-env bodies)
  (define dir (make-temporary-file "bf-sched-tests-~a" 'directory))
  (define now-box (box T0))
  (define fetch-count (box 0))
  (define fetch
    (lambda (url headers)
      (set-box! fetch-count (add1 (unbox fetch-count)))
      (values 200 (unbox bodies))))
  (define alerts (box '()))
  (define svc
    (open-service!
     #:data-dir dir
     #:settings-path (build-path dir "settings.json")
     #:fetch fetch
     #:store (make-memory-store)
     #:now (lambda () (unbox now-box))
     #:on-notify (lambda (account-id which message)
                   (set-box! alerts
                             (cons (list account-id which) (unbox alerts))))))
  (values svc dir now-box fetch-count alerts))

;; ---- fresh install: default account, no credential -> silent empty state ----

(define-values (svc0 dir0 _nb0 _fc0 _al0) (make-env (box (glm-body 10 20))))
(define accts0 (service-accounts svc0))
(check-equal? (length accts0) 1)
(check-equal? (account-config-id (car accts0)) "default")
(check-false (account-config-configured (car accts0)))
(check-equal? (service-refresh! svc0) 'refreshed)
(define info0 (service-snapshot-info svc0))
(check-equal? (snapshot-info-account-id info0) "default")
(check-false (snapshot-info-hourly-pct info0))
(check-false (snapshot-info-failure-kind info0))
(service-stop! svc0)

;; ---- save-account, snapshot mapping, alerts + hysteresis ----------------------

(define bodies1 (box (glm-body 85 40)))
(define-values (svc1 dir1 nb1 fc1 alerts1) (make-env bodies1))
(define accts1
  (service-save-account!
   svc1 (account-draft #f "Work" 'glm "https://open.bigmodel.cn" "test-key-1" #f)))
(check-equal? (length accts1) 2)
(check-true (account-config-configured (cadr accts1)))

(define info1 (service-snapshot-info svc1))
(check-equal? (snapshot-info-account-id info1) (account-config-id (cadr accts1)))
(check-equal? (snapshot-info-hourly-pct info1) 85.0)
(check-equal? (snapshot-info-weekly-pct info1) 40.0)
(check-equal? (snapshot-info-plan-level info1) "Max")

;; 85 >= 80: fired exactly once for the hourly window.
(check-equal? (unbox alerts1)
              (list (list (account-config-id (cadr accts1)) "hourly")))

;; Staying above the threshold does not re-fire...
(set-box! bodies1 (glm-body 86 40))
(service-refresh! svc1)
(check-equal? (length (unbox alerts1)) 1)
;; ...falling below threshold - 5 re-arms...
(set-box! bodies1 (glm-body 70 40))
(service-refresh! svc1)
(check-equal? (length (unbox alerts1)) 1)
;; ...and crossing again fires again.
(set-box! bodies1 (glm-body 85 40))
(service-refresh! svc1)
(check-equal? (length (unbox alerts1)) 2)

;; Weekly fires independently.
(set-box! bodies1 (glm-body 10 90))
(service-refresh! svc1)
(check-true (for/or ([a (in-list (unbox alerts1))])
              (equal? (cadr a) "weekly")))

;; ---- burn tracking + comparison (needs two samples >= 5 min apart) ------------

(define bodies2 (box (glm-body 30 50)))
(define now2 (box T0))
(define dir2 (make-temporary-file "bf-sched-tests-~a" 'directory))
(define svc2
  (open-service! #:data-dir dir2
                 #:settings-path (build-path dir2 "settings.json")
                 #:fetch (lambda (u h) (values 200 (unbox bodies2)))
                 #:store (make-memory-store)
                 #:now (lambda () (unbox now2))))
(define accts2
  (service-save-account!
   svc2 (account-draft #f "A" 'glm "https://open.bigmodel.cn" "k2" #f)))
(define id2 (account-config-id (cadr accts2)))

;; Too close together: no rate yet, history still collecting.
(define details-none (service-details svc2 id2))
(check-false (details-info-hourly-rate details-none))
(check-true (positive? (string-length (details-info-history-note details-none))))

;; 30 -> 60 over 6 min: 300 %/h; the 24 h average is 30/24 = 1.25 %/h, so the
;; momentary burn reads as "faster than average".
(set-box! now2 (+ T0 (* 6 minute)))
(set-box! bodies2 (glm-body 60 50))
(service-refresh! svc2)
(define details2 (service-details svc2 id2))
(check-equal? (details-info-hourly-rate details2) 300.0)
;; (100 - 60) / 300 h = 8 minutes to empty.
(check-equal? (details-info-hourly-minutes-to-empty details2) 8)
(check-true (string-contains? (details-info-hourly-comparison details2) "快"))
;; Weekly has no daily baseline -> no comparison.
(check-false (details-info-weekly-comparison details2))
;; Two samples: the collecting note is gone.
(check-equal? (length (details-info-history-samples details2)) 2)
(check-equal? (details-info-history-note details2) "")

;; History survives a service restart (drop-in files).
(define svc2b
  (open-service! #:data-dir dir2
                 #:settings-path (build-path dir2 "settings.json")
                 #:fetch (lambda (u h) (values 200 (unbox bodies2)))
                 #:store (make-memory-store)
                 #:now (lambda () (unbox now2))))
(check-true
 (ormap (lambda (a) (equal? (account-config-id a) id2))
        (service-accounts svc2b)))
(check-equal?
 (length (details-info-history-samples (service-details svc2b id2))) 2)
(service-stop! svc2b)

;; ---- transient failures shorten the retry interval -----------------------------

(define bodies3 (box "internal error"))
(define-values (svc3 dir3 _nb3 _fc3 _al3) (make-env bodies3))
(service-save-account!
 svc3 (account-draft #f "A" 'glm "https://open.bigmodel.cn" "k3" #f))
(define info3 (service-snapshot-info svc3))
(check-equal? (snapshot-info-failure-kind info3) 'invalid-response)
(check-equal? (service-timer-interval-ms svc3) 45000)

;; Permanent causes keep the configured interval (5 min default).
(set-box! bodies3 "{\"code\":1001,\"msg\":\"no auth\",\"success\":false}")
(service-refresh! svc3)
(check-equal? (snapshot-info-failure-kind (service-snapshot-info svc3))
              'authentication)
(check-equal? (service-timer-interval-ms svc3) (* 5 minute))

;; Back to healthy restores the data and the normal interval.
(set-box! bodies3 (glm-body 12 34))
(service-refresh! svc3)
(check-equal? (snapshot-info-hourly-pct (service-snapshot-info svc3)) 12.0)
(check-false (snapshot-info-failure-kind (service-snapshot-info svc3)))
(check-equal? (service-timer-interval-ms svc3) (* 5 minute))
(service-stop! svc3)

;; ---- single-flight: manual refresh and ticks never overlap ----------------------

(define release (box #t))
(define dir4 (make-temporary-file "bf-sched-tests-~a" 'directory))
(define svc4
  (open-service! #:data-dir dir4
                 #:settings-path (build-path dir4 "settings.json")
                 #:fetch (lambda (u h)
                           (let loop ()
                             (unless (unbox release)
                               (sleep 0.01)
                               (loop)))
                           (values 200 (glm-body 1 1)))
                 #:store (make-memory-store)
                 #:now (lambda () T0)))
(service-save-account!
 svc4 (account-draft #f "A" 'glm "https://open.bigmodel.cn" "k4" #f))
(set-box! release #f)
(define worker (thread (lambda () (service-refresh! svc4))))
(sleep 0.05) ; let the worker take the flight
(check-equal? (service-refresh! svc4) 'skipped)
(set-box! release #t)
(sync worker)
(check-equal? (service-refresh! svc4) 'refreshed)
(service-stop! svc4)

;; ---- switch-account applies instantly from cached data ---------------------------

(define bodies5 (box (glm-body 10 10)))
(define-values (svc5 _dir5 _nb5 fc5 _al5) (make-env bodies5))
(define a5
  (service-save-account!
   svc5 (account-draft #f "One" 'glm "https://open.bigmodel.cn" "k5a" #f)))
(define id-a (account-config-id (cadr a5)))

(define b5
  (service-save-account!
   svc5 (account-draft #f "Two" 'glm "https://api.z.ai" "k5b" #f)))
(define id-b
  (account-config-id
   (findf (lambda (a) (equal? (account-config-name a) "Two"))
          (service-accounts svc5)))
  )
(define fetches-before-switch (unbox fc5))

(check-equal? (service-active-account-id svc5) id-b)
(check-true (service-switch-account! svc5 id-a))
(check-equal? (service-active-account-id svc5) id-a)
;; Switching used only cached state - no additional fetch happened.
(check-equal? (unbox fc5) fetches-before-switch)
(check-equal? (snapshot-info-account-id (service-snapshot-info svc5)) id-a)
(check-equal? (snapshot-info-hourly-pct (service-snapshot-info svc5)) 10.0)
(check-false (service-switch-account! svc5 id-a))   ; same account: no-op
(check-false (service-switch-account! svc5 "nope")) ; unknown: no-op

;; ---- remove-account, tombstone semantics ------------------------------------------

(check-true (service-remove-account! svc5 id-b))
(check-false
 (findf (lambda (a) (equal? (account-config-id a) id-b))
        (service-accounts svc5)))
(check-equal? (service-active-account-id svc5) id-a)

;; Removing everything normalizes back to the default account.
(for ([a (in-list (service-accounts svc5))])
  (service-remove-account! svc5 (account-config-id a)))
(check-equal? (map account-config-id (service-accounts svc5)) '("default"))
(check-equal? (service-active-account-id svc5) "default")
(service-stop! svc5)

;; ---- credential states: protected, protected-unavailable, plaintext --------------

;; Protected: the blob lives in the store, never in settings.json.
(define dir6 (make-temporary-file "bf-sched-tests-~a" 'directory))
(define store6 (make-memory-store))
(define settings6 (build-path dir6 "settings.json"))
(define svc6
  (open-service! #:data-dir dir6 #:settings-path settings6
                 #:fetch (lambda (u h) (values 200 (glm-body 5 5)))
                 #:store store6 #:now (lambda () T0)))
(service-save-account!
 svc6 (account-draft #f "A" 'glm "https://open.bigmodel.cn" "secret-key-6" #f))
(check-equal? (settings-info-credential-state (service-settings-info svc6))
              "protected")
(check-false (string-contains? (file->string settings6) "secret-key-6"))
(service-stop! svc6)

;; Keyring unavailable on the next start: markers preserved, no downgrade,
;; refresh retries silently.
(set-box! (memory-store-available store6) #f)
(define svc6b
  (open-service! #:data-dir dir6 #:settings-path settings6
                 #:fetch (lambda (u h) (values 200 (glm-body 5 5)))
                 #:store store6 #:now (lambda () T0)))
(check-equal? (settings-info-credential-state (service-settings-info svc6b))
              "protected-unavailable")
(check-false (string-contains? (file->string settings6) "secret-key-6"))
(service-refresh! svc6b)
(check-equal? (settings-info-credential-state (service-settings-info svc6b))
              "protected-unavailable")

;; Store returns: the next refresh recovers protected keys without a restart.
(set-box! (memory-store-available store6) #t)
(service-refresh! svc6b)
(check-equal? (settings-info-credential-state (service-settings-info svc6b))
              "protected")
(check-equal? (snapshot-info-hourly-pct (service-snapshot-info svc6b)) 5.0)
(service-stop! svc6b)

;; Plaintext fallback only when saving a key with no system store at all.
(define dir7 (make-temporary-file "bf-sched-tests-~a" 'directory))
(define settings7 (build-path dir7 "settings.json"))
(define svc7
  (open-service! #:data-dir dir7 #:settings-path settings7
                 #:fetch (lambda (u h) (values 200 (glm-body 5 5)))
                 #:store (make-unavailable-store) #:now (lambda () T0)))
(service-save-account!
 svc7 (account-draft #f "A" 'glm "https://open.bigmodel.cn" "plain-key-7" #f))
(check-equal? (settings-info-credential-state (service-settings-info svc7))
              "plaintext-fallback")
(check-true (string-contains? (file->string settings7) "plain-key-7"))
(service-stop! svc7)

;; ---- CLI-login accounts ------------------------------------------------------------

(define codex-dir (make-temporary-file "bf-sched-codex-~a" 'directory))
(define codex-path (build-path codex-dir "auth.json"))

(define dir8 (make-temporary-file "bf-sched-tests-~a" 'directory))
(define svc8
  (open-service! #:data-dir dir8
                 #:settings-path (build-path dir8 "settings.json")
                 #:fetch (lambda (u h) (values 200 "{}"))
                 #:store (make-memory-store)
                 #:now (lambda () T0)))
;; No local CLI login -> cannot save (CliLoginMissing).
(check-exn exn:fail?
           (lambda ()
             (parameterize ([current-codex-auth-path codex-path])
               (service-save-account!
                svc8 (account-draft #f "Codex" 'codex "" #f #f)))))
;; With a local login the account saves configured and keyless.
(with-output-to-file codex-path
  (lambda ()
    (display "{\"tokens\":{\"access_token\":\"tok\"}}")))
(define accts8
  (parameterize ([current-codex-auth-path codex-path])
    (service-save-account!
     svc8 (account-draft #f "Codex" 'codex "" #f #f))))
(define codex-account
  (findf (lambda (a) (eq? (account-config-provider a) 'codex)) accts8))
(check-true (account-config-configured codex-account))
;; clear-key tombstones the account even though nothing was stored.
(define accts8b
  (parameterize ([current-codex-auth-path codex-path])
    (service-save-account!
     svc8
     (account-draft (account-config-id codex-account) "Codex" 'codex "" #f #t))))
(check-false
 (account-config-configured
  (findf (lambda (a) (eq? (account-config-provider a) 'codex)) accts8b)))
(service-stop! svc8)

;; ---- settings application: clamps + immediate refresh -------------------------------

(define bodies9 (box (glm-body 7 7)))
(define-values (svc9 _dir9 _nb9 _fc9 _al9) (make-env bodies9))
(define applied
  (service-apply-settings!
   svc9 (settings-info 0 #f #f #f 500 "light" "en" "compact" #t #t
                       #t "Ctrl+Alt+B" "classic" 0.5 "none")))
(check-equal? (settings-info-refresh-interval-minutes applied) 1) ; clamped
(check-equal? (settings-info-notify-threshold applied) 99)        ; clamped
(check-equal? (settings-info-theme applied) "light")
(check-equal? (settings-info-language applied) "en")
(check-equal? (settings-info-hourly-remaining applied) #f)
(check-equal? (settings-info-card-opacity applied) 0.5)
;; Applying settings refreshed immediately and the new interval is live.
(check-equal? (service-timer-interval-ms svc9) minute)
(service-stop! svc9)

;; ---- init starts the scheduler thread; the startup refresh runs on it ---------------

(define bodies10 (box (glm-body 21 22)))
(define changes (box '()))
(define dir10 (make-temporary-file "bf-sched-tests-~a" 'directory))
(define svc10
  (open-service! #:data-dir dir10
                 #:settings-path (build-path dir10 "settings.json")
                 #:fetch (lambda (u h) (values 200 (unbox bodies10)))
                 #:store (make-memory-store)
                 #:now (lambda () T0)
                 #:on-change (lambda (what)
                               (set-box! changes (cons what (unbox changes))))))
(service-save-account!
 svc10 (account-draft #f "A" 'glm "https://open.bigmodel.cn" "k10" #f))
(set-box! changes '())
(service-init! svc10) ; startup refresh happens on the scheduler thread
(let loop ([n 0])
  (unless (ormap (lambda (w) (eq? w 'snapshot)) (unbox changes))
    (when (> n 200) (error "scheduler thread did not refresh"))
    (sleep 0.05)
    (loop (add1 n))))
(service-stop! svc10)

;; ---- diagnostics never contain key material ------------------------------------------

(define diag (service-diagnostics svc1))
(check-true (string-contains? diag "BrainFuel"))
(check-true
 (string-contains? diag (account-config-id (cadr (service-accounts svc1)))))
(check-false (string-contains? diag "test-key-1"))
(check-true (string-contains? diag "provider=glm"))
(service-stop! svc1)
