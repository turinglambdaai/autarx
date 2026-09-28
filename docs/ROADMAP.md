# Roadmap

## M0 — Foundation ✅

Streaming ARXML parser, ECUC projection (module → container → parameter /
reference), structural validation (ARX001–ARX006), CLI (`info` / `modules` /
`validate`, `--json`), Avalonia workspace shell, CI + release pipelines.

## M1 — Workspace + Semantic Index ✅

Workspace as the core abstraction: documents, identifiables with stable
absolute paths, semantic kind classification (unknown-safe), forward/reverse
reference graph, duplicate-path detection, workspace summary API shared by
CLI/GUI/future AI.

## M2 — OEM Delivery Inspection ✅

`inspect` (workspace summary + semantic inventory), `find`, `refs`
(in/outgoing), `trace` (generic depth-limited graph walk), `ecus` / `ecu`
(ECU discovery with direct relations), `unresolved` (delivery quality seed).
Non-ECUC System/Communication fixtures in the test suite.

## M3 — Semantic Diff ✅

`autarx diff old/ new/` — Added / Removed / Modified / Moved /
ReferenceChanged, keyed on stable absolute paths (identity is in place; no
XML line numbers).

## M4 — ECU Impact Analysis ✅

From an object or change set to the affected ECU instances — built on the
reference graph, not hardcoded relationships.

## M5 — Communication Trace ✅

Domain trace on top of the generic graph API: Signal → PDU → Frame →
Cluster → ECU. No communication semantics hardcoded into the CLI.

## M6 — Vendor Tool Adapters ✅

Drive DaVinci / tresos / ISOLAR where they are strong: `autarx vendor
validate` and generation hand-off. Autarx stays above the generators.

## M7 — Reviewed Editing ✅

Plan → Diff → Review → Apply → Vendor Validate. Controlled ARXML
modifications with full auditability.

## M8 — AI Agent ✅

AI assistance on top of the stable semantic tool API — the `autarx mcp`
Model Context Protocol server. AI never edits XML directly; every mutation
flows through M7's reviewed pipeline.

## M9 — Commercial Hardening

Installer-grade distribution (per-platform installers instead of raw zips),
licensing, performance profiling on large OEM deliveries, and code signing /
notarization.
