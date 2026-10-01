#lang racket/base

;; Server-level test driving app/backend.rkt over a real RVT1 pipe pair
;; (taskboard pattern). Proves the declared surface end to end, including
;; quota-updated / alert-triggered Events emitted from the scheduler thread
;; that `init` spawns inside its RPC handler.

(require racket/file
         racket/string
         rackunit
         rivet/backend
         rivet/protocol
         "../../app/backend.rkt"
         "../../racket/brainfuel/credentials.rkt")

;; ---- stubs: no network, no user data ----------------------------------------

(define T0 1780000000000)

(define (glm-body hourly weekly level)
  (format "{\"code\":200,\"msg\":\"ok\",\"success\":true,\"data\":{\"level\":\"~a\",\"limits\":[
             {\"type\":\"TOKENS_LIMIT\",\"number\":5,\"percentage\":~a,\"nextResetTime\":1790000000000},
             {\"type\":\"TOKENS_LIMIT\",\"number\":1,\"percentage\":~a,\"nextResetTime\":1791000000000}]}}"
          level hourly weekly))

(define current-body (box (glm-body 30 50 "Max")))

(define fetch-log (box '()))
(define (stub-fetch url headers)
  (set-box! fetch-log (cons url (unbox fetch-log)))
  (values 200 (unbox current-body)))

(define data-dir (make-temporary-file "bf-backend-test-~a" 'directory))
(set-fetch-override! stub-fetch)
(set-store-override! (make-memory-store))
(set-data-dir-override! data-dir)

;; ---- start a real server ------------------------------------------------------

(define-values (server-in client-out) (make-pipe))
(define-values (client-in server-out) (make-pipe))
(define server-thread (thread (lambda () (serve server-in server-out))))

(define hello (read-frame client-in))
(check-equal? (frame-type hello) message:hello)

(define next-id 1)

(define (send-request name . arguments)
  (define id next-id)
  (set! next-id (add1 next-id))
  (write-frame (frame message:request id (encode-value (cons name arguments)))
               client-out)
  id)

;; Reads terminal frames for `id`, collecting Events; gives up after a bound
;; so a missing Event fails the test instead of hanging it.
(define (read-terminal id [max-frames 100])
  (let loop ([events '()] [n 0])
    (when (> n max-frames)
      (error 'read-terminal "no terminal frame for request ~a" id))
    (define response (read-frame client-in))
    (cond
      [(= (frame-type response) message:event)
       (loop (cons (decode-value (frame-payload response)) events)
             (add1 n))]
      [(= (frame-id response) id)
       (values response (reverse events))]
      [else (loop events (add1 n))])))

(define (call* name . arguments)
  (define-values (v _e) (apply call name arguments))
  v)

(define (call name . arguments)
  (define id (apply send-request name arguments))
  (define-values (response events) (read-terminal id))
  (when (= (frame-type response) message:error)
    (error 'call "~a" (decode-value (frame-payload response))))
  (values (decode-value (frame-payload response)) events))

;; Drains Events (no request in flight) until `pred` matches one.
(define (wait-for-event pred [max-frames 200])
  (let loop ([n 0])
    (define f (read-frame client-in))
    (check-equal? (frame-type f) message:event)
    (define event (decode-value (frame-payload f)))
    (if (pred event)
        event
        (begin
          (when (> n max-frames)
            (error 'wait-for-event "expected event never arrived"))
          (loop (add1 n))))))

;; ---- init: starts the scheduler; its startup refresh emits an Event ----------

(define-values (init-result init-events) (call "init"))
(check-equal? init-result (void))
;; publish-accounts! runs before the response: only $state Events here.
(check-true (andmap (lambda (e) (equal? (car e) "$state")) init-events))
;; The initial refresh runs on the scheduler thread created inside the init
;; handler; this proves background threads inherit the event emitter.
(void (wait-for-event (lambda (e) (equal? (car e) "quota-updated"))))

;; Unconfigured fresh install: one default account, no quota data.
(define accounts0 (call* "$state/get" "accounts"))
(check-equal? (length accounts0) 1)
(check-equal? (list-ref (car accounts0) 0) "default")
(check-equal? (list-ref (car accounts0) 4) #f)

;; ---- save-account: GLM key lands in the store, not in settings.json ----------

(define-values (accounts1 save-events)
  (call "save-account" (list (void) "Work" "glm" "https://open.bigmodel.cn"
                             "test-key-abc" #f)))
(check-equal? (length accounts1) 2)
(define work-id (list-ref (cadr accounts1) 0))
(check-equal? (list-ref (cadr accounts1) 1) "Work")
(check-equal? (list-ref (cadr accounts1) 2) "glm")
(check-equal? (list-ref (cadr accounts1) 4) #t)
(check-true
 (for/or ([e (in-list save-events)])
   (equal? (car e) "$state")))

;; save-account refreshed immediately; the refresh pass emits before the
;; response, so the quota-updated Event is collected with it.
(check-true
 (for/or ([e (in-list save-events)])
   (and (equal? (car e) "quota-updated")
        (equal? (list-ref (cadr e) 0) work-id)
        (equal? (list-ref (cadr e) 1) "Max"))))
;; snapshot wire: (account-id plan-level hourly weekly severity fetched failure)
;; hourly wire: (used-bp reset-at-ms)
(check-true
 (for/or ([e (in-list save-events)])
   (and (equal? (car e) "quota-updated")
        (equal? (list-ref (list-ref (cadr e) 2) 0) 3000))))
(define settings-file (build-path data-dir "settings.json"))
(check-false (string-contains? (file->string settings-file) "test-key-abc"))

;; The State mirrors the snapshot too.
(define snap-state (call* "$state/get" "snapshot"))
(check-equal? (list-ref snap-state 0) work-id)

;; ---- refresh-now: synchronous pass, then alert with hysteresis ----------------

(set-box! current-body (glm-body 85 60 "Max"))
(define-values (refresh-result refresh-events) (call "refresh-now"))
(check-true
 (for/or ([e (in-list refresh-events)])
   (and (equal? (car e) "quota-updated")
        (equal? (list-ref (list-ref (cadr e) 2) 0) 8500))))
(check-true
 (for/or ([e (in-list refresh-events)])
   (and (equal? (car e) "alert-triggered")
        (equal? (list-ref (cadr e) 0) work-id)
        (equal? (list-ref (cadr e) 1) "hourly")
        (string-contains? (list-ref (cadr e) 2) "5 小时额度即将耗尽"))))

;; Still above threshold: no repeat alert.
(set-box! current-body (glm-body 86 60 "Max"))
(define-values (repeat-result repeat-events) (call "refresh-now"))
(check-false
 (for/or ([e (in-list repeat-events)])
   (equal? (car e) "alert-triggered")))

;; Below threshold - 5 re-arms; crossing again fires again.
(set-box! current-body (glm-body 70 60 "Max"))
(void (call* "refresh-now"))
(set-box! current-body (glm-body 85 60 "Max"))
(define-values (refire-result refire-events) (call "refresh-now"))
(check-true
 (for/or ([e (in-list refire-events)])
   (and (equal? (car e) "alert-triggered")
        (equal? (list-ref (cadr e) 1) "hourly"))))

;; ---- get-details ----------------------------------------------------------------

(define details (call* "get-details" work-id))
;; (hourly weekly hourly-burn weekly-burn history history-note)
(check-equal? (length details) 6)
(check-equal? (list-ref (list-ref details 0) 0) 8500)
(check-true (>= (length (list-ref details 4)) 4)) ; one sample per refresh
;; History samples: (at-ms hourly-bp weekly-bp)
(define sample0 (car (list-ref details 4)))
(check-true (exact-integer? (list-ref sample0 0)))
(check-equal? (list-ref sample0 1) 3000)

;; ---- settings --------------------------------------------------------------------

(define settings1 (call* "get-settings"))
;; (interval hourly-remaining weekly-remaining notify threshold theme language
;;  size-mode always-on-top autostart hotkey hotkey-combo palette opacity state)
(check-equal? (list-ref settings1 0) 5)
(check-equal? (list-ref settings1 4) 80)
(check-equal? (list-ref settings1 14) "protected") ; key lives in the store

(define-values (settings2 save2-events)
  (call "save-settings" (list 7 #t #f #t 90 "light" "en" "compact" #f #f
                              #f "Ctrl+Alt+B" "classic" 7500 "none")))
(check-equal? (list-ref settings2 0) 7)
(check-equal? (list-ref settings2 4) 90)
(check-equal? (list-ref settings2 14) "protected")
;; Saving settings refreshed immediately.
(check-true
 (for/or ([e (in-list save2-events)])
   (equal? (car e) "quota-updated")))
(define settings3 (call* "get-settings"))
(check-equal? (list-ref settings3 0) 7)

;; Clamps out-of-range values like the old dialog.
(define settings4 (call* "save-settings" (list 0 #t #f #t 500 "dark"
                                                         "zh" "standard" #f #f
                                                         #f "Ctrl+Alt+B"
                                                         "classic" 10000 "none")))
(check-equal? (list-ref settings4 0) 1)
(check-equal? (list-ref settings4 4) 99)

;; ---- switch-account ----------------------------------------------------------------

(define accounts2 (call* "save-account" (list (void) "Second" "glm"
                                                        "https://api.z.ai"
                                                        "second-key" #f)))
(define second-id (list-ref (cadr accounts2) 0))
(define-values (switch-result switch-events) (call "switch-account" work-id))
(check-true
 (for/or ([e (in-list switch-events)])
   (and (equal? (car e) "$state")
        (equal? (list-ref e 1) (list "active-account-id" work-id)))))
(define active (call* "$state/get" "active-account-id"))
(check-equal? active work-id)

;; ---- diagnostics: metadata only ------------------------------------------------------

(define diag (call* "get-diagnostics"))
(check-true (string-contains? diag "BrainFuel"))
(check-true (string-contains? diag work-id))
(check-true (string-contains? diag "provider=glm"))
(check-false (string-contains? diag "test-key-abc"))
(check-false (string-contains? diag "second-key"))

;; ---- remove-account -------------------------------------------------------------------

(define accounts3 (call* "remove-account" second-id))
(check-false
 (for/or ([a (in-list accounts3)]) (equal? (list-ref a 0) second-id)))
(check-equal? (length accounts3) 2)

;; ---- errors are terminal frames, not crashes --------------------------------------------

(define bad-id (send-request "get-details" "no-such-account"))
(define bad-response (let-values (((r e) (read-terminal bad-id))) r))
(check-equal? (frame-type bad-response) message:error)

(define unknown-rpc (send-request "no_such_rpc"))
(define unknown-response (let-values (((r e) (read-terminal unknown-rpc))) r))
(check-equal? (frame-type unknown-response) message:error)

;; ---- graceful shutdown ---------------------------------------------------------------------

(write-frame (frame message:shutdown 0 #"") client-out)
(thread-wait server-thread)

(set-fetch-override! #f)
(set-store-override! #f)
(set-data-dir-override! #f)
(reset-backend-for-test!)
