#lang racket/base

;; Claude subscription usage client, ported from Services/ClaudeQuotaClient.cs.
;; The oauth-2025-04-20 beta flag and the claude-code/* User-Agent are
;; mandatory; without them the endpoint sits in an aggressively rate-limited
;; bucket. Token is read fresh by the scheduler on every refresh.

(require racket/string
         "failure.rkt"
         "http-fetch.rkt"
         "timestamps.rkt"
         "usage-models.rkt")

(provide claude-endpoint
         claude-fetch-usage
         parse-claude-usage)

(define claude-endpoint "https://api.anthropic.com/api/oauth/usage")

(define (clamp-pct v)
  (min 100.0 (max 0.0 (or v 0.0))))

;; five_hour / seven_day windows with utilization (percent) and resets_at ISO.
(define (parse-claude-usage body now-ms)
  (define doc (json-parse-safe body))
  (unless doc
    (raise-usage 'invalid-response "Claude usage response was not valid JSON"))
  (define five-hour (jref doc "five_hour"))
  (define seven-day (jref doc "seven_day"))
  (unless (or (hash? five-hour) (hash? seven-day))
    (raise-usage 'no-coding-plan
                 "Claude usage response contained no usage windows"))

  (define (window h)
    (values (clamp-pct (jreal h "utilization"))
            (iso8601->ms (jstring h "resets_at"))))

  (define-values (hourly-pct hourly-reset)
    (if (hash? five-hour) (window five-hour) (values #f #f)))
  (define-values (weekly-pct weekly-reset)
    (if (hash? seven-day) (window seven-day) (values #f #f)))

  (usage-snapshot hourly-pct weekly-pct hourly-reset weekly-reset #f now-ms))

(define (claude-headers token)
  (list "User-Agent: claude-code/2.0"
        "anthropic-beta: oauth-2025-04-20"
        "Accept: application/json"
        (string-append "Authorization: Bearer " token)))

(define (claude-fetch-usage fetch token now-ms)
  (unless (and (string? token) (non-empty-string? (string-trim token)))
    (raise-usage 'authentication
                 "Claude Code login not found (~/.claude/.credentials.json)"))

  (define-values (status body)
    (with-handlers
        ([transport-error?
          (lambda (e)
            (if (eq? (transport-error-kind e) 'timeout)
                (raise-usage 'timeout "Claude usage request timed out")
                (raise-usage 'network
                             (format "Claude connection failure: ~a"
                                     (transport-error-message e)))))])
      (fetch claude-endpoint (claude-headers token))))

  (cond
    [(or (= status 401) (= status 403))
     (raise-usage 'authentication
                  (format "Claude rejected the local login (HTTP ~a) - re-login via Claude Code"
                          status)
                  status)]
    [(= status 429)
     (raise-usage 'rate-limited "Claude usage endpoint rate-limited us" status)]
    [(or (< status 200) (>= status 300))
     (raise-usage 'service-unavailable
                  (format "Unexpected Claude response (HTTP ~a)" status)
                  status)]
    [else (parse-claude-usage body now-ms)]))
