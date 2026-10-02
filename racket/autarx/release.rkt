#lang racket/base

;; Derives the AUTOSAR release from an xsi:schemaLocation value — port of
;; AutosarReleaseParser.cs. Never guesses: unrecognised forms yield #f.
;; Pattern order is part of the contract: classic (4-3-0 -> 4.3.0), foundry
;; (R23-11), numeric (00050).

(require racket/string)

(provide derive-autosar-release)

(define (derive-autosar-release schema-location)
  (and schema-location
       (or (classic schema-location)
           (foundry schema-location)
           (numeric schema-location))))

;; AUTOSAR_4-3-0.xsd / AUTOSAR_4-3-0 / AUTOSAR_10-2-1-1 (1-2 digit parts)
(define (classic s)
  (define m (regexp-match #px"AUTOSAR_([0-9]{1,2}-[0-9]{1,2}(-[0-9]{1,2})?)(\\.xsd)?" s))
  (and m (string-replace (cadr m) "-" ".")))

;; AUTOSAR_R23-11
(define (foundry s)
  (define m (regexp-match #px"AUTOSAR_(R[0-9]{2}-[0-9]{2})" s))
  (and m (cadr m)))

;; AUTOSAR_00050.xsd
(define (numeric s)
  (define m (regexp-match #px"AUTOSAR_([0-9]{5})(\\.xsd)?" s))
  (and m (cadr m)))
