#lang racket/base

;; Ports BrainFuel.Tests/UsageFailureTests.cs plus the classification
;; heuristics from GlmUsageClientTests (body wording decides the kind).

(require rackunit
         "../brainfuel/failure.rkt")

;; ---- transient / save-anyway tables (UsageFailureTests) ---------------------

(define transient-cases
  '((network #t)
    (tls #t)
    (proxy #t)
    (timeout #t)
    (rate-limited #t)
    (service-unavailable #t)
    (invalid-response #t)
    (authentication #f)
    (no-coding-plan #f)
    (unknown #f)))

(for ([c (in-list transient-cases)])
  (check-equal? (transient-failure? (car c)) (cadr c)
                (format "IsTransient ~a" (car c))))

(check-false (save-anyway? 'authentication))
(check-true (save-anyway? 'no-coding-plan))
(check-true (save-anyway? 'network))
(check-true (save-anyway? 'timeout))
(check-true (save-anyway? 'unknown))

;; ---- body heuristics ----------------------------------------------------------

(check-true (looks-like-auth-failure?
             "Header中未收到Authorization参数，无法进行身份验证。"))
(check-true (looks-like-auth-failure? "Invalid API key"))
(check-false (looks-like-auth-failure? "all good"))
(check-false (looks-like-auth-failure? ""))
(check-false (looks-like-auth-failure? #f))

(check-true (looks-like-missing-plan? "coding plan not subscribed"))
(check-true (looks-like-missing-plan? "套餐不存在"))
(check-true (looks-like-missing-plan? "subscription not activated"))
(check-false (looks-like-missing-plan? "coding plan is great"))     ; no missing word
(check-false (looks-like-missing-plan? "not found"))               ; no plan word
(check-false (looks-like-missing-plan? ""))

;; ---- HTTP status mapping (GlmUsageClientTests.HttpStatus_IsClassified) -------

(define status-cases
  '((401 authentication)
    (403 authentication)
    (402 no-coding-plan)
    (429 rate-limited)
    (500 service-unavailable)
    (503 service-unavailable)
    (407 proxy)))

(for ([c (in-list status-cases)])
  (check-equal? (classify-http-status (car c) "{}") (cadr c)
                (format "HTTP ~a" (car c))))

;; Body wording overrides an innocent status.
(check-equal? (classify-http-status 404 "coding plan not found")
              'no-coding-plan)
(check-equal? (classify-http-status 400 "unauthorized request")
              'authentication)
(check-equal? (classify-http-status 418 "teapot") 'service-unavailable)

;; ---- error envelope mapping -----------------------------------------------------

(check-equal? (classify-error-envelope "401" "token expired" "{}")
              'authentication)
(check-equal? (classify-error-envelope "1001" "Header中未收到Authorization参数" "irrelevant")
              'authentication)
(check-equal? (classify-error-envelope "999" "msg" "coding plan not subscribed")
              'no-coding-plan)
(check-equal? (classify-error-envelope "429" "too many requests" "{}")
              'rate-limited)
(check-equal? (classify-error-envelope "4217" "something novel" "{}")
              'invalid-response)

;; ---- transport classification ----------------------------------------------------

(check-equal? (classify-transport-message
               "The SSL connection could not be established")
              'tls)
(check-equal? (classify-transport-message "certificate verify failed 证书错误")
              'tls)
(check-equal? (classify-transport-message "proxy tunnel failed") 'proxy)
(check-equal? (classify-transport-message "Connection refused") 'network)

;; ---- raise-usage -------------------------------------------------------------------

(check-exn exn:usage?
           (lambda ()
             (raise-usage 'authentication "bad key" 401)))
(with-handlers ([exn:usage?
                 (lambda (e)
                   (check-equal? (exn:usage-kind e) 'authentication)
                   (check-equal? (exn-message e) "bad key")
                   (check-equal? (exn:usage-status-code e) 401))])
  (raise-usage 'authentication "bad key" 401))
