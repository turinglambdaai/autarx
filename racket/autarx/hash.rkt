#lang racket/base

;; FNV-1a hash over the canonical form of an element subtree — port of
;; ContentHasher.cs. The numeric value is a cross-implementation contract
;; (the semantic diff compares .NET-indexed and Racket-indexed hashes), so:
;;   - iteration is over UTF-16 code units exactly like C# `foreach char`
;;     (non-BMP characters contribute two surrogate units)
;;   - arithmetic wraps at 2^64
;;   - attributes are visited sorted by key (ordinal/codepoint order)
;;
;; The hash state circulates as two 32-bit fixnum halves (cons hi . lo) so
;; every mixing step is allocation-free; only the public boundary joins.

(require racket/fixnum
         (only-in "arxml.rkt"
                  arxml-element-name arxml-element-text arxml-element-attrs
                  arxml-element-children))

(provide content-hash
         fnv1a-mix-string)

(define +fnv-offset+ 14695981039346656037)
(define +fnv-prime+ 1099511628211)

;; prime split: 0x100000001B3 = 256 * 2^32 + 435
(define +prime-hi+ 256)
(define +prime-lo+ 435)
(define +mask32+ 4294967295)

(define (split-lo v) (bitwise-and v +mask32+))
(define (split-hi v) (fxand (arithmetic-shift v -32) +mask32+))
;; the FNV offset exceeds fixnum range — split once at module load
(define +offset-hi+ (split-hi +fnv-offset+))
(define +offset-lo+ (split-lo +fnv-offset+))
(define (join64 hi lo) (bitwise-ior (arithmetic-shift hi 32) lo))

;; (hi . lo) * prime mod 2^64, schoolbook with the prime's two halves; all
;; intermediate products stay under 2^50 so fixnums hold them
(define (mul-prime hi lo)
  (define cross (fx+ (fx* hi +prime-lo+) (fx* lo +prime-hi+)))
  (define lo-prod (fx* lo +prime-lo+))
  (cons (fxand (fx+ cross (fxrshift lo-prod 32)) +mask32+)
        (fxand lo-prod +mask32+)))

;; xor one UTF-16 unit into the low half, then fold with the prime
(define (xor-unit p unit)
  (mul-prime (car p) (fxxor (cdr p) unit)))

;; Mix(h, s) from ContentHasher: per-unit fold, terminator xor 0x1f (low
;; half only — 0x1f < 2^8), then one more prime fold.
(define (mix-string/halves p s)
  (define n (string-length s))
  (let loop ([i 0] [p p])
    (if (fx= i n)
        (xor-unit p 31)
        (let ([cp (char->integer (string-ref s i))])
          (loop (add1 i)
                (if (> cp #xFFFF)
                    (let ([cp^ (- cp #x10000)])
                      (xor-unit
                       (xor-unit p (+ #xD800 (arithmetic-shift cp^ -10)))
                       (+ #xDC00 (bitwise-and cp^ #x3FF))))
                    (xor-unit p cp)))))))

(define (mix-string p s) (mix-string/halves p s))

;; subtree hash in halves; children fold as (h ^ child) * prime
(define (content-hash/halves e)
  (define p0 (mix-string (cons +offset-hi+ +offset-lo+)
                         (arxml-element-name e)))
  (define p1
    (let loop ([as (sort (arxml-element-attrs e) string<? #:key car)] [p p0])
      (if (null? as)
          p
          (loop (cdr as)
                (mix-string (mix-string p (caar as)) (cdar as))))))
  (define p2 (let ([text (arxml-element-text e)])
               (if text (mix-string p1 text) p1)))
  (let loop ([children (arxml-element-children e)] [p p2])
    (if (null? children)
        p
        (let* ([child-p (content-hash/halves (car children))]
               [xored (cons (fxxor (car p) (car child-p))
                            (fxxor (cdr p) (cdr child-p)))])
          (loop (cdr children) (mul-prime (car xored) (cdr xored)))))))

(define (content-hash e)
  (define p (content-hash/halves e))
  (join64 (car p) (cdr p)))

(define (fnv1a-mix-string h s)
  (define p (mix-string (cons (split-hi h) (split-lo h)) s))
  (join64 (car p) (cdr p)))
