#lang racket/base

;; Ports the Codex/Claude replay tests from GlmUsageClientTests.cs.

(require racket/string
         rackunit
         "../brainfuel/claude-client.rkt"
         "../brainfuel/codex-client.rkt"
         "../brainfuel/failure.rkt"
         "../brainfuel/usage-models.rkt")

(define T0 1780000000000)

(define (kind-of thunk)
  (with-handlers ([exn:usage? (lambda (e) (exn:usage-kind e))])
    (thunk)
    'no-error))

(define (headers-ref headers name)
  (for/or ([h (in-list headers)]
           #:when (string-prefix? (string-downcase h)
                                  (string-downcase (string-append name ":"))))
    h))

;; ---- Codex ------------------------------------------------------------------

;; Replay of a real Plus-account response (2026-09): 5h exhausted, weekly 26%.
(define codex-body
  "{\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":false,\"limit_reached\":true,
    \"primary_window\":{\"used_percent\":100,\"limit_window_seconds\":18000,\"reset_after_seconds\":1840,\"reset_at\":1790588083},
    \"secondary_window\":{\"used_percent\":26,\"limit_window_seconds\":604800,\"reset_after_seconds\":480812,\"reset_at\":1791067056}}}")

(define last-codex (box #f))

(define (codex-fetch url headers)
  (set-box! last-codex (cons url headers))
  (values 200 codex-body))

(define codex-snap (codex-fetch-usage codex-fetch "test-token" "acc-42" T0))
(check-true (and (usage-snapshot-hourly-pct codex-snap) #t))
(check-equal? (usage-snapshot-hourly-pct codex-snap) 100.0)
(check-true (and (usage-snapshot-weekly-pct codex-snap) #t))
(check-equal? (usage-snapshot-weekly-pct codex-snap) 26.0)
(check-equal? (usage-snapshot-plan-level codex-snap) "plus")
(check-equal? (usage-snapshot-hourly-reset-ms codex-snap) (* 1790588083 1000))
(check-equal? (usage-snapshot-weekly-reset-ms codex-snap) (* 1791067056 1000))

;; Regression for the wrong-send bug: the auth header must actually be on the
;; outgoing request.
(check-equal? (car (unbox last-codex)) codex-endpoint)
(check-true (and (member "Authorization: Bearer test-token" (cdr (unbox last-codex))) #t))
(check-true (and (member "chatgpt-account-id: acc-42" (cdr (unbox last-codex))) #t))
(check-true (and (member "User-Agent: codex_cli_rs" (cdr (unbox last-codex))) #t))

;; No account id -> header omitted.
(void (codex-fetch-usage (lambda (u h) (set-box! last-codex (cons u h)) (values 200 codex-body))
                          "test-token" #f T0))
(check-false (for/or ([h (cdr (unbox last-codex))])
               (string-prefix? (string-downcase h) "chatgpt-account-id:")))

(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 401 "")) "t" #f T0)))
              'authentication)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 403 "")) "t" #f T0)))
              'authentication)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 429 "")) "t" #f T0)))
              'rate-limited)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 500 "")) "t" #f T0)))
              'service-unavailable)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 200 "{\"plan_type\":\"free\",\"rate_limit\":{}}")) "t" #f T0)))
              'no-coding-plan)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 200 "not json")) "t" #f T0)))
              'invalid-response)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (values 200 "{}")) "  " #f T0)))
              'authentication)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (raise (transport-error 'timeout "t" #f))) "t" #f T0)))
              'timeout)
(check-equal? (kind-of (lambda () (codex-fetch-usage (lambda (u h) (raise (transport-error 'raw "refused" #f))) "t" #f T0)))
              'network)

;; ---- Claude -------------------------------------------------------------------

;; Shape documented by the community (Claude Code /usage data source).
(define claude-body
  "{\"five_hour\":{\"utilization\":33.0,\"resets_at\":\"2026-04-11T07:00:00.528743+00:00\"},
    \"seven_day\":{\"utilization\":13.0,\"resets_at\":\"2026-04-17T00:59:59.951713+00:00\"},
    \"seven_day_opus\":null,
    \"seven_day_sonnet\":{\"utilization\":1.0,\"resets_at\":\"2026-04-16T03:00:00.951719+00:00\"},
    \"extra_usage\":{\"is_enabled\":false,\"monthly_limit\":null,\"utilization\":null}}")

(define last-claude (box #f))

(define claude-snap
  (claude-fetch-usage (lambda (u h)
                        (set-box! last-claude (cons u h))
                        (values 200 claude-body))
                      "test-token" T0))
(check-true (and (usage-snapshot-hourly-pct claude-snap) #t))
(check-equal? (usage-snapshot-hourly-pct claude-snap) 33.0)
(check-true (and (usage-snapshot-weekly-pct claude-snap) #t))
(check-equal? (usage-snapshot-weekly-pct claude-snap) 13.0)
(check-equal? (usage-snapshot-hourly-reset-ms claude-snap) 1775890800528)
(check-equal? (usage-snapshot-weekly-reset-ms claude-snap) 1776387599951)

(check-true (and (member "User-Agent: claude-code/2.0" (cdr (unbox last-claude))) #t))
(check-true (and (member "anthropic-beta: oauth-2025-04-20" (cdr (unbox last-claude))) #t))
(check-true (and (member "Authorization: Bearer test-token" (cdr (unbox last-claude))) #t))

(check-equal? (kind-of (lambda () (claude-fetch-usage (lambda (u h) (values 401 "")) "t" T0)))
              'authentication)
(check-equal? (kind-of (lambda () (claude-fetch-usage (lambda (u h) (values 429 "")) "t" T0)))
              'rate-limited)
(check-equal? (kind-of (lambda () (claude-fetch-usage (lambda (u h) (values 200 "{\"five_hour\":null,\"seven_day\":null}")) "t" T0)))
              'no-coding-plan)
(check-equal? (kind-of (lambda () (claude-fetch-usage (lambda (u h) (values 200 "{}")) "t" T0)))
              'no-coding-plan)
(check-equal? (kind-of (lambda () (claude-fetch-usage (lambda (u h) (values 200 "{}")) "" T0)))
              'authentication)
