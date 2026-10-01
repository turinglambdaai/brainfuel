#lang racket/base

;; Ports BrainFuel.Tests/UsageHistoryStoreTests.cs + AverageBurnRateTests.cs.

(require json
         racket/file
         racket/path
         racket/string
         rackunit
         "../brainfuel/history-store.rkt"
         "../brainfuel/timestamps.rkt")

(define T0 (* 1780000000000)) ; 2026-09-26T12:00:00Z, unix ms
(define minute (* 60 1000))

(define (temp-dir)
  (make-temporary-file "bf-history-tests-~a" 'directory))

;; ---- SaveThenLoad_RoundTripsSamples ------------------------------------------

(define dir1 (temp-dir))
(define path1 (build-path dir1 "usage-history.json"))
(define store1 (load-history path1))
(history-append! store1 (usage-sample T0 42.5 68.25))
(history-append! store1 (usage-sample (+ T0 (* 5 minute)) 43.5 #f)) ; absent weekly
(history-save! store1)

(define back (load-history path1))
(check-equal? (length (history-samples back)) 2)
(check-equal? (usage-sample-at-ms (car (history-samples back))) T0)
(check-equal? (usage-sample-hourly-pct (car (history-samples back))) 42.5)
(check-equal? (usage-sample-weekly-pct (car (history-samples back))) 68.25)
(check-false (usage-sample-weekly-pct (cadr (history-samples back))))

;; The file is drop-in compatible: ISO at strings, absent windows omitted.
(define roundtrip-doc (with-input-from-file path1 read-json))
(check-true (list? roundtrip-doc))
(check-equal? (hash-ref (car roundtrip-doc) 'at)
              (ms->iso8601 T0))
(check-equal? (hash-ref (car roundtrip-doc) 'h) 42.5)
(check-false (hash-has-key? (cadr roundtrip-doc) 'w)) ; absent window omitted

;; ---- OutOfOrderAppend_KeepsSortOrder ------------------------------------------

(define store2 (load-history (build-path (temp-dir) "h.json")))
(history-append! store2 (usage-sample (+ T0 (* 5 minute)) 10 20))
(history-append! store2 (usage-sample T0 5 15)) ; clock jumped back
(check-equal? (usage-sample-at-ms (car (history-samples store2))) T0)
(check-equal? (usage-sample-at-ms (cadr (history-samples store2)))
              (+ T0 (* 5 minute)))

;; ---- Prune_DropsOldAndCapsCount -------------------------------------------------

(define store3 (load-history (build-path (temp-dir) "h.json")))
(for ([i (in-range 20)])
  (history-append! store3
                   (usage-sample (+ (- T0 (* 9 24 60 minute)) (* i 10 minute))
                                 (* 1.0 i) (* 1.0 i))))
(history-prune! store3 T0)
(check-equal? (history-samples store3) '())

(for ([i (in-range 3010)])
  (history-append! store3 (usage-sample (+ T0 (* i minute))
                                        (* 1.0 (modulo i 100))
                                        (* 1.0 (modulo i 100)))))
(history-prune! store3 (+ T0 (* 3009 minute)))
(check-equal? (length (history-samples store3)) 3000)
(check-equal? (usage-sample-at-ms (car (history-samples store3)))
              (+ T0 (* 10 minute)))

;; ---- CorruptFile_LoadsEmptyWithoutThrowing / MissingFile_LoadsEmpty -------------

(define corrupt-dir (temp-dir))
(define corrupt-path (build-path corrupt-dir "h.json"))
(with-output-to-file corrupt-path (lambda () (display "{ this is not json ")))
(check-equal? (history-samples (load-history corrupt-path)) '())

(check-equal? (history-samples (load-history (build-path (temp-dir) "gone.json")))
              '())

;; ---- Legacy shared-file migration -----------------------------------------------

(define mig-dir (temp-dir))
(define shared (build-path mig-dir "usage-history.json"))
(define shared-store (load-history shared))
(history-append! shared-store (usage-sample T0 10 20))
(history-save! shared-store)
(define migrated-path (history-path-for-account mig-dir "default"))
(check-equal? (path->string migrated-path)
              (path->string (build-path mig-dir "usage-history-default.json")))
(check-false (file-exists? shared))
(check-equal? (length (history-samples (load-history migrated-path))) 1)
;; Calling again is a no-op.
(check-equal? (history-path-for-account mig-dir "default") migrated-path)

;; ---- AverageBurnRateTests ----------------------------------------------------------

(define (samples . items)
  (for/list ([item (in-list items)])
    (usage-sample (+ T0 (- (* (car item) minute))) (cadr item) #f)))

;; 20 -> 40 -> 35 -> 55: two rises (+40) and one drop (-5, ignored).
(check-equal? (average-burn-rate-24h
                (samples (list 240 20.0) (list 180 40.0)
                         (list 120 35.0) (list 60 55.0))
                T0)
              (/ 40.0 24.0))

;; 45 -> (reset) -> 5 -> 40: positive deltas are 35; the reset drop is ignored.
(check-equal? (average-burn-rate-24h
                (samples (list 180 45.0) (list 120 5.0) (list 60 40.0))
                T0)
              (/ 35.0 24.0))

;; Absent window between samples: pairs across it must not be compared.
(check-false (average-burn-rate-24h
              (samples (list 240 20.0) (list 180 #f) (list 120 40.0))
              T0))

;; 30 h / 29 h ago: outside the 24 h window.
(check-false (average-burn-rate-24h
              (samples (list (* 30 60) 10.0) (list (* 29 60) 90.0))
              T0))

;; A single sample yields no average.
(check-false (average-burn-rate-24h (samples (list 60 42.0)) T0))
(check-false (average-burn-rate-24h '() T0))
