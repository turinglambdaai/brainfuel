#lang racket/base

;; BrainFuel Rivet backend: the typed RPC surface hosts talk to (RVT1).
;; The module only DECLARES the surface; every runtime thing (settings load,
;; credentials, scheduler thread) starts in the `init` RPC so the scheduler
;; thread inherits the server's event emitter and custodian.

(require racket/file
         racket/list
         racket/runtime-path
         racket/string
         rivet/backend
         rivet/system
         "../racket/brainfuel/credentials.rkt"
         "../racket/brainfuel/history-store.rkt"
         "../racket/brainfuel/http-fetch.rkt"
         "../racket/brainfuel/scheduler.rkt"
         "../racket/brainfuel/settings.rkt"
         "../racket/brainfuel/updater.rkt"
         "../racket/brainfuel/usage-models.rkt")

(provide start
         set-fetch-override!
         set-store-override!
         set-data-dir-override!
         reset-backend-for-test!)

;; ---- schema types -----------------------------------------------------------

(define-enum Provider (glm codex claude))
(define-enum FailureKind
  (authentication network tls proxy timeout rate-limited
                  no-coding-plan service-unavailable invalid-response unknown))
(define-enum Severity (calm amber red))

(define-record Account
  ([id : String] [name : String] [provider : Provider]
   [base-domain : String] [configured : Bool]))
(define-record WindowUsage
  ([used-bp : (Optional Int64)] [reset-at-ms : (Optional Int64)]))
(define-record FailureInfo ([kind : FailureKind] [message : String]))
(define-record QuotaSnapshot
  ([account-id : String] [plan-level : String]
   [hourly : WindowUsage] [weekly : WindowUsage]
   [severity : Severity] [fetched-at-ms : Int64]
   [failure : (Optional FailureInfo)]))
(define-record BurnInfo
  ([rate-bp-per-hour : (Optional Int64)]
   [minutes-to-empty : (Optional Int64)]
   [comparison : (Optional String)]))
(define-record HistorySample
  ([at-ms : Int64]
   [hourly-bp : (Optional Int64)] [weekly-bp : (Optional Int64)]))
(define-record AccountDraft
  ([id : (Optional String)] [name : String] [provider : Provider]
   [base-domain : String] [api-key : (Optional String)]
   [clear-key : Bool]))
(define-record Details
  ([hourly : WindowUsage] [weekly : WindowUsage]
   [hourly-burn : BurnInfo] [weekly-burn : BurnInfo]
   [history : (List HistorySample)] [history-note : String]))
(define-record Alert
  ([account-id : String] [which : String] [message : String]))
(define-record SettingsData
  ([refresh-interval-minutes : Int64]
   [hourly-remaining : Bool] [weekly-remaining : Bool]
   [notify-enabled : Bool] [notify-threshold : Int64]
   [theme : String] [language : String] [size-mode : String]
   [always-on-top : Bool] [autostart : Bool]
   [hotkey-enabled : Bool] [hotkey-combo : String]
   [ring-palette : String] [card-opacity-bp : Int64]
   [credential-state : String]))

;; Result of check-updates. status: "available" | "up-to-date" | "error";
;; the descriptive fields are only filled for "available" (the family
;; UpdateCheck shape, taskly racket/taskly/rivet-schema.rkt).
(define-record UpdateCheck
  ([status : String]
   [error : (Optional String)]
   [current-version : String]
   [available-version : (Optional String)]
   [build : (Optional Int64)]
   [published-at : (Optional String)]
   [installer : (Optional String)]
   [size-bytes : (Optional Int64)]))

;; Polled by the host while a download runs. phase: idle | checking |
;; downloading | downloaded | error.
(define-record UpdateState
  ([phase : String]
   [percent : Int64]
   [message : (Optional String)]
   [downloaded-path : (Optional String)]
   [available-version : (Optional String)]))

;; ---- events / states ---------------------------------------------------------

(define-event quota-updated : QuotaSnapshot)
(define-event alert-triggered : Alert)

(define-state accounts : (List Account) (list))
(define-state active-account-id : String "")
(define-state snapshot : (Optional QuotaSnapshot) (void))

;; ---- app service wiring ------------------------------------------------------

(define service-box (box #f))

;; Test hooks: overridden before `init` to keep tests off the real network
;; and the user's real data directory.
(define fetch-override (box #f))
(define store-override (box #f))
(define data-dir-override-box (box #f))

(define (set-fetch-override! f) (set-box! fetch-override f))
(define (set-store-override! s) (set-box! store-override s))
(define (set-data-dir-override! p) (set-box! data-dir-override-box p))

(define (reset-backend-for-test!)
  (define svc (unbox service-box))
  (when svc (service-stop! svc))
  (set-box! service-box #f))

(define-runtime-path rivet-manifest-path "../rivet.rktd")

(define (manifest-version)
  (with-handlers ([exn:fail? (lambda (_) "0.0.0")])
    (define manifest (with-input-from-file rivet-manifest-path read))
    (if (and (hash? manifest) (hash-ref manifest 'version #f))
        (format "~a" (hash-ref manifest 'version))
        "0.0.0")))

(define secure-store-service-name "BrainFuel")
(define secure-store-account-name "glm-coding-plan-api-key")

;; One blob JSON {"accountId": key} inside the host's secure store. Without a
;; native adapter every call errors; those errors are caught so the domain
;; core can keep state protected-unavailable and retry later - never
;; downgrade.
(define (system-credential-store)
  (credential-store
   (lambda ()
     (with-handlers ([exn:fail? (lambda (_) #f)])
       (secure-store-ref secure-store-service-name
                         secure-store-account-name)))
   (lambda (blob)
     (with-handlers ([exn:fail? (lambda (_) #f)])
       (secure-store-set! secure-store-service-name
                          secure-store-account-name blob)
       #t))
   (lambda ()
     (with-handlers ([exn:fail? (lambda (_) #f)])
       (secure-store-remove! secure-store-service-name
                             secure-store-account-name)
       #t))
   "system secure store"))

(define (require-service)
  (or (unbox service-box)
      (error 'brainfuel "backend is not initialized; call init first")))

;; ---- DTO conversion -----------------------------------------------------------

(define (opt-real->bp v)
  (if v (pct->bp v) (void)))

(define (opt-ms->int v)
  (cond
    [(not v) (void)]
    [(exact-integer? v) v]
    [(real? v) (inexact->exact (truncate v))]
    [else (void)]))

;; #f travels over the wire as the absent Optional.
(define (opt-value v)
  (if v v (void)))

(define (window-usage->dto pct reset-ms)
  (WindowUsage (opt-real->bp pct) (opt-ms->int reset-ms)))

(define (severity-rank s)
  (case s [(red) 2] [(amber) 1] [else 0]))

(define (snapshot-info->dto info)
  (define severities
    (append
     (if (snapshot-info-hourly-pct info)
         (list (severity-from-used-pct (snapshot-info-hourly-pct info)))
         '())
     (if (snapshot-info-weekly-pct info)
         (list (severity-from-used-pct (snapshot-info-weekly-pct info)))
         '())))
  (define severity
    (if (null? severities)
        'calm
        (argmax severity-rank severities)))
  (QuotaSnapshot
   (snapshot-info-account-id info)
   (or (snapshot-info-plan-level info) "")
   (window-usage->dto (snapshot-info-hourly-pct info)
                      (snapshot-info-hourly-reset-ms info))
   (window-usage->dto (snapshot-info-weekly-pct info)
                      (snapshot-info-weekly-reset-ms info))
   (Severity severity)
   (or (opt-ms->int (snapshot-info-fetched-at-ms info)) 0)
   (if (snapshot-info-failure-kind info)
       (FailureInfo
        (FailureKind (snapshot-info-failure-kind info))
        (or (snapshot-info-failure-message info) ""))
       (void))))

(define (account->dto a)
  (Account (account-config-id a)
           (account-config-name a)
           (Provider (account-config-provider a))
           (account-config-base-domain a)
           (account-config-configured a)))

(define (accounts->dto lst)
  (map account->dto lst))

(define (publish-accounts!)
  (define svc (unbox service-box))
  (when svc
    (state-set! accounts (accounts->dto (service-accounts svc)))
    (state-set! active-account-id (service-active-account-id svc))))

(define (publish-change! what)
  (define svc (unbox service-box))
  (when svc
    (case what
      [(accounts) (publish-accounts!)]
      [(snapshot)
       (define info (service-snapshot-info svc))
       (define dto (if info (snapshot-info->dto info) (void)))
       (state-set! snapshot dto)
       (unless (void? dto)
         (quota-updated dto))])))

(define (burn-info->dto rate minutes comparison)
  (BurnInfo
   (if rate (max 0 (min 1000000 (inexact->exact (round (* rate 100))))) (void))
   (if minutes minutes (void))
   (if (and comparison (positive? (string-length comparison)))
       comparison
       (void))))

(define (details-info->dto info)
  (Details
   (window-usage->dto (details-info-hourly-pct info)
                      (details-info-hourly-reset-ms info))
   (window-usage->dto (details-info-weekly-pct info)
                      (details-info-weekly-reset-ms info))
   (burn-info->dto (details-info-hourly-rate info)
                   (details-info-hourly-minutes-to-empty info)
                   (details-info-hourly-comparison info))
   (burn-info->dto (details-info-weekly-rate info)
                   (details-info-weekly-minutes-to-empty info)
                   (details-info-weekly-comparison info))
   (for/list ([s (in-list (details-info-history-samples info))])
     (HistorySample
      (usage-sample-at-ms s)
      (opt-real->bp (usage-sample-hourly-pct s))
      (opt-real->bp (usage-sample-weekly-pct s))))
   (details-info-history-note info)))

(define (settings-info->dto info)
  (SettingsData
   (settings-info-refresh-interval-minutes info)
   (settings-info-hourly-remaining info)
   (settings-info-weekly-remaining info)
   (settings-info-notify-enabled info)
   (settings-info-notify-threshold info)
   (settings-info-theme info)
   (settings-info-language info)
   (settings-info-size-mode info)
   (settings-info-always-on-top info)
   (settings-info-autostart info)
   (settings-info-hotkey-enabled info)
   (settings-info-hotkey-combo info)
   (settings-info-ring-palette info)
   (pct->bp (settings-info-card-opacity info))
   (settings-info-credential-state info)))

(define (dto->settings-info dto)
  (settings-info
   (record-ref dto 'refresh-interval-minutes)
   (record-ref dto 'hourly-remaining)
   (record-ref dto 'weekly-remaining)
   (record-ref dto 'notify-enabled)
   (record-ref dto 'notify-threshold)
   (record-ref dto 'theme)
   (record-ref dto 'language)
   (record-ref dto 'size-mode)
   (record-ref dto 'always-on-top)
   (record-ref dto 'autostart)
   (record-ref dto 'hotkey-enabled)
   (record-ref dto 'hotkey-combo)
   (record-ref dto 'ring-palette)
   (/ (record-ref dto 'card-opacity-bp) 10000.0)
   (record-ref dto 'credential-state)))

(define (draft-dto->domain dto)
  (account-draft
   (let ([id (record-ref dto 'id)])
     (if (void? id) #f id))
   (record-ref dto 'name)
   (enum-case (record-ref dto 'provider))
   (record-ref dto 'base-domain)
   (let ([key (record-ref dto 'api-key)])
     (if (void? key) #f key))
   (record-ref dto 'clear-key)))

;; ---- RPC surface ---------------------------------------------------------------

;; Starts the app service and the scheduler thread; returns immediately. The
;; initial refresh runs on the scheduler thread, which inherits THIS handler's
;; event emitter - background quota updates therefore reach the host.
(define-rpc (initialize : Void)
  (unless (unbox service-box)
    (define data-dir
      (or (unbox data-dir-override-box) (brainfuel-data-dir)))
    (define svc
      (open-service!
       #:data-dir data-dir
       #:settings-path (settings-path-for data-dir)
       #:fetch (or (unbox fetch-override) default-fetch)
       #:store (or (unbox store-override) (system-credential-store))
       #:on-notify
       (lambda (account-id which message)
         (alert-triggered (Alert account-id which message)))
       #:on-change publish-change!))
    (set-box! service-box svc)
    (publish-accounts!)
    (service-init! svc))
  (void))

;; Single-flight like MainViewModel.RefreshAsync: while a refresh is already
;; running this returns immediately instead of bursting duplicate requests.
(define-rpc (refresh-now : Void)
  (service-refresh! (require-service))
  (void))

(define-rpc (switch-account [id : String] : Void)
  (service-switch-account! (require-service) id)
  (void))

(define-rpc (get-details [account-id : String] : Details)
  (details-info->dto (service-details (require-service) account-id)))

(define-rpc (save-account [draft : AccountDraft] : (List Account))
  (define svc (require-service))
  (accounts->dto (service-save-account! svc (draft-dto->domain draft))))

(define-rpc (remove-account [id : String] : (List Account))
  (define svc (require-service))
  (service-remove-account! svc id)
  (accounts->dto (service-accounts svc)))

(define-rpc (get-settings : SettingsData)
  (settings-info->dto (service-settings-info (require-service))))

(define-rpc (save-settings [settings : SettingsData] : SettingsData)
  (settings-info->dto
   (service-apply-settings! (require-service) (dto->settings-info settings))))

(define-rpc (get-diagnostics : String)
  (parameterize ([current-app-version (manifest-version)])
    (service-diagnostics (require-service))))

;; ---- online update -----------------------------------------------------------
;; The family pattern (rivet/distribution, taskly racket/taskly/backend.rkt):
;; this backend verifies and downloads the signed artifact; hosts own
;; installation and the silent 4-hour throttle (last-update-check
;; updater-state key, shared/spec/UPDATE.md).

(define (update-check->record result)
  (UpdateCheck
   (hash-ref result 'status "error")
   (opt-value (hash-ref result 'message #f))
   (hash-ref result 'currentVersion app-version)
   (opt-value (hash-ref result 'availableVersion #f))
   (opt-value (hash-ref result 'build #f))
   (opt-value (hash-ref result 'publishedAt #f))
   (opt-value (hash-ref result 'installer #f))
   (opt-value (hash-ref result 'sizeBytes #f))))

;; Never raises: network/manifest failures surface as status "error" so a
;; headless check can't take the host down with it.
(define-rpc (check-updates : UpdateCheck)
  (with-handlers
      ([exn:fail?
        (lambda (e)
          (UpdateCheck "error" (void) app-version
                       (void) (void) (void) (void) (void)))])
    (update-check->record (perform-check!))))

;; Runs on a backend worker thread; the host follows progress via
;; update-state. Never raises: failures surface through the state's phase.
(define-rpc (start-download : Void)
  (with-handlers
      ([exn:fail? (lambda (e) (set-update-error! (exn-message e)))])
    (start-download! (update-data-dir)))
  (void))

(define-rpc (update-state : UpdateState)
  (define s (update-state-snapshot))
  (UpdateState
   (hash-ref s 'phase "idle")
   (hash-ref s 'percent 0)
   (opt-value (hash-ref s 'message #f))
   (opt-value (hash-ref s 'downloadedPath #f))
   (opt-value (hash-ref s 'availableVersion #f))))

;; Minimal validated key-value channel for host-side updater bookkeeping —
;; the BrainFuel equivalent of taskly's get_setting/set_setting pair plus
;; its config.ini write whitelist: exactly the updater-state keys are
;; readable/writable, values are integers-as-strings. Unset keys read as
;; the empty string so hosts parse a single shape ("value or default").
(define-rpc (get-setting [key : String] : String)
  (updater-setting (string-downcase (string-trim key))))

(define-rpc (set-setting [key : String] [value : String] : Void)
  (set-updater-setting! (string-downcase (string-trim key))
                        (string-trim value))
  (void))

;; ---- entry point ---------------------------------------------------------------

;; Native embedded hosts pass anonymous pipe file descriptors here.
(define (start in-fd out-fd)
  (serve-fds in-fd out-fd))
