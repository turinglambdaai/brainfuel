#lang racket/base

;; Credential storage, ported from Services/CredentialStore.cs and
;; LocalCliCredentials.cs.
;;
;; A `credential-store` is a pluggable backend holding ONE blob for all
;; accounts: a JSON object {"accountId": key}. Blobs written by v0.5.x (a bare
;; key string) read back as {"default": key} so migration is lossless. The
;; production backend is wired in app/backend.rkt from rivet/system's
;; secure-store; tests use make-memory-store.
;;
;; Local CLI logins (Codex/Claude) are never stored here: tokens are read
;; fresh from the CLI files on every refresh.

(require racket/file
         json
         racket/string
         "failure.rkt"
         "usage-models.rkt")

(provide legacy-account-id
         (struct-out credential-store)
         make-unavailable-store
         (struct-out memory-store)
         make-memory-store
         parse-credential-blob
         credential-blob->bytes
         plaintext-keys-from-doc
         current-codex-auth-path
         current-claude-credentials-path
         cli-credentials-exist?
         read-cli-token
         read-codex-account-id)

(define legacy-account-id "default")

;; ref: (-> bytes-or-#f)   set!: (bytes -> boolean)   remove!: (-> boolean)
(struct credential-store (ref set! remove! display-name) #:transparent)

;; Simulates "no system secret store": every operation fails, and the caller
;; must keep state protected-unavailable instead of downgrading.
(define (make-unavailable-store)
  (credential-store (lambda () #f)
                    (lambda (blob) #f)
                    (lambda () #f)
                    "none"))

(struct memory-store credential-store (data available) #:transparent)
;; data: mutable hash id -> key; available: box of boolean (test hook).

(define (make-memory-store [available? #t])
  (define data (make-hash))
  (define available (box available?))
  (memory-store
   (lambda ()
     (and (unbox available)
          (credential-blob->bytes data)))
   (lambda (blob)
     (and (unbox available)
          (let ([parsed (parse-credential-blob blob)])
            (hash-clear! data)
            (for ([(k v) (in-hash parsed)])
              (hash-set! data k v))
            #t)))
   (lambda ()
     (and (unbox available)
          (begin (hash-clear! data) #t)))
   "memory"
   data
   available))

;; blob bytes -> hash of accountId -> key (string-keyed). Invalid/empty input
;; reads as empty (never throws); a legacy bare key maps to the "default"
;; account.
(define (parse-credential-blob blob)
  (cond
    [(not blob) (hash)]
    [else
     (define raw (string-trim (bytes->string/utf-8 blob #\uFFFD)))
     (cond
       [(equal? raw "") (hash)]
       [(string-prefix? raw "{")
        (with-handlers ([exn:fail? (lambda (_) (hash))])
          (define doc (string->jsexpr raw))
          (if (hash? doc)
              (for/hash ([(k v) (in-hash doc)]
                         #:when (and (symbol? k)
                                     (string? v)
                                     (not (equal? "" (string-trim v)))))
                (values (symbol->string k) v))
              (hash)))]
       [else (hash legacy-account-id raw)])]))

;; hash -> bytes, or #f when empty (meaning "no blob exists"). The blob JSON
;; is symbol-keyed (jsexpr shape); the runtime keyring is string-keyed.
(define (credential-blob->bytes keys)
  (if (= (hash-count keys) 0)
      #f
      (string->bytes/utf-8
       (jsexpr->string
        (for/hash ([(k v) (in-hash keys)])
          (values (string->symbol k) v))))))

;; "AccountKeys" node from a raw settings.json document (plaintext fallback
;; from a previous run without a system secret store). String-keyed result.
(define (plaintext-keys-from-doc doc)
  (define node (jref doc "AccountKeys"))
  (if (hash? node)
      (for/hash ([(k v) (in-hash node)]
                 #:when (and (symbol? k)
                             (string? v)
                             (not (equal? "" (string-trim v)))))
        (values (symbol->string k) v))
      (hash)))

;; ---- Local CLI credentials -------------------------------------------------

(define current-codex-auth-path
  (make-parameter (build-path (find-system-path 'home-dir) ".codex" "auth.json")))

(define current-claude-credentials-path
  (make-parameter
   (build-path (find-system-path 'home-dir) ".claude" ".credentials.json")))

(define (file->jsexpr-safe path)
  (with-handlers ([exn:fail? (lambda (e)
                               (log-error (format "credentials unreadable: ~a"
                                                  (exn-message e)))
                               #f)])
    (and (file-exists? path)
         (with-input-from-file path read-json))))

(define (cli-credentials-exist? provider)
  (case provider
    [(codex) (file-exists? (current-codex-auth-path))]
    [(claude) (file-exists? (current-claude-credentials-path))]
    ;; glm-style providers rely on pasted keys, not a CLI login.
    [else #t]))

(define (read-cli-token provider)
  (case provider
    [(codex)
     (define doc (file->jsexpr-safe (current-codex-auth-path)))
     (and doc (jstring (jref doc "tokens") "access_token"))]
    [(claude)
     (define doc (file->jsexpr-safe (current-claude-credentials-path)))
     (and doc (jstring (jref doc "claudeAiOauth") "accessToken"))]
    [else #f]))

;; ChatGPT account id the Codex CLI sends as chatgpt-account-id.
(define (read-codex-account-id)
  (define doc (file->jsexpr-safe (current-codex-auth-path)))
  (and doc (jstring (jref doc "tokens") "account_id")))
