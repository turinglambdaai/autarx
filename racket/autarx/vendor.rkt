#lang racket/base

;; Vendor hand-off — port of VendorAdapters.cs, ProcessToolRunner.cs and
;; VendorHandoff.cs. Adapters know how to detect an installation and how to
;; hand a project off to the tool; they never reimplement the tool. The
;; report relays the vendor's own result — never an Autarx verdict.

(require racket/match
         racket/file
         racket/string
         racket/system
         racket/port)

(provide (struct-out vendor-adapter)
         vendor-adapter-display-name
         (struct-out vendor-tool)
         (struct-out vendor-invocation)
         (struct-out vendor-run-result)
         (struct-out vendor-report)
         (struct-out vendor-diagnostic)
         vendor-adapters
         find-vendor-adapter
         detect-vendor
         build-invocation
         run-vendor-tool
         execute-vendor-handoff
         vendor-diagnostics-of)

(struct vendor-tool (name executable-path version))
(struct vendor-invocation (executable arguments working-directory))
(struct vendor-run-result (exit-code standard-output standard-error timed-out?))
(struct vendor-report (tool executable arguments working-directory exit-code
                           timed-out? duration-milliseconds diagnostics
                           error-count warning-count raw-output-file))
(struct vendor-diagnostic (severity message raw-line))

(struct vendor-adapter (name display-name home-variable executable-names))

(define vendor-adapters
  (list (vendor-adapter "davinci" "DaVinci Configurator" "DAVINCI_HOME"
                        '("DaVinciConfigurator.exe" "dvcmd.exe"))
        (vendor-adapter "tresos" "EB tresos Studio" "TRESOS_HOME"
                        '("tresos_cmd.bat" "tresos_studio.bat" "tresos.bat"))
        (vendor-adapter "isolar" "ETAS ISOLAR" "ISOLAR_HOME"
                        '("isolar.exe" "ISOLAR-B.exe"))))

(define (find-vendor-adapter name)
  (for/first ([a (in-list vendor-adapters)]
              #:when (string-ci=? (vendor-adapter-name a) name))
    a))

;; HOME env var probes (root and bin), then PATH. env-lookup reads env
;; vars; path-resolver resolves a bare name via PATH or an absolute path
;; via existence — injected so detection is testable.
(define (detect-vendor adapter env-lookup path-resolver)
  (define home (env-lookup (vendor-adapter-home-variable adapter)))
  (for/or ([executable (in-list (vendor-adapter-executable-names adapter))])
    (or (and (and home (> (string-length home) 0))
             (for/or ([candidate (in-list (list (path-string home "bin" executable)
                                                (path-string home executable)))])
               (define resolved (path-resolver candidate))
               (and resolved (vendor-tool (vendor-adapter-name adapter) resolved #f))))
        (let ([on-path (path-resolver executable)])
          (and on-path (vendor-tool (vendor-adapter-name adapter) on-path #f))))))

(define (path-string . parts)
  (string-join parts "/"))

;; template overrides the default when provided and must contain {project};
;; extra arguments are appended verbatim
(define (build-invocation adapter tool project-path arguments-template extra-arguments)
  (define full-project
    (if (string? project-path)
        project-path
        (path->string project-path)))
  (define full-project-abs
    (path->string (simplify-path (path->complete-path (string->path full-project)))))
  (define arguments
    (if (and arguments-template (> (string-length arguments-template) 0))
        (string-replace arguments-template "{project}" full-project-abs)
        (format "\"~a\"" full-project-abs)))
  ;; Path.GetDirectoryName carries no trailing separator
  (define working-directory
    (let-values ([(base _dir _) (split-path (string->path full-project-abs))])
      (define dir (if (path? base) (path->string base) "."))
      (if (and (> (string-length dir) 1)
               (char=? (string-ref dir (- (string-length dir) 1)) #\/))
          (substring dir 0 (- (string-length dir) 1))
          dir)))
  (vendor-invocation
   (vendor-tool-executable-path tool)
   (if (null? extra-arguments)
       arguments
       (string-append arguments " " (string-join extra-arguments " ")))
   working-directory))

;; Runs the tool as a child process, capturing both streams. Batch files go
;; through cmd.exe. On timeout the child is killed and timed-out? is
;; reported — exit code stays #f.
(define (run-vendor-tool invocation timeout-seconds)
  (define executable (vendor-invocation-executable invocation))
  (define is-batch?
    (or (string-suffix-ci? executable ".bat")
        (string-suffix-ci? executable ".cmd")))
  (define cmd (if is-batch? "cmd.exe" executable))
  (define argv
    (if is-batch?
        (cons "/c" (cons (format "\"~a\"" executable)
                         (shell-split (vendor-invocation-arguments invocation))))
        (shell-split (vendor-invocation-arguments invocation))))
  (with-handlers ([exn:fail? (λ (ex)
                               (vendor-run-result
                                #f "" (format "failed to start ~a" executable) #f))])
    (define-values (proc stdout-in stderr-in stdout-out stderr-out)
      (apply subprocess
             (list* stdout-out stderr-out #f cmd argv)))
    ;; drain both pipes on separate threads (deadlock-free), then wait with
    ;; timeout
    (define stdout-b (box #""))
    (define stderr-b (box #""))
    (define t1 (thread (λ () (set-box! stdout-b (port->bytes stdout-in))
                         (close-input-port stdout-in))))
    (define t2 (thread (λ () (set-box! stderr-b (port->bytes stderr-in))
                         (close-input-port stderr-in))))
    (define done? (box #f))
    (define waiter
      (thread (λ () (subprocess-wait proc) (set-box! done? #t))))
    (define timed-out?
      (not (sync/timeout timeout-seconds waiter)))
    (when timed-out?
      (with-handlers ([exn:fail? (λ (_) (void))])
        (subprocess-kill proc #t)))
    (thread-wait t1)
    (thread-wait t2)
    (define exit-code
      (if timed-out?
          #f
          (begin (unless (unbox done?) (subprocess-wait proc))
                 (subprocess-status proc))))
    (vendor-run-result exit-code
                       (bytes->string/utf-8 (unbox stdout-b) #\?)
                       (bytes->string/utf-8 (unbox stderr-b) #\?)
                       timed-out?)))

(define (string-suffix-ci? s suffix)
  (define n (string-length s))
  (define m (string-length suffix))
  (and (>= n m)
       (string-ci=? (substring s (- n m)) suffix)))

;; Executes the hand-off and normalizes the tool's output into a structured
;; report; severity tags come from a loose line heuristic and the raw
;; output is preserved for audit.
(define (execute-vendor-handoff tool invocation timeout-seconds raw-output-file)
  (define t0 (current-inexact-milliseconds))
  (define result (run-vendor-tool invocation timeout-seconds))
  (define duration (inexact->exact (round (- (current-inexact-milliseconds) t0))))
  (define combined
    (string-append (vendor-run-result-standard-output result)
                   (vendor-run-result-standard-error result)))
  (make-directory* (path-only-directory raw-output-file))
  (call-with-output-file raw-output-file
    (λ (out) (display combined out))
    #:exists 'replace)
  (define diagnostics (vendor-diagnostics-of result))
  (vendor-report
   (vendor-tool-name tool)
   (vendor-invocation-executable invocation)
   (vendor-invocation-arguments invocation)
   (vendor-invocation-working-directory invocation)
   (vendor-run-result-exit-code result)
   (vendor-run-result-timed-out? result)
   duration
   diagnostics
   (count-where (λ (d) (string=? (vendor-diagnostic-severity d) "error")) diagnostics)
   (count-where (λ (d) (string=? (vendor-diagnostic-severity d) "warning")) diagnostics)
   raw-output-file))

(define (vendor-diagnostics-of result)
  (define lines
    (string-split
     (string-append (vendor-run-result-standard-output result)
                    (vendor-run-result-standard-error result))
     #rx"\r\n|\n|\r"))
  (for/list ([line (in-list lines)]
             #:when (non-empty-string? line)
             #:when (or (word-match? line "error") (word-match? line "warning")))
    (define severity (if (word-match? line "error") "error" "warning"))
    (vendor-diagnostic severity line line)))

(define (non-empty-string? s) (> (string-length s) 0))

;; \bword\b, case-insensitive
(define (word-match? line word)
  (regexp-match? (pregexp (string-append "\\b" word "\\b")) (string-downcase line)))

;; .NET ProcessStartInfo-style argument splitting: whitespace-separated
;; tokens, double quotes group
(define (shell-split s)
  (define out '())
  (define i 0)
  (define n (string-length s))
  (let loop ()
    (when (< i n)
      (cond
        [(char-whitespace? (string-ref s i)) (set! i (add1 i)) (loop)]
        [else
         (define start i)
         (define token (open-output-string))
         (let scan ()
           (when (< i n)
             (define c (string-ref s i))
             (cond
               [(char=? c #\") (set! i (add1 i)) (scan)]
               [(char-whitespace? c) (void)]
               [else (write-char c token) (set! i (add1 i)) (scan)])))
         (set! out (cons (get-output-string token) out))
         (loop)])))
  (reverse out))

(define (count-where pred lst)
  (for/sum ([x (in-list lst)] #:when (pred x)) 1))

(define (path-only-directory p)
  (define-values (base _dir _) (split-path (path->complete-path p)))
  (if (path? base) base (current-directory)))
