# Autarx

AUTOSAR Integration Workbench: UI-free semantic engine + Avalonia GUI + JSON
CLI. Inspects, traces and automates OEM-to-supplier integration data. Sits
above vendor generators — never replaces DaVinci/tresos/ISOLAR, never
generates BSW/RTE/MCAL code. Private commercial project.

## Commands

- Build: `dotnet build Autarx.slnx`
- Test: `dotnet test`
- Run CLI: `dotnet run --project src/Autarx.Cli -- <command> <args>`
- Run GUI: `dotnet run --project src/Autarx.Gui`
- Fixture workspace: `tests/Autarx.Tests/Fixtures/OemDelivery`

CLI commands: `inspect` `find` `refs` `trace` `ecus` `ecu` `unresolved`
(workspace) · `info` `modules` `validate` (single file).

## Layout

- `src/Autarx.Core` — semantic engine; no UI dependencies here
  - `Models/` — SemanticObject, SemanticKind, references, documents
  - `Parsing/` — ArxmlParser (streaming), EcucReader (ECUC projection),
    AutosarReleaseParser
  - `Index/` — WorkspaceIndex builder, reference graphs, trace, summary
  - `Validation/` — structural rules (ARX00NN)
- `src/Autarx.Cli` — commands + `--json`; no domain logic in command files
- `src/Autarx.Gui` — Avalonia MVVM (`Views/` + `ViewModels/`); no domain logic
  in ViewModels
- `tests/Autarx.Tests` — xunit + fixtures (ECUC and non-ECUC)

## Rules

- net10.0, nullable enabled
- Core's top-level model is Workspace / SemanticObject / reference graph —
  ECUC stays a projection; never promote ECUC types to the Core top level
- Identity = AUTOSAR absolute short-name path; never XML line numbers
- CLI JSON: camelCase keys, enums as camelCase strings, additive changes only;
  exit codes 0/1/2 are documented contracts
- Diagnostic codes ARX00NN never renumbered (table in ARCHITECTURE.md)
- ARXML matching is LocalName-based; no namespace-aware matching
- Validation wording: `validate` is structural only — never write "AUTOSAR
  compliant", "production valid", or vendor-validation equivalent; vendor
  adapters belong under `autarx vendor ...` (M6)
- Commit style: imperative summary line + bulleted detail
