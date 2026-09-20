# Roadmap

The roadmap is intentionally analysis-first. Autarx should become excellent at understanding OEM/supplier AUTOSAR deliveries before it becomes a broad editor.

## M0 — Skeleton (current baseline)

- Streaming ARXML parser
- ECUC projection (module → container → parameter/reference)
- Structural ECUC validation (ARX001–ARX006)
- CLI (`info` / `modules` / `validate`, `--json`)
- Avalonia workspace shell
- CI + release pipelines

## M1 — Workspace & semantic index

Goal: stop treating one ECUC file as the whole product model.

- Multi-file ARXML workspace
- Identifiable object index from `SHORT-NAME`
- Source-file provenance
- Simple reference graph (`*-REF` / `*-TREF`)
- CLI `find`
- CLI `refs`
- Multi-file reference-resolution tests
- Detect AUTOSAR release / namespace metadata explicitly

## M2 — OEM delivery inspection

Goal: answer “what did the OEM give me?” before editing anything.

- `inspect <delivery>` summary
- Package/document inventory
- Detect likely System Description / System Extract / ECU Extract / ECUC content
- System and ECU projection foundations
- ECU list, network/cluster inventory, basic SWC/communication counts
- Broken/unresolved reference report
- GUI workspace explorer based on the shared semantic index

## M3 — Semantic diff

Goal: replace XML-line diff with engineering diff.

- Stable semantic identities across files/versions
- Added / removed / modified objects
- Property-level changes
- Reference/mapping changes
- CLI `diff <before> <after> --json`
- Human-readable report
- GUI compare view

## M4 — ECU impact analysis

Goal: answer “what changed for my ECU?” across OEM deliveries.

- `impact <before> <after> --ecu <name>`
- ECU-related object closure through mappings/references
- Relevant vs unrelated changes
- Communication impact summaries
- Initial compatibility/breaking-change rule framework where deterministic rules exist
- Exportable report for review and CI

## M5 — Communication trace

Goal: make cross-layer relationships understandable.

- Signal / I-Signal / PDU / Frame / Cluster projections
- Sender/receiver and ECU relationships
- `trace <object>` graph traversal
- ECUC communication projections where available (Com/PduR/SecOC/CanIf)
- GUI graph view
- DBC import only as an auxiliary interchange/input feature, not as a path to auto-generate a complete BSW configuration

## M6 — Vendor adapters

Goal: connect Autarx analysis/editing workflows to authoritative vendor tools.

- Adapter interface with capability discovery
- DaVinci validation/generation hand-off
- EB tresos validation/generation hand-off
- ETAS ISOLAR integration where practical
- Capture vendor diagnostics into a normalized report while preserving original output
- No reimplementation of production BSW/RTE/MCAL generators

## M7 — Reviewed editing & patching

Goal: enable safe, reviewable engineering changes without turning Autarx into a full configurator replacement.

- Semantic patch model
- Rename / targeted property edit / reference update
- Round-trip ARXML writing with provenance
- Before/after semantic diff required for every write
- Undo/rollback
- Optional vendor validation after apply

Broad typed editors for every BSW module are deliberately not a milestone. Add domain-specific editors only when they support a proven workflow.

## M8 — AI layer

Goal: let AI operate deterministic engineering tools instead of editing XML blindly.

- Tool API over inspect/find/refs/trace/diff/impact/validate/patch
- Plan → review → apply → validate workflow
- Full audit trail
- AI never bypasses semantic patch/review boundaries
- Offline/private model options for commercial customers where needed

## M9 — Commercial hardening

- Installer and update channel
- Licensing/activation
- Crash reporting / opt-in telemetry strategy
- Large-workspace performance profiling
- Documentation site and realistic sample deliveries
- Plugin/vendor-adapter SDK
- Enterprise CI/reporting integration
- Security review and supply-chain hardening

## Explicit non-goals for the foreseeable roadmap

- Complete AUTOSAR BSW implementation
- Production RTE generator
- Production BSW/MCAL generator
- Replacing DaVinci Configurator / EB tresos / ISOLAR end-to-end
- Complete OEM E/E architecture authoring suite

If one of these ever becomes strategically necessary, it requires an explicit product decision rather than emerging accidentally from incremental feature work.
