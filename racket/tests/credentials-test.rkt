#lang racket/base

;; Ports BrainFuel.Tests/AccountCredentialTests.cs (fake store instead of the
;; OS keyring) plus the CLI-credential file readers.

(require racket/file
         racket/string
         rackunit
         "../brainfuel/credentials.rkt"
         "../brainfuel/providers.rkt"
         "../brainfuel/settings.rkt")

;; ---- TryWriteAll_TryReadAll_RoundTripsDict (memory store) ----------------------

(define store (make-memory-store))
(check-true ((credential-store-set! store)
             (string->bytes/utf-8 "{\"a1\":\"key-one\",\"a2\":\"key-two\"}")))
(define blob ((credential-store-ref store)))
(check-true (bytes? blob))
(define keys (parse-credential-blob blob))
(check-equal? (hash-count keys) 2)
(check-equal? (hash-ref keys "a1") "key-one")
(check-equal? (hash-ref keys "a2") "key-two")

;; ---- LegacyBareKeyBlob_ReadsAsDefaultAccount -----------------------------------

(define store2 (make-memory-store))
(check-true ((credential-store-set! store2) (string->bytes/utf-8 "legacy-plain-key")))
(define keys2 (parse-credential-blob ((credential-store-ref store2))))
(check-equal? (hash-count keys2) 1)
(check-equal? (hash-ref keys2 legacy-account-id) "legacy-plain-key")

;; ---- EmptyDictWrite_DeletesBlob --------------------------------------------------

(check-true ((credential-store-set! store) (string->bytes/utf-8 "{\"a1\":\"k\"}")))
(check-true ((credential-store-remove! store)))
(check-false ((credential-store-ref store)))

;; ---- Unavailable store never pretends to work --------------------------------------

(define unavailable (make-unavailable-store))
(check-false ((credential-store-ref unavailable)))
(check-false ((credential-store-set! unavailable) (string->bytes/utf-8 "{}")))
(check-false ((credential-store-remove! unavailable)))

;; The memory store can simulate the keyring going away (ProtectedUnavailable).
(define flaky (make-memory-store))
((credential-store-set! flaky) (string->bytes/utf-8 "{\"a\":\"k\"}"))
(check-true (bytes? ((credential-store-ref flaky))))
(set-box! (memory-store-available flaky) #f)
(check-false ((credential-store-ref flaky)))
(check-false ((credential-store-set! flaky) (string->bytes/utf-8 "{}")))
(set-box! (memory-store-available flaky) #t)
(check-true (bytes? ((credential-store-ref flaky))))

;; Corrupt JSON blob reads as empty, never throws.
(check-equal? (hash-count (parse-credential-blob (string->bytes/utf-8 "{ broken"))) 0)
(check-equal? (hash-count (parse-credential-blob (string->bytes/utf-8 ""))) 0)
(check-equal? (hash-count (parse-credential-blob #f)) 0)

;; ---- AccountDisplay_StringFallsBackToPlatform --------------------------------------

(check-equal? (account-display-label "工作号" "https://open.bigmodel.cn") "工作号")
(check-equal? (account-display-label "" "https://api.z.ai") "Z.ai")
(check-equal? (account-display-label "" "https://open.bigmodel.cn") "bigmodel.cn")

;; ---- new account ids: 12 hex chars (guid "N" format) --------------------------------

(check-equal? (string-length (new-account-id)) 12)
(check-true (regexp-match? #px"^[0-9a-f]{12}$" (new-account-id)))
(check-not-equal? (new-account-id) (new-account-id))

;; ---- Local CLI credentials -----------------------------------------------------------

(define dir (make-temporary-file "bf-cred-cli-~a" 'directory))
(define codex-path (build-path dir "auth.json"))
(define claude-path (build-path dir ".credentials.json"))

(parameterize ([current-codex-auth-path codex-path]
               [current-claude-credentials-path claude-path])
  (check-false (cli-credentials-exist? 'codex))
  (check-false (cli-credentials-exist? 'claude))
  (check-true (cli-credentials-exist? 'glm)) ; key-based providers always "exist"

  (with-output-to-file codex-path
    (lambda ()
      (display "{\"tokens\":{\"access_token\":\"codex-tok\",\"account_id\":\"acct-9\"}}")))
  (with-output-to-file claude-path
    (lambda ()
      (display "{\"claudeAiOauth\":{\"accessToken\":\"claude-tok\"}}")))

  (check-true (cli-credentials-exist? 'codex))
  (check-true (cli-credentials-exist? 'claude))
  (check-equal? (read-cli-token 'codex) "codex-tok")
  (check-equal? (read-cli-token 'claude) "claude-tok")
  (check-equal? (read-codex-account-id) "acct-9")
  (check-false (read-cli-token 'glm))

  ;; Corrupt files read as absent, never throw.
  (with-output-to-file codex-path #:exists 'replace
    (lambda () (display "{ broken")))
  (check-false (read-cli-token 'codex))
  (check-false (read-codex-account-id)))
