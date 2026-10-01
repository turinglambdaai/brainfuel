#lang racket/base

;; Ports the spirit of UsageFailureTests.CardText_CoversEveryKindInBothLanguages:
;; every failure kind has non-empty, distinct card text in both languages.
;; The tables come from shared/i18n/{zh,en}.json (ported from Strings.cs).

(require rackunit
         "../brainfuel/failure.rkt"
         "../brainfuel/strings.rkt")

(check-equal? (current-language) 'zh)

;; Placeholder formatting matches string.Format({0}).
(check-equal? (tr "NotifyUsed" 42) "已用 42%")
(check-equal? (tr "TipBurn" 12 "3 小时后") "燃速 ≈12%/时 · 预计 3 小时后 后耗尽")

;; Language switch.
(check-equal? (parameterize ([current-language 'en]) (tr "NotifyUsed" 42))
              "42% used")

;; Unknown keys fall back to the key itself (Strings.Get behavior).
(check-equal? (tr "NoSuchKey") "NoSuchKey")

;; Every failure kind has a card line in both languages, zh != en.
(define failure-keys
  (hasheq 'authentication "FailureAuthentication"
          'network "FailureNetwork"
          'tls "FailureTls"
          'proxy "FailureProxy"
          'timeout "FailureTimeout"
          'rate-limited "FailureRateLimited"
          'no-coding-plan "FailureNoCodingPlan"
          'service-unavailable "FailureServiceUnavailable"
          'invalid-response "FailureInvalidResponse"
          'unknown "FailureUnknown"))

(for ([kind (in-list usage-failure-kinds)])
  (define key (hash-ref failure-keys kind))
  (define zh (parameterize ([current-language 'zh]) (tr-raw key)))
  (define en (parameterize ([current-language 'en]) (tr-raw key)))
  (check-true (and (string? zh) (positive? (string-length zh)))
              (format "~a zh text" kind))
  (check-true (and (string? en) (positive? (string-length en)))
              (format "~a en text" kind))
  (check-false (equal? zh en) (format "~a zh/en distinct" kind)))

;; Notification + status copy exists.
(for ([key (in-list '("NotifyHourlyTitle" "NotifyWeeklyTitle" "NotifyBurnSuffix"
                      "MinutesLater" "HoursLater" "DaysLater"
                      "HistoryCollecting" "BurnFaster" "BurnTypical" "BurnSlower"
                      "CliLoginMissing" "Refreshing" "NotConfigured"))])
  (check-false (equal? (tr-raw key) key)
               (format "~a present in zh table" key)))
