#lang racket/base

;; The semantic view of one workspace — port of WorkspaceIndexBuilder.cs,
;; WorkspaceIndex.cs and WorkspaceSummary.cs. Documents, identifiables by
;; absolute path, forward/reverse reference graphs, duplicate paths, file
;; errors, resolution (ambiguity is a first-class outcome), find, BFS trace
;; and the derived summary. CLI, MCP and the GUI RPC surface all read through
;; this module; none of them re-parses ARXML.

(require racket/file
         racket/path
         "arxml.rkt"
         "classify.rkt"
         "hash.rkt"
         "release.rkt")

(provide (struct-out semantic-object)
         (struct-out arx-reference)
         (struct-out workspace-document)
         (struct-out duplicate-path)
         (struct-out file-error)
         (struct-out workspace-index)
         (struct-out resolution)
         (struct-out trace-node)
         (struct-out trace-edge)
         (struct-out trace-result)
         (struct-out workspace-summary)
         build-workspace-index
         resolve-object
         find-by-name
         outgoing-of
         incoming-of
         trace
         summarize
         count-where)

;; ---------------------------------------------------------------- models

;; Identity is the AUTOSAR absolute short-name path — never the XML line
;; number. content-hash is the FNV-1a subtree hash captured at index time.
(struct semantic-object (short-name element-type kind absolute-path source-file content-hash))

;; A *-REF / *-TREF capture; resolved is set during the build.
(struct arx-reference (kind source-path source-element-path target-path source-file resolved))

(struct workspace-document (file-path        ; absolute, as resolved at build
                            relative-path     ; stable cross-workspace identity
                            file-size-bytes
                            namespace         ; string | #f
                            schema-location   ; string | #f
                            autosar-release   ; string | #f
                            element-count
                            identifiable-count
                            package-count
                            named-element-count
                            semantic-counts)) ; hasheq kind -> count, unknown excluded

(struct duplicate-path (absolute-path source-files))
(struct file-error (file-path message line-number))

(struct workspace-index (source-path
                         documents
                         objects
                         references
                         duplicate-paths
                         file-errors
                         package-count))

(struct resolution (status object candidates)) ; 'found 'not-found 'ambiguous
(struct trace-node (path depth))
(struct trace-edge (source-path kind target-path depth))
(struct trace-result (root direction max-depth nodes edges))
(struct workspace-summary (file-count
                           total-file-bytes
                           total-element-count
                           identifiable-count
                           package-count
                           reference-count
                           unresolved-reference-count
                           duplicate-path-count
                           file-error-count
                           namespaces
                           schema-locations
                           autosar-release
                           mixed-schema
                           semantic-counts      ; list of (kind . count), enum order
                           unknown-named-count))

;; ---------------------------------------------------------------- helpers

(define (basename p) (path->string (file-name-from-path p)))

(define (prefix? s p)
  (define m (string-length p))
  (and (>= (string-length s) m) (string=? (substring s 0 m) p)))

(define (suffix? s suf)
  (define n (string-length s))
  (define m (string-length suf))
  (and (>= n m) (string=? (substring s (- n m)) suf)))

(define (contains? hay needle)
  (define n (string-length hay))
  (define m (string-length needle))
  (if (or (= m 0) (> m n))
      (= m 0)
      (let loop ([i 0])
        (cond [(> i (- n m)) #f]
              [(string=? (substring hay i (+ i m)) needle) #t]
              [else (loop (add1 i))]))))

;; ---------------------------------------------------------------- build

(define (arxml-files-in dir)
  ;; *.arxml recursive, exact lowercase extension, full paths Ordinal-sorted
  (sort (for/list ([p (in-list (find-files (λ (p)
                                              (and (file-exists? p)
                                                   (equal? (filename-extension p) #"arxml")))
                                            dir))])
          (path->string p))
        string<?))

(define (relative-to base file)
  ;; the two shapes Path.GetRelativePath sees here: the same file (single
  ;; file mode) or a file under the base directory
  (if (string=? base file)
      (basename file)
      (substring file (+ (string-length base) 1))))

(define (count-elements root)
  ;; 1 + descendant count, without materializing the descendant list
  (let loop ([e root]) (add1 (for/sum ([c (in-list (arxml-element-children e))]) (loop c)))))

(define (is-reference-element? e)
  (define name (arxml-element-name e))
  (define text (arxml-element-text e))
  (and (suffix? name "REF")
       (not (string=? name "DEFINITION-REF"))
       text
       (> (string-length text) 0)
       (char=? (string-ref text 0) #\/)
       #t))

;; per-file walk state: a mutable hash for accumulation
(define (make-state file)
  (make-hash (list (cons 'file file)
                   (cons 'objects '())
                   (cons 'references '())
                   (cons 'package-count 0)
                   (cons 'named-count 0)
                   (cons 'kind-counts (make-hasheq)))))

(define (collect-file! file relative-path st)
  (define root (parse-arxml-file file))
  (define namespace (element-attribute root "xmlns"))
  (define schema-location (element-attribute root "schemaLocation"))

  (define packages (element-child root "AR-PACKAGES"))
  (when packages
    (for ([pkg (in-list (element-children-named packages "AR-PACKAGE"))])
      (visit-package! pkg "" st)))

  (hash-set! st 'document
             (workspace-document
              file
              relative-path
              (file-size (string->path file))
              namespace
              schema-location
              (derive-autosar-release schema-location)
              (count-elements root)
              (length (hash-ref st 'objects))
              (hash-ref st 'package-count)
              (hash-ref st 'named-count)
              (hash-ref st 'kind-counts))))

(define (visit-package! package parent-path st)
  (hash-update! st 'package-count add1 0)
  (define short-name (element-child-text package "SHORT-NAME"))
  (define package-path (string-append parent-path "/" (or short-name "(unnamed)")))
  (define elements (element-child package "ELEMENTS"))
  (when elements
    (for ([element (in-list (arxml-element-children elements))])
      (visit-identifiable! element package-path st)))
  (define sub-packages (element-child package "AR-PACKAGES"))
  (when sub-packages
    (for ([sub (in-list (element-children-named sub-packages "AR-PACKAGE"))])
      (visit-package! sub package-path st))))

(define (visit-identifiable! element package-path st)
  (define short-name (element-child-text element "SHORT-NAME"))
  ;; schema-invalid identifiable: nothing to index under
  (when short-name
    (define path (string-append package-path "/" short-name))
    (define kind (classify (arxml-element-name element)))
    (hash-update! st 'objects
                  (λ (l)
                    (cons (semantic-object short-name
                                            (arxml-element-name element)
                                            kind
                                            path
                                            (hash-ref st 'file)
                                            (content-hash element))
                          l))
                  '())
    (hash-update! st 'named-count add1 0)
    (unless (eq? kind 'unknown)
      (hash-update! (hash-ref st 'kind-counts) kind add1 0))
    (visit-content! element path "" st)))

;; Walks everything below an identifiable: *REF children become references
;; attributed to the owning object; nested SHORT-NAMEs lengthen the
;; attribution chain and add semantic counts.
(define (visit-content! element owner-path nested-chain st)
  (for ([child (in-list (arxml-element-children element))])
    (cond
      [(is-reference-element? child)
       (hash-update! st 'references
                     (λ (l)
                       (cons (arx-reference (arxml-element-name child)
                                            owner-path
                                            (string-append owner-path nested-chain)
                                            (arxml-element-text child)
                                            (hash-ref st 'file)
                                            #f)
                             l))
                     '())]
      [else
       (define child-short-name (element-child-text child "SHORT-NAME"))
       (when child-short-name
         (define kind (classify (arxml-element-name child)))
         (hash-update! st 'named-count add1 0)
         (unless (eq? kind 'unknown)
           (hash-update! (hash-ref st 'kind-counts) kind add1 0)))
       (visit-content! child owner-path
                       (if child-short-name
                           (string-append nested-chain "/" child-short-name)
                           nested-chain)
                       st)])))

(define (build-workspace-index path)
  (define full-path (simplify-path (path->complete-path path)))
  (define full-str (path->string full-path))
  (define single-file? (file-exists? full-path))
  (define dir? (directory-exists? full-path))

  (unless (or single-file? dir?)
    (raise-arguments-error 'build-workspace-index "workspace path not found" "path" full-str))

  ;; an explicitly named file is a hard input: parse errors abort; in a
  ;; directory scan one broken file must not sink the workspace
  (define files (if single-file? (list full-str) (arxml-files-in full-path)))

  (define documents '())
  (define objects '())
  (define references '())
  (define file-errors '())
  (define package-count 0)

  (for ([file (in-list files)])
    (define st (make-state file))
    (define ok?
      (with-handlers ([exn:fail:autarx:parse?
                       (λ (ex)
                         (when single-file? (raise ex))
                         (set! file-errors
                               (cons (file-error file
                                                 (exn-message ex)
                                                 (exn:fail:autarx:parse-line ex))
                                     file-errors))
                         #f)]
                      [exn:fail:filesystem?
                       (λ (ex)
                         (when single-file? (raise ex))
                         (set! file-errors
                               (cons (file-error file (exn-message ex) 0) file-errors))
                         #f)])
        (collect-file! file (relative-to full-str file) st)
        #t))
    (when ok?
      (set! documents (cons (hash-ref st 'document) documents))
      (set! objects (append (reverse (hash-ref st 'objects)) objects))
      (set! references (append (reverse (hash-ref st 'references)) references))
      (set! package-count (+ package-count (hash-ref st 'package-count)))))

  (define sorted-objects
    (sort objects
          (λ (a b)
            (define pa (semantic-object-absolute-path a))
            (define pb (semantic-object-absolute-path b))
            (if (string=? pa pb)
                (string<? (semantic-object-source-file a) (semantic-object-source-file b))
                (string<? pa pb)))))

  (define path-index (make-hash))
  (for ([o (in-list sorted-objects)])
    (hash-set! path-index (semantic-object-absolute-path o) #t))

  (define sorted-references
    (sort (for/list ([r (in-list references)])
             (struct-copy arx-reference r
                          [resolved (hash-ref path-index (arx-reference-target-path r) #f)]))
          ref<?))

  ;; duplicate absolute paths: distinct source files per path, sorted
  (define by-path (make-hash))
  (for ([o (in-list sorted-objects)])
    (hash-update! by-path
                  (semantic-object-absolute-path o)
                  (λ (files) (cons (semantic-object-source-file o) files))
                  '()))
  (define duplicates
    (sort (for/list ([(path files) (in-hash by-path)] #:when (> (length files) 1))
            (duplicate-path path
                            (sort (remove-dups files string=?) string<?)))
          string<?
          #:key duplicate-path-absolute-path))

  (workspace-index
   full-str
   (sort documents string<? #:key workspace-document-file-path)
   sorted-objects
   sorted-references
   duplicates
   (sort file-errors string<? #:key file-error-file-path)
   package-count))

;; (source, target, kind, file) ordinal ordering without per-comparison
;; allocations — the C# ThenBy chain
(define (ref<? a b)
  (define a-src (arx-reference-source-path a))
  (define b-src (arx-reference-source-path b))
  (cond
    [(not (string=? a-src b-src)) (string<? a-src b-src)]
    [else
     (define a-tgt (arx-reference-target-path a))
     (define b-tgt (arx-reference-target-path b))
     (cond
       [(not (string=? a-tgt b-tgt)) (string<? a-tgt b-tgt)]
       [else
        (define a-kind (arx-reference-kind a))
        (define b-kind (arx-reference-kind b))
        (if (not (string=? a-kind b-kind))
            (string<? a-kind b-kind)
            (string<? (arx-reference-source-file a)
                      (arx-reference-source-file b)))])]))

(define (remove-dups lst same?)
  (let loop ([lst lst] [seen '()] [out '()])
    (cond
      [(null? lst) (reverse out)]
      [(for/first ([s (in-list seen)] #:when (same? s (car lst))) #t)
       (loop (cdr lst) seen out)]
      [else (loop (cdr lst) (cons (car lst) seen) (cons (car lst) out))])))

;; ---------------------------------------------------------------- queries

;; Resolves an absolute path (/Vehicle/Signals/VehicleSpeed) or a plain
;; SHORT-NAME. Short names are only unique per workspace — ambiguity is a
;; first-class outcome, never silently resolved.
(define (resolve-object index name-or-path)
  (if (prefix? name-or-path "/")
      (let loop ([objs (workspace-index-objects index)])
        (cond
          [(null? objs) (resolution 'not-found #f '())]
          [(string=? (semantic-object-absolute-path (car objs)) name-or-path)
           (resolution 'found (car objs) '())]
          [else (loop (cdr objs))]))
      (let ([matches (sort (filter (λ (o) (string=? (semantic-object-short-name o)
                                                    name-or-path))
                                   (workspace-index-objects index))
                           string<?
                           #:key semantic-object-absolute-path)])
        (cond
          [(null? matches) (resolution 'not-found #f '())]
          [(null? (cdr matches)) (resolution 'found (car matches) '())]
          [else (resolution 'ambiguous #f matches)]))))

;; Case-insensitive SHORT-NAME substring search, ordered by path.
(define (find-by-name index pattern)
  (define lower (string-downcase pattern))
  (sort (filter (λ (o) (contains-ci? (semantic-object-short-name o) lower))
                (workspace-index-objects index))
        string<?
        #:key semantic-object-absolute-path))

(define (contains-ci? s lower-pattern)
  (contains? (string-downcase s) lower-pattern))

(define (outgoing-of index path)
  (filter (λ (r) (string=? (arx-reference-source-path r) path))
          (workspace-index-references index)))

(define (incoming-of index path)
  (filter (λ (r) (string=? (arx-reference-target-path r) path))
          (workspace-index-references index)))

;; Breadth-first walk over the reference graph, at most max-depth hops from
;; the root. Each object is visited once; edges carry the depth of the node
;; they were expanded from. Purely generic — no AUTOSAR semantics inside.
(define (trace index root-path direction max-depth0)
  (define max-depth (max 0 max-depth0))
  (define outgoing? (memq direction '(outgoing both)))
  (define incoming? (memq direction '(incoming both)))

  (define nodes (list (trace-node root-path 0)))
  (define edges '())
  (define visited (make-hash))
  (hash-set! visited root-path #t)
  (define queue (list (cons root-path 0)))

  (let loop ()
    (unless (or (null? queue) (>= (cdar queue) max-depth))
      (define current-path (caar queue))
      (define depth (cdar queue))
      (define next-depth (add1 depth))
      (define (visit! source kind target neighbour)
        (set! edges (cons (trace-edge source kind target next-depth) edges))
        (unless (hash-ref visited neighbour #f)
          (hash-set! visited neighbour #t)
          (set! nodes (cons (trace-node neighbour next-depth) nodes))
          (set! queue (append queue (list (cons neighbour next-depth))))))
      (when outgoing?
        (for ([r (in-list (outgoing-of index current-path))])
          (visit! (arx-reference-source-path r)
                  (arx-reference-kind r)
                  (arx-reference-target-path r)
                  (arx-reference-target-path r))))
      (when incoming?
        (for ([r (in-list (incoming-of index current-path))])
          (visit! (arx-reference-source-path r)
                  (arx-reference-kind r)
                  (arx-reference-target-path r)
                  (arx-reference-source-path r))))
      (set! queue (cdr queue))
      (loop)))

  (trace-result root-path direction max-depth (reverse nodes) (reverse edges)))

;; ---------------------------------------------------------------- summary

(define (summarize index)
  (define documents (workspace-index-documents index))
  (define namespaces
    (sort (remove-dups
           (filter values (map workspace-document-namespace documents)) string=?)
          string<?))
  (define schema-locations
    (sort (remove-dups
           (filter (λ (s) (and s (> (string-length s) 0)))
                   (map workspace-document-schema-location documents))
           string=?)
          string<?))
  (define releases
    (remove-dups (filter values (map workspace-document-autosar-release documents))
                 string=?))
  ;; a workspace mixing schema flavours cannot be summarised as one release
  (define release (if (= (length releases) 1) (car releases) #f))
  (define mixed-schema (or (> (length namespaces) 1)
                           (> (length schema-locations) 1)
                           (> (length releases) 1)))

  (define counts
    (for/list ([kind (in-list '(system ecu communication-cluster frame pdu signal
                                       software-component port port-interface ecuc-module))])
      (define total
        (for/sum ([d (in-list documents)])
          (hash-ref (workspace-document-semantic-counts d) kind 0)))
      (and (> total 0) (cons kind total))))
  (define present (filter values counts))
  (define named-total (for/sum ([d (in-list documents)])
                        (workspace-document-named-element-count d)))
  (define known-total (for/sum ([p (in-list present)]) (cdr p)))

  (workspace-summary
   (length documents)
   (for/sum ([d (in-list documents)]) (workspace-document-file-size-bytes d))
   (for/sum ([d (in-list documents)]) (workspace-document-element-count d))
   (length (workspace-index-objects index))
   (workspace-index-package-count index)
   (length (workspace-index-references index))
   (count-where (λ (r) (not (arx-reference-resolved r))) (workspace-index-references index))
   (length (workspace-index-duplicate-paths index))
   (length (workspace-index-file-errors index))
   namespaces
   schema-locations
   release
   mixed-schema
   present
   (- named-total known-total)))

(define (count-where pred lst)
  (for/sum ([x (in-list lst)] #:when (pred x)) 1))
