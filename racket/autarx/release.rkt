#lang racket/base

;; Derives the AUTOSAR release from an xsi:schemaLocation value — port of
;; AutosarReleaseParser.cs. Never guesses: unrecognised forms yield #f.
;; Pattern order is part of the contract: classic (4-3-0 -> 4.3.0), foundry
;; (R23-11), numeric (00050).
;;
;; The numeric form is the XSD revision number printed in the AUTOSAR
;; release overview's Classic Platform column (00043 -> 4.3.0, 00044 ->
;; 4.3.1, 00047 -> 4.4.0, 00048 -> 4.5.0, 00049 -> 4.6.0, 00050 -> 4.7.0).
;; Vendor generators (EB tresos, MCAL swcd) emit these instead of the
;; classic AUTOSAR_x-y-z.xsd names; known revisions translate to the
;; classic version, unknown ones stay raw rather than guessing.

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

;; numeric XSD revisions with a known classic-platform release
(define xsd-revision->release
  '#hash(("00043" . "4.3.0")
         ("00044" . "4.3.1")
         ("00047" . "4.4.0")
         ("00048" . "4.5.0")
         ("00049" . "4.6.0")
         ("00050" . "4.7.0")))

;; AUTOSAR_00050.xsd — translate known revisions, keep unknown ones raw
(define (numeric s)
  (define m (regexp-match #px"AUTOSAR_([0-9]{5})(\\.xsd)?" s))
  (and m (hash-ref xsd-revision->release (cadr m) (cadr m))))
