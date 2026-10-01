#lang racket/base

;; The application service, ported from ViewModels/MainViewModel.cs +
;; Services/SettingsService.cs key handling. Owns settings, credentials,
;; per-account refresh state, burn trackers, histories and alerts.
;;
;; Concurrency: one lock guards all runtime state; refreshes are single-flight
;; (a manual refresh and the timer tick never overlap - a second caller is
;; skipped, like MainViewModel.IsRefreshing). The timer thread is started by
;; service-init!, which the backend calls from an RPC handler so the thread
;; inherits the server's event emitter (module-level threads have none).

(require racket/list
         racket/string
         "burn-tracker.rkt"
         "claude-client.rkt"
         "codex-client.rkt"
         "credentials.rkt"
         "failure.rkt"
         "glm-client.rkt"
         "history-store.rkt"
         "http-fetch.rkt"
         "providers.rkt"
         "settings.rkt"
         "strings.rkt"
         "timestamps.rkt"
         ;; burn-tracker also provides severity-from-used-pct; only the
         ;; snapshot struct is needed here, so require it explicitly.
         (only-in "usage-models.rkt"
                  usage-snapshot
                  usage-snapshot?
                  usage-snapshot-hourly-pct
                  usage-snapshot-weekly-pct
                  usage-snapshot-hourly-reset-ms
                  usage-snapshot-weekly-reset-ms
                  usage-snapshot-plan-level
                  usage-snapshot-fetched-at-ms))

(provide (struct-out service)
         (struct-out account-runtime)
         (struct-out snapshot-info)
         (struct-out details-info)
         (struct-out settings-info)
         (struct-out account-draft)
         (struct-out alert-info)
         open-service!
         service-stop!
         service-init!
         service-refresh!
         service-switch-account!
         service-save-account!
         service-remove-account!
         service-apply-settings!
         service-accounts
         service-active-account-id
         service-snapshot-info
         service-details
         service-settings-info
         service-diagnostics
         service-timer-interval-ms
         current-app-version
         transient-retry-ms)

;; ---- runtime state ----------------------------------------------------------

(struct account-runtime
  (config              ; account-config (shared with settings)
   last                ; usage-snapshot or #f
   failure-kind        ; symbol or #f
   failure-message     ; string or #f
   fetched-at-ms       ; unix millis of the last attempt (success or failure)
   hourly-burn weekly-burn
   history
   hourly-alerted weekly-alerted)
  #:transparent #:mutable)

(struct runtime
  (settings
   keys                ; mutable hash id -> key (never persisted except fallback)
   plaintext-fallback?
   storage-state       ; 'none | 'protected | 'plaintext-fallback | 'protected-unavailable
   account-runtimes    ; list of account-runtime, settings order
   runtime-hash        ; id -> account-runtime
   timer-thread
   timer-interval-ms
   wake                ; semaphore; posts reset the timer wait
   stop?)
  #:transparent #:mutable)

(struct service
  ;; `lock` guards all runtime state; `flight` makes refreshes single-flight
  ;; (two of them, because change callbacks fired inside a refresh pass read
  ;; state back through `lock`).
  (data-dir settings-path fetch store now on-notify on-change runtime-box lock flight)
  #:transparent)

(struct snapshot-info
  (account-id plan-level
              hourly-pct hourly-reset-ms
              weekly-pct weekly-reset-ms
              fetched-at-ms
              failure-kind failure-message)
  #:transparent)

(struct details-info
  (hourly-pct hourly-reset-ms weekly-pct weekly-reset-ms
   hourly-rate hourly-minutes-to-empty hourly-comparison
   weekly-rate weekly-minutes-to-empty weekly-comparison
   history-samples history-note)
  #:transparent)

(struct settings-info
  (refresh-interval-minutes
   hourly-remaining weekly-remaining
   notify-enabled notify-threshold
   theme language size-mode
   always-on-top autostart
   hotkey-enabled hotkey-combo
   ring-palette card-opacity
   credential-state)
  #:transparent)

(struct account-draft
  (id name provider base-domain api-key clear-key?)
  #:transparent)

(struct alert-info (account-id which message) #:transparent)

(define transient-retry-ms 45000)

;; ---- construction -----------------------------------------------------------

(define (open-service! #:data-dir data-dir
                       #:settings-path settings-path
                       #:fetch [fetch default-fetch]
                       #:store [store (make-unavailable-store)]
                       #:now [now now-ms]
                       #:on-notify [on-notify #f]
                       #:on-change [on-change #f])
  (define loaded (load-settings settings-path))
  (define rt (runtime (loaded-settings-settings loaded)
                      (make-hash)
                      #f
                      'none
                      '()
                      (hasheq)
                      #f
                      (* 5 60 1000)
                      (make-semaphore)
                      #f))
  (define svc (service data-dir settings-path fetch store now on-notify on-change
                       (box #f) (make-semaphore 1) (make-semaphore 1)))
  (set-box! (service-runtime-box svc) rt)
  (load-keys! svc rt (loaded-settings-raw-doc loaded))
  (build-runtimes! svc rt)
  svc)

(define (runtime-lock svc thunk)
  (call-with-semaphore (service-lock svc) thunk))

(define (current-runtime svc)
  (unbox (service-runtime-box svc)))

;; ---- key storage ------------------------------------------------------------

(define (key-for rt account-id)
  (hash-ref (runtime-keys rt) account-id #f))

;; Port of SettingsService.LoadKeys.
(define (load-keys! svc rt raw-doc)
  (hash-clear! (runtime-keys rt))
  (set-runtime-plaintext-fallback?! rt #f)

  (define plaintext (plaintext-keys-from-doc raw-doc))
  (for ([(k v) (in-hash plaintext)])
    (hash-set! (runtime-keys rt) k v))
  (when (> (hash-count plaintext) 0)
    (set-runtime-plaintext-fallback?! rt #t))

  (define protected (parse-credential-blob ((credential-store-ref (service-store svc)))))
  (define settings (runtime-settings rt))
  (cond
    [(> (hash-count protected) 0)
     (for ([(k v) (in-hash protected)])
       (hash-set! (runtime-keys rt) k v))
     (set-runtime-plaintext-fallback?! rt #f)
     (set-runtime-storage-state! rt 'protected)
     (persist-keys! svc rt)]
    [(> (hash-count (runtime-keys rt)) 0)
     (set-runtime-storage-state! rt 'plaintext-fallback)]
    [(ormap account-config-configured (app-settings-accounts settings))
     ;; A protected credential is known to exist, but the OS store cannot be
     ;; read right now. Preserve markers and retry during refreshes.
     (set-runtime-storage-state! rt 'protected-unavailable)]
    [else
     (set-runtime-storage-state! rt 'none)])

  ;; Mark accounts configured: keyring keys for key-based providers, local
  ;; CLI login presence for Codex/Claude (they hold no keyring entry).
  (for ([a (in-list (app-settings-accounts settings))])
    (cond
      [(cli-login-provider? (account-config-provider a))
       (set-account-config-configured!
        a (cli-credentials-exist? (account-config-provider a)))]
      [else
       (define key (key-for rt (account-config-id a)))
       (when (and key (positive? (string-length key)))
         (set-account-config-configured! a #t))])))

;; Port of SettingsService.PersistKeys. Never downgrades: while the system
;; store is unavailable the markers stay and keys remain untouched.
(define (persist-keys! svc rt)
  (define settings (runtime-settings rt))
  (define configured-ids
    (for/list ([a (in-list (app-settings-accounts settings))]
               #:when (account-config-configured a))
      (account-config-id a)))
  (for ([k (in-hash-keys (runtime-keys rt))])
    (unless (member k configured-ids)
      (hash-remove! (runtime-keys rt) k)))
  (cond
    [(and (= (hash-count (runtime-keys rt)) 0)
          (not (eq? (runtime-storage-state rt) 'protected-unavailable)))
     ((credential-store-remove! (service-store svc)))
     (set-runtime-storage-state! rt 'none)
     (set-runtime-plaintext-fallback?! rt #f)]
    [(eq? (runtime-storage-state rt) 'protected-unavailable)
     (void)]
    [else
     (define blob (credential-blob->bytes (runtime-keys rt)))
     (if (and blob ((credential-store-set! (service-store svc)) blob))
         (begin
           (set-runtime-storage-state! rt 'protected)
           (set-runtime-plaintext-fallback?! rt #f))
         (begin
           (set-runtime-storage-state! rt 'plaintext-fallback)
           (set-runtime-plaintext-fallback?! rt #t)))]))

;; Port of SettingsService.TryRefreshProtectedKeys.
(define (try-refresh-protected-keys! svc rt)
  (cond
    [(eq? (runtime-storage-state rt) 'protected) #t]
    [(not (eq? (runtime-storage-state rt) 'protected-unavailable)) #f]
    [else
     (define protected
       (parse-credential-blob ((credential-store-ref (service-store svc)))))
     (if (> (hash-count protected) 0)
         (begin
           (for ([(k v) (in-hash protected)])
             (hash-set! (runtime-keys rt) k v))
           (set-runtime-storage-state! rt 'protected)
           (for ([a (in-list (app-settings-accounts (runtime-settings rt)))]
                 #:when (key-for rt (account-config-id a)))
             (set-account-config-configured! a #t))
           #t)
         #f)]))

;; ---- settings persistence ----------------------------------------------------

(define (save-settings-file! svc rt)
  (persist-keys! svc rt)
  (save-settings-json
   (service-settings-path svc)
   (runtime-settings rt)
   (and (runtime-plaintext-fallback? rt)
        (hash-copy (runtime-keys rt)))))

;; ---- account runtimes --------------------------------------------------------

;; Aligns runtime state with the account list, preserving snapshots, trackers
;; and histories across edits (MainViewModel.BuildAccounts).
(define (build-runtimes! svc rt)
  (define settings (runtime-settings rt))
  (define old (runtime-runtime-hash rt))
  (define fresh
    (for/list ([a (in-list (app-settings-accounts settings))])
      (or (hash-ref old (account-config-id a) #f)
          (account-runtime a #f #f #f 0
                           (make-burn-tracker) (make-burn-tracker)
                           (load-history
                            (history-path-for-account
                             (service-data-dir svc) (account-config-id a)))
                           #f #f))))
  ;; Keep config objects in sync (name/domain/provider may have been edited).
  (for ([art (in-list fresh)])
    (define current
      (findf (lambda (a)
               (equal? (account-config-id a)
                       (account-config-id (account-runtime-config art))))
             (app-settings-accounts settings)))
    (when current
      (set-account-runtime-config! art current)))
  (set-runtime-account-runtimes! rt fresh)
  (set-runtime-runtime-hash!
   rt
   (for/hash ([art (in-list fresh)])
     (values (account-config-id (account-runtime-config art)) art))))

(define (active-runtime rt)
  (define settings (runtime-settings rt))
  (or (hash-ref (runtime-runtime-hash rt)
                (app-settings-active-account-id settings)
                #f)
      (and (pair? (runtime-account-runtimes rt))
           (car (runtime-account-runtimes rt)))))

(define (account-label config)
  (define name (account-config-name config))
  (if (blank-string? name)
      (account-config-id config)
      name))

;; ---- refresh ------------------------------------------------------------------

(define (fetch-usage svc config provider credential now-ms)
  (case provider
    [(codex)
     (codex-fetch-usage (service-fetch svc) credential
                        (read-codex-account-id) now-ms)]
    [(claude)
     (claude-fetch-usage (service-fetch svc) credential now-ms)]
    [else
     (glm-fetch-usage (service-fetch svc)
                      (account-config-base-domain config)
                      credential
                      now-ms)]))

;; Port of MainViewModel.RefreshAccount.
(define (refresh-account! svc rt art)
  (define config (account-runtime-config art))
  (define provider (account-config-provider config))
  (define now-ms ((service-now svc)))
  (define credential
    (if (cli-login-provider? provider)
        (read-cli-token provider)
        (key-for rt (account-config-id config))))
  (cond
    ;; Nothing to poll (no key, no CLI login): clear state silently, exactly
    ;; like the old app.
    [(or (not credential) (equal? "" (string-trim credential)))
     (set-account-runtime-last! art #f)
     (set-account-runtime-failure-kind! art #f)
     (set-account-runtime-failure-message! art #f)
     (set-account-runtime-fetched-at-ms! art now-ms)]
    [else
     (define result
       (with-handlers
           ([exn:usage?
             (lambda (e)
               (log-error
                (format "refresh failed [~a] (~a): ~a"
                        (account-label config)
                        (exn:usage-kind e)
                        (exn-message e)))
               (cons 'failed e))]
            [exn:fail?
             (lambda (e)
               (log-error
                (format "refresh failed [~a] (Unknown): ~a"
                        (account-label config)
                        (exn-message e)))
               (cons 'failed e))])
         (define snap (fetch-usage svc config provider credential now-ms))
         (set-account-runtime-last! art snap)
         (set-account-runtime-failure-kind! art #f)
         (set-account-runtime-failure-message! art #f)
         (set-account-runtime-fetched-at-ms!
          art (usage-snapshot-fetched-at-ms snap))
         ;; Feed the burn-rate estimators only from real observations, then
         ;; persist the history sample.
         (when (usage-snapshot-hourly-pct snap)
           (burn-add-sample! (account-runtime-hourly-burn art)
                             (usage-snapshot-fetched-at-ms snap)
                             (usage-snapshot-hourly-pct snap)))
         (when (usage-snapshot-weekly-pct snap)
           (burn-add-sample! (account-runtime-weekly-burn art)
                             (usage-snapshot-fetched-at-ms snap)
                             (usage-snapshot-weekly-pct snap)))
         (define hstore (account-runtime-history art))
         (history-append!
          hstore
          (usage-sample (usage-snapshot-fetched-at-ms snap)
                        (usage-snapshot-hourly-pct snap)
                        (usage-snapshot-weekly-pct snap)))
         (history-prune! hstore (usage-snapshot-fetched-at-ms snap))
         (history-save! hstore)
         (cons 'ok #f)))
     (case (car result)
       [(ok) (void)]
       [else
        (define e (cdr result))
        (log-error
         (format "refresh failed [~a] (~a): ~a"
                 (account-label config)
                 (if (exn:usage? e) (exn:usage-kind e) 'unknown)
                 (exn-message e)))
        (set-account-runtime-last! art #f)
        (set-account-runtime-failure-kind!
         art
         (if (exn:usage? e) (exn:usage-kind e) 'unknown))
        (set-account-runtime-failure-message! art (exn-message e))
        (set-account-runtime-fetched-at-ms! art now-ms)])]))

;; The active account's transient failure shortens the wait so recovery needs
;; at most ~45 s; permanent causes keep the configured interval.
(define (compute-timer-interval! rt)
  (define active (active-runtime rt))
  (set-runtime-timer-interval-ms!
   rt
   (if (and active
            (account-runtime-failure-kind active)
            (transient-failure? (account-runtime-failure-kind active)))
       transient-retry-ms
       (* 60 1000
          (max 1 (app-settings-refresh-interval-minutes
                  (runtime-settings rt)))))))

;; ---- alerts ---------------------------------------------------------------------

(define (round-int* x) (inexact->exact (round x)))

(define (format-span settings hours)
  (parameterize ([current-language (app-settings-language settings)])
    (cond
      [(< hours 1) (tr "MinutesLater" (round-int* (ceiling (* hours 60))))]
      [(< hours 48) (tr "HoursLater" (round-int* (round hours)))]
      [else (tr "DaysLater" (round-int* (round (/ hours 24))))])))

;; One of three flavor lines, plus a burn-rate projection when available.
(define (fun-body settings key-prefix used-pct burn now-ms)
  (parameterize ([current-language (app-settings-language settings)])
    (define body
      (tr (string-append key-prefix (number->string (+ 1 (random 3))))
          (round-int* used-pct)))
    (define hours (burn-project-hours-to-exhaustion burn used-pct now-ms))
    (if hours
        (string-append body (tr "NotifyBurnSuffix" (format-span settings hours)))
        body)))

;; Fires once when used >= threshold; re-arms only after falling below
;; threshold - 5 (hysteresis). Per-account, per-window.
(define (collect-alerts svc rt)
  (define settings (runtime-settings rt))
  (if (not (app-settings-notify-enabled settings))
      '()
      (let* ([thr (min 99 (max 1 (app-settings-notify-threshold settings)))]
             [configured-count
              (count account-config-configured
                     (map account-runtime-config
                          (runtime-account-runtimes rt)))]
             [multi (> configured-count 1)]
             [now-ms ((service-now svc))])
        (apply
         append
         (for/list ([art (in-list (runtime-account-runtimes rt))])
           (define config (account-runtime-config art))
           (define snap (account-runtime-last art))
           (define title-prefix
             (if multi
                 (string-append (account-label config) " · ")
                 ""))
           (define h (if (and snap (usage-snapshot-hourly-pct snap))
                         (usage-snapshot-hourly-pct snap)
                         0.0))
           (define w (if (and snap (usage-snapshot-weekly-pct snap))
                         (usage-snapshot-weekly-pct snap)
                         0.0))
           (define alerts '())
           (when (and snap (usage-snapshot-hourly-pct snap)
                      (>= h thr)
                      (not (account-runtime-hourly-alerted art)))
             (set-account-runtime-hourly-alerted! art #t)
             (set! alerts
                   (cons (alert-info
                          (account-config-id config) "hourly"
                          (string-append
                           title-prefix
                           (parameterize ([current-language
                                           (app-settings-language settings)])
                             (tr "NotifyHourlyTitle"))
                           "\n"
                           (fun-body settings "NotifyHourlyBody" h
                                     (account-runtime-hourly-burn art)
                                     now-ms)))
                         alerts)))
           (when (< h (- thr 5))
             (set-account-runtime-hourly-alerted! art #f))

           (when (and snap (usage-snapshot-weekly-pct snap)
                      (>= w thr)
                      (not (account-runtime-weekly-alerted art)))
             (set-account-runtime-weekly-alerted! art #t)
             (set! alerts
                   (cons (alert-info
                          (account-config-id config) "weekly"
                          (string-append
                           title-prefix
                           (parameterize ([current-language
                                           (app-settings-language settings)])
                             (tr "NotifyWeeklyTitle"))
                           "\n"
                           (fun-body settings "NotifyWeeklyBody" w
                                     (account-runtime-weekly-burn art)
                                     now-ms)))
                         alerts)))
           (when (< w (- thr 5))
             (set-account-runtime-weekly-alerted! art #f))
           alerts)))))

;; ---- the single-flight refresh pass ----------------------------------------------

(define (notify-change svc what)
  (define cb (service-on-change svc))
  (when cb (cb what)))

(define (fire-alerts svc alerts)
  (define cb (service-on-notify svc))
  (when cb
    (for ([a (in-list alerts)])
      (cb (alert-info-account-id a)
          (alert-info-which a)
          (alert-info-message a)))))

;; Returns 'refreshed or 'skipped (another refresh was in flight). Alerts and
;; the snapshot callback fire while the flight is still held (so emitted
;; events keep refresh order) but outside the runtime lock, which the change
;; callbacks read back through.
(define (service-refresh! svc)
  (if (semaphore-try-wait? (service-flight svc))
      (dynamic-wind
          (lambda () (void))
          (lambda ()
            (define alerts
              (runtime-lock
               svc
               (lambda ()
                 (define rt (current-runtime svc))
                 (if (runtime-stop? rt)
                     '()
                     (begin
                       ;; Locked credentials from a previous run recover on
                       ;; any refresh.
                       (when (eq? (runtime-storage-state rt)
                                  'protected-unavailable)
                         (try-refresh-protected-keys! svc rt))
                       (for ([art (in-list (runtime-account-runtimes rt))])
                         (refresh-account! svc rt art))
                       (compute-timer-interval! rt)
                       (collect-alerts svc rt))))))
            (fire-alerts svc alerts)
            (notify-change svc 'snapshot)
            'refreshed)
          (lambda () (semaphore-post (service-flight svc))))
      'skipped))


;; ---- timer ------------------------------------------------------------------

;; Started lazily from an RPC handler: the thread inherits the handler's
;; event emitter and custodian, so background refreshes can emit events and
;; die with the server. A module-level thread would have neither.
(define (service-init! svc)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (unless (runtime-timer-thread rt)
                    (set-runtime-stop?! rt #f)
                    (define th
                      (thread (lambda () (timer-loop svc))))
                    (set-runtime-timer-thread! rt th)))))

(define (drain-wake! wake)
  (let drain ()
    (when (semaphore-try-wait? wake) (drain))))

(define (timer-loop svc)
  ;; Startup refresh: one pass right away, then the configured cadence.
  (service-refresh! svc)
  (let loop ()
    (define rt (current-runtime svc))
    (cond
      [(runtime-stop? rt) (void)]
      [else
       (drain-wake! (runtime-wake rt))
       (sync/timeout (/ (max (runtime-timer-interval-ms rt) 1000) 1000.0)
                     (runtime-wake rt))
       (define rt2 (current-runtime svc))
       (unless (runtime-stop? rt2)
         (service-refresh! svc)
         (loop))])))

(define (service-stop! svc)
  (define rt (current-runtime svc))
  (set-runtime-stop?! rt #t)
  (define th (runtime-timer-thread rt))
  (when th (kill-thread th))
  (set-runtime-timer-thread! rt #f))

;; ---- queries ------------------------------------------------------------------

(define (service-accounts svc)
  (runtime-lock svc
                (lambda ()
                  (app-settings-accounts (runtime-settings (current-runtime svc))))))

;; Current timer wait, exposed for tests (45 s after a transient failure of
;; the active account, otherwise the configured interval).
(define (service-timer-interval-ms svc)
  (runtime-lock svc
                (lambda ()
                  (runtime-timer-interval-ms (current-runtime svc)))))

(define (service-active-account-id svc)
  (runtime-lock svc
                (lambda ()
                  (app-settings-active-account-id
                   (runtime-settings (current-runtime svc))))))

(define (service-snapshot-info svc)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define art (active-runtime rt))
                  (and art
                       (let ([snap (account-runtime-last art)]
                             [config (account-runtime-config art)])
                         (snapshot-info
                          (account-config-id config)
                          (and snap (usage-snapshot-plan-level snap))
                          (and snap (usage-snapshot-hourly-pct snap))
                          (and snap (usage-snapshot-hourly-reset-ms snap))
                          (and snap (usage-snapshot-weekly-pct snap))
                          (and snap (usage-snapshot-weekly-reset-ms snap))
                          (account-runtime-fetched-at-ms art)
                          (account-runtime-failure-kind art)
                          (account-runtime-failure-message art)))))))

(define (burn-info* settings burn snap-pct now-ms samples compare-to-average?)
  (define rate (and burn (burn-tracker-rate-pct-per-hour burn)))
  (define has-rate
    (and rate snap-pct (burn-has-current-rate? burn now-ms)))
  (define hours
    (and has-rate
         (burn-project-hours-to-exhaustion burn snap-pct now-ms)))
  (define comparison
    (and has-rate compare-to-average?
         (parameterize ([current-language (app-settings-language settings)])
           (define avg (average-burn-rate-24h samples now-ms))
           ;; Tiny averages (< 0.5 %/h) would produce absurd ratios.
           (and avg (> avg 0.5)
                (let* ([delta
                        (max 1
                             (round-int*
                              (/ (* (abs (- rate avg)) 100) avg)))]
                       [key
                        (cond
                          [(> rate (* avg 1.15)) "BurnFaster"]
                          [(< rate (* avg 0.85)) "BurnSlower"]
                          [else "BurnTypical"])])
                  (tr key delta))))))

  (values (and has-rate rate)
          (and hours (round-int* (* hours 60)))
          comparison))

(define (service-details svc account-id)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define art (hash-ref (runtime-runtime-hash rt) account-id #f))
                  (unless art
                    (error 'get-details "unknown account: ~a" account-id))
                  (define settings (runtime-settings rt))
                  (define snap (account-runtime-last art))
                  (define now-ms ((service-now svc)))
                  (define samples (history-samples (account-runtime-history art)))
                  (define-values (h-rate h-minutes h-comparison)
                    (burn-info* settings
                                (account-runtime-hourly-burn art)
                                (and snap (usage-snapshot-hourly-pct snap))
                                now-ms samples #t))
                  (define-values (w-rate w-minutes w-comparison)
                    ;; The weekly window has no meaningful daily baseline.
                    (burn-info* settings
                                (account-runtime-weekly-burn art)
                                (and snap (usage-snapshot-weekly-pct snap))
                                now-ms samples #f))
                  (details-info
                   (and snap (usage-snapshot-hourly-pct snap))
                   (and snap (usage-snapshot-hourly-reset-ms snap))
                   (and snap (usage-snapshot-weekly-pct snap))
                   (and snap (usage-snapshot-weekly-reset-ms snap))
                   h-rate h-minutes h-comparison
                   w-rate w-minutes w-comparison
                   samples
                   (if (< (length samples) 2)
                       (parameterize ([current-language
                                       (app-settings-language settings)])
                         (tr "HistoryCollecting"))
                       "")))))

(define (service-settings-info svc)
  (runtime-lock svc
                (lambda ()
                  (define s (runtime-settings (current-runtime svc)))
                  (settings-info
                   (app-settings-refresh-interval-minutes s)
                   (eq? (app-settings-hourly-display-style s) 'remaining)
                   (eq? (app-settings-weekly-display-style s) 'remaining)
                   (app-settings-notify-enabled s)
                   (app-settings-notify-threshold s)
                   (symbol->string (app-settings-theme-mode s))
                   (symbol->string (app-settings-language s))
                   (symbol->string (app-settings-size-mode s))
                   (app-settings-always-on-top s)
                   (app-settings-autostart s)
                   (app-settings-hotkey-enabled s)
                   (app-settings-hotkey-combo s)
                   (app-settings-ring-palette s)
                   (app-settings-card-opacity s)
                   (symbol->string (runtime-storage-state (current-runtime svc)))))))

;; ---- mutations ------------------------------------------------------------------

(define (service-switch-account! svc account-id)
  (define changed? #f)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define settings (runtime-settings rt))
                  (when (and (not (equal? account-id
                                          (app-settings-active-account-id settings)))
                             (findf (lambda (a)
                                      (equal? (account-config-id a) account-id))
                                    (app-settings-accounts settings)))
                    ;; Instant: every account is polled already.
                    (set-app-settings-active-account-id! settings account-id)
                    (save-settings-file! svc rt)
                    (set! changed? #t))))
  (when changed?
    (notify-change svc 'accounts)
    (notify-change svc 'snapshot))
  changed?)

;; Applies a settings payload from the host (clamped like the old dialog),
;; persists, rebuilds accounts and refreshes immediately.
(define (service-apply-settings! svc info)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define settings (runtime-settings rt))
                  (set-app-settings-refresh-interval-minutes!
                   settings
                   (min 60 (max 1
                                (settings-info-refresh-interval-minutes info))))
                  (set-app-settings-hourly-display-style!
                   settings
                   (if (settings-info-hourly-remaining info) 'remaining 'used))
                  (set-app-settings-weekly-display-style!
                   settings
                   (if (settings-info-weekly-remaining info) 'remaining 'used))
                  (set-app-settings-notify-enabled!
                   settings (settings-info-notify-enabled info))
                  (set-app-settings-notify-threshold!
                   settings
                   (min 99 (max 1 (settings-info-notify-threshold info))))
                  (set-app-settings-theme-mode!
                   settings
                   (string->symbol (settings-info-theme info)))
                  (set-app-settings-language!
                   settings
                   (string->symbol (settings-info-language info)))
                  (set-app-settings-size-mode!
                   settings
                   (string->symbol (settings-info-size-mode info)))
                  (set-app-settings-always-on-top!
                   settings (settings-info-always-on-top info))
                  (set-app-settings-autostart!
                   settings (settings-info-autostart info))
                  (set-app-settings-hotkey-enabled!
                   settings (settings-info-hotkey-enabled info))
                  (set-app-settings-hotkey-combo!
                   settings (settings-info-hotkey-combo info))
                  (set-app-settings-ring-palette!
                   settings (settings-info-ring-palette info))
                  (set-app-settings-card-opacity!
                   settings
                   (min 1.0 (max 0.0 (settings-info-card-opacity info))))
                  (save-settings-file! svc rt)
                  (build-runtimes! svc rt)
                  (semaphore-post (runtime-wake rt))))
  (service-refresh! svc)
  (notify-change svc 'accounts)
  (notify-change svc 'snapshot)
  (service-settings-info svc))

(define (require-existing-account settings account-id)
  (or (findf (lambda (a)
               (equal? (account-config-id a) account-id))
             (app-settings-accounts settings))
      (error 'save-account "unknown account: ~a" account-id)))

(define (service-save-account! svc draft)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define settings (runtime-settings rt))
                  (define provider
                    (normalize-provider (account-draft-provider draft)))
                  (define api-key
                    (let ([k (account-draft-api-key draft)])
                      (and k (string-trim k))))
                  (define name (account-draft-name draft))
                  (define base-domain
                    (if (blank-string? (account-draft-base-domain draft))
                        default-base-domain
                        (account-draft-base-domain draft)))
                  ;; Codex/Claude accounts cannot be saved without a local CLI
                  ;; login (CliLoginMissing).
                  (when (cli-login-provider? provider)
                    (unless (cli-credentials-exist? provider)
                      (error 'save-account
                             (parameterize ([current-language
                                             (app-settings-language settings)])
                               (tr "CliLoginMissing"
                                   (symbol->string provider))))))

                  (define account-id
                    (or (account-draft-id draft) (new-account-id)))
                  (define account
                    (if (account-draft-id draft)
                        (require-existing-account settings account-id)
                        (account-config account-id "" provider base-domain #f)))

                  (set-account-config-name! account name)
                  (set-account-config-provider! account provider)
                  (set-account-config-base-domain! account base-domain)

                  (cond
                    [api-key
                     (hash-set! (runtime-keys rt) account-id api-key)
                     (set-account-config-configured! account #t)]
                    [(account-draft-clear-key? draft)
                     ;; Tombstone semantics: even if keychain delete fails,
                     ;; the account stays de-configured.
                     (hash-remove! (runtime-keys rt) account-id)
                     (set-account-config-configured! account #f)])
                  ;; CLI-login accounts are configured exactly when the local
                  ;; CLI login exists (re-read on save).
                  (when (and (cli-login-provider? provider)
                             (not (account-draft-clear-key? draft))
                             (not api-key))
                    (set-account-config-configured!
                     account (cli-credentials-exist? provider)))

                  (unless (account-draft-id draft)
                    (set-app-settings-accounts!
                     settings
                     (append (app-settings-accounts settings)
                             (list account)))
                    (set-app-settings-active-account-id! settings account-id))

                  (save-settings-file! svc rt)
                  (build-runtimes! svc rt)))
  (service-refresh! svc)
  (notify-change svc 'accounts)
  (notify-change svc 'snapshot)
  (service-accounts svc))

(define (service-remove-account! svc account-id)
  (define changed? #f)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define settings (runtime-settings rt))
                  (define remaining
                    (filter (lambda (a)
                              (not (equal? account-id
                                           (account-config-id a))))
                            (app-settings-accounts settings)))
                  (unless (= (length remaining)
                             (length (app-settings-accounts settings)))
                    (hash-remove! (runtime-keys rt) account-id)
                    (set-app-settings-accounts! settings remaining)
                    (when (equal? account-id
                                  (app-settings-active-account-id settings))
                      (set-app-settings-active-account-id!
                       settings
                       (if (pair? remaining)
                           (account-config-id (car remaining))
                           "")))
                    ;; Never persist an empty list: fresh installs get the
                    ;; default account, mirroring NormalizeAccounts.
                    (normalize-accounts! settings)
                    (save-settings-file! svc rt)
                    (build-runtimes! svc rt)
                    (set! changed? #t))))
  (when changed?
    (notify-change svc 'accounts)
    (notify-change svc 'snapshot))
  changed?)

;; ---- diagnostics -----------------------------------------------------------------

(define storage-state-names
  '([none . "None"]
    [protected . "Protected"]
    [plaintext-fallback . "PlaintextFallback"]
    [protected-unavailable . "ProtectedUnavailable"]))

;; Version comes from rivet.rktd via the backend; overridable for tests.
(define current-app-version (make-parameter "0.9.0"))

(define (service-diagnostics svc)
  (runtime-lock svc
                (lambda ()
                  (define rt (current-runtime svc))
                  (define settings (runtime-settings rt))
                  (define accounts (app-settings-accounts settings))
                  (string-append
                   (format "BrainFuel ~a~n" (current-app-version))
                   (format "OS: ~a (~a)~n" (system-type) (system-type 'arch))
                   (format "Credential storage: ~a via ~a~n"
                           (cdr (assq (runtime-storage-state rt)
                                      storage-state-names))
                           (credential-store-display-name
                            (service-store svc)))
                   (format "Data dir: ~a~n" (path->string (service-data-dir svc)))
                   (format "Accounts (~a):~n" (length accounts))
                   (string-append
                    (apply string-append
                           (for/list ([a (in-list accounts)])
                             ;; Metadata only - keys never enter this output.
                             (format "  - id=~a name=~a provider=~a domain=~a configured=~a~n"
                                     (account-config-id a)
                                     (if (blank-string? (account-config-name a))
                                         "-"
                                         (account-config-name a))
                                     (account-config-provider a)
                                     (account-config-base-domain a)
                                     (account-config-configured a))))
                    (format "Active account: ~a~n"
                            (or (app-settings-active-account-id settings)
                                "-")))))))
