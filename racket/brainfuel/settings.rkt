#lang racket/base

;; settings.json file format, ported from Services/SettingsService.cs.
;; Drop-in compatible with the old app: PascalCase keys, enums written as
;; strings ("Dark", "Compact") and read back from BOTH strings and legacy
;; numeric values, v0.5.x single-account fields migrated to the "default"
;; account, legacy ApiKey never written, atomic .tmp -> move writes with
;; 0600 permissions on unix.

(require json
         racket/file
         racket/path
         racket/string
         "failure.rkt"
         "providers.rkt"
         "usage-models.rkt")

(provide (struct-out account-config)
         (struct-out app-settings)
         (struct-out loaded-settings)
         blank-string?
         default-data-dir
         data-dir-override
         brainfuel-data-dir
         settings-path-for
         default-settings
         default-account
         normalize-accounts!
         active-account
         load-settings
         settings-from-doc
         settings->json
         save-settings-json)

(struct account-config (id name provider base-domain configured)
  #:transparent #:mutable)

(struct app-settings
  (accounts                    ; list of account-config (replaced wholesale)
   active-account-id
   refresh-interval-minutes
   window-x window-y
   window-screen-name
   window-screen-x window-screen-y window-screen-width window-screen-height
   window-relative-x window-relative-y
   always-on-top
   weekly-display-style        ; 'used | 'remaining
   hourly-display-style
   autostart
   notify-enabled notify-threshold
   theme-mode                  ; 'system | 'light | 'dark
   card-opacity                ; real 0..1
   language                    ; 'zh | 'en
   size-mode                   ; 'standard | 'compact
   hotkey-enabled hotkey-combo
   ring-palette)
  #:transparent #:mutable)

(struct loaded-settings (settings raw-doc existing-install?) #:transparent)

(define legacy-account-id "default")

;; ---- enums: symbol <-> string (written) / legacy number (read) -------------

(define display-style-names '([used . "Used"] [remaining . "Remaining"]))
(define display-style-legacy '([0 . used] [1 . remaining]))
(define theme-names '([system . "System"] [light . "Light"] [dark . "Dark"]))
(define theme-legacy '([0 . system] [1 . light] [2 . dark]))
(define language-names '([zh . "Zh"] [en . "En"]))
(define language-legacy '([0 . zh] [1 . en]))
(define size-mode-names '([standard . "Standard"] [compact . "Compact"]))
(define size-mode-legacy '([0 . standard] [1 . compact]))

(define ((read-enum names legacy) doc key default)
  (define v (jref doc key))
  (cond
    [(not v) default]
    [(string? v)
     (or (for/or ([entry (in-list names)]
                  #:when (string-ci=? (cdr entry) v))
           (car entry))
         (raise-usage 'unknown (format "unknown enum value for ~a: ~a" key v)))]
    [(exact-integer? v)
     (or (let loop ([entries legacy])
           (cond [(null? entries) #f]
                 [(= v (car (car entries))) (cdr (car entries))]
                 [else (loop (cdr entries))]))
         (raise-usage 'unknown (format "unknown enum value for ~a: ~a" key v)))]
    [else (raise-usage 'unknown (format "unknown enum value for ~a: ~a" key v))]))

(define read-display-style (read-enum display-style-names display-style-legacy))
(define read-theme (read-enum theme-names theme-legacy))
(define read-language (read-enum language-names language-legacy))
(define read-size-mode (read-enum size-mode-names size-mode-legacy))

(define (enum->name names symbol)
  (cdr (assq symbol names)))

;; ---- paths ------------------------------------------------------------------

(define (default-data-dir)
  (case (system-type)
    [(macosx)
     (build-path (find-system-path 'home-dir)
                 "Library" "Application Support" "BrainFuel")]
    [(windows)
     (build-path (or (getenv "APPDATA") (find-system-path 'home-dir)) "BrainFuel")]
    [else
     (build-path
      (or (getenv "XDG_CONFIG_HOME")
          (build-path (find-system-path 'home-dir) ".config"))
      "BrainFuel")]))

;; Tests (and headless tools) redirect the data dir here instead of touching
;; the user's real settings.
(define data-dir-override (make-parameter #f))

(define (brainfuel-data-dir)
  (or (data-dir-override) (default-data-dir)))

(define (settings-path-for data-dir)
  (build-path data-dir "settings.json"))

;; ---- defaults / normalization ------------------------------------------------

(define (default-account)
  (account-config legacy-account-id "" default-provider default-base-domain #f))

(define (default-settings)
  (app-settings
   (list (default-account))
   legacy-account-id
   5                       ; RefreshIntervalMinutes
   #f #f                   ; WindowX WindowY
   #f #f #f #f #f          ; WindowScreenName/X/Y/Width/Height
   #f #f                   ; WindowRelativeX/Y
   #f                      ; AlwaysOnTop (set true during migration below)
   'remaining 'used        ; Weekly=Used, Hourly=Remaining (C# defaults)
   #f                      ; AutoStart
   #t 80                   ; NotifyEnabled NotifyThreshold
   'dark 1.0 'zh 'standard
   #f "Ctrl+Alt+B" "classic"))

(define (active-account settings)
  (or (findf (lambda (a)
               (equal? (account-config-id a)
                       (app-settings-active-account-id settings)))
             (app-settings-accounts settings))
      (and (pair? (app-settings-accounts settings))
           (car (app-settings-accounts settings)))))

;; Mirrors SettingsService.NormalizeAccounts: fresh installs get one unnamed
;; "default" account; empty ids are never persisted; the active id must point
;; at a real account.
(define (normalize-accounts! settings)
  (when (null? (app-settings-accounts settings))
    (set-app-settings-accounts! settings (list (default-account))))
  (for ([a (in-list (app-settings-accounts settings))]
        #:when (blank-string? (account-config-id a)))
    (set-account-config-id! a legacy-account-id))
  (unless (findf (lambda (a)
                   (equal? (account-config-id a)
                           (app-settings-active-account-id settings)))
                 (app-settings-accounts settings))
    (set-app-settings-active-account-id!
     settings (account-config-id (car (app-settings-accounts settings))))))

;; ---- load ---------------------------------------------------------------------

(define (blank-string? v)
  (or (not v) (not (string? v)) (equal? "" (string-trim v))))

(define (read-int doc key default)
  (or (jint doc key) default))

(define (read-real doc key default)
  (define v (jreal doc key))
  (if v v default))

;; #f is a boolean in Racket, so a missing key must be distinguished from an
;; explicit false before falling back to the default.
(define (read-bool doc key default)
  (if (and (hash? doc) (hash-has-key? doc (string->symbol key)))
      (let ([v (hash-ref doc (string->symbol key) #f)])
        (if (boolean? v) v default))
      default))

(define (read-string doc key default)
  (or (jstring doc key) default))

(define (settings-from-doc doc)
  (define accounts
    (for/list ([a (in-list (or (jref doc "Accounts") '()))]
               #:when (hash? a))
      (account-config
       (read-string a "Id" "")
       (read-string a "Name" "")
       (normalize-provider
        (let ([p (jstring a "Provider")])
          (if p (string->symbol (string-downcase p)) default-provider)))
       (read-string a "BaseDomain" default-base-domain)
       (read-bool a "Configured" #f))))
  (app-settings
   accounts
   (read-string doc "ActiveAccountId" "")
   (read-int doc "RefreshIntervalMinutes" 5)
   (jint doc "WindowX")
   (jint doc "WindowY")
   (jstring doc "WindowScreenName")
   (jint doc "WindowScreenX")
   (jint doc "WindowScreenY")
   (jint doc "WindowScreenWidth")
   (jint doc "WindowScreenHeight")
   (jreal doc "WindowRelativeX")
   (jreal doc "WindowRelativeY")
   (read-bool doc "AlwaysOnTop" #f)
   (read-display-style doc "WeeklyDisplayStyle" 'used)
   (read-display-style doc "HourlyDisplayStyle" 'remaining)
   (read-bool doc "AutoStart" #f)
   (read-bool doc "NotifyEnabled" #t)
   (read-int doc "NotifyThreshold" 80)
   (read-theme doc "ThemeMode" 'dark)
   (read-real doc "CardOpacity" 1.0)
   (read-language doc "Language" 'zh)
   (read-size-mode doc "SizeMode" 'standard)
   (read-bool doc "HotkeyEnabled" #f)
   (read-string doc "HotkeyCombo" "Ctrl+Alt+B")
   (read-string doc "RingPalette" "classic")))

;; v0.5.x files carry one implicit account in ApiKey/BaseDomain: any existing
;; file without accounts becomes the "default" account.
(define (migrate-legacy-account! settings doc existing?)
  (when (and existing? doc (null? (app-settings-accounts settings)))
    (define api-key (jstring doc "ApiKey"))
    (define configured-field (jref doc "ApiKeyConfigured"))
    (define configured-present (boolean? configured-field))
    (define had-credential
      (and (not (and configured-present (not configured-field)))
           (or (and api-key (not (blank-string? api-key)))
               (and configured-present configured-field))))
    (define legacy-domain (jstring doc "BaseDomain"))
    (set-app-settings-accounts!
     settings
     (list (account-config
            legacy-account-id
            ""
            default-provider
            (if (blank-string? legacy-domain) default-base-domain legacy-domain)
            had-credential)))))

;; Old builds were always-on-top without recording a setting; preserve that
;; behavior for existing installs whose file lacks the field.
(define (migrate-always-on-top! settings doc existing?)
  (when (and existing? (or (not doc) (not (hash? doc))
                           (not (hash-has-key? doc 'AlwaysOnTop))))
    (set-app-settings-always-on-top! settings #t)))

;; Returns loaded-settings; never throws. A corrupt or unmappable file falls
;; back to defaults (with a log line) instead of silently losing preferences.
(define (load-settings path)
  (define existing? (file-exists? path))
  (define-values (doc parse-ok?)
    (if (not existing?)
        (values #f #t)
        (with-handlers ([exn:fail? (lambda (_) (values #f #f))])
          (values (with-input-from-file path read-json) #t))))
  (define settings
    (cond
      [(not parse-ok?)
       (log-error "settings.json load failed, using defaults")
       (default-settings)]
      [(not doc) (default-settings)]
      [else
       (with-handlers
           ([exn:fail?
             (lambda (e)
               (log-error (format "settings.json load failed, using defaults: ~a"
                                  (exn-message e)))
               (default-settings))])
         (settings-from-doc doc))]))
  (migrate-always-on-top! settings doc existing?)
  (migrate-legacy-account! settings doc existing?)
  (normalize-accounts! settings)
  (loaded-settings settings doc existing?))

;; ---- save ----------------------------------------------------------------------

(define (optional-number v)
  (cond [(not v) 'null]
        [(real? v) v]
        [else 'null]))

(define (settings->json settings plaintext-keys)
  (define doc
    (hasheq
     'Accounts
     (for/list ([a (in-list (app-settings-accounts settings))])
       (hasheq 'Id (account-config-id a)
               'Name (account-config-name a)
               'BaseDomain (account-config-base-domain a)
               'Provider (symbol->string (account-config-provider a))
               'Configured (account-config-configured a)))
     'ActiveAccountId (app-settings-active-account-id settings)
     ;; Mirror the active account's domain into the legacy field so older
     ;; builds (or external readers) still see something sane.
     'BaseDomain
     (let ([active (active-account settings)])
       (if active
           (account-config-base-domain active)
           default-base-domain))
     'RefreshIntervalMinutes (app-settings-refresh-interval-minutes settings)
     'WindowX (optional-number (app-settings-window-x settings))
     'WindowY (optional-number (app-settings-window-y settings))
     'WindowScreenName
     (or (app-settings-window-screen-name settings) 'null)
     'WindowScreenX (optional-number (app-settings-window-screen-x settings))
     'WindowScreenY (optional-number (app-settings-window-screen-y settings))
     'WindowScreenWidth (optional-number (app-settings-window-screen-width settings))
     'WindowScreenHeight (optional-number (app-settings-window-screen-height settings))
     'WindowRelativeX (optional-number (app-settings-window-relative-x settings))
     'WindowRelativeY (optional-number (app-settings-window-relative-y settings))
     'AlwaysOnTop (app-settings-always-on-top settings)
     'WeeklyDisplayStyle
     (enum->name display-style-names (app-settings-weekly-display-style settings))
     'HourlyDisplayStyle
     (enum->name display-style-names (app-settings-hourly-display-style settings))
     'AutoStart (app-settings-autostart settings)
     'NotifyEnabled (app-settings-notify-enabled settings)
     'NotifyThreshold (app-settings-notify-threshold settings)
     'ThemeMode (enum->name theme-names (app-settings-theme-mode settings))
     'CardOpacity (app-settings-card-opacity settings)
     'Language (enum->name language-names (app-settings-language settings))
     'SizeMode (enum->name size-mode-names (app-settings-size-mode settings))
     'HotkeyEnabled (app-settings-hotkey-enabled settings)
     'HotkeyCombo (app-settings-hotkey-combo settings)
     'RingPalette (app-settings-ring-palette settings)))
  ;; Keys ride in settings.json only in the explicit plaintext fallback;
  ;; legacy ApiKey/ApiKeyConfigured are never written.
  (if plaintext-keys
      (hash-set doc
                'AccountKeys
                (for/hash ([(k v) (in-hash plaintext-keys)])
                  (values (string->symbol k) v)))
      doc))

(define (harden-permissions! path)
  (with-handlers ([exn:fail? void])    ; some filesystems have no unix modes
    ;; Two-argument form sets the mode: 384 bits = 0600 (user read+write).
    (file-or-directory-permissions path 384)))

(define (save-settings-json path settings plaintext-keys)
  (define dir (path-only path))
  (when dir (make-directory* dir))
  (define tmp (string->path (string-append (path->string path) ".tmp")))
  (with-output-to-file tmp #:exists 'replace
    (lambda () (write-json (settings->json settings plaintext-keys))))
  (harden-permissions! tmp)
  (rename-file-or-directory tmp path #t)
  (harden-permissions! path)
  (void))
