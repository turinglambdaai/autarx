#lang racket/base

;; Compose the `autarx update` feed (latest.json) from the release
;; directory. Feed shape is the contract in racket/autarx/update.rkt +
;; cli.rkt: {"version": …, "assets": {"cli": {"<platform>": {"url": …,
;; "sha256": …}}}}. The gui leg ships the DMG for manual install and is
;; deliberately not in the feed — self-update covers the CLI.
;;
;; Usage: racket scripts/make-update-feed.rkt <version> <dist-dir> <base-url>
;; Writes <dist-dir>/latest.json.

(require racket/file
         racket/format
         racket/string
         racket/port
         racket/path
         racket/system
         json)

(define version (vector-ref (current-command-line-arguments) 0))
(define dist (vector-ref (current-command-line-arguments) 1))
(define base-url (string-trim (vector-ref (current-command-line-arguments) 2) "/" #:right? #t))

(define (sha256-file path)
  (define out
    (with-output-to-string
      (lambda ()
        (unless (zero? (system*/exit-code (find-executable-path "shasum") "-a" "256" path))
          (error 'make-update-feed "shasum failed for ~a" path)))))
  (string-trim (car (string-split out))))

(define entries
  (for/hash ([path (in-directory dist)]
             #:when (and (file-exists? path)
                         (regexp-match? #rx"autarx-cli-[0-9][^-]*-(macos-arm64|linux-x64|windows-x64)\\.zip$"
                                        (path->string path))))
    (define name (path->string (file-name-from-path path)))
    (define platform (cadr (regexp-match #rx"autarx-cli-[0-9][^-]*-(macos-arm64|linux-x64|windows-x64)\\.zip$" name)))
    (values (string->symbol platform)
            (hasheq 'url (string-append base-url "/" name)
                    'sha256 (sha256-file path)))))

(when (hash-empty? entries)
  (error 'make-update-feed "no autarx-cli-*.zip artifacts found in ~a" dist))

(define feed (hasheq 'version version
                     'assets (hasheq 'cli entries)))

(define out-path (build-path dist "latest.json"))
(with-output-to-file out-path
  (lambda () (write-json feed))
  #:exists 'truncate/replace)
(printf "make-update-feed: ~a (~a platforms)\n" out-path (length (hash-keys entries)))
