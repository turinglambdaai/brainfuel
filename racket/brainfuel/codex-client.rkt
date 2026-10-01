#lang racket/base

;; Codex (ChatGPT subscription) usage client, ported from
;; Services/CodexQuotaClient.cs. The OAuth token is passed in fresh by the
;; scheduler on every refresh so CLI-side renewals are picked up.

(require racket/string
         "failure.rkt"
         "http-fetch.rkt"
         "usage-models.rkt")

(provide codex-endpoint
         codex-fetch-usage
         parse-codex-usage)

(define codex-endpoint "https://chatgpt.com/backend-api/wham/usage")

(define (clamp-pct v)
  (min 100.0 (max 0.0 (or v 0.0))))

;; rate_limit.primary_window (5 h) and secondary_window (week) with
;; used_percent and reset_at (unix seconds).
(define (parse-codex-usage body now-ms)
  (define doc (json-parse-safe body))
  (unless doc
    (raise-usage 'invalid-response "Codex usage response was not valid JSON"))
  (define rate (jref doc "rate_limit"))
  (define primary (and (hash? rate) (jref rate "primary_window")))
  (define secondary (and (hash? rate) (jref rate "secondary_window")))
  (unless (or (hash? primary) (hash? secondary))
    (raise-usage 'no-coding-plan
                 "Codex usage response contained no rate-limit windows"))

  (define (window h)
    (values (clamp-pct (jreal h "used_percent"))
            (let ([r (jreal h "reset_at")])
              (and r (> r 0) (inexact->exact (truncate (* r 1000)))))))

  (define-values (hourly-pct hourly-reset)
    (if (hash? primary) (window primary) (values #f #f)))
  (define-values (weekly-pct weekly-reset)
    (if (hash? secondary) (window secondary) (values #f #f)))

  (usage-snapshot hourly-pct weekly-pct hourly-reset weekly-reset
                  (jstring doc "plan_type") now-ms))

(define (codex-headers token account-id)
  (append
   (list "User-Agent: codex_cli_rs"
         "Accept: application/json"
         (string-append "Authorization: Bearer " token))
   (if (and (string? account-id) (non-empty-string? account-id))
       (list (string-append "chatgpt-account-id: " account-id))
       '())))

(define (codex-fetch-usage fetch token account-id now-ms)
  (unless (and (string? token) (non-empty-string? (string-trim token)))
    (raise-usage 'authentication
                 "Codex CLI login not found (~/.codex/auth.json)"))

  (define-values (status body)
    (with-handlers
        ([transport-error?
          (lambda (e)
            (if (eq? (transport-error-kind e) 'timeout)
                (raise-usage 'timeout "Codex usage request timed out")
                (raise-usage 'network
                             (format "Codex connection failure: ~a"
                                     (transport-error-message e)))))])
      (fetch codex-endpoint (codex-headers token account-id))))

  (cond
    [(or (= status 401) (= status 403))
     (raise-usage
      'authentication
      (format "Codex rejected the local login (HTTP ~a): ~a"
              status (substring body 0 (min 200 (string-length body))))
      status)]
    [(= status 429)
     (raise-usage 'rate-limited "Codex usage endpoint rate-limited us" status)]
    [(or (< status 200) (>= status 300))
     (raise-usage 'service-unavailable
                  (format "Unexpected Codex response (HTTP ~a)" status)
                  status)]
    [else (parse-codex-usage body now-ms)]))
