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
(Signal → PDU → Frame → Cluster → ECU) will layer on top of this API later —
nothing is hardcoded into the CLI.

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
  findings reported (validate/unresolved) · `2` usage/file/parse error or
  ambiguous object name. Deterministic, documented in `--help`.
- Ambiguity is a first-class outcome: a SHORT-NAME matching several objects
  exits 2 and lists the candidate paths — never a silent guess.
- Additive changes only — scripts, CI and AI agents parse this output.

## Testing

Parser, ECUC, classification, workspace index, reference graph, summary and
trace suites run against checked-in fixtures — including a synthetic
non-ECUC System/Communication workspace (`Fixtures/OemDelivery`, with
deliberate unresolved references and duplicate/ambiguous-path fixtures).
CI builds, tests, and smoke-runs every workspace command on every push.
