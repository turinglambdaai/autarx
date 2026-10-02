#lang racket/base

;; Ordered JSON writer matching the CLI's System.Text.Json profile:
;; WriteIndented (2 spaces), camelCase field names supplied explicitly in
;; declaration order, null fields OMITTED, string enums. Objects are
;; plain association lists (ordered); arrays are lists. Escaping follows
;; JavaScriptEncoder.Default: HTML-sensitive ASCII (< > & ' ` +),
;; control characters and non-ASCII are escaped.

(provide jstr
         jnum
         jbool
         jnull
         json->string
         jesc)

;; values: string | number | boolean | 'null | alist (object) | list (array)
(define (jstr s) s)
(define (jnum n) n)
(define (jbool b) b)
(define jnull 'null)

(define (json->string v)
  (define out (open-output-string))
  (write-value v out 0)
  (get-output-string out))

(define (write-value v out depth)
  (cond
    [(string? v) (display (jesc v) out)]
    [(number? v) (display (number->json v) out)]
    [(boolean? v) (display (if v "true" "false") out)]
    [(eq? v 'null) (display "null" out)]
    [(list? v)
     (if (and (pair? v) (pair? (car v)) (string? (caar v)))
         (write-object v out depth)      ; association list = object
         (write-array v out depth))]))

(define (write-object fields out depth)
  ;; null-valued fields are omitted entirely (WhenWritingNull)
  (define present
    (filter (λ (f) (not (eq? (cdr f) 'null))) fields))
  (cond
    [(null? present) (display "{}" out)]
    [else
     (display "{\n" out)
     (for ([f (in-list present)] [i (in-naturals)])
       (write-pad out (add1 depth))
       (display (jesc (car f)) out)
       (display ": " out)
       (write-value (cdr f) out (add1 depth))
       (display (if (= i (sub1 (length present))) "\n" ",\n") out))
     (write-pad out depth)
     (display "}" out)]))

(define (write-array items out depth)
  (cond
    [(null? items) (display "[]" out)]
    [else
     (display "[\n" out)
     (for ([item (in-list items)] [i (in-naturals)])
       (write-pad out (add1 depth))
       (write-value item out (add1 depth))
       (display (if (= i (sub1 (length items))) "\n" ",\n") out))
     (write-pad out depth)
     (display "]" out)]))

(define (write-pad out depth)
  (display (make-string (* 2 depth) #\space) out))

(define (number->json n)
  (cond
    [(integer? n) (number->string n)]
    [(exact? n) (number->string (exact->inexact n))]
    [else (number->string n)]))

(define (jesc s)
  (define needs-escape?
    (for/first ([c (in-string s)]
                #:when (or (char<? c #\space)
                           (char=? c #\") (char=? c #\\)
                           (char=? c #\<) (char=? c #\>) (char=? c #\&)
                           (char=? c #\') (char=? c #\`) (char=? c #\+)
                           (char>=? c #\u0080)))
      #t))
  (if (not needs-escape?)
      (string-append "\"" s "\"")
      (let ([out (open-output-string)])
        (write-char #\" out)
        (for ([c (in-string s)])
          (cond
            [(char=? c #\") (display "\\\"" out)]
            [(char=? c #\\) (display "\\\\" out)]
            [(char=? c #\newline) (display "\\n" out)]
            [(char=? c #\tab) (display "\\t" out)]
            [(char=? c #\return) (display "\\r" out)]
            [(char=? c #\backspace) (display "\\b" out)]
            [(char=? c #\u000C) (display "\\f" out)]
            [(or (char<? c #\space)
                 (char=? c #\<) (char=? c #\>) (char=? c #\&)
                 (char=? c #\') (char=? c #\`) (char=? c #\+)
                 (char>=? c #\u0080))
             (fprintf out "\\u~a" (pad4 (hex4 (char->integer c))))]
            [else (write-char c out)]))
        (write-char #\" out)
        (get-output-string out))))

(define (hex4 n)
  (define h (number->string n 16))
  (string-upcase h))

(define (pad4 h)
  (define pad (- 4 (string-length h)))
  (if (> pad 0) (string-append (make-string pad #\0) h) h))
