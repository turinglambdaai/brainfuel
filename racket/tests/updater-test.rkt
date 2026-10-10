#lang racket/base

;; Updater tests (ported from taskly racket/tests/updater-test.rkt):
;; platform mapping, manifest URL construction, download progress
;; accounting, the sticky rollout bucket and the two-key updater-state
;; whitelist, and the offline trust chain — craft a manifest, sign it with
;; a throwaway Ed25519 key, verify it, and select updates the same way the
;; live checker does, then download the artifact from a local TCP server
;; through the real download-with-progress! path.
;; (fetch-update-manifest itself only accepts HTTPS, so the network half is
;; exercised in production; everything it does with the bytes is tested
;; here, plus the error path against a closed local port.)

(require crypto
         crypto/all
         json
         net/base64
         rackunit
         rivet/distribution
         racket/file
         racket/path
         racket/tcp
         "../brainfuel/settings.rkt"
         "../brainfuel/updater.rkt")

(use-all-factories!)

;; Every state-file touch lands in a throwaway data dir, never the user's
;; (the box override crosses into serve threads too — see updater.rkt).
(set-update-data-dir-override!
 (make-temporary-file "brainfuel-updater-home-~a" 'directory))

(define download-limit (* 800 1024 1024))

(test-case "platform symbols match rivet release manifests"
  (case (system-type 'os)
    [(macosx) (check-equal? (platform-symbol) 'macos)]
    [(windows) (check-equal? (platform-symbol) 'windows)]
    [else (check-equal? (platform-symbol) 'linux)])
  (check-not-false (memq (architecture-symbol) '(arm64 x64))))

(test-case "installer extension follows platform"
  (check-not-false
   (member (installer-extension) '(".zip" ".tar.gz"))
   "known installer extension"))

(test-case "manifest url joins base and file name"
  (check-equal? (manifest-url)
                (string-append default-update-base-url "/update-manifest.json"))
  (check-equal?
   (parameterize ([current-update-base-url "https://dl.example/brainfuel/"])
     (manifest-url))
   "https://dl.example/brainfuel/update-manifest.json")
  (check-equal?
   (parameterize ([current-update-base-url "https://dl.example/brainfuel"])
     (manifest-url))
   "https://dl.example/brainfuel/update-manifest.json")
  ;; the box override wins over the parameter and crosses threads (the
  ;; RPC-handler test seam)
  (set-update-base-url-override! "https://feed.test/brainfuel")
  (check-equal? (manifest-url) "https://feed.test/brainfuel/update-manifest.json")
  (set-update-base-url-override! #f))

(test-case "download progress copies bytes and reports percent"
  (reset-update-state!)
  (define payload (make-bytes 250000 7))
  (define out (open-output-bytes))
  (copy-with-progress! (open-input-bytes payload) out 250000)
  (check-equal? (bytes-length (get-output-bytes out)) 250000)
  (check-equal? (hash-ref (update-state-snapshot) 'percent) 100)
  ;; percent tracks the declared total, not the end of input
  (reset-update-state!)
  (define short-out (open-output-bytes))
  (copy-with-progress! (open-input-bytes payload) short-out 1000000)
  (check-equal? (hash-ref (update-state-snapshot) 'percent) 25)
  (reset-update-state!))

(test-case "rollout bucket is sticky and persists to updater-state.json"
  (check-true (exact-integer? (rollout-bucket)))
  (define bucket0 (rollout-bucket))
  (check-true (<= 0 bucket0 99))
  ;; a second read returns the persisted bucket, not a fresh draw
  (check-equal? (rollout-bucket) bucket0)
  (define doc
    (with-input-from-file (updater-state-path) read-json))
  (check-equal? (hash-ref doc 'rollout-bucket) bucket0))

(test-case "updater-state whitelist: two integer keys, nothing else"
  (check-equal? updater-state-keys '("rollout-bucket" "last-update-check"))
  ;; unset keys read as the empty string (the host "value or default" shape)
  (check-equal? (updater-setting "last-update-check") "")
  (set-updater-setting! "last-update-check" "1760000000")
  (check-equal? (updater-setting "last-update-check") "1760000000")
  (check-exn exn:fail? (lambda () (set-updater-setting! "theme" "dark")))
  (check-exn exn:fail? (lambda () (set-updater-setting! "rollout-bucket" "abc")))
  ;; unknown keys dropped on write: the store stays two-key
  (write-updater-state! (hasheq 'rollout-bucket 7 'evil "x" 'last-update-check 5))
  (define doc (with-input-from-file (updater-state-path) read-json))
  (check-equal? (hash-count doc) 2)
  (check-equal? (hash-ref doc 'rollout-bucket) 7)
  (check-false (hash-has-key? doc 'evil)))

(test-case "check against an unreachable feed reports error state"
  (parameterize ([current-update-base-url "https://127.0.0.1:9/brainfuel"])
    (reset-update-state!)
    (define result (perform-check!))
    (check-equal? (hash-ref result 'status) "error")
    (check-equal? (hash-ref (update-state-snapshot) 'phase) "error")
    (check-true (string? (hash-ref (update-state-snapshot) 'message)))))

(test-case "signed manifest verifies and selects updates"
  ;; throwaway keypair: same DER formats the release pipeline uses
  (define priv (generate-private-key 'eddsa '((curve ed25519))))
  (define priv-der (pk-key->datum priv 'OneAsymmetricKey))
  (define priv-path (make-temporary-file "brainfuel-test-key-~a.der"))
  (with-output-to-file priv-path
    #:exists 'truncate
    (lambda () (write-bytes priv-der)))
  (define pub (datum->pk-key (pk-key->datum priv 'rkt-public) 'rkt-public))
  (define artifact-file (make-temporary-file "brainfuel-artifact-~a.zip"))
  (call-with-output-file artifact-file
    #:exists 'truncate
    (lambda (out) (write-bytes (make-bytes 128 3) out)))
  (define manifest
    (update-manifest
     app-identifier "9.9.9" 7 'stable
     "2026-10-10T00:00:00Z" "0.0.0" #f #t 100
     (list (update-artifact (platform-symbol) (architecture-symbol)
                            "https://dl.example/brainfuel/brainfuel-9.9.9.zip"
                            (sha256-file/hex artifact-file)
                            (file-size artifact-file)
                            'zip '()))))
  ;; write-signed-manifest refuses malformed manifests before signing
  (define wrapped (open-output-bytes))
  (write-signed-manifest manifest priv "brainfuel-test" wrapped)
  (define verified
    (verify-signed-manifest (open-input-bytes (get-output-bytes wrapped))
                            pub
                            #:key-id "brainfuel-test"))
  (check-equal? (update-manifest-version verified) "9.9.9")
  ;; a different key-id fails closed
  (check-exn exn:fail?
             (lambda ()
               (verify-signed-manifest
                (open-input-bytes (get-output-bytes wrapped))
                pub
                #:key-id "some-other-key")))

  ;; select-update picks the artifact for this platform/architecture the
  ;; same way the live checker does
  (define config
    (updater-config app-identifier "0.0.1" app-channel
                    (platform-symbol) (architecture-symbol)
                    pub "brainfuel-test" 42 download-limit))
  (define candidate (select-update config verified))
  (check-true (update-candidate? candidate))
  (check-equal? (update-artifact-size (update-candidate-artifact candidate))
                (file-size artifact-file))
  ;; the destination layout is <data-dir>/updates/BrainFuel-<version><ext>
  (check-equal?
   (destination-path (update-data-dir) candidate)
   (build-path (update-data-dir) "updates"
               (string-append "BrainFuel-9.9.9" (installer-extension))))
  (delete-file priv-path)
  (delete-file artifact-file))

;; A minimal one-shot HTTP artifact server: get-pure-port insists on a
;; well-formed response head, and the tampered-artifact case downloads
;; twice, so every connection gets the same bytes.
(define (serve-artifact-payload! payload)
  (define listener (tcp-listen 0 4 #f "127.0.0.1"))
  (define-values (_bind-host port-number _peer-host _peer-port)
    (tcp-addresses listener #t))
  (define server
    (thread
     (lambda ()
       (let accept-loop ()
         (define-values (in out) (tcp-accept listener))
         (thread
          (lambda ()
            (let drain ()
              (define line (read-line in 'any))
              (unless (or (eof-object? line) (equal? line "") (equal? line "\r"))
                (drain)))
            (write-string
             (format "HTTP/1.0 200 OK\r\nContent-Length: ~a\r\n"
                     (bytes-length payload))
             out)
            (write-string "Content-Type: application/zip\r\n\r\n" out)
            (write-bytes payload out)
            (close-output-port out)
            (close-input-port in)))
         (accept-loop)))))
  (values port-number
          (lambda ()
            (kill-thread server)
            (tcp-close listener))))

(test-case "download-with-progress! fetches, verifies and stages locally"
  (define payload (make-bytes 300000 42))
  ;; the digest rides through the same sha256-file/hex the pipeline uses
  (define payload-file (make-temporary-file "brainfuel-payload-~a.zip"))
  (with-output-to-file payload-file
    #:exists 'truncate
    (lambda () (write-bytes payload)))
  (define sha256 (sha256-file/hex payload-file))
  (define-values (port-number stop-server!) (serve-artifact-payload! payload))

  (define pub
    (datum->pk-key (base64-string->bytes update-public-key-b64)
                   'SubjectPublicKeyInfo))
  (define manifest
    (update-manifest
     app-identifier "9.9.9" 7 'stable
     "2026-10-10T00:00:00Z" "0.0.0" #f #t 100
     (list (update-artifact
            (platform-symbol) (architecture-symbol)
            (format "http://127.0.0.1:~a/brainfuel.zip" port-number)
            sha256 (bytes-length payload) 'zip '()))))
  (define config
    (updater-config app-identifier app-version app-channel
                    (platform-symbol) (architecture-symbol)
                    pub update-key-id 0 download-limit))
  (define candidate (select-update config manifest))
  (check-true (update-candidate? candidate))
  (reset-update-state!)
  (define destination
    (destination-path (update-data-dir) candidate))
  (check-equal? (download-with-progress! config candidate destination)
                destination)
  (check-true (file-exists? destination))
  (check-equal? (file-size destination) (bytes-length payload))
  (check-equal? (hash-ref (update-state-snapshot) 'percent) 100)
  (delete-file destination)

  ;; a tampered artifact fails the sha256 gate before landing
  (define bad-manifest
    (struct-copy update-manifest manifest
                 [artifacts
                  (list (struct-copy update-artifact
                                     (car (update-manifest-artifacts manifest))
                                     [sha256 (make-string 64 #\0)]))]))
  (define bad-candidate (select-update config bad-manifest))
  (check-exn exn:fail?
             (lambda ()
               (download-with-progress!
                config bad-candidate
                (build-path (update-data-dir) "updates" "bad.zip"))))

  (stop-server!)
  (delete-file payload-file))

(test-case "embedded release identity is coherent"
  (check-equal? app-identifier "site.jrtx.brainfuel")
  (check-equal? app-channel 'stable)
  (check-equal? update-key-id "brainfuel-2026-10")
  (check-true (string? app-version))
  (check-equal? app-build 4))

(set-update-data-dir-override! #f)
