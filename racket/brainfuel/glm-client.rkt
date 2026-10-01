#lang racket/base

;; GLM Coding Plan monitor client, ported from Services/GlmUsageClient.cs.
;;
;;   GET {baseDomain}/api/monitor/usage/quota/limit
;;   Authorization: <raw key>   (no Bearer prefix; a pasted one is stripped)
;;   Accept-Language: en-US,en
;;
;; The core lesson from v0.5.5: both gateways reject bad keys with HTTP 200 +
;; an error envelope, so the body - not the status code - decides the kind.

(require racket/string
         "failure.rkt"
         "http-fetch.rkt"
         "usage-models.rkt")

(provide glm-path
         glm-url
         normalize-api-key
         glm-fetch-usage)

(define glm-path "/api/monitor/usage/quota/limit")

(define (glm-url base-domain)
  (string-append (string-trim base-domain "/" #:repeat? #t) glm-path))

;; Tolerate pasted keys carrying a "Bearer " prefix; both gateways expect the
;; raw key and would otherwise reject the request.
(define (normalize-api-key api-key)
  (define key (string-trim (or api-key "")))
  (if (and (>= (string-length key) 7)
           (string-ci=? (substring key 0 7) "Bearer "))
      (string-trim (substring key 7))
      key))

(define (glm-headers api-key)
  (list "Accept-Language: en-US,en"
        (string-append "Authorization: " (normalize-api-key api-key))))

(define (http-failure-message kind status)
  (case kind
    [(proxy) (format "GLM proxy authentication failed (HTTP ~a)" status)]
    [(rate-limited) (format "GLM rate limit (HTTP ~a)" status)]
    [(no-coding-plan) (format "No Coding Plan quota (HTTP ~a)" status)]
    [(authentication) (format "GLM authentication failed (HTTP ~a)" status)]
    [else
     (if (>= status 500)
         (format "GLM service error (HTTP ~a)" status)
         (format "Unexpected GLM response (HTTP ~a)" status))]))

(define (glm-fetch-usage fetch base-domain api-key now-ms)
  (define-values (status body)
    (with-handlers
        ([transport-error?
          (lambda (e)
            (if (eq? (transport-error-kind e) 'timeout)
                (raise-usage 'timeout "GLM quota request timed out")
                (raise-usage
                 (classify-transport-message (transport-error-message e))
                 (format "~a: ~a"
                         (case (classify-transport-message
                                (transport-error-message e))
                           [(tls) "TLS/certificate connection failure"]
                           [(proxy) "Proxy connection failure"]
                           [else "Network connection failure"])
                         (transport-error-message e)))))])
      (fetch (glm-url base-domain) (glm-headers api-key))))

  (if (or (< status 200) (>= status 300))
      (let ([kind (classify-http-status status body)])
        (raise-usage kind (http-failure-message kind status) status))
      (parse-glm-usage body now-ms)))
