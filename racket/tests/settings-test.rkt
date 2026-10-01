#lang racket/base

;; Ports BrainFuel.Tests/SettingsJsonTests.cs plus the load migrations from
;; SettingsService (legacy single-account, AlwaysOnTop, plaintext keys) and
;; atomic-save behavior.

(require json
         racket/file
         racket/string
         rackunit
         "../brainfuel/credentials.rkt"
         "../brainfuel/settings.rkt")

;; ---- StringEnumValues_AreAccepted ---------------------------------------------

(define s1 (settings-from-doc
            (string->jsexpr "{\"ThemeMode\": \"Dark\", \"Language\": \"En\", \"SizeMode\": \"Compact\"}")))
(check-equal? (app-settings-theme-mode s1) 'dark)
(check-equal? (app-settings-language s1) 'en)
(check-equal? (app-settings-size-mode s1) 'compact)

;; ---- LegacyNumericEnumValues_AreAccepted ----------------------------------------

(define s2 (settings-from-doc
            (string->jsexpr "{\"ThemeMode\": 1, \"Language\": 0, \"SizeMode\": 1}")))
(check-equal? (app-settings-theme-mode s2) 'light)
(check-equal? (app-settings-language s2) 'zh)
(check-equal? (app-settings-size-mode s2) 'compact)

;; ---- UnknownEnumString_ThrowsAndIsCaughtByLoadFallback ---------------------------

(check-exn exn:fail?
           (lambda ()
             (settings-from-doc (string->jsexpr "{\"ThemeMode\": \"Midnight\"}"))))

(define dir (make-temporary-file "bf-settings-tests-~a" 'directory))
(define path (settings-path-for dir))
(with-output-to-file path
  (lambda () (display "{\"ThemeMode\": \"Midnight\"}")))
(define loaded (load-settings path))
(check-equal? (app-settings-theme-mode (loaded-settings-settings loaded)) 'dark)
(check-equal? (app-settings-always-on-top (loaded-settings-settings loaded))
              #t ; existing install without the field keeps the old behavior
              )

;; ---- RoundTrip_PreservesEnumValues -------------------------------------------------

(define original (default-settings))
(set-app-settings-theme-mode! original 'light)
(set-app-settings-size-mode! original 'compact)
(set-app-settings-language! original 'en)
(define json-out (jsexpr->string (settings->json original #f)))
(check-true (string-contains? json-out "\"Compact\""))
(check-true (string-contains? json-out "\"Light\""))
(check-true (string-contains? json-out "\"En\""))
(define back (settings-from-doc (string->jsexpr json-out)))
(check-equal? (app-settings-theme-mode back) 'light)
(check-equal? (app-settings-size-mode back) 'compact)
(check-equal? (app-settings-language back) 'en)

;; Legacy ApiKey fields are never written.
(check-false (hash-has-key? (settings->json original #f) 'ApiKey))
(check-false (hash-has-key? (settings->json original #f) 'ApiKeyConfigured))

;; ---- v0.5.x single-account migration -------------------------------------------------

(define dir2 (make-temporary-file "bf-settings-tests-~a" 'directory))
(define path2 (settings-path-for dir2))
(with-output-to-file path2
  (lambda ()
    (display "{\"ApiKey\": \"sk-legacy\", \"ApiKeyConfigured\": true, \"BaseDomain\": \"https://api.z.ai\"}")))
(define loaded2 (load-settings path2))
(define settings2 (loaded-settings-settings loaded2))
(check-equal? (length (app-settings-accounts settings2)) 1)
(define legacy (car (app-settings-accounts settings2)))
(check-equal? (account-config-id legacy) "default")
(check-equal? (account-config-base-domain legacy) "https://api.z.ai")
(check-equal? (account-config-configured legacy) #t)

;; Saving strips the legacy fields and mirrors the active domain.
(save-settings-json path2 settings2 #f)
(define doc2 (with-input-from-file path2 read-json))
(check-false (hash-has-key? doc2 'ApiKey))
(check-false (hash-has-key? doc2 'ApiKeyConfigured))
(check-equal? (hash-ref doc2 'BaseDomain) "https://api.z.ai")

;; A legacy file WITHOUT a credential reads as unconfigured.
(define dir3 (make-temporary-file "bf-settings-tests-~a" 'directory))
(with-output-to-file (settings-path-for dir3)
  (lambda ()
    (display "{\"ApiKeyConfigured\": false}")))
(check-equal?
 (account-config-configured
  (car (app-settings-accounts (loaded-settings-settings (load-settings (settings-path-for dir3))))))
 #f)

;; ---- plaintext AccountKeys round-trip -------------------------------------------------

(define plaintext (hash "acc1" "key-one"))
(define doc-with-keys (settings->json (default-settings) plaintext))
(check-equal? (hash-ref (hash-ref doc-with-keys 'AccountKeys) 'acc1) "key-one")
(define doc-no-keys (settings->json (default-settings) #f))
(check-false (hash-has-key? doc-no-keys 'AccountKeys))

;; Through a real file + reload:
(save-settings-json path2 (default-settings) plaintext)
(define reloaded (load-settings path2))
(check-equal? (plaintext-keys-from-doc (loaded-settings-raw-doc reloaded))
              (hash "acc1" "key-one"))

;; ---- atomic write + 0600 permissions ----------------------------------------------

(save-settings-json path2 (default-settings) #f)
(check-false (file-exists? (string->path (string-append (path->string path2) ".tmp"))))
;; 0600 = owner read (256) + write (128) bits only.
(check-equal? (file-or-directory-permissions path2 'bits) 384)

;; ---- default data dir / paths ----------------------------------------------------

(check-true (path? (default-data-dir)))
(check-equal? (path->string (settings-path-for (default-data-dir)))
              (path->string (build-path (default-data-dir) "settings.json")))
(check-true (path-string? (path->string (brainfuel-data-dir))))

;; Corrupt JSON in general (not just enums) falls back to defaults.
(define dir4 (make-temporary-file "bf-settings-tests-~a" 'directory))
(with-output-to-file (settings-path-for dir4)
  (lambda () (display "{ nope")))
(check-equal? (app-settings-refresh-interval-minutes
               (loaded-settings-settings (load-settings (settings-path-for dir4))))
              5)
