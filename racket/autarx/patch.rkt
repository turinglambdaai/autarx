#lang racket/base

;; Plan → apply → undo pipeline for reviewed ARXML changes — port of
;; PatchEngine.cs. Planning never touches the workspace: operations run
;; against in-memory trees, the result is materialized into a temp
;; workspace, and the mandatory before/after semantic diff is computed
;; against it. Applying writes only files the plan proved changed, keeps a
;; backup per file, and records a manifest entry for undo.

(require racket/match
         racket/file
         racket/path
         racket/string
         racket/list
         json
         "arxml.rkt"
         "index.rkt"
         "diff.rkt")

(provide (struct-out patch-op-result)
         (struct-out patch-plan)
         (struct-out patch-file-record)
         (struct-out patch-manifest)
         parse-patch-op-set
         plan-patch
         apply-patch
         undo-patch
         find-by-definition
         try-delete-directory)

(struct patch-op-result (index op target ok? error))
;; valid? + affected-files + diff + temp-workspace (string | #f)
(struct patch-plan (operations valid? affected-files diff temp-workspace))
(struct patch-file-record (relative-path backup-file))
(struct patch-manifest (timestamp files operation-count undone?))

;; ---- op set parsing (the JSON format agents and humans submit)

;; returns (cons ops errors)
(define (parse-patch-op-set json-string)
  (with-handlers ([exn:fail:read?
                   (λ (ex)
                     (cons '() (list (format "invalid JSON: ~a" (exn-message ex)))))])
    (define document (string->jsexpr json-string))
    (define operations (hash-ref document 'operations #f))
    (if (not (list? operations))
        (cons '() '("expected a top-level { \"operations\": [ ... ] } array"))
        (let loop ([elements operations] [index 0] [ops '()] [errors '()])
          (cond
            [(null? elements) (cons (reverse ops) (reverse errors))]
            [else
             (define element (car elements))
             (define op (str-of element 'op))
             (define target (str-of element 'target))
             (cond
               [(and (string=? (or op "") "rename")
                     target
                     (non-empty? (str-of element 'newName)))
                (loop (cdr elements) (add1 index)
                      (cons (list 'rename target (str-of element 'newName)) ops)
                      errors)]
               [(and (string=? (or op "") "set-parameter")
                     target
                     (non-empty? (str-of element 'definition))
                     (non-empty? (str-of element 'value)))
                (loop (cdr elements) (add1 index)
                      (cons (list 'set-parameter
                                  target
                                  (str-of element 'definition)
                                  (str-of element 'value))
                            ops)
                      errors)]
               [(and (string=? (or op "") "add-parameter")
                     target
                     (non-empty? (str-of element 'definition))
                     (non-empty? (str-of element 'value)))
                (define value-type (or (str-of element 'type) "numeric"))
                (if (not (or (string=? value-type "numeric")
                             (string=? value-type "textual")))
                    (loop (cdr elements) (add1 index) ops
                          (cons (format
                                 "operation ~a: type must be \"numeric\" or \"textual\""
                                 index)
                                errors))
                    (loop (cdr elements) (add1 index)
                          (cons (list 'add-parameter
                                      target
                                      (str-of element 'definition)
                                      (str-of element 'value)
                                      value-type)
                                ops)
                          errors))]
               [(and (string=? (or op "") "set-reference")
                     target
                     (non-empty? (str-of element 'definition))
                     (non-empty? (str-of element 'value)))
                (loop (cdr elements) (add1 index)
                      (cons (list 'set-reference
                                  target
                                  (str-of element 'definition)
                                  (str-of element 'value))
                            ops)
                      errors)]
               [else
                (loop (cdr elements) (add1 index) ops
                      (cons (format
                             "operation ~a: incomplete or unsupported op '~a' (rename | set-parameter | add-parameter | set-reference; all need target)"
                             index (or op "null"))
                            errors))])])))))

(define (str-of element name)
  (define v (hash-ref element name #f))
  (and (string? v) v))

(define (non-empty? s) (and s (> (string-length s) 0)))

;; ---- planning

(define (plan-patch workspace-path operations [provenance #f])
  (define results (box '()))
  (define (add-result! r) (set-box! results (cons r (unbox results))))

  (define index (build-workspace-index workspace-path))
  (define trees (make-hash))
  (define dirty (make-hash))
  (define path-map (make-hash)) ; original path -> current path (renames)

  (define workspace-root (workspace-index-source-path index))
  (define documents (workspace-index-documents index))

  (define (tree-for file-path)
    (or (hash-ref trees file-path #f)
        (let ([tree (parse-arxml-file file-path)])
          (hash-set! trees file-path tree)
          tree)))

  ;; resolves the target against the CURRENT tree state (renames tracked)
  (define (resolve-target target op-name op-index)
    (define mapped (hash-ref path-map target target))
    (let loop ([docs documents])
      (cond
        [(null? docs) #f]
        [else
         (define file-path (workspace-document-file-path (car docs)))
         (define element (find-object-element (tree-for file-path) mapped))
         (if element
             (cons file-path element)
             (loop (cdr docs)))])))

  (for ([op (in-list operations)]
        [i (in-naturals)])
    (match op
      [(list 'rename target new-name)
       (define resolved (resolve-target target "rename" i))
       (cond
         [(not resolved)
          (add-result!
           (patch-op-result i "rename" target #f
                            (format "target '~a' not found in workspace"
                                    (hash-ref path-map target target))))]
         [else
          (define file-path (car resolved))
          (define mapped-path (hash-ref path-map target target))
          (define element (find-object-element (tree-for file-path) mapped-path))
          (define short-name-element (element-child element "SHORT-NAME"))
          (cond
            [(not short-name-element)
             (add-result!
              (patch-op-result i "rename" target #f "target has no SHORT-NAME"))]
            [else
             (define old-path mapped-path)
             (define old-len (string-length (arxml-element-text short-name-element)))
             (define new-path
               (string-append (substring mapped-path 0 (- (string-length mapped-path)
                                                          old-len))
                              new-name))
             (element-set-text! short-name-element new-name)
             ;; rewrite every reference pointing into the renamed subtree,
             ;; across all workspace documents
             (for ([document (in-list documents)])
               (define doc-file (workspace-document-file-path document))
               (when (rewrite-references! (tree-for doc-file) old-path new-path)
                 (hash-set! dirty doc-file #t)))
             (hash-set! dirty file-path #t)
             (hash-set! path-map old-path new-path)
             ;; chained renames: entries mapping onto the old path move too
             (for ([(old-key new-key) (in-hash path-map)])
               (when (string=? new-key old-path)
                 (hash-set! path-map old-key new-path)))
             (add-result!
              (patch-op-result i "rename" target #t
                               (format "~a → ~a" old-path new-path)))])])]
      [(list 'set-parameter target definition value)
       (define resolved (resolve-target target "set-parameter" i))
       (cond
         [(not resolved)
          (add-result!
           (patch-op-result i "set-parameter" target #f
                            (format "target '~a' not found in workspace"
                                    (hash-ref path-map target target))))]
         [else
          (define file-path (car resolved))
          (define element
            (find-object-element (tree-for file-path) (hash-ref path-map target target)))
          (define param (find-by-definition element definition))
          (define value-element (and param (element-child param "VALUE")))
          (cond
            [(not param)
             (add-result!
              (patch-op-result i "set-parameter" target #f
                               (format "no element with DEFINITION-REF ~a" definition)))]
            [(not value-element)
             (add-result!
              (patch-op-result i "set-parameter" target #f
                               "parameter has no VALUE element"))]
            [else
             (element-set-text! value-element value)
             (hash-set! dirty file-path #t)
             (add-result!
              (patch-op-result i "set-parameter" target #t
                               (format "~a = ~a" definition value)))])])]
      [(list 'add-parameter target definition value value-type)
       (define resolved (resolve-target target "add-parameter" i))
       (cond
         [(not resolved)
          (add-result!
           (patch-op-result i "add-parameter" target #f
                            (format "target '~a' not found in workspace"
                                    (hash-ref path-map target target))))]
         [else
          (define file-path (car resolved))
          (define element
            (find-object-element (tree-for file-path) (hash-ref path-map target target)))
          (define container-definition
            (substring definition 0 (add1 (last-index-of-char definition #\/))))
          (define container (or (find-by-definition element container-definition)
                                element))
          (cond
            [(find-by-definition element definition)
             (add-result!
              (patch-op-result i "add-parameter" target #f
                               (format "parameter already exists: ~a" definition)))]
            [else
             (define parameter-values (element-child container "PARAMETER-VALUES"))
             (unless parameter-values
               (set! parameter-values (arxml-element "PARAMETER-VALUES" '() #f '()))
               (attach-element! container parameter-values))
             (define element-type
               (if (string=? value-type "textual")
                   "ECUC-TEXTUAL-PARAM-VALUE"
                   "ECUC-NUMERICAL-PARAM-VALUE"))
             (define parameter (arxml-element element-type '() #f '()))
             (define definition-ref (arxml-element "DEFINITION-REF" '() #f '()))
             (element-set-text! definition-ref definition)
             (define value-elem (arxml-element "VALUE" '() #f '()))
             (element-set-text! value-elem value)
             (attach-element! parameter definition-ref)
             (attach-element! parameter value-elem)
             (attach-element! parameter-values parameter)
             (hash-set! dirty file-path #t)
             (add-result!
              (patch-op-result i "add-parameter" target #t
                               (format "~a = ~a" definition value)))])])]
      [(list 'set-reference target definition value)
       (define resolved (resolve-target target "set-reference" i))
       (cond
         [(not resolved)
          (add-result!
           (patch-op-result i "set-reference" target #f
                            (format "target '~a' not found in workspace"
                                    (hash-ref path-map target target))))]
         [else
          (define file-path (car resolved))
          (define element
            (find-object-element (tree-for file-path) (hash-ref path-map target target)))
          (define reference (find-by-definition element definition))
          (define value-ref (and reference (element-child reference "VALUE-REF")))
          (cond
            [(not reference)
             (add-result!
              (patch-op-result i "set-reference" target #f
                               (format "no reference with DEFINITION-REF ~a" definition)))]
            [(not value-ref)
             (add-result!
              (patch-op-result i "set-reference" target #f
                               "reference has no VALUE-REF"))]
            [else
             (element-set-text! value-ref value)
             (hash-set! dirty file-path #t)
             (add-result!
              (patch-op-result i "set-reference" target #t
                               (format "~a → ~a" definition value)))])])]))

  ;; wait — attach-element! mutates an immutable-children struct: children
  ;; lists are rebuilt on close; patch needs a mutable append
  (define all-results (reverse (unbox results)))

  (if (for/or ([r (in-list all-results)]) (not (patch-op-result-ok? r)))
      (patch-plan all-results #f '() #f #f)
      (let ()
        ;; materialize the full workspace into a temp directory (patched
        ;; files rewritten, untouched files copied verbatim) and diff it
        (define temp-root
          (build-path (find-system-path 'temp-dir)
                      (string-append "autarx-patch-"
                                     (symbol->string (gensym 'p)))))
        (make-directory* temp-root)
        (define affected-relative
          (for/fold ([affected '()])
                    ([document (in-list documents)])
            (define file-path (workspace-document-file-path document))
            (define relative (workspace-document-relative-path document))
            (define target-path (build-path temp-root relative))
            (make-directory* (path-only target-path))
            (if (hash-ref dirty file-path #f)
                (begin
                  (write-arxml-file (tree-for file-path)
                                    (path->string target-path)
                                    provenance)
                  (cons relative affected))
                (begin
                  ;; untouched files are copied verbatim so the temp
                  ;; workspace is complete for the mandatory diff
                  (copy-file file-path target-path #:exists-ok? #t)
                  affected))))
        (with-handlers ([exn:fail?
                         (λ (ex)
                           (try-delete-directory (path->string temp-root))
                           (patch-plan all-results #f '() #f #f))])
          (define diff
            (compare-workspaces (build-workspace-index workspace-path)
                                (build-workspace-index (path->string temp-root))
                                #t))
          (patch-plan all-results #t affected-relative diff
                      (path->string temp-root))))))

;; ---- element mutation helpers (children lists are immutable after
;; close — rebuild through the parent)

(define (attach-element! parent child)
  (set-arxml-element-children!
   parent (append (arxml-element-children parent) (list child))))

(define (last-index-of-char s c)
  (let loop ([i (- (string-length s) 1)] [found #f])
    (cond
      [(< i 0) (or found -1)]
      [(char=? (string-ref s i) c) (or found i)]
      [else (loop (- i 1) (or found #f))])))

;; Rewrites reference texts pointing at old-path or into its subtree
(define (rewrite-references! root old-path new-path)
  (define changed? (box #f))
  (for ([element (in-list (cons root (element-descendants root)))])
    (define name (arxml-element-name element))
    (define text (arxml-element-text element))
    (when (and (suffix? name "REF")
               (not (string=? name "DEFINITION-REF"))
               text
               (>= (string-length text) 1)
               (char=? (string-ref text 0) #\/))
      (cond
        [(string=? text old-path)
         (element-set-text! element new-path)
         (set-box! changed? #t)]
        [(and (>= (string-length text) (add1 (string-length old-path)))
              (string=? (substring text 0 (string-length old-path)) old-path)
              (char=? (string-ref text (string-length old-path)) #\/))
         (element-set-text! element
                            (string-append
                             new-path
                             (substring text (string-length old-path))))
         (set-box! changed? #t)])))
  (unbox changed?))

(define (suffix? s suf)
  (define n (string-length s))
  (define m (string-length suf))
  (and (>= n m) (string=? (substring s (- n m)) suf)))

;; Finds a descendant whose DEFINITION-REF equals the given definition
(define (find-by-definition root definition)
  (define trimmed (string-trim definition))
  (for/first ([element (in-list (cons root (element-descendants root)))]
              #:when (let ([d (element-child-text element "DEFINITION-REF")])
                       (and d (string=? (string-trim d) trimmed))))
    element))

;; ---- apply / undo

(define (apply-patch workspace-path plan temp-workspace)
  (unless (and (patch-plan-valid? plan) (patch-plan-diff plan))
    (raise-arguments-error 'apply-patch
                           "apply requires a valid plan with a semantic diff"))
  (define workspace-root (workspace-index-source-path
                          (build-workspace-index workspace-path)))
  (define backup-dir (build-path workspace-root ".autarx"))
  (make-directory* backup-dir)

  (define files
    (for/list ([relative (in-list (patch-plan-affected-files plan))])
      (define original (string->path (string-append workspace-root "/" relative)))
      (define backup-name (string-append (string-replace relative "/" "_")
                                         ".autarx-bak"))
      (define backup (build-path backup-dir backup-name))
      (copy-file original backup #:exists-ok? #t)
      (copy-file (string->path (string-append temp-workspace "/" relative))
                 original #:exists-ok? #t)
      (patch-file-record relative backup-name)))

  (define manifest
    (patch-manifest (current-utc-timestamp-string)
                    files
                    (length (patch-plan-operations plan))
                    #f))
  (call-with-output-file #:exists 'append
    (build-path backup-dir "patch-history.jsonl")
    (λ (out) (displayln (manifest->json-line manifest) out)))

  (try-delete-directory temp-workspace)
  manifest)

;; Restores the most recent not-yet-undone patch; #f when nothing to undo.
(define (undo-patch workspace-path)
  (define workspace-root (workspace-index-source-path
                          (build-workspace-index workspace-path)))
  (define history-file (build-path workspace-root ".autarx" "patch-history.jsonl"))
  (and (file-exists? history-file)
       (let* ([lines (filter non-empty?
                             (file->lines history-file))]
              [entries (map json-line->manifest lines)]
              [last-index
               (let loop ([i (sub1 (length entries))] [found #f])
                 (cond
                   [(< i 0) found]
                   [(not (patch-manifest-undone? (list-ref entries i)))
                    (or found i)]
                   [else (loop (- i 1) (or found #f))]))])
         (and last-index
              (let* ([last (list-ref entries last-index)])
                (for ([file (in-list (patch-manifest-files last))])
                  (define original
                    (string->path
                     (string-append
                      workspace-root "/" (patch-file-record-relative-path file))))
                  (define backup
                    (build-path workspace-root ".autarx"
                                (patch-file-record-backup-file file)))
                  (copy-file backup original #:exists-ok? #t)
                  (delete-file backup))
                (define undone
                  (struct-copy patch-manifest last [undone? #t]))
                (define rewritten
                  (for/list ([i (in-range (length entries))])
                    (if (= i last-index) undone (list-ref entries i))))
                (call-with-output-file #:exists 'truncate
                  history-file
                  (λ (out)
                    (for ([m (in-list rewritten)])
                      (displayln (manifest->json-line m) out))))
                undone)))))

;; .NET DateTime.UtcNow default JSON form: 2026-10-02T07:33:19.1234567Z
(define (current-utc-timestamp-string)
  (define d (seconds->date (current-seconds) 0))
  (format "~a-~a~a-~a~aT~a~a:~a~a:~a~a.0000000Z"
          (date-year d)
          (pad2 (date-month d))
          ""
          (pad2 (date-day d))
          ""
          (pad2 (date-hour d))
          ""
          (pad2 (date-minute d))
          ""
          (pad2 (date-second d))
          ""))

(define (pad2 n)
  (if (< n 10) (string-append "0" (number->string n)) (number->string n)))

(define (manifest->json-line manifest)
  (jsexpr->string
   (hasheq 'timestamp (patch-manifest-timestamp manifest)
           'files (for/list ([f (in-list (patch-manifest-files manifest))])
                    (hasheq 'relativePath (patch-file-record-relative-path f)
                            'backupFile (patch-file-record-backup-file f)))
           'operationCount (patch-manifest-operation-count manifest)
           'undone (patch-manifest-undone? manifest))))

(define (json-line->manifest line)
  (define j (string->jsexpr line))
  (patch-manifest
   (hash-ref j 'timestamp)
   (for/list ([f (in-list (hash-ref j 'files))])
     (patch-file-record (hash-ref f 'relativePath) (hash-ref f 'backupFile)))
   (hash-ref j 'operationCount)
   (hash-ref j 'undone)))

;; Best-effort temp cleanup; temp leftovers never fail a patch
(define (try-delete-directory path)
  (with-handlers ([exn:fail:filesystem? (λ (_) (void))])
    (delete-directory/files path)))
