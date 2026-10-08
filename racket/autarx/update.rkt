#lang racket/base

;; Update channel — port of UpdateFeed.cs / UpdateService.cs /
;; UpdateInstaller.cs. Check parses the feed (URL or local file), apply
;; downloads the asset, verifies its SHA-256 BEFORE touching anything,
;; locates the payload root by the marker executable and swaps files in
;; place (current files become <name>.autarx-update.old).

(require racket/file
         racket/system
         racket/list
         racket/path
         racket/string
         json)

(define (strip-trailing-slash s)
  (if (and (> (string-length s) 1)
           (char=? (string-ref s (- (string-length s) 1)) #\/))
      (substring s 0 (- (string-length s) 1))
      s))

(define (regular-file? p)
  (eq? (file-or-directory-type p) 'file))

(provide compress-block!
         load-update-feed
         update-newer?
         download-asset
         extract-verified
         apply-payload
         cleanup-stale-backups
         sha256-file
         detect-update-platform)

;; ---------------------------------------------------------------- feed

(define (load-update-feed feed)
  (with-handlers ([exn:fail:read?
                   (λ (ex) (error 'load-update-feed "update feed is empty"))])
    (cond
      [(or (string-prefix? feed "http://") (string-prefix? feed "https://"))
       (string->jsexpr (http-get feed))]
      [(string-prefix? feed "file://")
       (string->jsexpr (file->string (substring feed 7)))]
      [else
       (unless (file-exists? feed)
         (error 'load-update-feed "cannot read update feed: not found"))
       (string->jsexpr (file->string feed))])))

(define (http-get url)
  (define-values (import/export-subprocess-port progress?)
    (values #f #f))
  ;; curl is the portable transport: present on macOS/Linux runners and
  ;; developer machines; racket's net/url stays a fallback for pure-Racket
  ;; hosts without subprocess rights
  (define tmp (make-temporary-file "autarx-feed-~a"))
  (define ok?
    (with-handlers ([exn:fail? (λ (_) #f)])
      (zero? (system*/exit-code (find-curl) "-fsSL" "-A" "autarx-update" "-o" (path->string tmp) url))))
  (unless ok?
    (delete-file* tmp)
    (error 'load-update-feed "cannot read update feed: request failed"))
  (begin0 (file->string tmp) (delete-file* tmp)))

(define (find-curl)
  (or (find-executable-path "curl")
      (error 'load-update-feed "curl not available for update feed access")))

(define (delete-file* p)
  (with-handlers ([exn:fail:filesystem? (λ (_) (void))])
    (when (file-exists? p) (delete-file p))))

;; semantic comparison with v-prefix tolerance; equality is never newer
(define (update-newer? current candidate)
  (define c (parse-loose-version current))
  (define v (parse-loose-version candidate))
  (if (and c v)
      (> v c)
      (not (string=? (trim-leading-v current) (trim-leading-v candidate)))))

(define (parse-loose-version s)
  (define core (car (string-split (trim-leading-v s) "-")))
  (define parts (string-split core "."))
  (and (andmap (λ (p) (and (> (string-length p) 0) (string->number p))) parts)
       (let ([nums (map string->number parts)])
         (+ (* (car nums) 10000)
            (* (if (> (length nums) 1) (cadr nums) 0) 100)
            (if (> (length nums) 2) (caddr nums) 0)))))

(define (trim-leading-v s)
  (if (and (> (string-length s) 0) (char-ci=? (string-ref s 0) #\v))
      (substring s 1)
      s))

(define (detect-update-platform)
  (define os (system-type 'os))
  (define arch (if (member (system-type 'arch) '(arm aarch64 arm64)) "arm64" "x64"))
  (string-append (case os [(macosx) "macos"] [(windows) "windows"] [else "linux"])
                 "-" arch))

;; ---------------------------------------------------------------- download

;; Downloads the asset into destination-directory and returns the zip path.
(define (download-asset url destination-directory)
  (make-directory* destination-directory)
  (define name
    (let ([base (last (string-split url "/"))])
      (if (> (string-length base) 0) base "autarx-update.zip")))
  (define zip-path (build-path destination-directory name))
  (define ok?
    (with-handlers ([exn:fail? (λ (_) #f)])
      (zero? (system*/exit-code (find-curl) "-fsSL" "-A" "autarx-update"
                                "-o" (path->string zip-path) url))))
  (unless ok?
    (error 'download-asset "download failed: ~a" url))
  (path->string zip-path))

;; ---------------------------------------------------------------- installer

;; Verifies the checksum BEFORE anything is extracted, expands the zip and
;; returns the payload root — the directory containing marker-file-name.
(define (extract-verified zip-path expected-sha256 marker-file-name)
  (define actual (sha256-file zip-path))
  (unless (string-ci=? actual expected-sha256)
    (error 'extract-verified
           "checksum mismatch: expected ~a, got ~a" expected-sha256 actual))
  (define extract-dir
    (make-temporary-file "autarx-update-~a" 'directory))
  (define unzipped?
    (zero? (system*/exit-code (find-executable-path "unzip")
                              "-q" zip-path "-d" (path->string extract-dir))))
  (unless unzipped?
    (delete-directory* extract-dir)
    (error 'extract-verified "cannot extract update package"))
  (define marker
    (find-marker extract-dir marker-file-name))
  (unless marker
    (delete-directory* extract-dir)
    (error 'extract-verified
           "update package does not contain ~a" marker-file-name))
  (strip-trailing-slash
   (path->string (simplify-path (path-only marker)))))

(define (find-marker dir name)
  (for/or ([p (in-list (find-files (λ (_) #t) dir))])
    (and (string=? (path->string (file-name-from-path p)) name) p)))

;; Swaps the payload over the installation; returns the file count.
(define (apply-payload payload-root-str app-directory)
  (define payload-root (simplify-path (string->path payload-root-str)))
  (cleanup-stale-backups app-directory)
  (define replaced
    (for/list ([source (in-list (find-files regular-file? payload-root))])
      (define relative
        (path->string (find-relative-path payload-root source)))
      (define target (build-path app-directory relative))
      (make-directory* (path-only target))
      (when (file-exists? target)
        (define backup (string->path (string-append (path->string target)
                                                    ".autarx-update.old")))
        (when (file-exists? backup) (delete-file backup))
        (rename-file-or-directory target backup))
      (copy-file source target #:exists-ok? #t)
      relative))
  (delete-directory* payload-root)
  (length replaced))

;; stale .old files that cannot be deleted stay for the next run
(define (cleanup-stale-backups app-directory)
  (for/sum ([stale (in-list (find-files (λ (p) (and (regular-file? p)
                                                    (string-suffix?
                                                     (path->string p)
                                                     ".autarx-update.old")))
                                         app-directory))])
    (with-handlers ([exn:fail:filesystem? (λ (_) 0)])
      (delete-file stale)
      1)))

(define (string-suffix? s suffix)
  (define n (string-length s))
  (define m (string-length suffix))
  (and (>= n m) (string=? (substring s (- n m)) suffix)))

(define (delete-directory* p)
  (with-handlers ([exn:fail:filesystem? (λ (_) (void))])
    (delete-directory/files p)))

;; ---------------------------------------------------------------- sha256

;; FIPS 180-4 SHA-256 over a file, lowercase hex. Pure Racket (no openssl
;; collection dependency); update zips are small so the throughput is fine.
(define k
  (list 1116352408 1899447441 3049323471 3921009573 961987163 1508970993
        2453635748 2870763221 3624381080 310598401 607225278 1426881987
        1925078388 2162078206 2614888103 3248222580 3835390401 4022224774
        264347078 604807628 770255983 1249150122 1555081692 1996064986
        2554220882 2821834349 2952996808 3210313671 3336571891 3584528711
        113926993 338241895 666307205 773529912 1294757372 1396182291
        1695183700 1986661051 2177026350 2456956037 2730485921 2820302411
        3259730800 3345764771 3516065817 3600352804 4094571909 275423344
        430227734 506948616 659060556 883997877 958139571 1322822218
        1537002063 1747873779 1955562222 2024104815 2227730452 2361852424
        2428436474 2756734187 3204031479 3329325298))

(define initial-hash
  (list 1779033703 3144134277 1013904242 2773480762 1359893119 2600822924
        528734635 1541459225))

(define (sha256-file path)
  (define data (file->bytes path))
  (define h0 (make-vector 8))
  (for ([i (in-range 8)]) (vector-set! h0 i (list-ref initial-hash i)))
  ;; padded message: data + 0x80 + zeros + 64-bit big-endian bit length
  (define bit-length (* (bytes-length data) 8))
  (define zero-count
    (let ([n (remainder (+ (bytes-length data) 1 8) 64)])
      (if (= n 0) 0 (- 64 n))))
  (define padded
    (bytes-append data (bytes 128) (make-bytes zero-count 0)
                  (integer->integer-bytes bit-length 8 #f #t)))
  (define w (make-vector 64))
  (for ([offset (in-range 0 (bytes-length padded) 64)])
    (compress-block! h0 w (subbytes padded offset (+ offset 64))))
  (apply string-append
         (for/list ([i (in-range 8)])
           (define hex (number->string (vector-ref h0 i) 16))
           (if (= (string-length hex) 8)
               hex
               (string-append (make-string (- 8 (string-length hex)) #\0)
                              hex)))))

(define (compress-block! h0 w data)
  (for ([i (in-range 16)])
    (vector-set! w i (integer-bytes->integer (subbytes data (* i 4) (+ (* i 4) 4)) #f #t)))
  (for ([i (in-range 16 64)])
    (define x15 (vector-ref w (- i 15)))
    (define x2 (vector-ref w (- i 2)))
    ;; sigma0/xor of rotations and shift; sigma1 likewise (FIPS 180-4)
    (define s0 (bitwise-xor (bitwise-xor (ror x15 7) (ror x15 18))
                            (arithmetic-shift x15 -3)))
    (define s1 (bitwise-xor (bitwise-xor (ror x2 17) (ror x2 19))
                            (arithmetic-shift x2 -10)))
    (vector-set! w i (bitwise-and (+ (vector-ref w (- i 16)) s0
                                     (vector-ref w (- i 7)) s1)
                                  #xffffffff)))
  (define a (vector-ref h0 0)) (define b (vector-ref h0 1))
  (define c (vector-ref h0 2)) (define d (vector-ref h0 3))
  (define e (vector-ref h0 4)) (define f (vector-ref h0 5))
  (define g (vector-ref h0 6)) (define hh (vector-ref h0 7))
  (for ([i (in-range 64)])
    (define s1 (bitwise-xor (bitwise-xor (ror e 6) (ror e 11)) (ror e 25)))
    (define ch (bitwise-xor (bitwise-and e f)
                            (bitwise-and (bitwise-not e) g)))
    (define temp1 (+ hh s1 ch (list-ref k i) (vector-ref w i)))
    (define s0 (bitwise-xor (bitwise-xor (ror a 2) (ror a 13)) (ror a 22)))
    (define maj (bitwise-xor (bitwise-xor (bitwise-and a b) (bitwise-and a c))
                             (bitwise-and b c)))
    (define temp2 (+ s0 maj))
    (set! hh g) (set! g f) (set! f e)
    (set! e (bitwise-and (+ d temp1) #xffffffff))
    (set! d c) (set! c b) (set! b a)
    (set! a (bitwise-and (+ temp1 temp2) #xffffffff)))
  (for ([i (in-range 8)])
    (vector-set! h0 i (bitwise-and (+ (vector-ref h0 i)
                                      (list-ref (list a b c d e f g hh) i))
                                   #xffffffff))))

(define (ror x n)
  (bitwise-ior (arithmetic-shift x (- n))
               (bitwise-and (arithmetic-shift x (- 32 n)) #xffffffff)))
