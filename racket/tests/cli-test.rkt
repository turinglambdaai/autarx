#lang racket/base

;; CLI launcher contract, exercised through a real subprocess (cli.rkt
;; exposes nothing to require): --version agrees with the repo's rivet.rktd
;; (the version single source), the usage / unhandled-crash exit codes hold
;; (2/3, with the crash log the contract promises), and the --json profile
;; is parseable JSON with the documented camelCase keys — ci.yml's CLI
;; smoke only checks exit codes; this pins the payload shape too.

(require rackunit
         json
         racket/file
         racket/list
         racket/port
         racket/runtime-path
         racket/string
         racket/system)

(define-runtime-path cli-path "../autarx/cli.rkt")
(define-runtime-path repo-root "../..")

(define racket-exe (find-system-path 'exec-file))
(define oem-fixture (build-path repo-root "shared" "fixtures" "OemDelivery"))

;; Runs the CLI in a subprocess; returns (values exit-code stdout stderr).
;; subprocess demands file-stream ports, so both streams ride temp files.
(define (run-cli . args)
  (define out-path (make-temporary-file "autarx-cli-out~a"))
  (define err-path (make-temporary-file "autarx-cli-err~a"))
  (define out (open-output-file out-path #:exists 'truncate))
  (define err (open-output-file err-path #:exists 'truncate))
  (define-values (proc _sub-out _sub-in _sub-err)
    (apply subprocess out #f err racket-exe cli-path args))
  (subprocess-wait proc)
  (close-output-port out)
  (close-output-port err)
  (begin0
    (values (subprocess-status proc)
            (port->string (open-input-file out-path))
            (port->string (open-input-file err-path)))
    (delete-file out-path)
    (delete-file err-path)))

(define expected-version
  (hash-ref (file->value (build-path repo-root "rivet.rktd")) 'version))

(test-case "--version reports the rivet.rktd version, exits 0"
  (for ([flag '("--version" "-v")])
    (define-values (code stdout stderr) (run-cli flag))
    (check-equal? code 0 (format "~a should exit 0" flag))
    (check-equal? stderr "")
    (check-equal? (string-trim stdout)
                  (string-append "autarx " expected-version))))

(test-case "usage errors exit 2"
  ;; the usage text itself prints on stdout; only command errors go to stderr
  (define-values (no-args-code no-args-out _no-args-err) (run-cli))
  (check-equal? no-args-code 2)
  (check-true (string-contains? no-args-out "Usage:")
              "no arguments prints the usage to stdout")
  (define-values (unknown-code _unknown-out unknown-err) (run-cli "bogus-command"))
  (check-equal? unknown-code 2)
  (check-true (string-contains? unknown-err "unknown command 'bogus-command'")
              "an unknown command names the command in the error"))

(test-case "an unhandled failure exits 3 and writes the crash log"
  (define-values (code _stdout stderr)
    (run-cli "inspect" (path->string (build-path repo-root "no" "such" "workspace"))))
  (check-equal? code 3)
  (check-true (string-contains? stderr "unexpected failure"))
  ;; the contract: a log lands under <TEMP>/autarx/crashes — the error
  ;; message names it, so pin that the file is really there
  (define log-line
    (findf (λ (line) (string-contains? line "crash log written to"))
           (string-split stderr "\n")))
  (check-not-false log-line)
  (when log-line
    (define log-path
      (string-trim (second (string-split log-line "crash log written to "))))
    (check-true (file-exists? log-path) "the named crash log exists")))

(test-case "inspect --json emits the documented camelCase profile"
  (define-values (code stdout stderr)
    (run-cli "inspect" (path->string oem-fixture) "--json"))
  (check-equal? code 0)
  (check-equal? stderr "")
  (define doc (string->jsexpr stdout))
  (check-true (hash? doc) "the JSON profile is an object")
  ;; read-json object keys are symbols; the wire spellings stay camelCase
  (for ([key '(workspace fileCount totalElementCount identifiableCount
               packageCount referenceCount unresolvedReferenceCount)])
    (check-true (hash-has-key? doc key)
                (format "profile carries ~a" key)))
  (check-equal? (hash-ref doc 'workspace) (path->string oem-fixture))
  (check-pred exact-nonnegative-integer? (hash-ref doc 'fileCount))
  (check-pred exact-nonnegative-integer? (hash-ref doc 'unresolvedReferenceCount)))
