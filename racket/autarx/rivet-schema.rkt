#lang racket/base

;; GUI DTOs for the Rivet RPC surface. The CLI/MCP keep their own JSON
;; contract (v1.0.3 parity); these records exist only for the native host
;; and are allowed to evolve with it.

(require rivet/backend)

(provide (all-defined-out))

(define-record WorkspaceSummary
  ([source-path : String]
   [file-count : Int64]
   [identifiable-count : Int64]
   [package-count : Int64]
   [reference-count : Int64]
   [unresolved-count : Int64]
   [duplicate-count : Int64]
   [file-error-count : Int64]
   [autosar-release : (Optional String)]
   [mixed-schema : Bool]))

(define-record SemanticObjectDto
  ([short-name : String]
   [element-type : String]
   [semantic-kind : String]
   [absolute-path : String]
   [source-file : String]
   [unresolved-references : Int64]))

(define-record ReferenceDto
  ([kind : String]
   [source-path : String]
   [source-element-path : String]
   [target-path : String]
   [resolved : Bool]))

(define-record TraceDto
  ([root : String]
   [max-depth : Int64]
   [node-paths : (List String)]
   [edges : (List ReferenceDto)]))

(define-record ObjectChangeDto
  ([change-type : String]
   [short-name : String]
   [semantic-kind : String]
   [absolute-path : String]
   [detail : String]))

(define-record DiffDto
  ([identical : Bool]
   [object-changes : (List ObjectChangeDto)]
   [reference-change-count : Int64]))

(define-record ClusterDto
  ([path : String]
   [connected-ecus : (List String)]
   [frames : (List String)]))

(define-record CommunicationDto
  ([clusters : (List ClusterDto)]
   [orphan-frames : (List String)]
   [orphan-pdus : (List String)]
   [orphan-signals : (List String)]))
