#lang racket/base

;; Communication projection — port of CommunicationProjection.cs. Derived
;; purely from the reference graph: ECU connectors (*CHANNEL-REF), frame
;; triggerings nested under clusters (attributed to the cluster),
;; PDU-to-frame mapping objects (holding both *FRAME-REF and *PDU-REF),
;; signal-to-PDU mappings nested inside PDUs. Relationship directions
;; (sender/receiver) are deliberately not claimed.

(require "index.rkt")

(provide (struct-out cluster-projection)
         (struct-out frame-projection)
         (struct-out pdu-projection)
         (struct-out communication-model)
         build-communication
         communication-model-empty?)

(struct cluster-projection (cluster-path connected-ecus frame-paths))
(struct frame-projection (frame-path pdu-paths))
(struct pdu-projection (pdu-path signal-paths))
(struct communication-model (clusters frames pdus
                                      orphan-frames orphan-pdus orphan-signals
                                      ecuc-comm-modules))

(define (communication-model-empty? m)
  (and (null? (communication-model-clusters m))
       (null? (communication-model-frames m))
       (null? (communication-model-pdus m))))

;; ECUC module short-names whose configuration is communication related;
;; presence only, contents never interpreted
(define communication-module-names
  '("CanIf" "CanSM" "CanNm" "CanTp" "Com" "ComM" "EthIf" "FrIf" "FrSM"
    "LinIf" "LinSM" "PduR" "SecOC" "SoAd" "TcpIp" "Xcp"))

(define (ends-with? s suffix)
  (define n (string-length s))
  (define m (string-length suffix))
  (and (>= n m) (string=? (substring s (- n m)) suffix)))

(define (build-communication index)
  (define objects-by-path (make-hash))
  (for ([o (in-list (workspace-index-objects index))])
    (define p (semantic-object-absolute-path o))
    (unless (hash-ref objects-by-path p #f)
      (hash-set! objects-by-path p o)))

  (define connected-ecus (make-hash))   ; cluster-path -> sorted set of ECU paths
  (define cluster-frames (make-hash))   ; cluster-path -> sorted set of frame paths
  (define frame-pdus (make-hash))       ; frame-path  -> sorted set of PDU paths
  (define pdu-signals (make-hash))      ; pdu-path    -> sorted set of signal paths

  (define (connected map key)
    (or (hash-ref map key #f)
        (let ([s (make-hash)])
          (hash-set! map key s)
          s)))

  ;; ECU -> cluster/channel edges (connectors are nested under the ECU, so
  ;; CHANNEL-REF is attributed to the ECU-INSTANCE)
  (for ([r (in-list (workspace-index-references index))])
    (define source (hash-ref objects-by-path (arx-reference-source-path r) #f))
    (when (and source
               (eq? (semantic-object-kind source) 'ecu)
               (ends-with? (arx-reference-kind r) "CHANNEL-REF"))
      (hash-set! (connected connected-ecus (arx-reference-target-path r))
                 (arx-reference-source-path r) #t)))

  ;; frame mappings: one object holding FRAME-REF + *PDU-REF pairs the two
  ;; endpoints; the mapping object itself stays anonymous
  (define refs-by-source (make-hash))
  (for ([r (in-list (workspace-index-references index))])
    (hash-update! refs-by-source (arx-reference-source-path r)
                  (λ (l) (cons r l)) '()))
  (for ([(source-path group) (in-hash refs-by-source)])
    (define frame-targets
      (for/list ([r (in-list group)]
                 #:when (ends-with? (arx-reference-kind r) "FRAME-REF"))
        (arx-reference-target-path r)))
    (define pdu-targets
      (for/list ([r (in-list group)]
                 #:when (ends-with? (arx-reference-kind r) "PDU-REF"))
        (arx-reference-target-path r)))
    (unless (or (null? frame-targets) (null? pdu-targets))
      (for ([frame (in-list frame-targets)])
        (for ([pdu (in-list pdu-targets)])
          (hash-set! (connected frame-pdus frame) pdu #t)))))

  ;; signal mappings are nested inside the PDU -> attributed to the PDU
  (for ([r (in-list (workspace-index-references index))])
    (when (ends-with? (arx-reference-kind r) "SIGNAL-REF")
      (hash-set! (connected pdu-signals (arx-reference-source-path r))
                 (arx-reference-target-path r) #t)))

  ;; frames under clusters: triggerings nested inside the cluster attribute
  ;; their FRAME-REF to the cluster object
  (for ([r (in-list (workspace-index-references index))])
    (define holder (hash-ref objects-by-path (arx-reference-source-path r) #f))
    (when (and (ends-with? (arx-reference-kind r) "FRAME-REF")
               (not (string=? (arx-reference-source-path r)
                              (arx-reference-target-path r)))
               holder
               (eq? (semantic-object-kind holder) 'communication-cluster))
      (hash-set! (connected cluster-frames (arx-reference-source-path r))
                 (arx-reference-target-path r) #t)))

  (define (sorted-members map key)
    (define s (hash-ref map key #f))
    (if s (sort (hash-keys s) string<?) '()))

  (define cluster-paths
    (sort (remove-dups
           (append (hash-keys connected-ecus) (hash-keys cluster-frames))
           string=?)
          string<?))
  (define clusters
    (for/list ([path (in-list cluster-paths)])
      (cluster-projection path
                          (sorted-members connected-ecus path)
                          (sorted-members cluster-frames path))))

  (define frame-objects
    (filter (λ (o) (eq? (semantic-object-kind o) 'frame))
            (workspace-index-objects index)))
  (define frames
    (for/list ([path (in-list (sort (map semantic-object-absolute-path frame-objects)
                                     string<?))])
      (frame-projection path (sorted-members frame-pdus path))))

  (define pdu-objects
    (filter (λ (o) (eq? (semantic-object-kind o) 'pdu))
            (workspace-index-objects index)))
  (define pdus
    (for/list ([path (in-list (sort (map semantic-object-absolute-path pdu-objects)
                                     string<?))])
      (pdu-projection path (sorted-members pdu-signals path))))

  ;; orphans: objects attached to nothing
  (define attached-pdus (make-hash))
  (for ([(frame members) (in-hash frame-pdus)])
    (for ([pdu (in-hash-keys members)]) (hash-set! attached-pdus pdu #t)))
  (define attached-signals (make-hash))
  (for ([(pdu members) (in-hash pdu-signals)])
    (for ([signal (in-hash-keys members)]) (hash-set! attached-signals signal #t)))
  (define clustered-frames (make-hash))
  (for ([(cluster members) (in-hash cluster-frames)])
    (for ([frame (in-hash-keys members)]) (hash-set! clustered-frames frame #t)))

  (define (paths-of-kind kind)
    (sort (for/list ([o (in-list (workspace-index-objects index))]
                     #:when (eq? (semantic-object-kind o) kind))
            (semantic-object-absolute-path o))
          string<?))

  (define orphan-frames
    (filter (λ (p) (not (hash-ref clustered-frames p #f))) (paths-of-kind 'frame)))
  (define orphan-pdus
    (filter (λ (p) (not (hash-ref attached-pdus p #f))) (paths-of-kind 'pdu)))
  (define orphan-signals
    (filter (λ (p) (not (hash-ref attached-signals p #f))) (paths-of-kind 'signal)))

  (define ecuc-comm-modules
    (sort (remove-dups
           (for/list ([o (in-list (workspace-index-objects index))]
                      #:when (and (eq? (semantic-object-kind o) 'ecuc-module)
                                  (member (semantic-object-short-name o)
                                          communication-module-names
                                          string=?)))
             (semantic-object-short-name o))
           string=?)
          string<?))

  (communication-model clusters frames pdus
                       orphan-frames orphan-pdus orphan-signals
                       ecuc-comm-modules))

(define (remove-dups lst same?)
  (let loop ([lst lst] [seen '()] [out '()])
    (cond
      [(null? lst) (reverse out)]
      [(for/first ([s (in-list seen)] #:when (same? s (car lst))) #t)
       (loop (cdr lst) seen out)]
      [else (loop (cdr lst) (cons (car lst) seen) (cons (car lst) out))])))
