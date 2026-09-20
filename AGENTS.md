# Autarx

AUTOSAR ECU configuration tool: UI-free core + Avalonia GUI + JSON CLI.
Private commercial project.

## Commands

- Build: `dotnet build Autarx.slnx`
- Test: `dotnet test`
- Run CLI: `dotnet run --project src/Autarx.Cli -- <command> <file.arxml>`
- Run GUI: `dotnet run --project src/Autarx.Gui`

## Layout

- `src/Autarx.Core` — ARXML parse, ECUC model, validation; no UI dependencies here
- `src/Autarx.Cli` — info/modules/validate, `--json` output, exit codes 0/1/2
- `src/Autarx.Gui` — Avalonia MVVM (`Views/` + `ViewModels/`)
- `tests/Autarx.Tests` — xunit + fixture ARXML

## Rules

- net10.0, nullable enabled
- CLI JSON keys are camelCase, changes additive only; diagnostic codes
  ARX00NN never renumbered (table in ARCHITECTURE.md)
- ARXML matching is LocalName-based; no namespace-aware matching
- Commit style: imperative summary line + bulleted detail
