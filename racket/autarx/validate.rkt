#lang racket/base

;; Structural validation over projected ECUC models — port of
;; ValidationEngine.cs. Rule codes are part of the CLI contract — never
;; renumber, only append.

(require "ecuc.rkt")

(provide (struct-out diagnostic)
         validate-modules)

;; severity: 'warning 'error
(struct diagnostic (code severity message path))

(define (validate-modules modules)
  (define diagnostics (box '()))
  (define (add! code severity message path)
    (set-box! diagnostics
              (cons (diagnostic code severity message path) (unbox diagnostics))))

  (for ([module (in-list modules)])
    (when (string=? (ecuc-module-short-name module) "(unnamed)")
      (add! "ARX006" 'error "module configuration is missing SHORT-NAME"
            (ecuc-module-xml-path module)))
    (when (definition-missing? (ecuc-module-definition-ref module))
      (add! "ARX001" 'error "module configuration is missing DEFINITION-REF"
            (ecuc-module-xml-path module)))
    (validate-containers! add! (ecuc-module-containers module)
                           (ecuc-module-definition-ref module)))

  (reverse (unbox diagnostics)))

;; duplicate sibling check first (all groups at this level), then each
;; container in order — matches the C# emission order
(define (validate-containers! add! containers module-definition-ref)
  (define by-name (make-hash))
  (for ([c (in-list containers)])
    (hash-update! by-name (ecuc-container-short-name c)
                  (λ (l) (cons c l)) '()))
  (for ([(name group) (in-hash by-name)])
    (when (> (length group) 1)
      (for ([c (in-list group)])
        (add! "ARX003" 'error
              (format "duplicate sibling container SHORT-NAME \"~a\"" name)
              (ecuc-container-xml-path c)))))

  (for ([c (in-list containers)])
    (when (string=? (ecuc-container-short-name c) "(unnamed)")
      (add! "ARX006" 'error "container is missing SHORT-NAME"
            (ecuc-container-xml-path c)))
    (when (definition-missing? (ecuc-container-definition-ref c))
      (add! "ARX002" 'error "container is missing DEFINITION-REF"
            (ecuc-container-xml-path c)))
    (for ([p (in-list (ecuc-container-parameters c))])
      (when (and (not (ecuc-parameter-value p)) (not (ecuc-parameter-value-ref p)))
        (add! "ARX004" 'error "parameter has neither VALUE nor VALUE-REF"
              (ecuc-parameter-xml-path p)))
      (when (and module-definition-ref
                 (ecuc-parameter-definition-ref p)
                 (> (string-length (ecuc-parameter-definition-ref p)) 0)
                 (not (prefix? (ecuc-parameter-definition-ref p)
                               (string-append module-definition-ref "/"))))
        (add! "ARX005" 'warning
              (format "parameter DEFINITION-REF is outside module definition \"~a\""
                      module-definition-ref)
              (ecuc-parameter-xml-path p))))
    (validate-containers! add! (ecuc-container-sub-containers c) module-definition-ref)))

(define (definition-missing? d)
  (or (not d) (= (string-length d) 0)))

(define (prefix? s p)
  (define m (string-length p))
  (and (>= (string-length s) m) (string=? (substring s 0 m) p)))
