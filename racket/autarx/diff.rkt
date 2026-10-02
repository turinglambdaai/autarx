#lang racket/base

;; Engineering diff between two workspaces — port of SemanticDiff.cs.
;; Objects are matched by absolute short-name path (never by file
;; position); modified detection uses the index-time content hashes, so
;; nothing beyond the two indexes is needed to know *that* something
;; changed. Property-level *explanations* re-read the affected source
;; files on demand and compare the object subtrees.

(require racket/file
         racket/string
         racket/list
         "arxml.rkt"
         "classify.rkt"
         "index.rkt")

(provide (struct-out diff-side)
         (struct-out object-change)
         (struct-out property-change)
         (struct-out reference-change)
         (struct-out file-change)
         (struct-out workspace-diff)
         compare-workspaces
         workspace-diff-empty?
         object-change-element-type-changed?
         object-change-element-type
         find-object-element
         compare-elements
         key-children-public)

(struct diff-side (path document-count object-count reference-count))

;; change: 'added 'removed 'modified
(struct object-change (change-type short-name kind absolute-path
                                   before-element-type    ; string | #f
                                   after-element-type     ; string | #f
                                   before-source-file     ; string | #f
                                   after-source-file      ; string | #f
                                   properties))           ; (listof property-change) | #f
(struct property-change (path change before after))
(struct reference-change (change-type source-path kind target-path source-file))
(struct file-change (change-type relative-path before-size-bytes after-size-bytes))
(struct workspace-diff (before after objects references files))

(define (workspace-diff-empty? d)
  (and (null? (workspace-diff-objects d))
       (null? (workspace-diff-references d))
       (null? (workspace-diff-files d))))

;; True when the element type — and possibly the semantic kind — changed
;; under the same identity, e.g. an SW component type retyped.
(define (object-change-element-type-changed? c)
  (and (object-change-before-element-type c)
       (object-change-after-element-type c)
       (not (string=? (object-change-before-element-type c)
                      (object-change-after-element-type c)))))

;; The element type as of the side the change belongs to (after for
;; added/modified, before for removed).
(define (object-change-element-type c)
  (if (eq? (object-change-change-type c) 'removed)
      (object-change-before-element-type c)
      (or (object-change-after-element-type c)
          (object-change-before-element-type c))))

;; ---- by-path projection: first occurrence wins (files in sorted order)

(define (index-by-path idx)
  (define h (make-hash))
  (for ([o (in-list (workspace-index-objects idx))])
    (define p (semantic-object-absolute-path o))
    (unless (hash-ref h p #f) (hash-set! h p o)))
  h)

(define (index-refs-by-key idx)
  (define h (make-hash))
  (for ([r (in-list (workspace-index-references idx))])
    (define key (list (arx-reference-source-path r)
                      (arx-reference-kind r)
                      (arx-reference-target-path r)))
    (unless (hash-ref h key #f) (hash-set! h key r)))
  h)

(define (make-object-change type before after)
  (define source (or after before))
  (object-change
   type
   (semantic-object-short-name source)
   (semantic-object-kind source)
   (semantic-object-absolute-path source)
   (and before (semantic-object-element-type before))
   (and after (semantic-object-element-type after))
   (and before (semantic-object-source-file before))
   (and after (semantic-object-source-file after))
   #f))

(define (compare-workspaces before after [include-property-detail? #f])
  (define before-by-path (index-by-path before))
  (define after-by-path (index-by-path after))

  (define changes
    (for/list ([(path before-object) (in-hash before-by-path)])
      (define after-object (hash-ref after-by-path path #f))
      (cond
        [(not after-object) (make-object-change 'removed before-object #f)]
        [(and (= (semantic-object-content-hash before-object)
                 (semantic-object-content-hash after-object))
              (string=? (semantic-object-element-type before-object)
                        (semantic-object-element-type after-object)))
         #f]
        [else (make-object-change 'modified before-object after-object)])))

  (define added
    (for/list ([(path after-object) (in-hash after-by-path)]
               #:unless (hash-ref before-by-path path #f))
      (make-object-change 'added #f after-object)))

  (define objects0
    (sort (filter values (append changes added))
          string<?
          #:key object-change-absolute-path))

  ;; property-level explanations re-read both source files
  (define parsed-files (make-hash))
  (define objects
    (if include-property-detail?
        (for/list ([c (in-list objects0)])
          (if (not (eq? (object-change-change-type c) 'modified))
              c
              (let ([before-element (load-object-element
                                     (object-change-before-source-file c)
                                     (object-change-absolute-path c)
                                     parsed-files)]
                    [after-element (load-object-element
                                    (object-change-after-source-file c)
                                    (object-change-absolute-path c)
                                    parsed-files)])
                (if (or (not before-element) (not after-element))
                    c
                    (struct-copy object-change c
                                 [properties (compare-elements before-element
                                                               after-element
                                                               '())])))))
        objects0))

  (workspace-diff
   (diff-side (workspace-index-source-path before)
              (length (workspace-index-documents before))
              (length (workspace-index-objects before))
              (length (workspace-index-references before)))
   (diff-side (workspace-index-source-path after)
              (length (workspace-index-documents after))
              (length (workspace-index-objects after))
              (length (workspace-index-references after)))
   objects
   (reference-changes before after)
   (file-changes before after)))

;; ---- reference diff: identity is (source, kind, target); a reference
;; moving between files is not a change

(define (reference-changes before after)
  (define before-refs (index-refs-by-key before))
  (define after-refs (index-refs-by-key after))
  (define removed
    (for/list ([(key r) (in-hash before-refs)]
               #:unless (hash-ref after-refs key #f))
      (reference-change 'removed
                        (arx-reference-source-path r)
                        (arx-reference-kind r)
                        (arx-reference-target-path r)
                        (arx-reference-source-file r))))
  (define added
    (for/list ([(key r) (in-hash after-refs)]
               #:unless (hash-ref before-refs key #f))
      (reference-change 'added
                        (arx-reference-source-path r)
                        (arx-reference-kind r)
                        (arx-reference-target-path r)
                        (arx-reference-source-file r))))
  (sort (append removed added)
        (λ (a b)
          (define a-src (reference-change-source-path a))
          (define b-src (reference-change-source-path b))
          (cond
            [(not (string=? a-src b-src)) (string<? a-src b-src)]
            [(not (string=? (reference-change-target-path a)
                            (reference-change-target-path b)))
             (string<? (reference-change-target-path a) (reference-change-target-path b))]
            [else (string<? (reference-change-kind a) (reference-change-kind b))]))))

;; ---- file diff: relative path identity, size-based modified

(define (file-changes before after)
  (define before-files (make-hash))
  (for ([d (in-list (workspace-index-documents before))])
    (hash-set! before-files (workspace-document-relative-path d) d))
  (define after-files (make-hash))
  (for ([d (in-list (workspace-index-documents after))])
    (hash-set! after-files (workspace-document-relative-path d) d))

  (define removed
    (for/list ([(rel d) (in-hash before-files)]
               #:unless (hash-ref after-files rel #f))
      (file-change 'removed rel (workspace-document-file-size-bytes d) #f)))
  (define added-or-modified
    (for/list ([(rel d) (in-hash after-files)])
      (define bd (hash-ref before-files rel #f))
      (cond
        [(not bd) (file-change 'added rel #f (workspace-document-file-size-bytes d))]
        [(= (workspace-document-file-size-bytes bd)
            (workspace-document-file-size-bytes d))
         #f]
        [else (file-change 'modified rel
                           (workspace-document-file-size-bytes bd)
                           (workspace-document-file-size-bytes d))])))

  (sort (filter values (append removed added-or-modified))
        string<?
        #:key file-change-relative-path))

;; ---- property-level explanation

;; Sibling identity inside one parent: SHORT-NAME wins, then the
;; DEFINITION-REF's last segment, then the element name. Repeated keys get
;; a 1-based occurrence suffix in document order on both sides.
(define (key-children e)
  (define seen (make-hash))
  (define result (make-hash))
  (for ([child (in-list (arxml-element-children e))])
    (define segment (element-child-text child "SHORT-NAME"))
    (define key0
      (cond
        [(and segment (> (string-length segment) 0)) segment]
        [else
         (define definition (element-child-text child "DEFINITION-REF"))
         (cond
           [(and definition (> (string-length definition) 0))
            (last-segment (trim-trailing-slashes definition))]
           [else (arxml-element-name child)])]))
    (define key
      (let next ([k key0] [occ 2])
        (if (hash-ref seen k #f)
            (next (format "~a[~a]" key0 occ) (add1 occ))
            (begin (hash-set! seen k #t) k))))
    (hash-set! result key child))
  result)

(define (trim-trailing-slashes s)
  (define n (string-length s))
  (let loop ([end n])
    (if (and (> end 0) (char=? (string-ref s (- end 1)) #\/))
        (loop (- end 1))
        (substring s 0 end))))

(define (last-segment path)
  (define idx
    (let loop ([i (- (string-length path) 1)] [found #f])
      (cond
        [(< i 0) found]
        [(char=? (string-ref path i) #\/) (or found i)]
        [else (loop (- i 1) (or found #f))])))
  (if idx (substring path (add1 idx)) path))

(define (describe-element e)
  (define short-name (element-child-text e "SHORT-NAME"))
  (define label (if short-name
                    (format "~a '~a'" (arxml-element-name e) short-name)
                    (arxml-element-name e)))
  (define text (arxml-element-text e))
  (if text (format "~a = ~a" label text) label))

(define (segment-path context leaf)
  (if (null? context)
      leaf
      (string-append (string-join context "/") "/" leaf)))

(define (string-join lst sep)
  (cond
    [(null? lst) ""]
    [(null? (cdr lst)) (car lst)]
    [else (string-append (car lst) sep (string-join (cdr lst) sep))]))

(define (string-trim-nullable s)
  (if s (string-trim-both s) #f))

(define (string-trim-both s)
  (define n (string-length s))
  (define start
    (let loop ([i 0]) (if (and (< i n) (char-whitespace? (string-ref s i))) (loop (add1 i)) i)))
  (define end
    (let loop ([i (- n 1)]) (if (and (>= i start) (char-whitespace? (string-ref s i))) (loop (- i 1)) i)))
  (if (>= end start) (substring s start (add1 end)) ""))

;; Compares two matched object subtrees; context already carries the
;; segments identifying the current element.
(define (compare-elements before after context)
  (define before-text (string-trim-nullable (arxml-element-text before)))
  (define after-text (string-trim-nullable (arxml-element-text after)))
  ;; null == null is equal (C# string.Equals on two nulls); values are
  ;; reported raw, comparison happens on trimmed text
  (define text-differs?
    (cond
      [(and (not before-text) (not after-text)) #f]
      [(not before-text) #t]
      [(not after-text) #t]
      [else (not (string=? before-text after-text))]))
  (define head
    (if text-differs?
        (list (property-change
               (if (null? context)
                   (arxml-element-name before)
                   (string-join context "/"))
               'changed
               (arxml-element-text before)
               (arxml-element-text after)))
        '()))

  (define before-children (key-children before))
  (define after-children (key-children after))

  (define removed-and-matched
    (for/list ([(key before-child) (in-hash before-children)])
      (define after-child (hash-ref after-children key #f))
      (if (not after-child)
          (list (property-change (segment-path context key)
                                 'removed
                                 (describe-element before-child)
                                 #f))
          (compare-elements before-child after-child (append context (list key))))))

  (define added
    (for/list ([(key after-child) (in-hash after-children)]
               #:unless (hash-ref before-children key #f))
      (property-change (segment-path context key)
                       'added
                       #f
                       (describe-element after-child))))

  (append head (apply append removed-and-matched) added))

;; Locates a package-level identifiable by absolute short-name path — the
;; mirror of the index walk: every segment but the last names an
;; AR-PACKAGE, the last names an element in the innermost package.
(define (find-object-element root absolute-path)
  (define segments
    (filter (λ (s) (> (string-length s) 0))
            (string-split absolute-path "/" #:trim? #f)))
  (and (pair? segments)
       (let loop ([pkg-segs (drop-right segments 1)]
                  [container (element-child root "AR-PACKAGES")]
                  [last-package #f])
         (cond
           [(null? pkg-segs)
            (define elements (and last-package (element-child last-package "ELEMENTS")))
            (and elements
                 (for/first ([e (in-list (arxml-element-children elements))]
                             #:when (string=? (or (element-child-text e "SHORT-NAME") "")
                                              (last segments)))
                   e))]
           [else
            (define package
              (let scan ([pkgs (if container
                                   (element-children-named container "AR-PACKAGE")
                                   '())])
                (cond
                  [(null? pkgs) #f]
                  [(string=? (or (element-child-text (car pkgs) "SHORT-NAME") "")
                             (car pkg-segs))
                   (car pkgs)]
                  [else (scan (cdr pkgs))])))
            (and package
                 (loop (cdr pkg-segs)
                       (element-child package "AR-PACKAGES")
                       package))]))))

(define (load-object-element source-file absolute-path parsed-files)
  (unless (hash-ref parsed-files source-file #f)
    (hash-set! parsed-files source-file
               (and (file-exists? source-file)
                    (parse-arxml-file source-file))))
  (define root (hash-ref parsed-files source-file))
  (and root (find-object-element root absolute-path)))

(define (key-children-public e) (key-children e))
