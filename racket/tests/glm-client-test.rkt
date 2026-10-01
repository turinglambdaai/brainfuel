#lang racket/base

;; Ports BrainFuel.Tests/GlmUsageClientTests.cs: the HTTP-200 error envelopes
;; both gateways really use, status mapping, transport classification,
;; percentage dual-scale mapping, Lite-plan credit limits and key
;; normalization. The HTTP layer is a stub with the fetch contract.

(require rackunit
         "../brainfuel/failure.rkt"
         "../brainfuel/glm-client.rkt"
         "../brainfuel/usage-models.rkt")

(define T0 1780000000000)

(define (kind-of fetch)
  (with-handlers ([exn:usage? (lambda (e) (exn:usage-kind e))])
    (glm-fetch-usage fetch "https://open.bigmodel.cn" "test-key" T0)
    'no-error))

(define (kind+exn-of fetch)
  (with-handlers ([exn:usage? (lambda (e) (values (exn:usage-kind e) e))])
    (glm-fetch-usage fetch "https://open.bigmodel.cn" "test-key" T0)
    (values 'no-error #f)))

;; ---- HTTP 200 error envelopes (the shape both gateways really use) ----------

(define (envelope-test name body expected-kind must-contain)
  (define-values (kind exn)
    (kind+exn-of (lambda (url headers) (values 200 body))))
  (check-equal? kind expected-kind name)
  (when must-contain
    (check-true (string-contains? (exn-message exn) must-contain)
                (format "~a message: ~a" name (and exn (exn-message exn))))))

(envelope-test "zai-rejected-token"
               "{\"code\":401,\"msg\":\"token expired or incorrect\",\"success\":false}"
               'authentication "token expired or incorrect")
(envelope-test "bigmodel-missing-auth-header"
               "{\"code\":1001,\"msg\":\"Header中未收到Authorization参数，无法进行身份验证。\",\"success\":false}"
               'authentication #f)
(envelope-test "missing-plan"
               "{\"code\":404,\"msg\":\"coding plan not subscribed\",\"success\":false}"
               'no-coding-plan #f)
(envelope-test "rate-limited-code"
               "{\"code\":429,\"msg\":\"too many requests\",\"success\":false}"
               'rate-limited #f)
(envelope-test "unknown-code"
               "{\"code\":4217,\"msg\":\"something novel\",\"success\":false}"
               'invalid-response "something novel")
;; `code` appears as string in the wild too.
(envelope-test "string-code-field"
               "{\"code\":\"401\",\"msg\":\"token expired or incorrect\",\"success\":false}"
               'authentication #f)

;; Explicit success bodies do NOT hit the envelope logic; without token
;; quotas they still throw - but as NoCodingPlan.
(envelope-test "explicit-success-empty"
               "{\"code\":200,\"msg\":\"ok\",\"success\":true,\"data\":{\"limits\":[]}}"
               'no-coding-plan "limits: none")
(envelope-test "code-zero-tolerated"
               "{\"code\":0,\"data\":{\"limits\":[]}}"
               'no-coding-plan "limits: none")

;; ---- HTTP status failures -----------------------------------------------------

(define (status-of status body)
  (kind-of (lambda (url headers) (values status body))))

(check-equal? (status-of 401 "{}") 'authentication)
(check-equal? (status-of 403 "{}") 'authentication)
(check-equal? (status-of 402 "{}") 'no-coding-plan)
(check-equal? (status-of 429 "{}") 'rate-limited)
(check-equal? (status-of 500 "{}") 'service-unavailable)
(check-equal? (status-of 503 "{}") 'service-unavailable)
(check-equal? (status-of 407 "{}") 'proxy)
(check-equal? (status-of 418 "{}") 'service-unavailable)

;; ---- transport failures ---------------------------------------------------------

(define (transport-of err)
  (kind-of (lambda (url headers) (raise err))))

(check-equal? (transport-of (transport-error 'timeout "timed out" #f)) 'timeout)
(check-equal? (transport-of (transport-error 'raw "Connection refused" #f))
              'network)
(check-equal? (transport-of
               (transport-error 'raw "The SSL connection could not be established" #f))
              'tls)
(check-equal? (transport-of (transport-error 'raw "proxy tunnel failed" #f))
              'proxy)

;; ---- success mapping --------------------------------------------------------------

(define success-01-body
  "{\"data\":{\"level\":\"Max\",\"limits\":[
      {\"type\":\"TOKENS_LIMIT\",\"number\":5,\"percentage\":0.35,\"nextResetTime\":1780000000000},
      {\"type\":\"TOKENS_LIMIT\",\"number\":1,\"percentage\":0.80,\"nextResetTime\":1790000000000},
      {\"type\":\"TIME_LIMIT\",\"number\":1,\"percentage\":0.50}]}}")

(define success-100-body
  "{\"data\":{\"level\":\"Max\",\"limits\":[
      {\"type\":\"TOKENS_LIMIT\",\"number\":5,\"percentage\":35,\"nextResetTime\":1780000000000},
      {\"type\":\"TOKENS_LIMIT\",\"number\":1,\"percentage\":80,\"nextResetTime\":1790000000000}]}}")

;; Both the 0..1 scale and the 0..100 scale map to the same snapshot.
(for ([body (list success-01-body success-100-body)])
  (define snap
    (glm-fetch-usage (lambda (url headers) (values 200 body))
                     "https://open.bigmodel.cn" "test-key" T0))
  (check-true (and (usage-snapshot-hourly-pct snap) #t) "has hourly")
  (check-true (and (usage-snapshot-weekly-pct snap) #t) "has weekly")
  (check-equal? (usage-snapshot-hourly-pct snap) 35.0)
  (check-equal? (usage-snapshot-weekly-pct snap) 80.0)
  (check-equal? (usage-snapshot-plan-level snap) "Max")
  (check-equal? (usage-snapshot-hourly-reset-ms snap) 1780000000000)
  (check-equal? (usage-snapshot-weekly-reset-ms snap) 1790000000000))

;; TIME_LIMIT is ignored; no token quota at all -> NoCodingPlan with a
;; structural summary (types + numbers, no values).
(define-values (no-token-kind no-token-exn)
  (kind+exn-of (lambda (url headers)
             (values 200
                     "{\"data\":{\"limits\":[{\"type\":\"TIME_LIMIT\",\"number\":1,\"percentage\":0.5}]}}"))))
(check-equal? no-token-kind 'no-coding-plan)
(check-true (string-contains? (exn-message no-token-exn) "TIME_LIMIT#1"))

;; Reset fallback: numeric extra fields whose key mentions RESET/EXPIRE/END/NEXT.
(define snap-extra
  (glm-fetch-usage
   (lambda (url headers)
     (values 200
             "{\"data\":{\"level\":\"Max\",\"limits\":[
                {\"type\":\"TOKENS_LIMIT\",\"number\":5,\"percentage\":10,
                 \"currentPeriodEnd\":1781234567890}]}}"))
   "https://open.bigmodel.cn" "test-key" T0))
(check-equal? (usage-snapshot-hourly-reset-ms snap-extra) 1781234567890)

;; ---- newer plans: CREDIT_LIMIT with integer 0..100 percentages ----------------
;; Replay of a real Lite-plan response: plain integers must NOT be scaled.

(define lite-plan-body
  "{\"code\":200,\"msg\":\"ok\",\"success\":true,\"data\":{\"level\":\"lite\",\"limits\":[
     {\"type\":\"CREDIT_LIMIT\",\"unit\":3,\"number\":5,\"usage\":2000,\"currentValue\":0,\"remaining\":2000,\"percentage\":0},
     {\"type\":\"CREDIT_LIMIT\",\"unit\":6,\"number\":1,\"usage\":10000,\"currentValue\":135,\"remaining\":9864,\"percentage\":1,\"nextResetTime\":1790748174980}]}}")

(define lite-snap
  (glm-fetch-usage (lambda (url headers) (values 200 lite-plan-body))
                   "https://open.bigmodel.cn" "test-key" T0))
(check-true (and (usage-snapshot-hourly-pct lite-snap) #t))
(check-true (and (usage-snapshot-weekly-pct lite-snap) #t))
(check-equal? (usage-snapshot-hourly-pct lite-snap) 0.0)
(check-equal? (usage-snapshot-weekly-pct lite-snap) 1.0) ; must NOT blow up to 100
(check-equal? (usage-snapshot-plan-level lite-snap) "lite")

;; A fresh window at 0 must not trip the 0..1 heuristic either.
(define fresh-snap
  (glm-fetch-usage
   (lambda (url headers)
     (values 200
             "{\"data\":{\"limits\":[{\"type\":\"CREDIT_LIMIT\",\"number\":5,\"percentage\":0}]}}"))
   "https://open.bigmodel.cn" "test-key" T0))
(check-true (and (usage-snapshot-hourly-pct fresh-snap) #t))
(check-equal? (usage-snapshot-hourly-pct fresh-snap) 0.0)

;; ---- structural diagnostics ------------------------------------------------------

(check-equal? (status-of 200 "{\"foo\":1,\"bar\":true}") 'invalid-response)
(define-values (missing-data-kind missing-data-exn)
  (kind+exn-of (lambda (url headers) (values 200 "{\"foo\":1,\"bar\":true}"))))
(check-true (and (string-contains? (exn-message missing-data-exn) "foo")
                 (string-contains? (exn-message missing-data-exn) "bar")))

(check-equal? (status-of 200 "<html>gateway login page</html>") 'invalid-response)

;; ---- key normalization (headers captured from the stub) -------------------------

(define last-headers (box #f))
(define (capturing-fetch url headers)
  (set-box! last-headers headers)
  (values 200 success-100-body))

(define (auth-header)
  (for/or ([h (in-list (unbox last-headers))]
           #:when (string-prefix? h "Authorization: "))
    (substring h (string-length "Authorization: "))))

(require racket/string)
(void (glm-fetch-usage capturing-fetch "https://open.bigmodel.cn/"
                      "  Bearer abc-def-123  " T0))
(check-equal? (auth-header) "abc-def-123" "Bearer prefix is stripped")

(void (glm-fetch-usage capturing-fetch "https://open.bigmodel.cn" "abc-def-123" T0))
(check-equal? (auth-header) "abc-def-123" "plain key is sent raw")

;; URL building + Accept-Language header.
(check-equal? (glm-url "https://open.bigmodel.cn/")
              "https://open.bigmodel.cn/api/monitor/usage/quota/limit")
(check-true
 (and (member "Accept-Language: en-US,en" (unbox last-headers)) #t))
