#lang racket/base

;; Coarse semantic classification of an AUTOSAR element type — port of
;; Autarx.Core/Models/SemanticKind.cs + SemanticClassifier. Deliberately
;; partial: unmapped element types fall back to 'unknown and never fail
;; parsing. Kinds are append-only, never renumbered.

(provide semantic-kind?
         semantic-kinds
         classify
         kind->json
         kind->text)

(define semantic-kinds
  '(unknown system ecu communication-cluster frame pdu signal
            software-component port port-interface ecuc-module))

(define (semantic-kind? v) (memq v semantic-kinds) #t)

(define exact-names
  '#hash(("SYSTEM" . system)
         ("ECU-INSTANCE" . ecu)
         ("CAN-CLUSTER" . communication-cluster)
         ("ETHERNET-CLUSTER" . communication-cluster)
         ("FLEXRAY-CLUSTER" . communication-cluster)
         ("LIN-CLUSTER" . communication-cluster)
         ("CAN-FRAME" . frame)
         ("LIN-FRAME" . frame)
         ("ETHERNET-FRAME" . frame)
         ("FLEXRAY-FRAME" . frame)
         ("I-SIGNAL" . signal)
         ("I-SIGNAL-GROUP" . signal)
         ("SYSTEM-SIGNAL" . signal)
         ("APPLICATION-SW-COMPONENT-TYPE" . software-component)
         ("COMPOSITION-SW-COMPONENT-TYPE" . software-component)
         ("ECUC-MODULE-CONFIGURATION-VALUES" . ecuc-module)))

(define (string-ends-with? s suffix)
  (define n (string-length s))
  (define m (string-length suffix))
  (and (>= n m) (string=? (substring s (- n m)) suffix)))

(define (classify element-type)
  (or (hash-ref exact-names element-type #f)
      (cond
        [(string-ends-with? element-type "-SW-COMPONENT-TYPE") 'software-component]
        [(string-ends-with? element-type "-PORT-PROTOTYPE") 'port]
        [(string-ends-with? element-type "-INTERFACE") 'port-interface]
        [(string-ends-with? element-type "-PDU") 'pdu]
        [(string-ends-with? element-type "-SDU") 'pdu]
        [else 'unknown])))

;; JSON contract: camelCase strings, e.g. "communicationCluster".
(define kind->json
  '#hash((unknown . "unknown")
         (system . "system")
         (ecu . "ecu")
         (communication-cluster . "communicationCluster")
         (frame . "frame")
         (pdu . "pdu")
         (signal . "signal")
         (software-component . "softwareComponent")
         (port . "port")
         (port-interface . "portInterface")
         (ecuc-module . "ecucModule")))

;; Human-readable inventory labels (inspect text output), matching KindText.
(define kind->text
  '#hash((system . "system")
         (ecu . "ecu")
         (communication-cluster . "communication-cluster")
         (frame . "frame")
         (pdu . "pdu")
         (signal . "signal")
         (software-component . "software-component")
         (port . "port")
         (port-interface . "port-interface")
         (ecuc-module . "ecuc-module")
         (unknown . "unknown")))
