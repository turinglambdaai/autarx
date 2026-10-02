#lang racket/base

;; Headless RVT1 smoke test: drives the backend's stdio serve loop over OS
;; pipes with raw frames, exactly the way an embedded host transport would.

(require rackunit
         racket/string
         "../autarx/index.rkt")

;; minimal RVT1 client
(define magic #"RVT1")
(define message:response 3)
(define message:error 4)
(define message:event 5)

(define (write-u32! n out)
  (write-bytes (integer->integer-bytes n 4 #f #f) out))
(define (write-u64! n out)
  (write-bytes (integer->integer-bytes n 8 #f #f) out))

(define (value->bytes v)
  (define out (open-output-bytes))
  (define (emit x)
    (cond
      [(void? x) (write-byte #x00 out)]
      [(eq? x #f) (write-byte #x01 out)]
      [(eq? x #t) (write-byte #x02 out)]
      [(exact-integer? x)
       (write-byte #x03 out)
       (write-bytes (integer->integer-bytes x 8 #t #f) out)]
      [(string? x)
       (define bs (string->bytes/utf-8 x))
       (write-byte #x04 out)
       (write-u32! (bytes-length bs) out)
       (write-bytes bs out)]
      [(pair? x)
       (define count (length x))
       (write-byte #x06 out)
       (write-u32! count out)
       (for ([item (in-list x)]) (emit item))]))
  (emit v)
  (get-output-bytes out))

(define (bytes->value bs)
  (define in (open-input-bytes bs))
  (define (read-u32)
    (integer-bytes->integer (read-bytes 4 in) #f #f))
  (define (read-u64)
    (integer-bytes->integer (read-bytes 8 in) #f #f #t))
  (let loop ()
    (define tag (read-byte in))
    (case tag
      [(#x00) (void)]
      [(#x01) #f]
      [(#x02) #t]
      [(#x03) (integer-bytes->integer (read-bytes 8 in) #t #f)]
      [(#x04)
       (define len (read-u32))
       (bytes->string/utf-8 (read-bytes len in))]
      [(#x06)
       (define count (read-u32))
       (for/list ([_ (in-range count)]) (loop))]
      [else (error 'bytes->value "unknown tag ~a" tag)])))


(define (send-request! out id method args)
  (define payload (value->bytes (cons method args)))
  (write-bytes magic out)
  (write-byte 1 out)
  (write-byte 2 out)
  (write-u64! id out)
  (write-u32! (bytes-length payload) out)
  (write-bytes payload out)
  (flush-output out))

(define (read-frame in)
  (define head (read-bytes 18 in))
  (when (eof-object? head) (error 'read-frame "backend closed"))
  (define type (bytes-ref head 5))
  (define id (integer-bytes->integer (subbytes head 6 14) #f #f))
  (define len (integer-bytes->integer (subbytes head 14 18) #f #f))
  (define payload (if (zero? len) #"" (read-bytes len in)))
  (values type id payload))

(define (resolve-path-under bases parts)
  (for/or ([base (in-list bases)])
    (define p (apply build-path base parts))
    (and (file-exists? p) (path->string p))))

(define bases
  '("." ".." "../.."))
(define fixtures
  (for/or ([base (in-list bases)])
    (define p (build-path base "shared/fixtures" "OemDelivery"))
    (and (directory-exists? p) (path->string p))))
(define backend-path
  (or (resolve-path-under bases '("racket/autarx/backend.rkt"))
      (error 'smoke "backend not found")))

(test-case "backend-speaks-RVT1-over-stdio"
  (check-true (and fixtures #t) "fixtures found")
  (define-values (proc in out err)
    (apply subprocess
           (list* #f #f #f (find-executable-path "racket")
                  (list backend-path))))
  ;; the backend greets first
  (define-values (hello-type hello-id hello-payload) (read-frame in))
  (check-equal? hello-type 1)
  (send-request! out 1 "open_workspace" (list fixtures))
  (define responses '())
  (let loop ()
    (define-values (type id payload) (read-frame in))
    (set! responses (cons (list type payload) responses))
    (unless (= type message:response) (loop)))
  ;; open_workspace returns a WorkspaceSummary record (list of 10 fields);
  ;; the last frame before it is the progress event
  (define response-payload (cadar responses))
  (define summary (bytes->value response-payload))
  (check-true (list? summary))
  (check-true (>= (length summary) 8))
  ;; source path is the first field
  (check-true (string-contains? (car summary) "OemDelivery"))
  ;; progress event fired during open
  (check-true (ormap (λ (r) (= (car r) message:event)) (reverse responses)))

  (send-request! out 2 "find_objects" (list "VehicleSpeed"))
  (let loop ()
    (define-values (type id payload) (read-frame in))
    (if (= type message:response)
        (let ([found (bytes->value payload)])
          (check-true (list? found))
          (check-pred exact-integer? (length found)))
        (loop)))

  (send-request! out 3 "communication" '())
  (let loop ()
    (define-values (type id payload) (read-frame in))
    (if (= type message:response)
        (let ([comm (bytes->value payload)])
          ;; CommunicationDto: [clusters, orphan-frames, orphan-pdus, orphan-signals]
          (check-equal? (length comm) 4))
        (loop)))

  ;; shutdown
  (write-bytes magic out)
  (write-byte 1 out)
  (write-byte 7 out)
  (write-u64! 0 out)
  (write-u32! 0 out)
  (flush-output out)
  (subprocess-wait proc)
  (check-equal? (subprocess-status proc) 0))
