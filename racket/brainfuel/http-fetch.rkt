#lang racket/base

;; The production HTTP transport for the quota clients: one GET, 15 s total
;; timeout, mirroring the old HttpClient configuration. Tests inject a stub
;; fetch of the same contract instead of this module.
;;
;; fetch contract: (fetch url headers) -> (values status-code body-string),
;; raising transport-error on transport-level trouble.

(require net/http-client
         racket/port
         "failure.rkt")

(provide default-fetch
         http-timeout-seconds)

(define http-timeout-seconds 15)

(define (status-code-from-line line)
  (define m (regexp-match #px"^HTTP/[0-9.]+[ ]+([0-9]{3})" line))
  (and m (string->number (cadr m))))

(define (default-fetch url headers)
  (define result (make-channel))
  ;; The request runs in a custodian-owned thread so a timeout can tear down
  ;; the socket and the worker together; nothing leaks past the sync.
  (define cust (make-custodian))
  (parameterize ([current-custodian cust])
    (thread
     (lambda ()
       (with-handlers ([exn:fail?
                        (lambda (e) (channel-put result (cons 'error e)))])
         (define-values (status-line response-headers content)
           (http-sendrecv url
                          #:method "GET"
                          #:headers (append headers '("Accept-Encoding: identity"))
                          #:ssl? 'auto
                          #:close? #t))
         (define code (status-code-from-line status-line))
         (define body (port->string content))
         (close-input-port content)
         (if code
             (channel-put result (cons 'ok (cons code body)))
             (channel-put
              result
              (cons 'error
                    (transport-error 'raw
                                     (format "unreadable HTTP status line: ~a" status-line)
                                     #f))))))))
  (define outcome (sync/timeout http-timeout-seconds result))
  (custodian-shutdown-all cust)
  (cond
    [(not outcome)
     (raise (transport-error 'timeout
                             (format "request timed out after ~a s"
                                     http-timeout-seconds)
                             #f))]
    [(eq? (car outcome) 'error)
     (define raised (cdr outcome))
     (if (transport-error? raised)
         (raise raised)
         (raise (transport-error 'raw (exn-message raised) #f)))]
    [else
     (values (car (cdr outcome)) (cdr (cdr outcome)))]))
