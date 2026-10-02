#lang racket/base

;; ECU-scoped impact analysis over a semantic diff — port of
;; ImpactAnalysis.cs. Relevance is closure over the union of both
;; deliveries' reference graphs, seeded at the ECU instance: anything the
;; ECU reaches, or that reaches it, in either delivery counts. Rule ids
;; (ARX-IMP-*) are a contract: never renamed, only appended.

(require racket/list
         "index.rkt"
         "diff.rkt")

(provide (struct-out impact-finding)
         (struct-out communication-impact)
         (struct-out impact-report)
         analyze-impact)

(struct impact-finding (rule severity object-path detail)) ; severity: 'breaking 'info
(struct communication-impact (changes signal-count pdu-count frame-count cluster-count))
(struct impact-report (ecu ecu-path ecu-removed closure-size relevant-count
                       unrelated-count breaking-count findings
                       relevant-changes communication))

;; Breadth-first closure over the union graph of both deliveries. Edges
;; only join the closure when their far endpoint is an indexed object on
;; that side, so unresolved targets never inflate the set.
(define (closure before after seed)
  (define before-paths (make-hash))
  (for ([o (in-list (workspace-index-objects before))])
    (hash-set! before-paths (semantic-object-absolute-path o) #t))
  (define after-paths (make-hash))
  (for ([o (in-list (workspace-index-objects after))])
    (hash-set! after-paths (semantic-object-absolute-path o) #t))

  (define out (make-hash))
  (hash-set! out seed #t)
  (define queue (list seed))
  (let loop ()
    (unless (null? queue)
      (define current (car queue))
      (define neighbours
        (append (known-neighbours before current before-paths)
                (known-neighbours after current after-paths)))
      (for ([n (in-list neighbours)])
        (unless (hash-ref out n #f)
          (hash-set! out n #t)
          (set! queue (append queue (list n)))))
      (set! queue (cdr queue))
      (loop)))
  out)

(define (known-neighbours index path known)
  (append
   (for/list ([r (in-list (outgoing-of index path))]
              #:when (hash-ref known (arx-reference-target-path r) #f))
     (arx-reference-target-path r))
   (for/list ([r (in-list (incoming-of index path))]
              #:when (hash-ref known (arx-reference-source-path r) #f))
     (arx-reference-source-path r))))

(define (find-object-detail change)
  (format "~a '~a' was added and connects to the ECU's closure"
          (or (object-change-element-type change) "?")
          (object-change-short-name change)))

(define (analyze-impact before after ecu-path ecu-removed? diff
                        [include-property-detail? #f])
  (define closure-set (closure before after ecu-path))

  (define relevant
    (filter (λ (c) (hash-ref closure-set (object-change-absolute-path c) #f))
            (workspace-diff-objects diff)))
  (define unrelated-count (- (length (workspace-diff-objects diff))
                             (length relevant)))

  (define object-findings
    (for/list ([change (in-list relevant)])
      (case (object-change-change-type change)
        [(added)
         (impact-finding "ARX-IMP-OBJECT-ADDED" 'info
                         (object-change-absolute-path change)
                         (find-object-detail change))]
        [(removed)
         (define had-incoming?
           (not (null? (incoming-of before (object-change-absolute-path change)))))
         (if had-incoming?
             (impact-finding "ARX-IMP-REMOVED-REFERENCED" 'breaking
                             (object-change-absolute-path change)
                             (format "~a '~a' was removed while other objects referenced it"
                                     (or (object-change-element-type change) "?")
                                     (object-change-short-name change)))
             (impact-finding "ARX-IMP-OBJECT-REMOVED" 'info
                             (object-change-absolute-path change)
                             (format "~a '~a' was removed (no references pointed at it)"
                                     (or (object-change-element-type change) "?")
                                     (object-change-short-name change))))]
        [(modified)
         (cond
           [(object-change-element-type-changed? change)
            (impact-finding "ARX-IMP-TYPE-CHANGED" 'breaking
                            (object-change-absolute-path change)
                            (format "element type changed ~a → ~a under the same identity"
                                    (object-change-before-element-type change)
                                    (object-change-after-element-type change)))]
           [(and include-property-detail?
                 (object-change-properties change)
                 (not (null? (object-change-properties change))))
            (define props (object-change-properties change))
            (define shown (take-up-to props 5))
            (impact-finding
             "ARX-IMP-PROPERTY-CHANGED" 'info
             (object-change-absolute-path change)
             (string-append
              (format "~a property change(s): " (length props))
              (string-join
               (map (λ (p)
                      (format "~a: ~a → ~a"
                              (property-change-path p)
                              (or (property-change-before p) "(absent)")
                              (or (property-change-after p) "(absent)")))
                    shown)
               "; ")
              (if (> (length props) 5) "; …" "")))]
           [else
            (impact-finding "ARX-IMP-OBJECT-MODIFIED" 'info
                            (object-change-absolute-path change)
                            (format "content changed under ~a '~a'"
                                    (or (object-change-element-type change) "?")
                                    (object-change-short-name change)))])]
        [else #f])))

  (define reference-findings
    (for/list ([reference (in-list (workspace-diff-references diff))])
      (define source-relevant?
        (hash-ref closure-set (reference-change-source-path reference) #f))
      (define target-relevant?
        (hash-ref closure-set (reference-change-target-path reference) #f))
      (and (or source-relevant? target-relevant?)
           (let* ([touches-ecu?
                   (or (string=? (reference-change-source-path reference) ecu-path)
                       (string=? (reference-change-target-path reference) ecu-path))]
                  [severity (if (and (eq? (reference-change-change-type reference) 'removed)
                                     touches-ecu?)
                                'breaking
                                'info)]
                  [rule (if (eq? (reference-change-change-type reference) 'added)
                            "ARX-IMP-REF-ADDED"
                            "ARX-IMP-REF-REMOVED")]
                  [verb (if (eq? (reference-change-change-type reference) 'added)
                            "added" "removed")])
             (impact-finding rule severity
                             (reference-change-source-path reference)
                             (format "~a ~a: ~a"
                                     (reference-change-kind reference)
                                     verb
                                     (reference-change-target-path reference)))))))

  (define findings
    (sort (filter values (append object-findings reference-findings))
          (λ (a b)
            (define sa (severity-rank (impact-finding-severity a)))
            (define sb (severity-rank (impact-finding-severity b)))
            (if (= sa sb)
                (string<? (impact-finding-object-path a) (impact-finding-object-path b))
                (< sa sb)))))

  (define communication-kinds '(signal pdu frame communication-cluster))
  (define communication-changes
    (filter (λ (c) (memq (object-change-kind c) communication-kinds)) relevant))

  (impact-report
   (last-segment-of ecu-path)
   ecu-path
   ecu-removed?
   (hash-count closure-set)
   (length relevant)
   unrelated-count
   (count-where (λ (f) (eq? (impact-finding-severity f) 'breaking)) findings)
   findings
   relevant
   (communication-impact
    communication-changes
    (count-where (λ (c) (eq? (object-change-kind c) 'signal)) communication-changes)
    (count-where (λ (c) (eq? (object-change-kind c) 'pdu)) communication-changes)
    (count-where (λ (c) (eq? (object-change-kind c) 'frame)) communication-changes)
    (count-where (λ (c) (eq? (object-change-kind c) 'communication-cluster))
                 communication-changes))))

(define (severity-rank s) (if (eq? s 'breaking) 0 1))

(define (last-segment-of path)
  (define idx
    (let loop ([i (- (string-length path) 1)] [found #f])
      (cond
        [(< i 0) found]
        [(char=? (string-ref path i) #\/) (or found i)]
        [else (loop (- i 1) (or found #f))])))
  (if idx (substring path (add1 idx)) path))

(define (take-up-to lst n)
  (if (> (length lst) n) (take lst n) lst))

(define (count-where pred lst)
  (for/sum ([x (in-list lst)] #:when (pred x)) 1))

(define (string-join lst sep)
  (cond
    [(null? lst) ""]
    [(null? (cdr lst)) (car lst)]
    [else (string-append (car lst) sep (string-join (cdr lst) sep))]))
