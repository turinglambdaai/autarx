#lang info

(define collection 'multi)
;; crypto backs the updater's Ed25519 signed-feed surface (catalog package;
;; CI installs the pinned rivet git URL and crypto before linking this
;; package). rivet/distribution is deliberately NOT declared: it is not a
;; catalog pin — a deps entry would resolve the catalog's moving entry
;; instead of the family-pinned tag.
(define deps '("base" "crypto"))
(define build-deps '("rackunit-lib"))
(define pkg-desc "Autarx AUTOSAR workbench — Racket domain core (Rivet rebuild)")
(define pkg-authors '("turinglambdaai"))
