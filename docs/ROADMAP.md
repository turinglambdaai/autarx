# Roadmap

## M0 — Skeleton (current)

Streaming ARXML parser, ECUC projection (module → container → parameter /
reference), structural validation (ARX001–ARX006), CLI (`info` / `modules` /
`validate`, `--json`), Avalonia workspace shell, CI + release pipelines.

## M1 — Read/write round trip

- Write ARXML back, preserving untouched subtrees byte-for-byte (targeted
  edit model)
- Per-module chunked loading for 10M+ line ECUC files

## M2 — Module packs

- Parameter dictionaries extracted from vendor module definitions
  (tresos / DaVinci / ISOLAR)
- First modules: Mcu, Port, Dio, Can — typed editors with range checks
- DBC import driving CAN configuration

## M3 — Validation & reports

- Multiplicity, value-range, and cross-reference validation
- Configuration diff (base vs variant) with report output

## M4 — Toolchain integration

- Import/export vendor projects (EB tresos, DaVinci Configurator,
  ETAS ISOLAR, VCOS Studio)
- Hand-off to code generation without opening the vendor GUI

## M5 — Agent layer

- LLM-assisted editing on top of the CLI tool surface: plan → review →
  apply → rollback, every change audited
- GUI plan panel; the CLI remains the stable machine interface

## M6 — Commercial

- Licensing/activation, installers, update channel
- Docs site and onboarding samples
