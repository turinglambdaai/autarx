# Architecture

## Positioning

Autarx is an **AUTOSAR Integration Workbench**: it inspects, explains,
compares, traces and automates OEM-to-supplier integration data. It sits
**above** the vendor generators (DaVinci / tresos / ISOLAR) — it does not
replace them and does not generate production BSW/RTE/MCAL code. See
[docs/PRODUCT.md](docs/PRODUCT.md) for the full boundary.

## Pipeline

```text
Raw ARXML
    ↓
Workspace
    ├── Documents          (per-file metadata, schema/release info)
    ├── Identifiables      (stable absolute paths)
    ├── References         (forward graph)
    ├── Reverse References (incoming graph)
    ├── Provenance         (source file per object/reference)
    └── Diagnostics        (unresolved, duplicates, file errors)
            ↓
Semantic Projections
    ├── System / ECU / Communication / SWC
    └── ECUC  ← one projection among several, never the top model
            ↓
Query · Trace · Diff · Impact · Validation
            ↓
CLI · GUI · CI · AI
```

## Stack

C# / .NET 10, Avalonia for the GUI. One UI-free core, two frontends.

- **One core, two frontends from day one.** The GUI and the CLI are thin
  shells over `Autarx.Core`; neither contains domain logic.
- **The BCL XML/JSON stack is production-grade** — streaming `XmlReader`
  handles the 10M+ line ECUC files real projects ship, and
  `System.Text.Json` powers the CLI's machine interface.
- **Self-contained single-file distribution** — customers install nothing.
- **IP protection is tractable** — compiled IL can be obfuscated for
  commercial release.
- Alternatives considered and rejected: Electron/TypeScript (weakest IP
  protection, no precedent in our toolchain) and our own Racket stack
  (pre-1.0 — a commercial product should not ride on framework risk).

## Layering

```text
Autarx.Core
   ├── Parsing/       ArxmlParser (streaming), EcucReader, AutosarReleaseParser
   ├── Models/        ArxmlElement, SemanticObject, SemanticKind, references, documents
   ├── Index/         WorkspaceIndexBuilder, WorkspaceIndex, WorkspaceSummary
   └── Validation/    ValidationEngine (structural, ARX00NN)
   ├── Autarx.Cli     workspace + single-file commands, --json
   └── Autarx.Gui     Avalonia workspace (MVVM)
```

Nothing outside Core re-parses ARXML. ViewModels and CLI commands hold no
AUTOSAR domain logic.

## Semantic model

- **Identity is the AUTOSAR absolute short-name path**
  (`/Vehicle/Signals/VehicleSpeed`) — stable across file rewrites. XML line
  numbers are never used as identity.
- `SemanticObject` = ShortName, ElementType, SemanticKind, AbsolutePath,
  SourceFile. Only package-level ELEMENTS children are indexed as objects;
  nested identifiables (ports, mappings, connectors) appear through reference
  attribution chains and semantic counts.
- `SemanticKind` classification is deliberately partial: exact names for the
  types every delivery contains, suffix rules for open families
  (`*-PDU`/`*-SDU`, `*-INTERFACE`, `*-PORT-PROTOTYPE`,
  `*-SW-COMPONENT-TYPE`), and `Unknown` for everything else. **Unknown never
  fails parsing** — new AUTOSAR model elements degrade gracefully.
- Duplicate absolute paths are recorded, never silently merged; path lookup
  deterministically resolves to the first occurrence (files in sorted order).

## Reference engine

- Captured from `*-REF` / `*-TREF` elements whose text is an absolute path.
- **DEFINITION-REF is excluded**: it points at module definitions that
  typically live outside the delivery and would flood the unresolved report
  with non-findings.
- References are attributed to the owning package-level object
  (`SourcePath`) plus the nested SHORT-NAME chain
  (`SourceElementPath`, e.g. `/Vehicle/Pdus/Pdu_VehicleSpeed/VehicleSpeedMapping`).
- Resolution is workspace-wide and cross-file; the reverse graph is derived,
  not maintained separately.
- `Unresolved` = target path absent from the workspace. This is the seed of
  the future OEM delivery quality check — it is a *finding*, not an error.

## Trace

Generic breadth-first graph walk (`WorkspaceIndex.Trace`) with direction
(outgoing/incoming/both) and depth limit. Communication semantics
(Signal → PDU → Frame → Cluster → ECU) layer on top of this API through the
communication projection — nothing is hardcoded into the CLI.

## Semantic diff

`Diff/SemanticDiff` compares two workspaces by **path identity**. Modified
detection uses FNV-1a content hashes captured per object at index time, so
"did anything change" needs nothing beyond the two indexes. Property-level
explanations re-read the affected source files on demand and compare subtrees
with SHORT-NAME / DEFINITION-REF sibling keying (occurrence-suffixed when
repeated) — ECUC parameter changes are reported as
`…/DiagParamA/VALUE: 1 → 2`, never as line numbers. Reference changes are
diffed by semantic identity (source, kind, target): a reference moving
between files is not a change.

## Impact analysis

`Impact/ImpactAnalysis` seeds a breadth-first closure at the ECU instance
over the **union** reference graph of both deliveries, then classifies every
diff entry relevant/unrelated. Findings carry stable rule ids (ARX-IMP-*,
append only): removed-referenced, type-changed and ECU-touching
reference-removals are breaking; additions, retargets and property changes
are informational. Communication impact counts relevant changes among
clusters/frames/PDUs/signals.

## Communication projection

`Communication/CommunicationProjection` derives chains purely from the
reference graph: ECU connectors (`*CHANNEL-REF`), frame triggerings nested
under clusters (attributed to the cluster), PDU-to-frame mapping objects
(holding both `*FRAME-REF` and `*PDU-REF`), signal-to-PDU mappings nested
inside PDUs. Objects attached to nothing are reported as orphans. Direction
(sender/receiver) is deliberately **not claimed** — the structural graph
does not carry it reliably.

## Vendor hand-off

`Vendor/` detects DaVinci / EB tresos / ISOLAR through HOME env vars and
PATH (probes injected for testability) and runs a **hand-off**: the vendor
tool executes, Autarx normalizes its output (loose error/warning line
heuristic) and preserves the raw output under `.autarx/`. The report relays
the vendor's own result — never an Autarx verdict — and no generation is
reimplemented.

## Reviewed patching

`Patch/PatchEngine` is plan → apply → undo. Planning runs operations against
in-memory trees, materializes the full patched workspace into a temp
directory and computes the mandatory before/after **semantic diff** —
nothing is written. Apply writes only files the diff proved changed, keeps
`.autarx/` backups plus a `patch-history.jsonl` manifest for undo, and adds
a provenance comment. Renames rewrite incoming reference targets across all
workspace files. `Parsing/ArxmlWriter` emits canonical serialization
(deterministic indent, sorted attributes, root namespace reconstruction) —
semantically equivalent, deliberately not byte-faithful.

## Self-update

`Update/` implements the update channel. The release pipeline publishes a
`latest.json` asset with every release (version, per-platform download URL,
SHA-256, size); `UpdateFeed` parses it (URL or local file — local files keep
CI gated without network), `UpdateService` checks and downloads,
`UpdateInstaller` verifies the checksum BEFORE touching anything, then swaps
files in place: current files move to `<name>.autarx-update.old` (allowed
while the executable runs on Windows), stale backups are cleaned on the next
update. The GUI checks quietly on startup and offers the install under
Help. Unhandled exceptions write a local crash log under
`<TEMP>/autarx/crashes/` and exit 3 — nothing is sent anywhere.

## AI layer (MCP)

`Mcp/McpServer` is a newline-delimited JSON-RPC 2.0 stdio server exposing
the full tool API (inspect/find/refs/trace/ecus/unresolved/diff/impact/
comm/validate/patch_plan/patch_apply). The review boundary is structural:
no raw-write tool exists, both patch tools run the plan/diff gate, and every
tools/call is appended to `<workspace>/.autarx/audit.jsonl`.

## Diagnostics (structural validation)

Structural validation reports SHORT-NAME paths over the **ECUC projection**.
Rule codes are part of the CLI contract. **Never renumber; append only.**

| Code   | Severity | Rule |
| :----- | :------- | :--- |
| ARX001 | Error    | Module configuration missing DEFINITION-REF |
| ARX002 | Error    | Container missing DEFINITION-REF |
| ARX003 | Error    | Duplicate sibling container SHORT-NAME |
| ARX004 | Error    | Parameter has neither VALUE nor VALUE-REF |
| ARX005 | Warning  | Parameter DEFINITION-REF outside module definition |
| ARX006 | Error    | Module/container missing SHORT-NAME |

**Scope wording:** `validate` covers XML correctness, workspace integrity,
broken references, duplicate paths and structural rules only. It is *not*
vendor validation and must never be presented as "AUTOSAR compliant" or
"production valid". Vendor validation arrives later as explicit adapters
(`autarx vendor validate`).

## CLI JSON contract

- `--json` emits camelCase keys, indented; enums serialize as camelCase
  strings (`"communicationCluster"`), never integers.
- Exit codes: `0` success · `1` nothing matched (find/refs/trace/ecu) or
  findings reported (validate/unresolved/diff/impact) · `2` usage/file/parse
  error or ambiguous object name · `3` unhandled crash (local crash log
  written). Deterministic, documented in `--help`.
  Per-command variants: diff `0` identical/`1` differences; impact `0` no
  relevant changes/`1` relevant changes; patch `1` plan failures with
  nothing written; vendor validate `0` tool clean/`1` tool reported
  failure/`2` autarx-side problem (unknown tool, not detected, timeout).
- Ambiguity is a first-class outcome: a SHORT-NAME matching several objects
  exits 2 and lists the candidate paths — never a silent guess.
- Additive changes only — scripts, CI and AI agents parse this output.

## Testing

Parser, ECUC, classification, workspace index, reference graph, summary,
trace, diff, impact, communication projection, vendor hand-off
normalization, patch plan/apply/undo round trips and the MCP protocol run
against checked-in fixtures — including a synthetic non-ECUC
System/Communication workspace (`Fixtures/OemDelivery`, with deliberate
unresolved references and duplicate/ambiguous-path fixtures) and the
`DiffBefore`/`DiffAfter` delivery pair. CI builds, tests, and smoke-runs
every command's exit-code contract on every push.
