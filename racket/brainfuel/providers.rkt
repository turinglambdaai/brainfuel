#lang racket/base

;; Provider registry (Services/QuotaProviders.cs). Providers are symbols:
;; 'glm (pasted key), 'codex / 'claude (local CLI logins).

(require racket/format
         racket/random
         racket/string)

(provide known-providers
         default-provider
         default-base-domain
         normalize-provider
         cli-login-provider?
         new-account-id
         account-display-label)

(define known-providers '(glm codex claude))
(define default-provider 'glm)
(define default-base-domain "https://open.bigmodel.cn")

;; Unknown provider ids fall back to GLM, like QuotaProviders.Create.
(define (normalize-provider provider)
  (if (memq provider known-providers) provider default-provider))

;; Codex/Claude hold no stored key by design: they read the local CLI login.
(define (cli-login-provider? provider) (memq provider '(codex claude)))

;; 12-char guid "N" format, like the old AccountConfig ids.
(define (new-account-id)
  (apply string-append
         (for/list ([b (in-bytes (crypto-random-bytes 6))])
           (~r b #:base 16 #:min-width 2 #:pad-string "0"))))

;; ComboBox display text: name, else the platform (AccountConfig.ToString).
(define (account-display-label name base-domain)
  (if (and (string? name) (positive? (string-length name)))
      name
      (if (and (string? base-domain)
               (string-contains? (string-downcase base-domain) "z.ai"))
          "Z.ai"
          "bigmodel.cn")))
