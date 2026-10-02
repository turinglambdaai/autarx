#lang racket/base

;; ECUC projection — port of EcucReader.cs. Modules can sit directly in
;; AR-PACKAGE/ELEMENTS or inside ECUC-VALUE-COLLECTION / ECUC-VALUES
;; wrappers, so they are located by a whole-tree scan instead of a fixed
;; path.

(require "arxml.rkt")

(provide (struct-out ecuc-module)
         (struct-out ecuc-container)
         (struct-out ecuc-parameter)
         (struct-out ecuc-reference-value)
         read-modules
         module-total-containers
         module-total-parameters
         container-total-sub-containers
         container-total-parameters)

(struct ecuc-module (short-name definition-ref xml-path containers))
(struct ecuc-container (short-name definition-ref xml-path
                                    sub-containers parameters references))
;; carries either a literal VALUE or a symbolic reference, never both
(struct ecuc-parameter (definition-ref value value-ref xml-path))
(struct ecuc-reference-value (definition-ref value-ref xml-path))

(define (read-modules root)
  (for/list ([element (in-list (element-descendants root))]
             #:when (string=? (arxml-element-name element)
                              "ECUC-MODULE-CONFIGURATION-VALUES"))
    (define short-name (element-child-text element "SHORT-NAME"))
    (define path (or short-name "(unnamed)"))
    (define containers
      (for/list ([containers-elem (in-list (element-children-named element "CONTAINERS"))])
        (for/list ([container (in-list (element-children-named containers-elem
                                                            "ECUC-CONTAINER-VALUE"))])
          (read-container container path))))
    (ecuc-module (or short-name "(unnamed)")
                 (element-child-text element "DEFINITION-REF")
                 path
                 (apply append containers))))

(define (read-container element parent-path)
  (define short-name (element-child-text element "SHORT-NAME"))
  (define path (string-append parent-path "/" (or short-name "(unnamed)")))

  (define parameters
    (let ([parameter-values (element-child element "PARAMETER-VALUES")])
      (if (not parameter-values)
          '()
          (for/list ([parameter (in-list (arxml-element-children parameter-values))]
                     #:when (param-value? (arxml-element-name parameter)))
            (ecuc-parameter
             (element-child-text parameter "DEFINITION-REF")
             (element-child-text parameter "VALUE")
             (or (element-child-text parameter "VALUE-REF")
                 (element-child-text parameter "SYMBOLIC-NAME-REFERENCE"))
             (string-append path "/" (leaf-of (element-child-text parameter
                                                                  "DEFINITION-REF"))))))))

  (define references
    (let ([reference-values (element-child element "REFERENCE-VALUES")])
      (if (not reference-values)
          '()
          (for/list ([reference (in-list (arxml-element-children reference-values))]
                     #:when (string=? (arxml-element-name reference)
                                      "ECUC-REFERENCE-VALUE"))
            (ecuc-reference-value
             (element-child-text reference "DEFINITION-REF")
             (or (element-child-text reference "VALUE-REF")
                 (element-child-text reference "SYMBOLIC-NAME-REFERENCE"))
             (string-append path "/" (leaf-of (element-child-text reference
                                                                  "DEFINITION-REF"))))))))

  (define sub-containers
    (let ([subs (element-child element "SUB-CONTAINERS")])
      (if (not subs)
          '()
          (for/list ([sub (in-list (element-children-named subs "ECUC-CONTAINER-VALUE"))])
            (read-container sub path)))))

  (ecuc-container (or short-name "(unnamed)")
                  (element-child-text element "DEFINITION-REF")
                  path
                  sub-containers
                  parameters
                  references))

(define (param-value? name)
  (define n (string-length name))
  (and (>= n 11)
       (string=? (substring name (- n 11)) "PARAM-VALUE")))

(define (leaf-of definition-ref)
  (if (or (not definition-ref) (= (string-length definition-ref) 0))
      "(no-definition-ref)"
      (let ([idx (find-last-slash definition-ref)])
        (if idx (substring definition-ref (add1 idx)) definition-ref))))

(define (find-last-slash s)
  (let loop ([i (- (string-length s) 1)] [found #f])
    (cond
      [(< i 0) found]
      [(char=? (string-ref s i) #\/) (or found i)]
      [else (loop (- i 1) (or found #f))])))

(define (module-total-containers m)
  (for/sum ([c (in-list (ecuc-module-containers m))])
    (add1 (container-total-sub-containers c))))

(define (module-total-parameters m)
  (for/sum ([c (in-list (ecuc-module-containers m))])
    (container-total-parameters c)))

(define (container-total-sub-containers c)
  (for/sum ([s (in-list (ecuc-container-sub-containers c))])
    (add1 (container-total-sub-containers s))))

(define (container-total-parameters c)
  (+ (length (ecuc-container-parameters c))
     (for/sum ([s (in-list (ecuc-container-sub-containers c))])
       (container-total-parameters s))))
