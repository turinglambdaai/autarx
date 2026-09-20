# Autarx

**AUTOSAR Integration Workbench for OEM-to-supplier engineering workflows.**

Autarx opens AUTOSAR engineering data directly and helps engineers **inspect, search, trace references, compare deliveries, analyze ECU impact, validate structure, and automate the same workflows from CLI/CI/AI**.

It is not intended to replace DaVinci Configurator, EB tresos, ETAS ISOLAR, or vendor production code generators.

[![CI](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml/badge.svg)](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-12-9B4FBE?logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE)

[中文](README.zh-CN.md) · English

---

## Product direction

The first target users are Tier-1 system integration engineers and ECU AUTOSAR engineers receiving OEM deliveries such as:

- System Description / System Extract
- ECU Extract
- communication-related ARXML
- ECU configuration (ECUC) ARXML
- multi-file delivery packages

Autarx focuses on the engineering gap between receiving those artifacts and opening the vendor configuration/generation toolchain.

```text
OEM delivery
System / ECU Extract / ARXML package
                |
                v
             Autarx
   inspect / find / refs / trace
   semantic diff / ECU impact
      validation / reporting
                |
                v
       reviewed engineering data
                |
        +-------+--------+
        |                |
        v                v
     DaVinci          tresos / ISOLAR
        |                |
        +-- authoritative vendor validation / generation --> production code
```

**Product rule:** Autarx may integrate with vendor generators, but production BSW/RTE/MCAL generation is not owned by the Autarx core product.

See [docs/PRODUCT.md](docs/PRODUCT.md) for the scope and non-goals.

## Why Autarx?

- **OEM deliveries are difficult to understand** — useful engineering meaning is spread across packages, references, mappings, and many ARXML files.
- **XML diff is not engineering diff** — suppliers need to know what changed for their ECU, not which lines moved.
- **Cross-references are expensive to follow manually** — engineers need fast answers to “who references this?” and “where does this object lead?”.
- **Vendor tools are authoritative but heavy** — many analysis tasks should be possible before launching a full configurator.
- **Automation needs a stable surface** — the same analysis should run in CLI, CI, GUI, and AI workflows.

## Current capabilities

The repository is still early, but the architecture now separates raw ARXML, shared workspace semantics, and ECUC-specific projections.

- **ARXML parser** — streaming `XmlReader`, LocalName-based matching
- **Multi-file workspace** — open one `.arxml` file or recursively load a directory
- **Semantic object index** — discover AUTOSAR identifiables by `SHORT-NAME`
- **Reference graph** — index simple `*-REF` / `*-TREF` edges for incoming/outgoing tracing
- **ECUC projection** — existing module → container → parameter/reference view
- **Structural ECUC validation** — current ARX001–ARX006 rules
- **JSON-first CLI** — stable machine-readable output for scripts/CI/agents
- **Avalonia GUI shell** — native desktop frontend; domain logic remains in the UI-free core

## CLI

Existing ECUC-oriented commands remain available:

```bash
autarx info Mcu_Can.arxml
autarx modules Mcu_Can.arxml --json
autarx validate Mcu_Can.arxml
```

Workspace/semantic commands can operate on one file or a directory of ARXML files:

```bash
# find by SHORT-NAME, path fragment, or AUTOSAR element type
autarx find VehicleSpeed ./oem-delivery
autarx find ECU-INSTANCE ./oem-delivery --json

# trace incoming/outgoing references for matching objects
autarx refs /Can/Can/CanGeneralConfiguration ./project
autarx refs VehicleSpeed ./oem-delivery --json
```

Planned high-value commands include `inspect`, `trace`, `diff`, and `impact --ecu <name>`.

## Architecture

```text
                    Autarx.Core
                         |
             +-----------+-----------+
             |                       |
       Raw ARXML model          Workspace index
             |                 objects / refs
             |                       |
             +-----------+-----------+
                         |
               Semantic projections
          System / ECU / Communication / ECUC
                         |
              +----------+----------+
              |                     |
         Autarx.Cli             Autarx.Gui
              |                     |
            CI / AI              Avalonia
                         |
                  Vendor adapters
             DaVinci / tresos / ISOLAR
```

ECUC is intentionally **one projection**, not the root model for the whole product. This keeps the engine suitable for OEM System Description and ECU Extract workflows as the product grows.

See [ARCHITECTURE.md](ARCHITECTURE.md).

## What Autarx is not

Autarx is not currently trying to:

- implement a complete AUTOSAR BSW stack
- generate production BSW/RTE/MCAL C code
- replace DaVinci Configurator, EB tresos, or ISOLAR
- become a complete OEM E/E architecture authoring suite
- use AI as the source of truth for deterministic engineering data

Those boundaries are deliberate so the product remains feasible for a small team while solving real integration pain.

## Getting started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
dotnet build Autarx.slnx
dotnet test

dotnet run --project src/Autarx.Cli -- find Mcu tests/Autarx.Tests/Fixtures/minimal.arxml
dotnet run --project src/Autarx.Gui
```

## Project structure

```text
autarx/
├── src/
│   ├── Autarx.Core/
│   │   ├── Models/          # raw ARXML + current ECUC projection
│   │   ├── Parsing/         # ARXML parser + ECUC reader
│   │   ├── Workspace/       # multi-file object/reference semantic index
│   │   └── Validation/      # deterministic diagnostics
│   ├── Autarx.Cli/          # CLI / JSON automation surface
│   └── Autarx.Gui/          # Avalonia frontend
├── tests/Autarx.Tests/
└── docs/
    ├── PRODUCT.md
    └── ROADMAP.md
```

## Roadmap

The near-term roadmap prioritizes **workspace understanding before editing**:

1. multi-file workspace and reference resolution
2. delivery inspection and System/ECU projections
3. semantic diff
4. ECU impact analysis
5. communication trace views
6. vendor validation adapters
7. reviewed editing/patches
8. AI plan/review/apply on top of deterministic tools

See [docs/ROADMAP.md](docs/ROADMAP.md).

## License

Proprietary — © TuringLambdaAI, all rights reserved. See [LICENSE](LICENSE).
