# Autarx

**A modern AUTOSAR engineering workbench for inspecting, understanding, comparing, tracing and automating OEM-to-supplier integration data.**
Point it at an OEM delivery or ECU extract directory and get a semantic workspace: inventories, reference graphs, ECU discovery and unresolved-reference checks — scriptable end to end from an agent-friendly JSON CLI. Autarx sits **above** the vendor generators; it does not replace DaVinci, tresos or ISOLAR.

[![CI](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml/badge.svg)](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml) [![Release](https://img.shields.io/github/v/release/turinglambdaai/autarx)](https://github.com/turinglambdaai/autarx/releases/latest) ![Racket](https://img.shields.io/badge/Racket-CS-9F1D20?logo=racket&logoColor=white) [![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE)

**English** · [中文](README.zh-CN.md)

---

## Why Autarx?

- **Deliveries are opaque** — an OEM drop is dozens of ARXML files with system descriptions, ECU extracts and ECUC bundles; nothing answers "what is in here, and does it hold together" without opening a full vendor toolchain
- **ARXML is grep-hostile** — engineers hand-navigate XML trees of millions of lines; tracing one signal from the ECU back to the cluster means mentally resolving dozens of REF elements across files
- **No automation surface** — CI cannot diff, trace or sanity-check deliveries because no vendor tool exposes a stable machine interface
- **Agents cannot help** — LLM agents work on text surfaces with structured feedback; vendor tools offer neither

Autarx fixes that: one UI-free semantic engine that reads ARXML directly, a native macOS workbench for humans, and a JSON-first CLI for scripts, CI and AI agents.

## What Autarx is — and is not

Autarx **can**: read ARXML workspaces, index identifiables with stable absolute paths, build forward/reverse reference graphs, search, trace, inspect, check basic consistency, emit deterministic JSON.

Autarx **does not**: replace DaVinci/tresos/ISOLAR, generate production BSW/RTE/MCAL code, or claim its validation substitutes for vendor validation. `validate` means structural checks only. The chain is: **Autarx analyses → Vendor Tool → Vendor Validation → Vendor Generator.** See [docs/PRODUCT.md](docs/PRODUCT.md).

## Features

- **Workspace semantic index** — every package-level identifiable gets a stable absolute short-name path (`/Vehicle/Signals/VehicleSpeed`); identity never relies on XML line numbers
- **Semantic classification** — systems, ECUs, clusters, frames, PDUs, signals, SW components, ports, interfaces, ECUC modules; unknown element types are reported honestly as unknown and never break parsing
- **Reference engine** — forward and reverse graphs over `*-REF`/`*-TREF` elements, cross-file resolution, nested-element attribution (`/Pdu_VehicleSpeed/VehicleSpeedMapping`), duplicate-path detection, unresolved-reference findings
- **OEM delivery inspection** — `inspect` reports files, sizes, element counts, packages, AUTOSAR namespace/schema/release and the full semantic inventory in one shot
- **ECU discovery** — `ecus` lists ECU instances; `ecu <name>` shows an ECU's direct relations with explicit `direct` relationship confidence (no guessed semantics)
- **Semantic diff** — `diff` compares two deliveries by path identity with content hashes; property-level explanations address ECUC values by their DEFINITION-REF, so "McuFrequency: 80000000 → 160000000" is a first-class finding, never a line-number noise
- **ECU impact analysis** — `impact --ecu RadarFL` classifies every change relevant/unrelated by closure over the union reference graph of both deliveries; deterministic ARX-IMP-* findings flag removed-referenced objects and type changes as breaking
- **Communication projection** — `comm` derives cluster → frame → PDU → signal chains from the reference graph and reports frames/PDUs/signals not attached anywhere; structural only, no invented sender/receiver semantics
- **Vendor hand-off** — `vendor list` detects DaVinci/tresos/ISOLAR installations; `vendor validate` relays the vendor tool's own run with normalized diagnostics and the raw output preserved — never an Autarx verdict
- **Reviewed editing** — `patch plan` previews every change as a semantic diff before anything is written; `patch apply` keeps backups and an undo manifest; renames rewrite incoming reference targets across the workspace
- **AI agent surface** — `autarx mcp` runs a Model Context Protocol stdio server exposing the whole tool API; there is no raw-write tool, so an agent cannot bypass the plan/diff/review boundary, and every call is audit-logged
- **Self-update (CLI)** — `autarx update` checks the release feed (a `latest.json` asset published with every release), verifies the SHA-256 checksum, and swaps the new binaries in place. The GUI has no updater: when a new workbench release ships, download its DMG from [Releases](https://github.com/turinglambdaai/autarx/releases) and drag-install. Unhandled crashes leave a local log under `<TEMP>/autarx/crashes/` — nothing is sent anywhere
- **Agent-friendly CLI** — camelCase JSON, deterministic exit codes, ambiguity is reported instead of guessed; a ready-made command chain for CI gates
- **Native macOS workbench** — SwiftUI host over the embedded Racket core (Windows/Linux hosts planned)
- **Single binary core** — one Racket executable carries CLI + MCP; the GUI embeds it

## Command line

```bash
# inspect a whole OEM delivery directory
autarx inspect ./OEM_Delivery/
autarx inspect ./OEM_Delivery/ --json

# find objects by SHORT-NAME substring
autarx find VehicleSpeed ./OEM_Delivery/

# who references whom
autarx refs VehicleSpeed ./OEM_Delivery/
autarx refs VehicleSpeed ./OEM_Delivery/ --incoming
autarx refs /Vehicle/Clusters/VehicleCan ./OEM_Delivery/ --outgoing --json

# follow the reference graph
autarx trace Pdu_VehicleSpeed ./OEM_Delivery/ --depth 3

# ECU discovery
autarx ecus ./OEM_Delivery/
autarx ecu ./OEM_Delivery/ Gateway --json

# delivery quality seed — exit code 1 when findings exist
autarx unresolved ./OEM_Delivery/

# semantic diff between two deliveries — exit 1 when they differ
autarx diff ./V32/ ./V33/ --detail

# what changed for my ECU — exit 1 when relevant changes exist
autarx impact ./V32/ ./V33/ --ecu RadarFL

# communication chains and orphans
autarx comm ./OEM_Delivery/ --cluster VehicleCan

# vendor tool hand-off (detection + relayed validation)
autarx vendor list
autarx vendor validate davinci ./Project.dvcfg

# reviewed editing: plan is read-only, apply keeps backups, undo restores
autarx patch plan ./OEM_Delivery/ --file ops.json
autarx patch apply ./OEM_Delivery/ --file ops.json
autarx patch undo ./OEM_Delivery/

# MCP stdio server for AI agents (JSON-RPC 2.0, audit-logged)
autarx mcp

# self-update: check and apply newer builds from GitHub Releases
autarx update --check        # report only (exit 1 when newer exists)
autarx update                # download, verify SHA-256, swap in place

# single-file ECUC commands (structural validation only — not vendor validation)
autarx info Mcu.arxml
autarx modules Mcu.arxml --json
autarx validate Mcu.arxml
```

`autarx unresolved --json` output:

```json
[
  {
    "kind": "REQUIRED-INTERFACE-TREF",
    "sourcePath": "/Vehicle/SwCs/SpeedSensor",
    "sourceElementPath": "/Vehicle/SwCs/SpeedSensor/CalibrationIn",
    "targetPath": "/Vehicle/Interfaces/IF_Calibration",
    "sourceFile": "communication.arxml",
    "resolved": false
  }
]
```

## Install

Releases follow `autarx-<version>-<os>-<arch>.<ext>` and ship from
[GitHub Releases](https://github.com/turinglambdaai/autarx/releases):

| Download | Platform | Kind |
| --- | --- | --- |
| `autarx-0.1.0-macos-arm64.dmg` | macOS 14+, Apple silicon | GUI workbench |
| `autarx-0.1.0-macos-arm64.zip` | macOS (arm64) | CLI (self-contained) |
| `autarx-0.1.0-linux-x64.zip` | Linux (x64) | CLI (self-contained) |
| `autarx-0.1.0-windows-x64.zip` | Windows (x64) | CLI (self-contained) |

The CLI zips are the self-update payload: once installed, `autarx update`
pulls newer builds from the checksum-verified feed. The DMG is ad-hoc
signed — on first launch, right-click the app and choose Open.

To build from source instead:

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
raco rivet build          # staged app + CLI runtime
racket racket/autarx/cli.rkt --version
```

Prerequisites: Racket CS 9.x with [Rivet](https://github.com/turinglambdaai/rivet)
linked — see [Getting started](#getting-started) below for the full path.

## Getting started

### Build from source

Requirements: Racket CS 9.x with [Rivet](https://github.com/turinglambdaai/rivet)
linked (inside a rivet checkout: `raco pkg install --auto --no-docs --name rivet --link file://$PWD`);
the macOS host additionally needs Xcode.

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
raco rivet build          # staged layout: embedded backend + macOS host
racket racket/autarx/cli.rkt inspect shared/fixtures/OemDelivery --json
raco rivet dev            # dev loop: rebuild + launch the workbench
```

### Tests

```bash
raco test racket/
```

The 108-test suite (101 contract tests ported one-to-one from the .NET
suite plus the update-channel tests) covers the parser (namespace
stripping, attributes, error line numbers), semantic classification,
workspace indexing, forward/reverse reference graphs, traces, semantic
diff, impact analysis, the communication projection, validation, the patch
pipeline, vendor hand-off normalization and version comparison — over the
same synthetic workspaces in `shared/fixtures/`, including deliberate
unresolved references and ambiguous/duplicate paths.

## Project structure

```text
autarx/
├── rivet.rktd                  Rivet application manifest
├── racket/
│   ├── autarx/                 # semantic engine (Racket; no UI dependencies)
│   │   ├── arxml.rkt           # streaming parser + canonical writer
│   │   ├── index.rkt           # workspace index, graphs, trace, summary
│   │   ├── hash.rkt            # FNV-1a content hashes (cross-impl contract)
│   │   ├── diff.rkt            # semantic diff (path identity + content hashes)
│   │   ├── impact.rkt          # ECU relevance closure, ARX-IMP-* findings
│   │   ├── comm.rkt            # cluster→frame→PDU→signal projection
│   │   ├── vendor.rkt          # adapter detection + vendor hand-off relay
│   │   ├── patch.rkt           # plan/apply/undo, reviewed editing
│   │   ├── update.rkt          # update feed, SHA-256 verification, in-place swap
│   │   ├── cli.rkt             # workspace + single-file commands, --json
│   │   ├── mcp.rkt             # MCP stdio server + tool registry
│   │   └── backend.rkt         # Rivet RPC surface (GUI hosts)
│   └── tests/                  # 101 contract tests + RVT1 smoke
├── macos-host/                 # SwiftUI workbench
├── shared/fixtures/            # synthetic workspaces (ECUC and non-ECUC)
├── docs/RIVET-MIGRATION.md     # the R0–R8 rebuild plan and stop-rule record
├── docs/PRODUCT.md             # positioning, boundaries, non-goals
└── ARCHITECTURE.md             # architecture decisions
```

## Design notes

- **Core is the semantic engine, not an ECUC parser** — ECUC is one projection; the top-level model (Workspace, SemanticObject, reference graph) must serve System Descriptions and ECU Extracts just as well
- **Identity = AUTOSAR absolute path** — stable across rewrites and diff-ready; XML line numbers are never identity
- **Unknown degrades gracefully** — an unmapped element type is indexed, referenceable and reported as unknown, never a parse failure
- **Ambiguity is surfaced, not guessed** — duplicate SHORT-NAMEs resolve to an explicit ambiguity result listing every candidate path
- **The CLI JSON is a machine contract** — camelCase keys, string enums, deterministic exit codes, additive changes only; scripts and AI agents depend on it

## Roadmap

The Rivet rebuild (R0–R8) is complete: Racket semantic core, CLI/MCP
with byte-compatible JSON, native macOS workbench, and the R0 performance
stop-rule record — see [docs/RIVET-MIGRATION.md](docs/RIVET-MIGRATION.md).
The project is now in a 0.x functional-validation phase (0.1.0 is the
first epoch release). What remains: Windows/Linux hosts.

## License

Proprietary — © TuringLambdaAI, all rights reserved. See [LICENSE](LICENSE).
