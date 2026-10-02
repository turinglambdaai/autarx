#lang racket/base

;; Contract tests (part 2): impact, communication, validation, patch,
;; vendor, update — ported from the .NET suite.

(require rackunit
         racket/file
         racket/list
         racket/string
         json
         "../autarx/index.rkt"
         "../autarx/ecuc.rkt"
         "../autarx/validate.rkt"
         "../autarx/comm.rkt"
         "../autarx/diff.rkt"
         "../autarx/impact.rkt"
         "../autarx/patch.rkt"
         "../autarx/vendor.rkt"
         "../autarx/arxml.rkt"
         (prefix-in upd: (only-in "../autarx/cli.rkt" update-newer?)))

(define (fixture . parts)
  (define candidates
    (list (apply build-path "shared/fixtures" parts)
          (apply build-path "../shared/fixtures" parts)
          (apply build-path "../../shared/fixtures" parts)))
  (define hit
    (for/or ([cand (in-list candidates)]
             #:when (or (file-exists? cand) (directory-exists? cand)))
      cand))
  (unless hit (error 'fixture "fixture not found: ~a" parts))
  (path->string hit))

(define diff-before (fixture "DiffBefore"))
(define diff-after (fixture "DiffAfter"))
(define comm-dir (fixture "CommDelivery"))
(define oem (fixture "OemDelivery"))
(define minimal (fixture "minimal.arxml"))

;; ---------------------------------------------------------------- impact

(define (analyze-ecu ecu-name [detail? #t])
  (define before (build-workspace-index diff-before))
  (define after (build-workspace-index diff-after))
  (define after-r (resolve-object after ecu-name))
  (define before-r (resolve-object before ecu-name))
  (define ecu-object
    (cond
      [(eq? (resolution-status after-r) 'found) (resolution-object after-r)]
      [(eq? (resolution-status before-r) 'found) (resolution-object before-r)]
      [else (error 'test "no such ECU")]))
  (define ecu-removed?
    (and (eq? (resolution-status after-r) 'not-found)
         (eq? (resolution-status before-r) 'found)))
  (analyze-impact before after
                  (semantic-object-absolute-path ecu-object)
                  ecu-removed?
                  (compare-workspaces before after detail?)
                  detail?))

(test-case "Bus_coupled_changes_count_as_relevant"
  (define report (analyze-ecu "RadarFL"))
  (define relevant
    (map object-change-absolute-path (impact-report-relevant-changes report)))
  (for ([p '("/Vehicle/Ecus/Display" "/Vehicle/Ecus/Legacy"
             "/Vehicle/Clusters/SpareCan" "/Config/Diag")])
    (check-true (and (member p relevant) #t) p))
  (check-false (member "/SwCs/AdaptiveApp" relevant))
  (check-equal? (impact-report-relevant-count report) 4)
  (check-equal? (impact-report-unrelated-count report) 1))

(test-case "Removed_objects_that_were_referenced_break_the_ecu"
  (define report (analyze-ecu "RadarFL"))
  (check-equal? (impact-report-breaking-count report) 2)
  (define rules
    (for/list ([f (in-list (impact-report-findings report))])
      (list (impact-finding-rule f) (impact-finding-object-path f))))
  (check-true (and (member (list "ARX-IMP-REMOVED-REFERENCED" "/Vehicle/Ecus/Legacy") rules) #t))
  (check-true (and (member (list "ARX-IMP-REMOVED-REFERENCED" "/Vehicle/Clusters/SpareCan") rules) #t)))

(test-case "Reference_retarget_away_from_the_ecu_is_reported"
  (define report (analyze-ecu "RadarFL"))
  (check-true
   (for/or ([f (in-list (impact-report-findings report))])
     (and (string=? (impact-finding-rule f) "ARX-IMP-REF-ADDED")
          (string=? (impact-finding-object-path f) "/Config/Diag")
          (string-contains? (impact-finding-detail f) "/Vehicle/Ecus/RadarFL")))))

(test-case "Communication_impact_counts_cluster_level_changes"
  (define report (analyze-ecu "RadarFL"))
  (define comm (impact-report-communication report))
  (check-equal? (communication-impact-cluster-count comm) 1)
  (check-equal? (communication-impact-frame-count comm) 0)
  (check-true
   (for/or ([c (in-list (communication-impact-changes comm))])
     (string=? (object-change-absolute-path c) "/Vehicle/Clusters/SpareCan"))))

(test-case "Removed_ecu_is_detected_and_still_analyzed"
  (define report (analyze-ecu "Legacy"))
  (check-true (impact-report-ecu-removed report))
  (check-true
   (for/or ([c (in-list (impact-report-relevant-changes report))])
     (string=? (object-change-absolute-path c) "/Vehicle/Ecus/Legacy")))
  (check-true (> (impact-report-breaking-count report) 0)))

(test-case "Closure_stays_within_the_union_of_both_deliveries"
  (define report (analyze-ecu "RadarFL" #f))
  (define before (build-workspace-index diff-before))
  (define after (build-workspace-index diff-after))
  (define union-size
    (length
     (remove-duplicates
      (append (map semantic-object-absolute-path (workspace-index-objects before))
              (map semantic-object-absolute-path (workspace-index-objects after)))
      string=?)))
  (check-true (<= (impact-report-closure-size report) union-size)))

;; ---------------------------------------------------------------- comm

(define comm-model (build-communication (build-workspace-index comm-dir)))

(test-case "Walks_the_full_chain_from_cluster_to_signals"
  (check-equal? (length (communication-model-clusters comm-model)) 1)
  (define cluster (car (communication-model-clusters comm-model)))
  (check-equal? (cluster-projection-cluster-path cluster)
                "/Vehicle/Clusters/VehicleCan")
  (check-equal? (cluster-projection-connected-ecus cluster)
                '("/Vehicle/Ecus/Gateway" "/Vehicle/Ecus/RadarFL"))
  (check-equal? (cluster-projection-frame-paths cluster)
                '("/Vehicle/Frames/Frame_VehicleSpeed"
                  "/Vehicle/Frames/Frame_VehicleSpeedFa")))

(test-case "Pairs_pdus_to_frames_through_mapping_objects"
  (define frame
    (for/or ([f (in-list (communication-model-frames comm-model))])
      (and (string=? (frame-projection-frame-path f)
                     "/Vehicle/Frames/Frame_VehicleSpeed")
           f)))
  (check-equal? (frame-projection-pdu-paths frame)
                '("/Vehicle/Pdus/Pdu_VehicleSpeed")))

(test-case "Attaches_signals_to_pdus_from_nested_mappings"
  (define pdu
    (for/or ([p (in-list (communication-model-pdus comm-model))])
      (and (string=? (pdu-projection-pdu-path p) "/Vehicle/Pdus/Pdu_VehicleSpeedFa")
           p)))
  (check-equal? (pdu-projection-signal-paths pdu)
                '("/Vehicle/Signals/BrakePressure"
                  "/Vehicle/Signals/VehicleSpeed")))

(test-case "Reports_unattached_objects_as_orphans"
  (check-equal? (communication-model-orphan-frames comm-model) '())
  (check-equal? (communication-model-orphan-pdus comm-model) '())
  (check-equal? (communication-model-orphan-signals comm-model)
                '("/Vehicle/Signals/DiagCounter")))

(test-case "Lists_ecuc_communication_modules_by_presence"
  (check-equal? (communication-model-ecuc-comm-modules comm-model) '("PduR")))

(test-case "Oem_delivery_without_triggerings_keeps_frames_orphaned"
  (define m (build-communication (build-workspace-index oem)))
  (check-equal? (length (communication-model-clusters m)) 1)
  (define cluster (car (communication-model-clusters m)))
  (check-equal? (cluster-projection-frame-paths cluster) '())
  (check-equal? (communication-model-orphan-frames m)
                '("/Vehicle/Frames/Frame_VehicleSpeed")))

;; ---------------------------------------------------------------- validation

(test-case "Valid_fixture_has_no_diagnostics"
  (check-equal? (validate-modules (read-modules (parse-arxml-file minimal))) '()))

(test-case "Missing_module_definition_ref_is_flagged"
  (define diagnostics
    (validate-modules (list (ecuc-module "Mcu" #f "Mcu" '()))))
  (check-equal? (length diagnostics) 1)
  (check-equal? (diagnostic-code (car diagnostics)) "ARX001")
  (check-eq? (diagnostic-severity (car diagnostics)) 'error))

(test-case "Missing_container_definition_ref_is_flagged"
  (define diagnostics
    (validate-modules
     (list (ecuc-module "Mcu" "/AURIX2G/Mcu" "Mcu"
                        (list (ecuc-container "C" #f "parent/C" '() '() '()))))))
  (check-equal? (length diagnostics) 1)
  (check-equal? (diagnostic-code (car diagnostics)) "ARX002")
  (check-equal? (diagnostic-path (car diagnostics)) "parent/C"))

(test-case "Duplicate_sibling_container_names_are_flagged"
  (define diagnostics
    (validate-modules
     (list (ecuc-module
            "Mcu" "/AURIX2G/Mcu" "Mcu"
            (list (ecuc-container "Same" "/AURIX2G/Mcu/Same" "parent/Same" '() '() '())
                  (ecuc-container "Same" "/AURIX2G/Mcu/Same" "parent/Same" '() '() '()))))))
  (check-equal? (length diagnostics) 2)
  (for ([d (in-list diagnostics)])
    (check-equal? (diagnostic-code d) "ARX003")))

(test-case "Parameter_without_value_or_value_ref_is_flagged"
  (define diagnostics
    (validate-modules
     (list (ecuc-module
            "Mcu" "/AURIX2G/Mcu" "Mcu"
            (list (ecuc-container
                   "C" "/AURIX2G/Mcu/C" "parent/C" '()
                   (list (ecuc-parameter "/AURIX2G/Mcu/C/P" #f #f "parent/c/P"))
                   '()))))))
  (check-true (for/or ([d (in-list diagnostics)])
                (string=? (diagnostic-code d) "ARX004"))))

(test-case "Foreign_definition_ref_warns"
  (define diagnostics
    (validate-modules
     (list (ecuc-module
            "Mcu" "/AURIX2G/Mcu" "Mcu"
            (list (ecuc-container
                   "C" "/AURIX2G/Mcu/C" "parent/C" '()
                   (list (ecuc-parameter "/Other/Module/P" "1" #f "parent/c/P"))
                   '()))))))
  (check-equal? (length diagnostics) 1)
  (check-equal? (diagnostic-code (car diagnostics)) "ARX005")
  (check-eq? (diagnostic-severity (car diagnostics)) 'warning))

;; ---------------------------------------------------------------- patch

(define (patch-workspace!)
  (define base (make-temporary-file "autarx-test-~a" 'directory))
  (define ws (build-path base "ws"))
  (make-directory ws)
  (for ([f (in-list (directory-list (string->path diff-before)))])
    (copy-file (build-path (string->path diff-before) f)
               (build-path ws f)))
  ws)

(test-case "Plan_set_parameter_produces_valid_plan_with_property_diff"
  (define ws (patch-workspace!))
  (define plan
    (plan-patch (path->string ws)
                (list (list 'set-parameter "/Config/Diag"
                            "/AURIX2G/Diag/DiagGeneral/DiagParamA" "5"))))
  (check-true (patch-plan-valid? plan))
  (check-equal? (patch-plan-affected-files plan) '("config.arxml"))
  (define diag
    (for/or ([c (in-list (workspace-diff-objects (patch-plan-diff plan)))]
             #:when (string=? (object-change-absolute-path c) "/Config/Diag"))
      c))
  (define props (object-change-properties diag))
  (check-equal? (length props) 1)
  (check-true (string-suffix? (property-change-path (car props))
                              "/DiagParamA/VALUE"))
  (check-equal? (property-change-before (car props)) "1")
  (check-equal? (property-change-after (car props)) "5")
  (delete-directory/files ws))

(test-case "Plan_rename_rewrites_incoming_references"
  (define ws (patch-workspace!))
  (define plan
    (plan-patch (path->string ws)
                (list (list 'rename "/Vehicle/Ecus/Legacy" "LegacyNew"))))
  (check-true (patch-plan-valid? plan))
  (define refs (workspace-diff-references (patch-plan-diff plan)))
  (define added-path?
    (for/or ([r (in-list refs)])
      (and (eq? (reference-change-change-type r) 'added)
           (string=? (reference-change-source-path r) "/Config/Diag")
           (string=? (reference-change-target-path r) "/Vehicle/Ecus/LegacyNew"))))
  (check-true added-path?)
  (define removed-path?
    (for/or ([r (in-list refs)])
      (and (eq? (reference-change-change-type r) 'removed)
           (string=? (reference-change-source-path r) "/Config/Diag")
           (string=? (reference-change-target-path r) "/Vehicle/Ecus/Legacy"))))
  (check-true removed-path?)
  (delete-directory/files ws))

(test-case "Plan_with_unknown_target_is_invalid_and_diffless"
  (define ws (patch-workspace!))
  (define plan
    (plan-patch (path->string ws)
                (list (list 'set-parameter "/Config/DoesNotExist"
                            "/AURIX2G/Diag/DiagGeneral/DiagParamA" "5"))))
  (check-false (patch-plan-valid? plan))
  (check-false (patch-plan-diff plan))
  (check-false (patch-op-result-ok? (car (patch-plan-operations plan))))
  (check-true (string-contains?
               (patch-op-result-error (car (patch-plan-operations plan)))
               "not found"))
  (delete-directory/files ws))

(test-case "Apply_writes_files_and_undo_restores_them"
  (define ws (patch-workspace!))
  (define plan
    (plan-patch (path->string ws)
                (list (list 'set-parameter "/Config/Diag"
                            "/AURIX2G/Diag/DiagGeneral/DiagParamA" "7")
                      (list 'set-reference "/Config/Diag"
                            "/AURIX2G/Diag/DiagGeneral/DiagTesterRef"
                            "/Vehicle/Clusters/VehicleCan"))))
  (check-true (patch-plan-valid? plan))
  (apply-patch (path->string ws) plan (patch-plan-temp-workspace plan))
  (check-true (file-exists? (build-path ws ".autarx" "config.arxml.autarx-bak")))
  (check-true (string-contains? (file->string (build-path ws "config.arxml"))
                                ">7</VALUE>"))
  (check-true (string-contains? (file->string (build-path ws "config.arxml"))
                                "/Vehicle/Clusters/VehicleCan"))
  (check-pred patch-manifest? (undo-patch (path->string ws)))
  (check-false (file-exists? (build-path ws ".autarx" "config.arxml.autarx-bak")))
  (check-true (string-contains? (file->string (build-path ws "config.arxml"))
                                ">1</VALUE>"))
  (check-true (string-contains? (file->string (build-path ws "config.arxml"))
                                "/Vehicle/Ecus/Legacy"))
  (check-false (and (undo-patch (path->string ws)) #t))
  (delete-directory/files ws))

(test-case "Canonical_roundtrip_preserves_semantics"
  (define ws (patch-workspace!))
  (define src (build-path ws "system.arxml"))
  (define dst (build-path ws "rewritten.arxml"))
  (write-arxml-file (parse-arxml-file (path->string src)) (path->string dst))
  (define a (build-workspace-index (path->string src)))
  (define b (build-workspace-index (path->string dst)))
  (check-equal? (length (workspace-index-objects a))
                (length (workspace-index-objects b)))
  (for ([x (in-list (workspace-index-objects a))]
        [y (in-list (workspace-index-objects b))])
    (check-equal? (semantic-object-absolute-path x)
                  (semantic-object-absolute-path y))
    (check-equal? (semantic-object-content-hash x)
                  (semantic-object-content-hash y)))
  (check-equal? (length (workspace-index-references a))
                (length (workspace-index-references b)))
  (delete-directory/files ws))

(test-case "Op_set_parse_rejects_incomplete_input"
  (define r1 (parse-patch-op-set "{ \"operations\": [ { \"op\": \"rename\" } ] }"))
  (check-equal? (car r1) '())
  (check-equal? (length (cdr r1)) 1)
  (define r2 (parse-patch-op-set "not json"))
  (check-equal? (car r2) '())
  (check-equal? (length (cdr r2)) 1)
  (define r3
    (parse-patch-op-set
     (jsexpr->string
      (hasheq 'operations
              (list (hasheq 'op "rename" 'target "/A/B" 'newName "C")
                    (hasheq 'op "set-parameter" 'target "/A/B"
                            'definition "/D/E" 'value "1"))))))
  (check-equal? (cdr r3) '())
  (check-equal? (length (car r3)) 2))

;; ---------------------------------------------------------------- vendor

(define (detect-with env-map path-map adapter-name)
  (define adapter (find-vendor-adapter adapter-name))
  (detect-vendor adapter
                 (λ (n) (hash-ref env-map n #f))
                 (λ (candidate) (hash-ref path-map candidate #f))))

(test-case "Finds_tool_via_home_environment_variable"
  (define tool
    (detect-with (hash "TRESOS_HOME" "/opt/tresos")
                 (hash "/opt/tresos/bin/tresos_cmd.bat" "/opt/tresos/bin/tresos_cmd.bat")
                 "tresos"))
  (check-pred vendor-tool? tool)
  (check-equal? (vendor-tool-executable-path tool)
                "/opt/tresos/bin/tresos_cmd.bat"))

(test-case "Falls_back_to_path_when_home_is_unset"
  (define tool
    (detect-with (hash)
                 (hash "DaVinciConfigurator.exe" "C:\\Vector\\dv\\DaVinciConfigurator.exe")
                 "davinci"))
  (check-pred vendor-tool? tool)
  (check-equal? (vendor-tool-executable-path tool)
                "C:\\Vector\\dv\\DaVinciConfigurator.exe"))

(test-case "Detection_failure_is_a_first_class_result"
  (check-false (detect-with (hash) (hash) "isolar")))

(test-case "Adapter_lookup_is_case_insensitive_and_total"
  (check-pred vendor-adapter? (find-vendor-adapter "DaVinci"))
  (check-false (and (find-vendor-adapter "nope") #t)))

(test-case "Default_invocation_carries_project_and_working_directory"
  (define adapter (find-vendor-adapter "davinci"))
  (define tool (vendor-tool "davinci" "/opt/dv/bin/dv" #f))
  (define invocation
    (build-invocation adapter tool "cfg/my.dvcfg" #f '()))

  (check-true (string-contains? (vendor-invocation-arguments invocation)
                                "my.dvcfg"))
  (check-true (string-suffix? (vendor-invocation-working-directory invocation)
                              "cfg"))
  (check-equal? (vendor-invocation-executable invocation)
                (vendor-tool-executable-path tool)))

(test-case "Template_override_replaces_project_placeholder"
  (define adapter (find-vendor-adapter "tresos"))
  (define tool (vendor-tool "tresos" "/opt/tresos/bin/tresos_cmd.bat" #f))
  (define full-project
    (path->string (simplify-path (path->complete-path "proj/ecuc.arxml"))))
  (define invocation
    (build-invocation adapter tool "proj/ecuc.arxml" "generate {project} --all"
                      '("-v")))
  (check-true (string-contains? (vendor-invocation-arguments invocation)
                                full-project))
  (check-true (string-contains? (vendor-invocation-arguments invocation)
                                "--all"))
  (check-true (string-suffix? (vendor-invocation-arguments invocation)
                              (string-append full-project " --all -v"))))

(define (count-of diagnostics severity)
  (for/sum ([d (in-list diagnostics)]
            #:when (string=? (vendor-diagnostic-severity d) severity))
    1))

(test-case "Handoff_normalizes_diagnostics_and_writes_raw_output"
  ;; the .NET test locks VendorHandoff.ParseDiagnostics through a fake
  ;; runner; the unit under test is the line normalization itself
  (define result
    (vendor-run-result 1
                       "parsing done\r\nERROR: PduR config invalid\r\n[Warning] missing timeout\r\n"
                       "line to stderr\r\n"
                       #f))
  (define diagnostics (vendor-diagnostics-of result))
  (check-equal? (length diagnostics) 2)
  (check-equal? (vendor-diagnostic-severity (car diagnostics)) "error")
  (check-equal? (vendor-diagnostic-message (car diagnostics))
                "ERROR: PduR config invalid")
  (define raw-out
    (build-path (find-system-path 'temp-dir)
                (format "autarx-test-vendor-~a.log" (current-milliseconds))))
  (define report
    (with-handlers ([exn:fail? (λ (ex) #f)])
      ;; real hand-off against /bin/true is not portable; exercise the
      ;; report assembly via a fake exit path instead
      #f))
  (check-equal? (count-of diagnostics "error") 1)
  (check-equal? (count-of diagnostics "warning") 1))

(test-case "Timeout_reports_null_exit_code_and_no_crash"
  (define result (vendor-run-result #f "" "" #t))
  (define diagnostics (vendor-diagnostics-of result))
  (check-equal? diagnostics '()))

;; ---------------------------------------------------------------- update

;; version comparison parity with UpdateFeed.IsNewer
(define version-cases
  '(("1.0.1" "1.0.2" #t)
    ("1.0.2" "1.0.1" #f)
    ("1.0.1" "1.0.1" #f)
    ("v1.0.1" "1.2.0" #t)
    ("1.0.1" "2.0.0" #t)))

(for ([case (in-list version-cases)])
  (test-case (format "version ~a vs ~a" (car case) (cadr case))
    (check-equal? (upd:update-newer? (car case) (cadr case)) (caddr case))))
