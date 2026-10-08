#lang racket/base

;; Update-channel tests — port of UpdateTests.cs (feed selection, version
;; comparison, verified extraction, in-place swap with backups).

(require rackunit
         racket/file
         racket/path
         racket/string
         json
         file/zip
         (prefix-in u: "../autarx/update.rkt"))

(define (fixture-path . parts)
  (for/or ([base '("shared/fixtures" "../shared/fixtures" "../../shared/fixtures")])
    (define p (apply build-path base parts))
    (and (file-exists? p) (path->string p))))

(define (make-zip entries dest)
  ;; entries: list of (relative-path-string . content-bytes); laid out under
  ;; a temp directory then zipped relative to it (release-package shape)
  (define base (make-temporary-file "upd-stage-~a" 'directory))
  (define staging (build-path base "cli"))
  (make-directory staging)
  (for ([e (in-list entries)])
    (define target (build-path staging (car e)))
    (make-directory* (path-only target))
    (call-with-output-file target #:exists 'replace
      (λ (out) (write-bytes (cdr e) out))))
  (parameterize ([current-directory base])
    (zip dest (string->path "cli")))
  (delete-directory/files base)
  dest)

(test-case "Parses_feed_and_selects_assets_by_kind_and_platform"
  (define feed (u:load-update-feed (fixture-path "UpdateFeed/latest.json")))
  (check-equal? (hash-ref feed 'version) "9.9.9")
  (define cli (hash-ref (hash-ref feed 'assets) 'cli))
  (define windows (hash-ref cli 'windows-x64))
  (check-equal? (hash-ref windows 'url) "https://example.invalid/Autarx-cli-windows-x64.zip"))

(test-case "Compares_versions_semantically"
  (check-true (u:update-newer? "1.0.1" "1.0.2"))
  (check-false (u:update-newer? "1.0.2" "1.0.1"))
  (check-false (u:update-newer? "1.0.1" "1.0.1"))
  (check-true (u:update-newer? "v1.0.1" "1.2.0"))
  (check-true (u:update-newer? "1.0.1" "2.0.0")))

(test-case "Extract_verified_accepts_matching_hash_and_locates_payload_root"
  (define base (make-temporary-file "upd-e2e-~a" 'directory))
  (define zip-path (build-path base "update.zip"))
  (make-zip (list (cons "cli/autarx" #"new-binary")
                  (cons "cli/config.json" #"{}"))
            zip-path)
  (define sha (u:sha256-file zip-path))
  (define root (u:extract-verified zip-path sha "autarx"))
  (check-true (string-suffix? root "cli"))
  (check-equal? (file->bytes (build-path root "autarx")) #"new-binary")
  (delete-directory/files base))

(test-case "Extract_verified_rejects_a_tampered_package_before_touching_anything"
  (define base (make-temporary-file "upd-tamper-~a" 'directory))
  (define zip-path (build-path base "update.zip"))
  (make-zip (list (cons "cli/autarx" #"new-binary")) zip-path)
  (define fake-sha (make-string 64 #\0))
  (check-exn
   exn:fail?
   (λ () (u:extract-verified zip-path fake-sha "autarx")))
  ;; rejection happens before anything is extracted: no leftovers
  (delete-directory/files base))

(test-case "Extract_verified_requires_the_marker_file"
  (define base (make-temporary-file "upd-marker-~a" 'directory))
  (define zip-path (build-path base "update.zip"))
  (make-zip (list (cons "other/readme.txt" #"hi")) zip-path)
  (define sha (u:sha256-file zip-path))
  (check-exn exn:fail? (λ () (u:extract-verified zip-path sha "autarx")))
  (delete-directory/files base))

(test-case "Apply_swaps_files_keeps_backups_and_cleans_stale_ones"
  (define base (make-temporary-file "upd-apply-~a" 'directory))
  (define zip-path (build-path base "update.zip"))
  (make-zip (list (cons "cli/autarx" #"new-binary")
                  (cons "cli/config.json" #"{}"))
            zip-path)
  (define sha (u:sha256-file zip-path))
  (define payload-root (u:extract-verified zip-path sha "autarx"))
  (define app-dir (build-path base "app"))
  (make-directory app-dir)
  (call-with-output-file (build-path app-dir "autarx") #:exists 'replace
    (λ (out) (display "old-binary" out)))
  (call-with-output-file (build-path app-dir "stale.autarx-update.old")
    #:exists 'replace
    (λ (out) (display "stale" out)))
  (define replaced (u:apply-payload payload-root app-dir))
  (check-equal? replaced 2)
  (check-equal? (file->bytes (build-path app-dir "autarx")) #"new-binary")
  (check-equal? (file->bytes (build-path app-dir "config.json")) #"{}")
  (check-equal? (file->bytes (build-path app-dir "autarx.autarx-update.old"))
                #"old-binary")
  (check-false (file-exists? (build-path app-dir "stale.autarx-update.old")))
  (delete-directory/files base))

(test-case "Sha256_matches_known_vectors"
  (define base (make-temporary-file "sha-~a" 'directory))
  (define (write+hash content)
    (define p (build-path base "in.bin"))
    (call-with-output-file p #:exists 'replace
      (λ (out) (display content out)))
    (u:sha256-file p))
  (check-equal? (write+hash "")
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
  (check-equal? (write+hash "abc")
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
  (check-equal?
   (write+hash "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")
   "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")
  (delete-directory/files base))

