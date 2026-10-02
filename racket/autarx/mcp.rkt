#lang racket/base

;; MCP stdio server — port of McpServer.cs / McpToolRegistry.cs /
;; McpJson.cs. Newline-delimited JSON-RPC 2.0. The envelope is written
;; COMPACT (single line, "id":null stays explicit); tool payloads use the
;; CLI JSON profile (indented, camelCase, null-omitted). No raw-write tool
;; exists and every tools/call is appended to <workspace>/.autarx/audit.jsonl.

(require racket/file
         racket/path
         racket/string
         json
         "classify.rkt"
         "index.rkt"
         "diff.rkt"
         "impact.rkt"
         "ecuc.rkt"
         "validate.rkt"
         "comm.rkt"
         "patch.rkt"
         "arxml.rkt"
         "jsonout.rkt")

(provide serve)

;; ---------------------------------------------------------------- envelope

;; compact writer — no spaces after ':' or ','
(define (compact-json v)
  (define out (open-output-string))
  (let go ([v v])
    (cond
      [(string? v) (display (jesc v) out)]
      [(number? v) (display v out)]
      [(boolean? v) (display (if v "true" "false") out)]
      [(eq? v 'null) (display "null" out)]
      [(list? v)
       (if (and (pair? v) (pair? (car v)) (string? (caar v)))
           (begin
             (display "{" out)
             (for ([f (in-list v)] [i (in-naturals)])
               (when (> i 0) (display "," out))
               (display (jesc (car f)) out)
               (display ":" out)
               (go (cdr f)))
             (display "}" out))
           (begin
             (display "[" out)
             (for ([item (in-list v)] [i (in-naturals)])
               (when (> i 0) (display "," out))
               (go item))
             (display "]" out)))]))
  (get-output-string out))

(define (respond out id result)
  (displayln (compact-json (list (cons "jsonrpc" "2.0")
                                 (cons "id" id)
                                 (cons "result" result)))
             out)
  (flush-output out))

(define (respond-error out id code message)
  (displayln (compact-json (list (cons "jsonrpc" "2.0")
                                 (cons "id" id)
                                 (cons "error"
                                       (list (cons "code" code)
                                             (cons "message" message)))))
             out)
  (flush-output out))

;; ---------------------------------------------------------------- tools

(struct mcp-tool (name description required optional))

(define tools
  (list
   (mcp-tool "inspect"
             "Workspace summary: files, identifiables, references, unresolved counts, AUTOSAR release"
             '(workspace) '())
   (mcp-tool "find"
             "Find identifiables by SHORT-NAME substring"
             '(workspace pattern) '())
   (mcp-tool "refs"
             "Outgoing and incoming references of one object"
             '(workspace object) '())
   (mcp-tool "trace"
             "Breadth-first walk over the reference graph from one object"
             '(workspace object) '((depth . "3")))
   (mcp-tool "ecus"
             "List ECU instances in the workspace"
             '(workspace) '())
   (mcp-tool "unresolved"
             "References whose target is not present in the workspace"
             '(workspace) '())
   (mcp-tool "diff"
             "Semantic diff between two deliveries"
             '(before after) '((detail . "false")))
   (mcp-tool "impact"
             "ECU-scoped impact analysis across two deliveries"
             '(before after ecu) '())
   (mcp-tool "comm"
             "Communication projection"
             '(workspace) '((cluster . "")))
   (mcp-tool "validate"
             "Structural checks on one ARXML file (ARX00NN) — not vendor validation"
             '(input) '())
   (mcp-tool "patch_plan"
             "Plan reviewed ARXML edits and return the mandatory before/after semantic diff (read-only)"
             '(workspace operations) '())
   (mcp-tool "patch_apply"
             "Apply a reviewed patch: re-plans, writes changed files with backups, records history"
             '(workspace operations) '())))

(define (tool-schema t)
  ;; every property is declared as a string type per the v1.0.3 registry
  (define names
    (append (map symbol->string (mcp-tool-required t))
            (map (λ (o) (symbol->string (car o))) (mcp-tool-optional t))))
  (define properties
    (for/list ([n (in-list names)])
      (cons n (list (cons "type" "string")
                    (cons "description" "")))))
  (define base
    (list (cons "type" "object")
          (cons "properties" properties)))
  (if (null? (mcp-tool-required t))
      base
      (append base (list (cons "required"
                               (map symbol->string (mcp-tool-required t)))))))

;; ---------------------------------------------------------------- serve

(define (serve version)
  (define in (current-input-port))
  (define out (current-output-port))
  (let loop ()
    (define line (read-line in))
    (unless (or (eof-object? line)
                (and (string? line) (string=? line "bye")))
      (unless (string-whitespace? line)
        (handle-line line out version))
      (loop))))

(define (string-whitespace? s)
  (for/and ([c (in-string s)]) (char-whitespace? c)))

(define (handle-line line out version)
  (define request
    (with-handlers ([exn:fail:read? (λ (ex) 'parse-error)])
      (string->jsexpr line)))
  (cond
    [(eq? request 'parse-error)
     (respond-error out 'null -32700 "parse error")]
    [(not (hash? request)) (void)]
    [else
     (define id (hash-ref request 'id 'null))
     (define method (hash-ref request 'method #f))
     (cond
       [(not method) (void)] ; notifications and bare objects stay silent
       [(and (hash-ref request 'id #f) (eq? id 'null)) (void)]
       [(string=? method "initialize")
        (respond out id
                 (list (cons "protocolVersion" "2024-11-05")
                       (cons "capabilities" (list (cons "tools" '())))
                       (cons "serverInfo"
                             (list (cons "name" "autarx")
                                   (cons "version" version)))))]
       [(string=? method "ping") (respond out id '())]
       [(string=? method "tools/list")
        (respond out id
                 (list
                  (cons "tools"
                        (for/list ([t (in-list tools)])
                          (list (cons "name" (mcp-tool-name t))
                                (cons "description" (mcp-tool-description-string t))
                                (cons "inputSchema" (tool-schema t)))))))]
       [(string=? method "tools/call")
        (define params (hash-ref request 'params (hash)))
        (define name (hash-ref params 'name #f))
        (define arguments (hash-ref params 'arguments (hash)))
        (with-handlers ([exn:fail?
                         (λ (ex)
                           (respond out id
                                    (list
                                     (cons "content"
                                           (list (list (cons "type" "text")
                                                       (cons "text" (exn-message ex)))))
                                     (cons "isError" #t))))])
          (audit-log! name arguments)
          (define found (for/first ([t (in-list tools)]
                                    #:when (string=? (mcp-tool-name t) name))
                          t))
          (if (not found)
              (respond out id
                       (list
                        (cons "content"
                              (list (list (cons "type" "text")
                                          (cons "text"
                                                (format "unknown tool '~a'" name)))))
                        (cons "isError" #t)))
              (respond out id
                       (list
                        (cons "content"
                              (list (list (cons "type" "text")
                                          (cons "text"
                                                (call-tool name arguments version)))))
                        (cons "isError" #f)))))]
       [(not (hash-ref request 'id #f)) (void)]
       [else
        (respond-error out id -32601
                       (format "unknown method '~a'" method))])]))

(define (mcp-tool-description-string t) (mcp-tool-description t))

;; best-effort audit: never blocks the tool
(define (audit-log! name arguments)
  (with-handlers ([exn:fail? (λ (_) (void))])
    (define ws
      (let ([candidate (or (hash-ref arguments 'workspace #f)
                           (hash-ref arguments 'before #f)
                           (hash-ref arguments 'input #f)
                           ".")])
        (path->string (simplify-path (path->complete-path candidate)))))
    (define audit-dir (build-path ws ".autarx"))
    (when (directory-exists? audit-dir)
      (define entry
        (json->string (list (cons "timestamp" (iso-now))
                            (cons "tool" name)
                            (cons "arguments" (jsexpr->compact arguments)))))
      (call-with-output-file #:exists 'append
        (build-path audit-dir "audit.jsonl")
        (λ (out) (displayln entry out))))))

(define (jsexpr->compact j)
  (cond
    [(hash? j)
     (for/list ([(k v) (in-hash j)])
       (cons (symbol->string k) (jsexpr->compact v)))]
    [(list? j) (map jsexpr->compact j)]
    [(symbol? j) (symbol->string j)]
    [(eq? j #t) #t]
    [(eq? j #f) #f]
    [(void? j) 'null]
    [else j]))

(define (iso-now)
  (define d (seconds->date (current-seconds) 0))
  (format "~a-~a-~aT~a:~a:~aZ"
          (date-year d) (pad2 (date-month d)) (pad2 (date-day d))
          (pad2 (date-hour d)) (pad2 (date-minute d)) (pad2 (date-second d))))

(define (pad2 n) (if (< n 10) (string-append "0" (number->string n)) (number->string n)))

;; ---------------------------------------------------------------- tools

(define (arg-string arguments name [default #f])
  (define v (hash-ref arguments (string->symbol name) default))
  (if (string? v) v default))

(define (arg-number arguments name [default #f])
  (define v (hash-ref arguments (string->symbol name) default))
  (cond [(number? v) v]
        [(string? v) (or (string->number v) default)]
        [else default]))

(define (arg-bool arguments name [default #f])
  (define v (hash-ref arguments (string->symbol name) default))
  (cond [(boolean? v) v]
        [(string? v) (member v '("true" "1"))]
        [else default]))

;; MCP object: different field order than CLI, sourceFile is the FILE NAME
(define (mcp-object-json o)
  (list (cons "shortName" (semantic-object-short-name o))
        (cons "elementType" (semantic-object-element-type o))
        (cons "semanticKind" (hash-ref kind->json (semantic-object-kind o)))
        (cons "absolutePath" (semantic-object-absolute-path o))
        (cons "sourceFile" (basename-of (semantic-object-source-file o)))))

(define (mcp-reference-json r)
  (list (cons "kind" (arx-reference-kind r))
        (cons "sourcePath" (arx-reference-source-path r))
        (cons "targetPath" (arx-reference-target-path r))
        (cons "resolved" (arx-reference-resolved r))))

(define (basename-of p)
  (define v (file-name-from-path (if (string? p) (string->path p) p)))
  (if v (path->string v) p))

(define (mcp-object-change-json c)
  (list (cons "changeType" (symbol->json-change (object-change-change-type c)))
        (cons "shortName" (object-change-short-name c))
        (cons "semanticKind" (hash-ref kind->json (object-change-kind c)))
        (cons "absolutePath" (object-change-absolute-path c))
        (cons "beforeElementType" (or (object-change-before-element-type c) 'null))
        (cons "afterElementType" (or (object-change-after-element-type c) 'null))
        (cons "beforeSourceFile"
              (or (and (object-change-before-source-file c)
                       (basename-of (object-change-before-source-file c)))
                  'null))
        (cons "afterSourceFile"
              (or (and (object-change-after-source-file c)
                       (basename-of (object-change-after-source-file c)))
                  'null))
        (cons "elementTypeChanged" (object-change-element-type-changed? c))
        (cons "elementType" (or (object-change-element-type c) 'null))
        (cons "properties"
              (if (object-change-properties c)
                  (for/list ([p (in-list (object-change-properties c))])
                    (list (cons "path" (property-change-path p))
                          (cons "changeType"
                                (symbol->json-pchange (property-change-change p)))
                          (cons "before" (or (property-change-before p) 'null))
                          (cons "after" (or (property-change-after p) 'null))))
                  'null))))

(define (symbol->json-change s)
  (case s [(added) "added"] [(removed) "removed"] [else "modified"]))

(define (symbol->json-pchange s)
  (case s [(added) "added"] [(removed) "removed"] [else "changed"]))

;; the full Core model serialization used by the diff/impact/patch tools
(define (mcp-diff-json d)
  (list (cons "before" (mcp-side-json (workspace-diff-before d)))
        (cons "after" (mcp-side-json (workspace-diff-after d)))
        (cons "objects" (map mcp-object-change-json (workspace-diff-objects d)))
        (cons "references"
              (for/list ([r (in-list (workspace-diff-references d))])
                (list (cons "changeType" (symbol->json-change
                                          (reference-change-change-type r)))
                      (cons "sourcePath" (reference-change-source-path r))
                      (cons "kind" (reference-change-kind r))
                      (cons "targetPath" (reference-change-target-path r))
                      (cons "sourceFile" (or (reference-change-source-file r) 'null)))))
        (cons "files"
              (for/list ([f (in-list (workspace-diff-files d))])
                (list (cons "changeType" (symbol->json-change
                                          (file-change-change-type f)))
                      (cons "relativePath" (file-change-relative-path f))
                      (cons "beforeSizeBytes" (or (file-change-before-size-bytes f)
                                                  'null))
                      (cons "afterSizeBytes" (or (file-change-after-size-bytes f)
                                                 'null)))))
        (cons "isEmpty" (workspace-diff-empty? d))))

(define (mcp-side-json side)
  (list (cons "path" (diff-side-path side))
        (cons "documentCount" (diff-side-document-count side))
        (cons "objectCount" (diff-side-object-count side))
        (cons "referenceCount" (diff-side-reference-count side))))

(define (call-tool name arguments version)
  (case name
    [("inspect")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (define s (summarize ws))
     (json->string
      (list (cons "workspace" (arg-string arguments "workspace"))
            (cons "fileCount" (workspace-summary-file-count s))
            (cons "totalFileBytes" (workspace-summary-total-file-bytes s))
            (cons "identifiableCount" (workspace-summary-identifiable-count s))
            (cons "packageCount" (workspace-summary-package-count s))
            (cons "referenceCount" (workspace-summary-reference-count s))
            (cons "unresolvedReferenceCount"
                  (workspace-summary-unresolved-reference-count s))
            (cons "duplicatePathCount" (workspace-summary-duplicate-path-count s))
            (cons "fileErrorCount" (workspace-summary-file-error-count s))
            (cons "autosarRelease" (or (workspace-summary-autosar-release s) 'null))
            (cons "mixedSchema" (workspace-summary-mixed-schema s))
            (cons "semanticCounts"
                  (map (λ (p) (cons (hash-ref kind->json (car p)) (cdr p)))
                       (workspace-summary-semantic-counts s)))))]
    [("find")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (json->string
      (map mcp-object-json (find-by-name ws (arg-string arguments "pattern"))))]
    [("refs")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (define name-or-path (arg-string arguments "object"))
     (define resolution (resolve-object ws name-or-path))
     (case (resolution-status resolution)
       [(found)
        (define path (semantic-object-absolute-path (resolution-object resolution)))
        (json->string
         (list (cons "objectPath" path)
               (cons "outgoing" (map mcp-reference-json (outgoing-of ws path)))
               (cons "incoming" (map mcp-reference-json (incoming-of ws path)))))]
       [(ambiguous)
        (error 'refs "'~a' is ambiguous, ~a objects match: ~a"
               name-or-path
               (length (resolution-candidates resolution))
               (string-join (map semantic-object-absolute-path
                                 (resolution-candidates resolution))
                            ", "))]
       [else (error 'refs "no workspace object matches '~a'" name-or-path)])]
    [("trace")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (define name-or-path (arg-string arguments "object"))
     (define depth (arg-number arguments "depth" 3))
     (define resolution (resolve-object ws name-or-path))
     (case (resolution-status resolution)
       [(found)
        (define result
          (trace ws (semantic-object-absolute-path (resolution-object resolution))
                 'both depth))
        (json->string
         (list (cons "root" (trace-result-root result))
               (cons "direction" "both")
               (cons "maxDepth" (trace-result-max-depth result))
               (cons "nodes"
                     (for/list ([n (in-list (trace-result-nodes result))])
                       (list (cons "path" (trace-node-path n))
                             (cons "depth" (trace-node-depth n)))))
               (cons "edges"
                     (for/list ([e (in-list (trace-result-edges result))])
                       (list (cons "sourcePath" (trace-edge-source-path e))
                             (cons "kind" (trace-edge-kind e))
                             (cons "targetPath" (trace-edge-target-path e))
                             (cons "depth" (trace-edge-depth e)))))))]
       [(ambiguous)
        (error 'trace "'~a' is ambiguous, ~a objects match: ~a"
               name-or-path
               (length (resolution-candidates resolution))
               (string-join (map semantic-object-absolute-path
                                 (resolution-candidates resolution))
                            ", "))]
       [else (error 'trace "no workspace object matches '~a'" name-or-path)])]
    [("ecus")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (json->string
      (map mcp-object-json
           (sort (filter (λ (o) (eq? (semantic-object-kind o) 'ecu))
                         (workspace-index-objects ws))
                 string<?
                 #:key semantic-object-absolute-path)))]
    [("unresolved")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (json->string
      (map mcp-reference-json
           (filter (λ (r) (not (arx-reference-resolved r)))
                   (workspace-index-references ws))))]
    [("diff")
     (define before (build-workspace-index (arg-string arguments "before")))
     (define after (build-workspace-index (arg-string arguments "after")))
     (json->string
      (mcp-diff-json (compare-workspaces before after
                                         (arg-bool arguments "detail" #f))))]
    [("impact")
     (define before (build-workspace-index (arg-string arguments "before")))
     (define after (build-workspace-index (arg-string arguments "after")))
     (define ecu-name (arg-string arguments "ecu"))
     (define after-r (resolve-object after ecu-name))
     (define before-r (resolve-object before ecu-name))
     (define ecu-object
       (cond
         [(eq? (resolution-status after-r) 'found) (resolution-object after-r)]
         [(eq? (resolution-status before-r) 'found) (resolution-object before-r)]
         [else (error 'impact "no workspace object matches '~a' in either delivery"
                      ecu-name)]))
     (define ecu-path (semantic-object-absolute-path ecu-object))
     (define ecu-removed?
       (and (eq? (resolution-status after-r) 'not-found)
            (eq? (resolution-status before-r) 'found)))
     (define report
       (analyze-impact before after ecu-path ecu-removed?
                       (compare-workspaces before after #f) #f))
     (json->string
      (list (cons "ecu" (impact-report-ecu report))
            (cons "ecuPath" (impact-report-ecu-path report))
            (cons "ecuRemoved" (impact-report-ecu-removed report))
            (cons "closureSize" (impact-report-closure-size report))
            (cons "relevantCount" (impact-report-relevant-count report))
            (cons "unrelatedCount" (impact-report-unrelated-count report))
            (cons "breakingCount" (impact-report-breaking-count report))
            (cons "findings"
                  (for/list ([f (in-list (impact-report-findings report))])
                    (list (cons "rule" (impact-finding-rule f))
                          (cons "severity"
                                (if (eq? (impact-finding-severity f) 'breaking)
                                    "breaking" "info"))
                          (cons "objectPath" (impact-finding-object-path f))
                          (cons "detail" (impact-finding-detail f)))))
            (cons "relevantChanges"
                  (map mcp-object-change-json (impact-report-relevant-changes report)))
            (cons "communication"
                  (let ([c (impact-report-communication report)])
                    (list (cons "changes"
                                (map mcp-object-change-json
                                     (communication-impact-changes c)))
                          (cons "signalCount" (communication-impact-signal-count c))
                          (cons "pduCount" (communication-impact-pdu-count c))
                          (cons "frameCount" (communication-impact-frame-count c))
                          (cons "clusterCount"
                                (communication-impact-cluster-count c)))))))]
    [("comm")
     (define ws (build-workspace-index (arg-string arguments "workspace")))
     (define model (build-communication ws))
     (define cluster-name (arg-string arguments "cluster"))
     (define filtered
       (if cluster-name
           (struct-copy communication-model model
                        [clusters
                         (filter
                          (λ (c)
                            (string=? (cluster-projection-cluster-path c) cluster-name))
                          (communication-model-clusters model))])
           model))
     (json->string
      (list (cons "clusters"
                  (for/list ([c (in-list (communication-model-clusters filtered))])
                    (list (cons "clusterPath" (cluster-projection-cluster-path c))
                          (cons "connectedEcus" (cluster-projection-connected-ecus c))
                          (cons "framePaths" (cluster-projection-frame-paths c)))))
            (cons "frames"
                  (for/list ([f (in-list (communication-model-frames model))])
                    (list (cons "framePath" (frame-projection-frame-path f))
                          (cons "pduPaths" (frame-projection-pdu-paths f)))))
            (cons "pdus"
                  (for/list ([p (in-list (communication-model-pdus model))])
                    (list (cons "pduPath" (pdu-projection-pdu-path p))
                          (cons "signalPaths" (pdu-projection-signal-paths p)))))
            (cons "orphanFrames" (communication-model-orphan-frames model))
            (cons "orphanPdus" (communication-model-orphan-pdus model))
            (cons "orphanSignals" (communication-model-orphan-signals model))
            (cons "ecucCommModules" (communication-model-ecuc-comm-modules model))
            (cons "isEmpty" (communication-model-empty? model))))]
    [("validate")
     (define input (arg-string arguments "input"))
     (define diagnostics (validate-modules (read-modules (parse-arxml-file input))))
     (json->string
      (list (cons "input" input)
            (cons "errors"
                  (for/sum ([d (in-list diagnostics)])
                    (if (eq? (diagnostic-severity d) 'error) 1 0)))
            (cons "warnings"
                  (for/sum ([d (in-list diagnostics)])
                    (if (eq? (diagnostic-severity d) 'warning) 1 0)))
            (cons "diagnostics"
                  (for/list ([d (in-list diagnostics)])
                    (list (cons "code" (diagnostic-code d))
                          (cons "severity"
                                (if (eq? (diagnostic-severity d) 'error)
                                    "error" "warning"))
                          (cons "message" (diagnostic-message d))
                          (cons "path" (diagnostic-path d)))))))]
    [("patch_plan")
     (define workspace (arg-string arguments "workspace"))
     (define operations (normalize-operations arguments))
     (define parsed (parse-patch-op-set operations))
     (define ops (car parsed))
     (define parse-errors (cdr parsed))
     (if (not (null? parse-errors))
         (json->string
          (list (cons "operations"
                      (for/list ([e (in-list parse-errors)]
                                 [i (in-naturals)])
                        (list (cons "index" i)
                              (cons "op" "parse")
                              (cons "target" "")
                              (cons "ok" #f)
                              (cons "error" e))))
                (cons "valid" #f)))
         (let ()
           (define plan (plan-patch workspace ops #f))
           (json->string
            (list (cons "operations"
                        (for/list ([r (in-list (patch-plan-operations plan))])
                          (list (cons "index" (patch-op-result-index r))
                                (cons "op" (patch-op-result-op r))
                                (cons "target" (patch-op-result-target r))
                                (cons "ok" (patch-op-result-ok? r))
                                (cons "error" (or (patch-op-result-error r)
                                                  'null)))))
                  (cons "valid" (patch-plan-valid? plan))
                  (cons "affectedFiles" (patch-plan-affected-files plan))
                  (cons "diff"
                        (if (patch-plan-diff plan)
                            (mcp-diff-json (patch-plan-diff plan))
                            'null))))))]
    [("patch_apply")
     (define workspace (arg-string arguments "workspace"))
     (define operations (normalize-operations arguments))
     (define parsed (parse-patch-op-set operations))
     (define ops (car parsed))
     (define provenance (string-append "semantic patch by autarx " version))
     (define plan (plan-patch workspace ops provenance))
     (if (not (patch-plan-valid? plan))
         (json->string
          (list (cons "applied" #f)
                (cons "operations"
                      (for/list ([r (in-list (patch-plan-operations plan))])
                        (list (cons "index" (patch-op-result-index r))
                              (cons "op" (patch-op-result-op r))
                              (cons "target" (patch-op-result-target r))
                              (cons "ok" (patch-op-result-ok? r))
                              (cons "error" (or (patch-op-result-error r)
                                                'null)))))
                (cons "reason" "plan invalid — nothing was written")))
         (let ()
           (define manifest
             (apply-patch workspace plan (patch-plan-temp-workspace plan)))
           (json->string
            (list (cons "applied" #t)
                  (cons "timestamp" (patch-manifest-timestamp manifest))
                  (cons "files"
                        (map patch-file-record-relative-path
                             (patch-manifest-files manifest)))
                  (cons "operationCount" (patch-manifest-operation-count manifest))
                  (cons "provenance" provenance)
                  (cons "diff" (mcp-diff-json (patch-plan-diff plan)))))))]
    [else (error 'mcp "unknown tool '~a'" name)]))

;; operations arrive as a JSON string (the whole ops file) or a bare array
(define (normalize-operations arguments)
  (define raw (hash-ref arguments 'operations #f))
  (cond
    [(string? raw) raw]
    [(list? raw)
     (jsexpr->string (jsexpr-wrap raw))]
    [else (error 'mcp "operations must be a JSON string or array")]))

(define (jsexpr-wrap ops-array)
  (hasheq 'operations ops-array))
