# Autarx

**Cross-platform AUTOSAR ECU configuration tool.**
Open ARXML directly — browse ECUC module configurations, validate structure, and script everything from an agent-friendly CLI with JSON output. No vendor configurator required.

[![CI](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml/badge.svg)](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-12-9B4FBE?logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE)

[中文](README.zh-CN.md) · English

---

## Why Autarx?

- **Vendor configurators are heavy and platform-bound** — EB tresos, DaVinci Configurator and ISOLAR lock configuration data behind Windows GUIs and proprietary project formats
- **ARXML is grep-hostile** — engineers hand-navigate XML trees of millions of lines; nothing answers "which modules are configured here" without launching a full toolchain
- **No automation surface** — CI cannot diff or validate ECUC configurations because no vendor tool exposes a stable machine interface
- **Agents cannot help** — LLM agents work on text surfaces with structured feedback; vendor tools offer neither

Autarx fixes that: one UI-free core that reads ARXML directly, a native Avalonia GUI for humans, and a JSON-first CLI for scripts, CI and AI agents.

## Features (v0)

- **ARXML engine** — single-pass streaming parser, tolerant of vendor namespace prefixes; built for the 10M+ line ECUC files real projects ship
- **ECUC projection** — module → container → parameter/reference model with rolled-up counts
- **Structural validation** — ARX001–ARX006 rules (missing refs, duplicate sibling names, orphan parameters), reported as SHORT-NAME paths
- **Agent-friendly CLI** — `info` / `modules` / `validate` with `--json` output and deterministic exit codes
- **Avalonia workspace** — native GUI on Windows, macOS and Linux: module tree, detail pane, status bar
- **Self-contained builds** — single-file executables per platform, no .NET install needed

## Command line

```bash
# summarize a file
autarx info Mcu_Can.arxml
autarx info Mcu_Can.arxml --json

# list ECUC module configurations
autarx modules Mcu_Can.arxml --json

# validate structure — exit code 1 when errors are found
autarx validate Mcu_Can.arxml
```

`autarx validate --json` output:

```json
{
  "errors": 0,
  "warnings": 0,
  "diagnostics": []
}
```

## Getting started

### Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
dotnet build Autarx.slnx
dotnet run --project src/Autarx.Gui
dotnet run --project src/Autarx.Cli -- info tests/Autarx.Tests/Fixtures/minimal.arxml
```

### Tests

```bash
dotnet test
```

The suite covers the parser (namespace stripping, attributes, error line numbers), the ECUC reader (containers, parameters, references, rollups) and the validation rules — all against a checked-in fixture ARXML.

## Project structure

```text
autarx/
├── src/
│   ├── Autarx.Core/            # ARXML parsing, ECUC model, validation (no UI dependencies)
│   │   ├── Models/             # ArxmlElement tree, EcucModule/Container/Parameter/Reference
│   │   ├── Parsing/            # ArxmlParser (streaming XmlReader), EcucReader (projection)
│   │   └── Validation/         # ValidationEngine, ARX00NN diagnostics
│   ├── Autarx.Cli/             # agent-friendly CLI — info / modules / validate, --json
│   └── Autarx.Gui/             # Avalonia workspace — module tree, details, status bar
├── tests/Autarx.Tests/         # parser / ECUC / validation suites + fixture ARXML
└── docs/ROADMAP.md
```

## Design notes

- The core library is UI-free; both frontends are thin shells over it, and the CLI's JSON output is a stable contract for scripts and AI agents — additive changes only
- ARXML matching is LocalName-based: vendor namespace prefixes never change what is parsed, mirroring how real-world configurators treat each other's files
- Validation reports SHORT-NAME paths (`Mcu/McuGeneralConfiguration/…`) — how AUTOSAR engineers actually refer to configuration locations
- Rule codes ARX001–ARX006 are documented in [ARCHITECTURE.md](ARCHITECTURE.md) and never renumbered — scripts and agents depend on them

## Roadmap

See [docs/ROADMAP.md](docs/ROADMAP.md): ARXML round-trip editing, per-module chunked loading, DBC import, vendor configurator integration, and an agent layer with plan/apply/rollback.

## License

Proprietary — © TuringLambdaAI, all rights reserved. See [LICENSE](LICENSE).
