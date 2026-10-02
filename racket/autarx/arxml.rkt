#lang racket/base

;; Streaming ARXML parser and canonical serializer — the Racket port of
;; Autarx.Core/Parsing (v1.0.3 contract). Behaviour is matched to the .NET
;; implementation line by line:
;;  - matching is on the XML LocalName (namespace prefixes stripped)
;;  - comments, PIs, the XML declaration and DOCTYPE are dropped
;;  - CDATA content is dropped too: the .NET parser only handles
;;    XmlNodeType.Text, so CDATA never reached AppendText there — kept for
;;    hash/behaviour parity
;;  - text nodes are trimmed and concatenated without separators
;;  - entity references are expanded, line ends normalized (CR/CRLF -> LF)

(require racket/string
         racket/file)

(provide (struct-out arxml-element)
         element-child
         element-children-named
         element-child-text
         element-attribute
         element-set-text!
         element-descendants
         parse-arxml
         parse-arxml-file
         exn:fail:autarx:parse?
         exn:fail:autarx:parse-line
         exn:fail:autarx:parse-column
         write-arxml-file
         arxml->string)

;; ---------------------------------------------------------------- elements

;; attrs: (listof (cons string string)) — LocalName -> normalized value, in
;; document order. children is a list in document order once the element is
;; closed (accumulated reversed in a box during construction).
(struct arxml-element (name attrs text children) #:mutable)

(define (element-child e name)
  (let loop ([cs (arxml-element-children e)])
    (cond [(null? cs) #f]
          [(equal? (arxml-element-name (car cs)) name) (car cs)]
          [else (loop (cdr cs))])))

(define (element-children-named e name)
  (filter (λ (c) (equal? (arxml-element-name c) name))
          (arxml-element-children e)))

(define (element-child-text e name)
  (let ([c (element-child e name)])
    (and c (arxml-element-text c))))

(define (element-attribute e name)
  (let loop ([as (arxml-element-attrs e)])
    (cond [(null? as) #f]
          [(equal? (caar as) name) (cdar as)]
          [else (loop (cdr as))])))

;; Replaces this element's text content (semantic patching). Mirrors
;; ArxmlElement.SetText: empty/whitespace-only becomes #f.
(define (element-set-text! e text)
  (set-arxml-element-text!
   e (if (or (not text) (string=? (string-trim text) "")) #f (string-trim text))))

;; Pre-order DFS over the subtree, excluding the element itself — the same
;; document order as the .NET Descendants() stack walk.
(define (element-descendants e)
  (define out '())
  (let walk ([elem e])
    (for ([c (in-list (arxml-element-children elem))])
      (set! out (cons c out))
      (walk c)))
  (reverse out))

;; ---------------------------------------------------------------- errors

(struct exn:fail:autarx:parse exn:fail (line column) #:transparent)

(define (parse-error message buf pos)
  (define line (byte-line-number buf pos))
  (define column (byte-column-number buf pos))
  (raise (exn:fail:autarx:parse (format "autarx parse: ~a" message)
                                (current-continuation-marks) line column)))

(define (byte-line-number buf pos)
  (let loop ([i 0] [line 1])
    (if (>= i pos)
        line
        (loop (add1 i) (if (eqv? (bytes-ref buf i) 10) (add1 line) line)))))

(define (byte-column-number buf pos)
  (let loop ([i pos] [col 0])
    (if (or (= i 0) (eqv? (bytes-ref buf (- i 1)) 10))
        col
        (loop (- i 1) (add1 col)))))

;; ---------------------------------------------------------------- scanning

(define name-char?
  (λ (b)
    (or (and (>= b 97) (<= b 122))          ; a-z
        (and (>= b 65) (<= b 90))           ; A-Z
        (and (>= b 48) (<= b 57))           ; 0-9
        (= b 45) (= b 46) (= b 95)          ; - . _
        (= b 58)                            ; ':' — prefix separator
        (>= b 128))))                        ; non-ASCII pass-through

(define ws-byte?
  (λ (b) (or (= b 32) (= b 9) (= b 10) (= b 13))))

(define (skip-ws buf pos end)
  (let loop ([p pos])
    (if (and (< p end) (ws-byte? (bytes-ref buf p)))
        (loop (add1 p))
        p)))

(define (find-byte buf start end b)
  (let loop ([p start])
    (cond [(>= p end) #f]
          [(eqv? (bytes-ref buf p) b) p]
          [else (loop (add1 p))])))

(define (find-sequence buf start end seq)
  (define n (bytes-length seq))
  (if (= n 0)
      start
      (let loop ([p start])
        (cond [(> p (- end n)) #f]
              [(bytes=? (subbytes buf p (+ p n)) seq) p]
              [else (loop (add1 p))]))))

;; Element names repeat massively across a delivery — intern the conversion
;; so name comparisons land on eq?-equal strings. Names are matched on the
;; LocalName: everything up to the first ':' (the vendor prefix) is dropped.
(define name-table (make-hash))

(define (intern-name! buf start end)
  (define ns-colon
    (let loop ([p start])
      (cond [(>= p end) #f]
            [(eqv? (bytes-ref buf p) 58) p]
            [else (loop (add1 p))])))
  (define s-start (if ns-colon (add1 ns-colon) start))
  (define key (subbytes buf s-start end))
  (or (hash-ref name-table key #f)
      (let ([s (bytes->string/utf-8 key)])
        (hash-set! name-table key s)
        s)))

;; ---- entity expansion & line-end normalization (text and attribute values)

(define (expand-segment buf start end [attr-mode? #f])
  ;; fast path: no '&' and no '\r' — nothing to expand or normalize
  (let loop ([p start])
    (if (>= p end)
        (bytes->string/utf-8 (subbytes buf start end))
        (let ([b (bytes-ref buf p)])
          (if (or (= b 38) (= b 13))
              (expand-slow buf start end attr-mode?)
              (loop (add1 p)))))))

(define (expand-slow buf start end attr-mode?)
  (define out (open-output-bytes))
  (let loop ([p start])
    (when (< p end)
      (define b (bytes-ref buf p))
      (cond
        [(= b 38) ; &
         (define semi (find-byte buf p end 59)) ; ';'
         (unless semi (parse-error "unterminated entity reference" buf p))
         (define name-b (subbytes buf (add1 p) semi))
         (cond
           [(equal? name-b #"amp") (write-byte 38 out)]
           [(equal? name-b #"lt") (write-byte 60 out)]
           [(equal? name-b #"gt") (write-byte 62 out)]
           [(equal? name-b #"quot") (write-byte 34 out)]
           [(equal? name-b #"apos") (write-byte 39 out)]
           [(and (>= (bytes-length name-b) 2) (eqv? (bytes-ref name-b 0) 35))
            (write-codepoint! out name-b buf p)]
           [else (parse-error "reference to undeclared entity" buf p)])
         (loop (add1 semi))]
        [(= b 13) ; \r — CR/CRLF normalize: text to LF, attribute value to space
         (write-byte (if attr-mode? 32 10) out)
         (loop (if (and (< (add1 p) end) (eqv? (bytes-ref buf (add1 p)) 10))
                   (+ p 2)
                   (add1 p)))]
        [(and attr-mode? (= b 9)) ; tab in attribute value -> space
         (write-byte 32 out)
         (loop (add1 p))]
        [(and attr-mode? (= b 10)) ; newline in attribute value -> space
         (write-byte 32 out)
         (loop (add1 p))]
        [else
         (write-byte b out)
         (loop (add1 p))])))
  (bytes->string/utf-8 (get-output-bytes out)))

(define (write-codepoint! out name-b buf at)
  (define n (bytes-length name-b))
  (define cp
    (cond
      [(eqv? (bytes-ref name-b 1) 120)
       (string->number (bytes->string/latin-1 (subbytes name-b 2 n)) 16)]
      [else
       (string->number (bytes->string/latin-1 (subbytes name-b 1 n)) 10)]))
  (unless (and cp (integer? cp) (>= cp 32) (<= cp #x10FFFF) (not (<= 55296 cp 57343)))
    (parse-error "invalid character reference" buf at))
  (write-string (string (integer->char cp)) out))

;; .NET String.Trim() removes Unicode whitespace: Zs/Zl/Zp plus the ASCII
;; control whitespace and NEL. Spelled out for parity with char.IsWhiteSpace.
(define (net-ws? c)
  (define cat (char-general-category c))
  (or (memq cat '(Zs Zl Zp))
      (and (char>=? c #\u0009) (char<=? c #\u000D))
      (char=? c #\u0085)))

(define (net-trim s)
  (define n (string-length s))
  (define start
    (let loop ([i 0]) (if (and (< i n) (net-ws? (string-ref s i))) (loop (add1 i)) i)))
  (if (= start n)
      ""
      (let ([end (let loop ([i (- n 1)])
                   (if (and (>= i start) (net-ws? (string-ref s i))) (loop (- i 1)) i))])
        (substring s start (add1 end)))))

;; ---------------------------------------------------------------- parser

(define (parse-arxml xml-string)
  (parse-bytes (string->bytes/utf-8 xml-string)))

(define (parse-arxml-file path)
  (parse-bytes (file->bytes path)))

(define (parse-bytes buf)
  (define len (bytes-length buf))
  (define root (box #f))
  ;; stack: list of (cons element children-box), children accumulated reversed
  (define stack '())
  (define pos 0)

  (define (attach! e self-closing?)
    (cond
      [(null? stack)
       (when (unbox root)
         (parse-error "Multiple root elements" buf pos))
       (set-box! root e)
       (unless self-closing?
         (set! stack (list (cons e (box '())))))]
      [else
       (define top (car stack))
       (set-box! (cdr top) (cons e (unbox (cdr top))))
       (unless self-closing?
         (set! stack (cons (cons e (box '())) stack)))]))

  (define (close! name)
    (cond
      [(null? stack) (parse-error "unexpected end element" buf pos)]
      [else
       (define top (car stack))
       (define elem (car top))
       (unless (equal? (arxml-element-name elem) name)
         (parse-error
          (format "mismatched end element: expected </~a>, found </~a>"
                  (arxml-element-name elem) name)
          buf pos))
       (set-arxml-element-children! elem (reverse (unbox (cdr top))))
       (set! stack (cdr stack))]))

  (define (append-text! s)
    (when (not (string=? s ""))
      (define top (car stack))
      (define elem (car top))
      (define old (arxml-element-text elem))
      (set-arxml-element-text! elem (if (not old) s (string-append old s)))))

  ;; text between tags [start, stop): ASCII-whitespace fast path, then trim
  ;; + entity expansion; AppendText concatenates without separators
  (define (handle-text start stop)
    (when (pair? stack)
      (let loop ([p start])
        (cond
          [(= p stop) (void)] ; pure whitespace — nothing to append
          [(ws-byte? (bytes-ref buf p)) (loop (add1 p))]
          [else
           (append-text! (net-trim (expand-segment buf start stop)))]))))

  (define (parse-start-tag)
    ;; pos is just past '<'
    (define p0 (skip-ws buf pos len))
    (define ne
      (let loop ([p p0]) (if (and (< p len) (name-char? (bytes-ref buf p))) (loop (add1 p)) p)))
    (when (= ne p0) (parse-error "invalid element name" buf p0))
    (define name (intern-name! buf p0 ne))
    (let parse-attrs ([p (skip-ws buf ne len)] [attrs '()])
      (when (>= p len) (parse-error "unexpected end of input in element" buf p))
      (define b (bytes-ref buf p))
      (cond
        [(= b 62) ; '>'
         (set! pos (add1 p))
         (attach! (arxml-element name (reverse attrs) #f '()) #f)]
        [(= b 47) ; '/>'
         (unless (and (< (add1 p) len) (= (bytes-ref buf (add1 p)) 62))
           (parse-error "expected '>' after '/'" buf p))
         (set! pos (+ p 2))
         (attach! (arxml-element name (reverse attrs) #f '()) #t)]
        [(name-char? b)
         (define ae
           (let loop ([q p]) (if (and (< q len) (name-char? (bytes-ref buf q))) (loop (add1 q)) q)))
         (define a-name (intern-name! buf p ae))
         (define q (skip-ws buf ae len))
         (when (or (>= q len) (not (= (bytes-ref buf q) 61)))
           (parse-error (format "expected '=' after attribute name '~a'" a-name) buf q))
         (define q2 (skip-ws buf (add1 q) len))
         (when (>= q2 len) (parse-error "unterminated attribute value" buf q2))
         (define quote-b (bytes-ref buf q2))
         (unless (or (= quote-b 34) (= quote-b 39))
           (parse-error "attribute value must be quoted" buf q2))
         (define ve (find-byte buf (add1 q2) len quote-b))
         (unless ve (parse-error "unterminated attribute value" buf q2))
         (define value (expand-segment buf (add1 q2) ve #t))
         (parse-attrs (skip-ws buf (add1 ve) len)
                      (cons (cons a-name value) attrs))]
        [else (parse-error "unexpected character in element" buf p)])))

  (let loop ()
    (when (< pos len)
      (define lt (find-byte buf pos len 60)) ; '<'
      (cond
        [(not lt)
         (handle-text pos len)
         (set! pos len)]
        [else
         (when (< pos lt) (handle-text pos lt))
         (set! pos (add1 lt))
         (when (= pos len) (parse-error "unexpected end of input" buf pos))
         (define b (bytes-ref buf pos))
         (cond
           [(= b 47) ; '/' — end tag
            (define gt (find-byte buf pos len 62))
            (unless gt (parse-error "unterminated end element" buf pos))
            (define name (intern-name! buf (skip-ws buf (add1 pos) gt) gt))
            (set! pos (add1 gt))
            (close! name)]
           [(= b 33) ; '!' — comment / CDATA / DOCTYPE, all dropped
            (cond
              [(and (<= (+ pos 3) len)
                    (bytes=? (subbytes buf (add1 pos) (+ pos 3)) #"--"))
               (define end (find-sequence buf (+ pos 3) len #"-->"))
               (unless end (parse-error "unterminated comment" buf pos))
               (set! pos (+ end 3))]
              [(and (<= (+ pos 9) len)
                    (bytes=? (subbytes buf (add1 pos) (+ pos 9)) #"[CDATA["))
               (define end (find-sequence buf (+ pos 9) len #"]]>"))
               (unless end (parse-error "unterminated CDATA section" buf pos))
               (set! pos (+ end 3))]
              [else
               (define gt (find-byte buf pos len 62))
               (unless gt (parse-error "unterminated declaration" buf pos))
               (set! pos (add1 gt))])]
           [(= b 63) ; '?' — XML declaration / PI, dropped
            (define end (find-sequence buf (add1 pos) len #"?>"))
            (unless end (parse-error "unterminated processing instruction" buf pos))
            (set! pos (+ end 2))]
           [else
            (parse-start-tag)])])
      (loop)))
  (or (unbox root)
      (parse-error "No root element found" buf (max 0 (- len 1)))))

;; ---------------------------------------------------------------- writer

;; Canonical ARXML serializer for patched documents: 2-space indent,
;; attributes Ordinal-sorted, root xmlns/xsi keys reconstructed. Deterministic
;; and semantically equivalent — NOT byte-faithful (comments and vendor
;; formatting are gone with the parser).

(define (xml-escape s)
  (define (special? c) (or (char=? c #\&) (char=? c #\<) (char=? c #\>) (char=? c #\")))
  (if (for/first ([c (in-string s)] #:when (special? c)) #t)
      (let ([out (open-output-string)])
        (for ([c (in-string s)])
          (cond [(char=? c #\&) (display "&amp;" out)]
                [(char=? c #\<) (display "&lt;" out)]
                [(char=? c #\>) (display "&gt;" out)]
                [(char=? c #\") (display "&quot;" out)]
                [else (write-char c out)]))
        (get-output-string out))
      s))

(define (attr->string a)
  (format " ~a=\"~a\"" (car a) (xml-escape (cdr a))))

(define (reorder-root-attrs sorted)
  ;; xmlns first; xsi -> xmlns:xsi; schemaLocation -> xsi:schemaLocation
  (define xmlns
    (for/first ([a (in-list sorted)] #:when (equal? (car a) "xmlns")) a))
  (define rest
    (for/list ([a (in-list sorted)] #:unless (equal? (car a) "xmlns"))
      (cond [(equal? (car a) "xsi") (cons "xmlns:xsi" (cdr a))]
            [(equal? (car a) "schemaLocation") (cons "xsi:schemaLocation" (cdr a))]
            [else a])))
  (if xmlns (cons xmlns rest) rest))

(define (sorted-attrs e is-root?)
  (define sorted (sort (arxml-element-attrs e) string<? #:key car))
  (if is-root? (reorder-root-attrs sorted) sorted))

(define (arxml->string root [provenance #f])
  (define out (open-output-string))
  (display "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" out)
  (when (and provenance (not (string=? provenance "")))
    (fprintf out "<!-- ~a -->\n" provenance))
  (let write-element ([e root] [indent 0] [is-root? #t])
    (define pad (make-string (* 2 indent) #\space))
    (define name (arxml-element-name e))
    (define attrs (sorted-attrs e is-root?))
    (define open-tag
      (if (null? attrs)
          name
          (string-append name (apply string-append (map attr->string attrs)))))
    (define children (arxml-element-children e))
    (define text (arxml-element-text e))
    (cond
      [(and (null? children) (not text))
       (fprintf out "~a<~a/>\n" pad open-tag)]
      [(null? children)
       (fprintf out "~a<~a>~a</~a>\n" pad open-tag (xml-escape text) name)]
      [else
       (fprintf out "~a<~a>\n" pad open-tag)
       (for ([c (in-list children)]) (write-element c (add1 indent) #f))
       (fprintf out "~a</~a>\n" pad name)]))
  (get-output-string out))

(define (write-arxml-file root path [provenance #f])
  (call-with-output-file*
   path
   (λ (out) (display (arxml->string root provenance) out))
   #:mode 'text
   #:exists 'replace))
