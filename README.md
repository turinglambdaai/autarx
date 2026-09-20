# Autarx

**A modern AUTOSAR engineering workbench for inspecting, understanding, comparing, tracing and automating OEM-to-supplier integration data.**
Point it at an OEM delivery or ECU extract directory and get a semantic workspace: inventories, reference graphs, ECU discovery and unresolved-reference checks — scriptable end to end from an agent-friendly JSON CLI. Autarx sits **above** the vendor generators; it does not replace DaVinci, tresos or ISOLAR.

[![CI](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml/badge.svg)](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-12-9B4FBE?logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE)

[中文](README.zh-CN.md) · English

---

## Why Autarx?

- **Deliveries are opaque** — an OEM drop is dozens of ARXML files with system descriptions, ECU extracts and ECUC bundles; nothing answers "what is in here, and does it hold together" without opening a full vendor toolchain
- **ARXML is grep-hostile** — engineers hand-navigate XML trees of millions of lines; tracing one signal from the ECU back to the cluster means mentally resolving dozens of REF elements across files
- **No automation surface** — CI cannot diff, trace or sanity-check deliveries because no vendor tool exposes a stable machine interface
- **Agents cannot help** — LLM agents work on text surfaces with structured feedback; vendor tools offer neither

Autarx fixes that: one UI-free semantic engine that reads ARXML directly, a native Avalonia GUI for humans, and a JSON-first CLI for scripts, CI and AI agents.

## What Autarx is — and is not

Autarx **can**: read ARXML workspaces, index identifiables with stable absolute paths, build forward/reverse reference graphs, search, trace, inspect, check basic consistency, emit deterministic JSON.

Autarx **does not**: replace DaVinci/tresos/ISOLAR, generate production BSW/RTE/MCAL code, or claim its validation substitutes for vendor validation. `validate` means structural checks only. The chain is: **Autarx analyses → Vendor Tool → Vendor Validation → Vendor Generator.** See [docs/PRODUCT.md](docs/PRODUCT.md).

## Features

- **Workspace semantic index** — every package-level identifiable gets a stable absolute short-name path (`/Vehicle/Signals/VehicleSpeed`); identity never relies on XML line numbers
- **Semantic classification** — systems, ECUs, clusters, frames, PDUs, signals, SW components, ports, interfaces, ECUC modules; unknown element types are reported honestly as unknown and never break parsing
- **Reference engine** — forward and reverse graphs over `*-REF`/`*-TREF` elements, cross-file resolution, nested-element attribution (`/Pdu_VehicleSpeed/VehicleSpeedMapping`), duplicate-path detection, unresolved-reference findings
- **OEM delivery inspection** — `inspect` reports files, sizes, element counts, packages, AUTOSAR namespace/schema/release and the full semantic inventory in one shot
- **ECU discovery** — `ecus` lists ECU instances; `ecu <name>` shows an ECU's direct relations with explicit `direct` relationship confidence (no guessed semantics)
- **Agent-friendly CLI** — camelCase JSON, deterministic exit codes, ambiguity is reported instead of guessed; a ready-made command chain for CI gates
- **Avalonia workspace** — native GUI on Windows, macOS and Linux
- **Self-contained builds** — single-file executables per platform, no .NET install needed

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

## Getting started

### Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
dotnet build Autarx.slnx
dotnet run --project src/Autarx.Gui
dotnet run --project src/Autarx.Cli -- inspect tests/Autarx.Tests/Fixtures/OemDelivery
```

### Tests

```bash
dotnet test
```

The suite covers the parser (namespace stripping, attributes, error line numbers), semantic classification, workspace indexing, forward/reverse reference graphs, summaries and traces — over a synthetic non-ECUC System/Communication workspace plus ECUC fixtures, including deliberate unresolved references and ambiguous/duplicate paths.

## Project structure

```text
autarx/
├── src/
│   ├── Autarx.Core/            # semantic engine (no UI dependencies)
│   │   ├── Models/             # SemanticObject, SemanticKind, references, documents
│   │   ├── Parsing/            # ArxmlParser (streaming), EcucReader, release parser
│   │   ├── Index/              # WorkspaceIndex builder, graphs, trace, summary
│   │   └── Validation/         # ValidationEngine, ARX00NN diagnostics
│   ├── Autarx.Cli/             # workspace + single-file commands, --json
│   └── Autarx.Gui/             # Avalonia workspace — module tree, details, status bar
├── tests/Autarx.Tests/         # unit suites + synthetic fixtures (ECUC and non-ECUC)
├── docs/PRODUCT.md             # positioning, boundaries, non-goals
└── docs/ROADMAP.md             # M0–M8
```

## Design notes

- **Core is the semantic engine, not an ECUC parser** — ECUC is one projection; the top-level model (Workspace, SemanticObject, reference graph) must serve System Descriptions and ECU Extracts just as well
- **Identity = AUTOSAR absolute path** — stable across rewrites and diff-ready; XML line numbers are never identity
- **Unknown degrades gracefully** — an unmapped element type is indexed, referenceable and reported as unknown, never a parse failure
- **Ambiguity is surfaced, not guessed** — duplicate SHORT-NAMEs resolve to an explicit ambiguity result listing every candidate path
- **The CLI JSON is a machine contract** — camelCase keys, string enums, deterministic exit codes, additive changes only; scripts and AI agents depend on it

## Roadmap

See [docs/ROADMAP.md](docs/ROADMAP.md): semantic diff (M3), ECU impact analysis (M4), communication trace (M5), vendor tool adapters (M6), reviewed editing (M7), AI agent on the stable tool API (M8).

## License

Proprietary — © TuringLambdaAI, all rights reserved. See [LICENSE](LICENSE).
