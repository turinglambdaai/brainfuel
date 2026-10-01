#lang racket/base

;; User-visible strings, single-sourced from shared/i18n/{zh,en}.json (ported
;; from Services/Strings.cs). zh is the default language. Lookups fall back to
;; the key itself when a table is unavailable, mirroring Strings.Get.

(require json
         racket/runtime-path
         racket/string)

(provide current-language
         tr
         tr-raw)

(define-runtime-path zh-i18n-path "../../shared/i18n/zh.json")
(define-runtime-path en-i18n-path "../../shared/i18n/en.json")

;; 'zh or 'en
(define current-language (make-parameter 'zh))

(define cache (box #f))

(define (load-one path)
  (with-handlers ([exn:fail? (lambda (_) (hasheq))])
    (define doc (with-input-from-file path read-json))
    (if (hash? doc) doc (hasheq))))

(define (tables)
  (or (unbox cache)
      (let ([t (hasheq 'zh (load-one zh-i18n-path)
                       'en (load-one en-i18n-path))])
        (set-box! cache t)
        t)))

(define (tr-raw key)
  (define lang (current-language))
  (define table (hash-ref (tables) lang (hasheq)))
  (hash-ref table (string->symbol key) key))

;; (tr "NotifyUsed" 42) formats {0}-style placeholders like string.Format.
(define (tr key . args)
  (define tmpl (tr-raw key))
  (for/fold ([s tmpl])
            ([arg (in-list args)]
             [i (in-naturals)])
    (string-replace s (format "{~a}" i) (format "~a" arg))))
