#lang racket/base

;; Contract tests ported from the .NET suite (tests/Autarx.Tests, v1.0.3).
;; The fixtures stay in their original location; every assertion below
;; mirrors the .NET test of the same name.

(require rackunit
         racket/file
         racket/list
         racket/string
         "../autarx/arxml.rkt"
         "../autarx/classify.rkt"
         "../autarx/hash.rkt"
         "../autarx/release.rkt"
         "../autarx/index.rkt"
         "../autarx/ecuc.rkt"
         "../autarx/validate.rkt"
         "../autarx/comm.rkt"
         "../autarx/diff.rkt"
         "../autarx/impact.rkt"
         "../autarx/patch.rkt"
         "../autarx/vendor.rkt")

;; fixtures resolve from the repo root (raco test racket/) or racket/
(define (fixture . parts)
  (define candidates
    (list (apply build-path "shared/fixtures" parts)
          (apply build-path "../shared/fixtures" parts)
          (apply build-path "../../shared/fixtures" parts)))
  (define hit
    (for/or ([p (in-list candidates)]
             #:when (or (file-exists? p) (directory-exists? p)))
      p))
  (unless hit (error 'fixture "fixture not found: ~a" parts))
  (path->string hit))

(define oem (fixture "OemDelivery"))
(define comm-delivery (fixture "CommDelivery" "comm.arxml"))
(define comm-dir (fixture "CommDelivery"))
(define diff-before (fixture "DiffBefore"))
(define diff-after (fixture "DiffAfter"))
(define ambiguous (fixture "Ambiguous"))
(define dup-paths (fixture "DuplicatePaths"))
(define minimal (fixture "minimal.arxml"))

;; ---------------------------------------------------------------- parser

(test-case "Parses_fixture_root_element"
  (check-equal? (arxml-element-name (parse-arxml-file minimal)) "AUTOSAR"))

(test-case "Strips_namespace_prefixes"
  (define root
    (parse-arxml
     "<?xml version=\"1.0\"?><a:AUTOSAR xmlns:a=\"http://autosar.org/schema/r4.0\"><a:SHORT-NAME>x</a:SHORT-NAME></a:AUTOSAR>"))
  (check-equal? (arxml-element-name root) "AUTOSAR")
  (check-equal? (element-child-text root "SHORT-NAME") "x"))

(test-case "Reads_attributes_by_local_name"
  (define root
    (parse-arxml
     "<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"http://autosar.org/schema/r4.0 AUTOSAR_4-3-0.xsd\"/>"))
  (check-equal? (element-attribute root "schemaLocation")
                "http://autosar.org/schema/r4.0 AUTOSAR_4-3-0.xsd"))

(test-case "Counts_fixture_elements"
  (define root (parse-arxml-file minimal))
  (define names (map arxml-element-name (element-descendants root)))
  (check-equal? (count (λ (n) (string=? n "ECUC-MODULE-CONFIGURATION-VALUES")) names) 2)
  (check-equal? (count (λ (n) (string=? n "ECUC-CONTAINER-VALUE")) names) 3))

(test-case "Invalid_xml_reports_line_number"
  (define ex
    (with-handlers ([exn:fail:autarx:parse? values])
      (parse-arxml "<AUTOSAR>\n<BROKEN>\n</AUTOSAR>")))
  (check-pred exn:fail:autarx:parse? ex)
  (check-equal? (exn:fail:autarx:parse-line ex) 3))

(test-case "Empty_input_throws"
  (check-exn exn:fail:autarx:parse? (λ () (parse-arxml ""))))

;; ---------------------------------------------------------------- ecuc

(define minimal-modules (read-modules (parse-arxml-file minimal)))

(test-case "Reads_both_modules"
  (check-equal? (map ecuc-module-short-name minimal-modules) '("Mcu" "Can"))
  (check-equal? (ecuc-module-definition-ref (car minimal-modules)) "/AURIX2G/Mcu")
  (check-equal? (ecuc-module-definition-ref (cadr minimal-modules)) "/AURIX2G/Can"))

(test-case "Reads_containers_recursively"
  (define mcu (car minimal-modules))
  (check-equal? (length (ecuc-module-containers mcu)) 1)
  (define general (car (ecuc-module-containers mcu)))
  (check-equal? (ecuc-container-short-name general) "McuGeneralConfiguration")
  (check-equal? (length (ecuc-container-sub-containers general)) 1)
  (check-equal? (ecuc-container-short-name
                 (car (ecuc-container-sub-containers general)))
                "McuModuleConfiguration"))

(test-case "Reads_parameter_values"
  (define mcu (car minimal-modules))
  (define general (car (ecuc-module-containers mcu)))
  (define param (car (ecuc-container-parameters general)))
  (check-equal? (ecuc-parameter-value param) "1")
  (check-equal? (ecuc-parameter-value (cadr (ecuc-container-parameters general))) "ON")
  (check-equal?
   (ecuc-parameter-value
    (car (ecuc-container-parameters
          (car (ecuc-container-sub-containers general)))))
   "80000000"))

(test-case "Reads_reference_values"
  (define can (cadr minimal-modules))
  (define refs (ecuc-container-references (car (ecuc-module-containers can))))
  (check-equal? (length refs) 1)
  (check-equal? (ecuc-reference-value-value-ref (car refs))
                "/Mcu/Mcu/McuModuleConfiguration"))

(test-case "Computes_totals"
  (check-equal? (module-total-containers (car minimal-modules)) 2)
  (check-equal? (module-total-parameters (car minimal-modules)) 3)
  (check-equal? (module-total-containers (cadr minimal-modules)) 1)
  (check-equal? (module-total-parameters (cadr minimal-modules)) 1))

;; ---------------------------------------------------------------- classify

(define classification-cases
  '(("SYSTEM" system)
    ("ECU-INSTANCE" ecu)
    ("CAN-CLUSTER" communication-cluster) ("ETHERNET-CLUSTER" communication-cluster)
    ("CAN-FRAME" frame) ("LIN-FRAME" frame)
    ("I-SIGNAL" signal) ("SYSTEM-SIGNAL" signal)
    ("I-SIGNAL-I-PDU" pdu) ("N-PDU" pdu) ("SECURED-I-PDU" pdu)
    ("APPLICATION-SW-COMPONENT-TYPE" software-component)
    ("COMPOSITION-SW-COMPONENT-TYPE" software-component)
    ("ECU-ABSTRACTION-SW-COMPONENT-TYPE" software-component)
    ("P-PORT-PROTOTYPE" port) ("R-PORT-PROTOTYPE" port)
    ("SENDER-RECEIVER-INTERFACE" port-interface)
    ("CLIENT-SERVER-INTERFACE" port-interface)
    ("ECUC-MODULE-CONFIGURATION-VALUES" ecuc-module)
    ("PDU-TO-FRAME-MAPPING" unknown)
    ("ISIGNAL-TO-I-PDU-MAPPING" unknown)
    ("SOME-FUTURE-TYPE" unknown)))

(for ([case (in-list classification-cases)])
  (test-case (format "classifies ~a" (car case))
    (check-eq? (classify (car case)) (cadr case))))

;; ---------------------------------------------------------------- index

(define oem-index (build-workspace-index oem))

(test-case "Indexes_both_files_and_all_identifiables"
  (check-equal? (length (workspace-index-documents oem-index)) 2)
  (check-equal? (length (workspace-index-objects oem-index)) 11)
  (check-equal? (workspace-index-package-count oem-index) 11))

(test-case "Builds_absolute_paths_across_nested_packages"
  (define paths
    (map semantic-object-absolute-path (workspace-index-objects oem-index)))
  (for ([p '("/Vehicle/Systems/VehicleSystem"
             "/Vehicle/Ecus/RadarFL"
             "/Vehicle/Clusters/VehicleCan"
             "/Vehicle/Signals/VehicleSpeed"
             "/Vehicle/Frames/Frame_VehicleSpeed")])
    (check-true (and (member p paths) #t) p)))

(test-case "Classifies_semantic_kinds"
  (define (kind-of path)
    (for/or ([o (in-list (workspace-index-objects oem-index))]
             #:when (string=? (semantic-object-absolute-path o) path))
      (semantic-object-kind o)))
  (check-eq? (kind-of "/Vehicle/Systems/VehicleSystem") 'system)
  (check-eq? (kind-of "/Vehicle/Ecus/RadarFL") 'ecu)
  (check-eq? (kind-of "/Vehicle/Clusters/VehicleCan") 'communication-cluster)
  (check-eq? (kind-of "/Vehicle/Pdus/Pdu_VehicleSpeed") 'pdu)
  (check-eq? (kind-of "/Vehicle/Frames/Frame_VehicleSpeed") 'frame)
  (check-eq? (kind-of "/Vehicle/SwCs/SpeedSensor") 'software-component)
  (check-eq? (kind-of "/Vehicle/Interfaces/IF_VehicleSpeed") 'port-interface)
  (check-eq? (kind-of "/Vehicle/FrameMappings/Map_VehicleSpeed") 'unknown))

(test-case "Finds_by_name_substring_case_insensitive"
  (define found (find-by-name oem-index "Speed"))
  (check-equal?
   (map semantic-object-absolute-path found)
   '("/Vehicle/FrameMappings/Map_VehicleSpeed"
     "/Vehicle/Frames/Frame_VehicleSpeed"
     "/Vehicle/Interfaces/IF_VehicleSpeed"
     "/Vehicle/Pdus/Pdu_VehicleSpeed"
     "/Vehicle/Signals/VehicleSpeed"
     "/Vehicle/SwCs/SpeedSensor")))

(test-case "Resolves_paths_and_names"
  (check-eq? (resolution-status (resolve-object oem-index "/Vehicle/Signals/VehicleSpeed"))
             'found)
  (check-eq? (resolution-status (resolve-object oem-index "VehicleSpeed")) 'found)
  (check-eq? (resolution-status (resolve-object oem-index "/No/Such/Object"))
             'not-found)
  (check-eq? (resolution-status (resolve-object oem-index "NoSuchName")) 'not-found))

(test-case "Reports_ambiguous_short_names_instead_of_guessing"
  (define idx (build-workspace-index ambiguous))
  (define r (resolve-object idx "SameName"))
  (check-eq? (resolution-status r) 'ambiguous)
  (check-equal?
   (map semantic-object-absolute-path (resolution-candidates r))
   '("/A/SameName" "/B/SameName"))
  (check-eq? (resolution-status (resolve-object idx "/A/SameName")) 'found))

(test-case "Detects_duplicate_paths_across_files"
  (define idx (build-workspace-index dup-paths))
  (define dups (workspace-index-duplicate-paths idx))
  (check-equal? (length dups) 1)
  (check-equal? (duplicate-path-absolute-path (car dups)) "/Dup/DupEcu")
  (check-equal? (length (duplicate-path-source-files (car dups))) 2)
  ;; path lookup deterministically resolves to the file sorted first
  (check-true (string-suffix? (semantic-object-source-file
                               (resolution-object (resolve-object idx "/Dup/DupEcu")))
                              "dup-a.arxml")))

(test-case "Single_ecuc_file_indexes_modules_but_no_definition_refs"
  (define idx (build-workspace-index minimal))
  (check-equal? (length (workspace-index-objects idx)) 2)
  (define refs (workspace-index-references idx))
  (check-equal? (length refs) 1)
  (check-equal? (arx-reference-kind (car refs)) "VALUE-REF")
  (check-false (arx-reference-resolved (car refs))))

;; ---------------------------------------------------------------- refs

(test-case "Counts_all_references_and_the_unresolved_one"
  (check-equal? (length (workspace-index-references oem-index)) 8)
  (define unresolved
    (filter (compose not arx-reference-resolved)
            (workspace-index-references oem-index)))
  (check-equal? (length unresolved) 1)
  (check-equal? (arx-reference-kind (car unresolved)) "REQUIRED-INTERFACE-TREF")
  (check-equal? (arx-reference-target-path (car unresolved))
                "/Vehicle/Interfaces/IF_Calibration"))

(test-case "Outgoing_references_carry_the_owning_object_and_nested_chain"
  (define out
    (outgoing-of oem-index "/Vehicle/Pdus/Pdu_VehicleSpeed"))
  (check-equal? (length out) 1)
  (check-equal? (arx-reference-kind (car out)) "I-SIGNAL-REF")
  (check-equal? (arx-reference-target-path (car out))
                "/Vehicle/Signals/VehicleSpeed")
  (check-true (arx-reference-resolved (car out)))
  (check-equal? (arx-reference-source-element-path (car out))
                "/Vehicle/Pdus/Pdu_VehicleSpeed/VehicleSpeedMapping"))

(test-case "Reverse_references_work_without_special_casing"
  (define in (incoming-of oem-index "/Vehicle/Signals/VehicleSpeed"))
  (check-equal? (length in) 1)
  (check-equal? (arx-reference-kind (car in)) "I-SIGNAL-REF")
  (check-equal? (arx-reference-source-path (car in))
                "/Vehicle/Pdus/Pdu_VehicleSpeed"))

(test-case "Ecu_connectors_reach_the_cluster_through_nested_refs"
  (define out (outgoing-of oem-index "/Vehicle/Ecus/RadarFL"))
  (check-equal? (length out) 1)
  (check-equal? (arx-reference-target-path (car out))
                "/Vehicle/Clusters/VehicleCan")
  (check-true (string-suffix? (arx-reference-source-element-path (car out))
                              "Con_RadarFL"))
  (check-equal?
   (length (incoming-of oem-index "/Vehicle/Clusters/VehicleCan")) 3))

(test-case "References_resolve_across_files"
  (for ([r (in-list (workspace-index-references oem-index))]
        #:when (string-suffix? (arx-reference-kind r) "CHANNEL-REF"))
    (check-true (arx-reference-resolved r))))

;; ---------------------------------------------------------------- summary

(test-case "Counts_files_elements_and_packages"
  (define s (summarize oem-index))
  (check-equal? (workspace-summary-file-count s) 2)
  (check-equal? (workspace-summary-package-count s) 11)
  (check-equal? (workspace-summary-identifiable-count s) 11)
  (check-true (> (workspace-summary-total-element-count s) 50))
  (check-true (> (workspace-summary-total-file-bytes s) 0)))

(test-case "Counts_references_and_unresolved"
  (define s (summarize oem-index))
  (check-equal? (workspace-summary-reference-count s) 8)
  (check-equal? (workspace-summary-unresolved-reference-count s) 1))

(test-case "Reports_semantic_inventory"
  (define s (summarize oem-index))
  (define (count-of k)
    (cdr (or (assq k (workspace-summary-semantic-counts s)) (cons k 0))))
  (check-equal? (count-of 'system) 1)
  (check-equal? (count-of 'ecu) 3)
  (check-equal? (count-of 'communication-cluster) 1)
  (check-equal? (count-of 'frame) 1)
  (check-equal? (count-of 'pdu) 1)
  (check-equal? (count-of 'signal) 1)
  (check-equal? (count-of 'software-component) 1)
  (check-equal? (count-of 'port) 2)
  (check-equal? (count-of 'port-interface) 1))

(test-case "Counts_nested_named_elements_including_unknowns"
  (define s (summarize oem-index))
  (check-equal? (workspace-summary-unknown-named-count s) 5)
  (check-equal?
   (+ (workspace-summary-unknown-named-count s)
      (for/sum ([p (in-list (workspace-summary-semantic-counts s))]) (cdr p)))
   17))

(test-case "Derives_autosar_release_from_schema_location"
  (define s (summarize oem-index))
  (check-equal? (workspace-summary-namespaces s)
                '("http://autosar.org/schema/r4.0"))
  (check-equal? (workspace-summary-autosar-release s) "4.3.0")
  (check-false (workspace-summary-mixed-schema s)))

(test-case "Release_parser_handles_known_patterns"
  (check-equal? (derive-autosar-release "AUTOSAR_4-3-0.xsd") "4.3.0")
  (check-equal? (derive-autosar-release "AUTOSAR_R24-11.xsd") "R24-11")
  (check-equal? (derive-autosar-release "AUTOSAR_00050.xsd") "00050")
  (check-false (derive-autosar-release "vendor-specific.xsd"))
  (check-false (derive-autosar-release #f)))

;; ---------------------------------------------------------------- trace

(define pdu-path "/Vehicle/Pdus/Pdu_VehicleSpeed")

(test-case "Both_directions_depth_one"
  (define r (trace oem-index pdu-path 'both 1))
  (check-equal? (length (trace-result-nodes r)) 3)
  (check-equal? (length (trace-result-edges r)) 2)
  (check-true
   (for/or ([e (in-list (trace-result-edges r))])
     (and (string=? (trace-edge-kind e) "I-SIGNAL-REF")
          (string=? (trace-edge-target-path e) "/Vehicle/Signals/VehicleSpeed")
          (= (trace-edge-depth e) 1))))
  (check-true
   (for/or ([e (in-list (trace-result-edges r))])
     (and (string=? (trace-edge-kind e) "I-PDU-REF")
          (string=? (trace-edge-source-path e)
                    "/Vehicle/FrameMappings/Map_VehicleSpeed")
          (= (trace-edge-depth e) 1)))))

(test-case "Both_directions_depth_two_reaches_the_frame"
  ;; nodes deduplicate by path (min depth); edges do NOT deduplicate
  (define r (trace oem-index pdu-path 'both 2))
  (check-equal? (length (trace-result-nodes r)) 4)
  (check-equal? (length (trace-result-edges r)) 5)
  (check-true
   (for/or ([n (in-list (trace-result-nodes r))])
     (and (string=? (trace-node-path n) "/Vehicle/Frames/Frame_VehicleSpeed")
          (= (trace-node-depth n) 2)))))

(test-case "Outgoing_only_does_not_walk_upstream"
  (define r (trace oem-index pdu-path 'outgoing 2))
  (check-equal? (length (trace-result-nodes r)) 2)
  (for ([e (in-list (trace-result-edges r))])
    (check-equal? (trace-edge-source-path e) pdu-path)))

(test-case "Incoming_only_lists_every_ecu_attached_to_the_cluster"
  (define r (trace oem-index "/Vehicle/Clusters/VehicleCan" 'incoming 1))
  (check-equal? (length (trace-result-edges r)) 3)
  (for ([e (in-list (trace-result-edges r))])
    (check-equal? (trace-edge-kind e) "CHANNEL-REF")
    (check-equal? (trace-edge-target-path e) "/Vehicle/Clusters/VehicleCan"))
  (check-true
   (for/or ([n (in-list (trace-result-nodes r))])
     (string=? (trace-node-path n) "/Vehicle/Ecus/Gateway"))))

(test-case "Depth_zero_returns_only_the_root"
  (define r (trace oem-index pdu-path 'both 0))
  (check-equal? (length (trace-result-nodes r)) 1)
  (check-equal? (trace-node-path (car (trace-result-nodes r))) pdu-path)
  (check-equal? (length (trace-result-edges r)) 0))

;; ---------------------------------------------------------------- diff

(test-case "Same_workspace_yields_empty_diff"
  (define d (compare-workspaces (build-workspace-index diff-before)
                                (build-workspace-index diff-before)))
  (check-true (workspace-diff-empty? d)))

(test-case "Detects_added_removed_and_modified_objects"
  (define d (compare-workspaces (build-workspace-index diff-before)
                                (build-workspace-index diff-after)))
  (define types
    (for/hash ([c (in-list (workspace-diff-objects d))])
      (values (object-change-absolute-path c)
              (object-change-change-type c))))
  (check-equal? (hash-ref types "/Vehicle/Ecus/Display") 'added)
  (check-equal? (hash-ref types "/Vehicle/Ecus/Legacy") 'removed)
  (check-equal? (hash-ref types "/Vehicle/Clusters/SpareCan") 'removed)
  (check-equal? (hash-ref types "/Config/Diag") 'modified)
  (check-false (hash-ref types "/Vehicle/Ecus/RadarFL" #f))
  (check-false (hash-ref types "/Vehicle/Systems/VehicleSystem" #f)))

(test-case "Explains_parameter_and_reference_changes_at_property_level"
  (define d (compare-workspaces (build-workspace-index diff-before)
                                (build-workspace-index diff-after) #t))
  (define diag
    (for/or ([c (in-list (workspace-diff-objects d))]
             #:when (string=? (object-change-absolute-path c) "/Config/Diag"))
      c))
  (define props (object-change-properties diag))
  (define value-change
    (for/or ([p (in-list props)]
             #:when (string-suffix? (property-change-path p) "/DiagParamA/VALUE"))
      p))
  (check-equal? (property-change-change value-change) 'changed)
  (check-equal? (property-change-before value-change) "1")
  (check-equal? (property-change-after value-change) "2")
  (define ref-change
    (for/or ([p (in-list props)]
             #:when (string-suffix? (property-change-path p)
                                    "/DiagTesterRef/VALUE-REF"))
      p))
  (check-equal? (property-change-before ref-change) "/Vehicle/Ecus/Legacy")
  (check-equal? (property-change-after ref-change) "/Vehicle/Ecus/RadarFL")
  (check-false (for/or ([p (in-list props)]
                        #:when (string-contains? (property-change-path p)
                                                 "DiagParamB"))
                 #t)))

(test-case "Flags_element_type_change_under_same_identity"
  (define d (compare-workspaces (build-workspace-index diff-before)
                                (build-workspace-index diff-after)))
  (define app
    (for/or ([c (in-list (workspace-diff-objects d))]
             #:when (string=? (object-change-absolute-path c) "/SwCs/AdaptiveApp"))
      c))
  (check-equal? (object-change-change-type app) 'modified)
  (check-true (object-change-element-type-changed? app))
  (check-equal? (object-change-before-element-type app)
                "APPLICATION-SW-COMPONENT-TYPE")
  (check-equal? (object-change-after-element-type app)
                "COMPOSITION-SW-COMPONENT-TYPE"))

(test-case "Diffs_references_by_semantic_identity_not_by_file"
  (define d (compare-workspaces (build-workspace-index diff-before)
                                (build-workspace-index diff-after)))
  (check-equal? (length (workspace-diff-references d)) 4)
  (define (has? type source target [kind #f])
    (for/or ([r (in-list (workspace-diff-references d))])
      (and (eq? (reference-change-change-type r) type)
           (string=? (reference-change-source-path r) source)
           (string=? (reference-change-target-path r) target)
           (or (not kind) (string=? (reference-change-kind r) kind)))))
  (check-true (has? 'removed "/Vehicle/Ecus/Legacy" "/Vehicle/Clusters/SpareCan"))
  (check-true (has? 'removed "/Config/Diag" "/Vehicle/Ecus/Legacy" "VALUE-REF"))
  (check-true (has? 'added "/Vehicle/Ecus/Display" "/Vehicle/Clusters/VehicleCan"))
  (check-true (has? 'added "/Config/Diag" "/Vehicle/Ecus/RadarFL" "VALUE-REF")))

(test-case "Without_detail_modified_objects_carry_no_property_explanation"
  (define d (compare-workspaces (build-workspace-index diff-before)
                                (build-workspace-index diff-after) #f))
  (define diag
    (for/or ([c (in-list (workspace-diff-objects d))]
             #:when (string=? (object-change-absolute-path c) "/Config/Diag"))
      c))
  (check-false (object-change-properties diag)))
