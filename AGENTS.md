# Autarx

AUTOSAR Integration Workbench for OEM-to-supplier engineering workflows.
Private commercial project.

## Product boundary

Autarx exists to understand, compare, validate, trace, and automate AUTOSAR engineering data across System Description / System Extract / ECU Extract / ECUC and related delivery artifacts.

Do **not** casually evolve the project into a DaVinci/tresos replacement.

Explicit non-goals unless a future product decision says otherwise:

- production BSW implementation
- production RTE/BSW/MCAL code generation
- complete OEM E/E architecture authoring
- broad typed editors for every BSW module

Vendor validation/generation should live behind adapters and remain authoritative for vendor BSW projects.

Read `docs/PRODUCT.md` before making architectural or roadmap changes.

## Commands

- Build: `dotnet build Autarx.slnx`
- Test: `dotnet test`
- Run CLI: `dotnet run --project src/Autarx.Cli -- <command> ...`
- Run GUI: `dotnet run --project src/Autarx.Gui`

## Architecture

- `src/Autarx.Core` — UI-free engine
  - `Models/` — raw ARXML + current domain models
  - `Parsing/` — parser/projections
  - `Workspace/` — multi-file object/reference index and provenance
  - `Validation/` — deterministic checks
- `src/Autarx.Cli` — stable JSON automation surface
- `src/Autarx.Gui` — Avalonia frontend; do not put domain logic here
- `tests/Autarx.Tests` — xUnit fixtures and semantic tests

The intended layering is:

```text
raw ARXML -> workspace/index -> semantic projections -> query/diff/impact/validation
                                                   -> CLI / GUI / CI / AI
                                                   -> optional vendor adapters
```

ECUC is one semantic projection, not the root model of the product.

## Engineering rules

- net10.0, nullable enabled
- Prefer deterministic analysis over AI inference
- Preserve source-file provenance for semantic results
- CLI JSON keys are camelCase; machine contract changes should be additive
- Diagnostic codes ARX00NN never renumber; append new codes/families
- ARXML element discovery currently uses LocalName; add namespace/release awareness explicitly rather than relying on prefixes
- Multi-file workspace behavior is first-class for new semantic features
- Editing must produce a reviewable semantic diff; do not write XML blindly
- Do not claim vendor-level or functional-safety validation for checks Autarx does not perform
- Commit style: imperative summary line + bulleted detail

## Product priority

When choosing between features, prefer this order unless the roadmap changes explicitly:

1. workspace / reference correctness
2. OEM delivery inspection
3. semantic diff
4. ECU impact analysis
5. communication trace
6. vendor adapters
7. reviewed editing
8. AI orchestration
