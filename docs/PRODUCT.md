# Product

## Positioning

Autarx is a **modern AUTOSAR engineering workbench for inspecting, understanding,
comparing, tracing and automating OEM-to-supplier integration data**.

It is **not** a configurator. Autarx does not replace DaVinci Configurator,
EB tresos, ETAS ISOLAR, or any vendor generator — it sits **above** them:

```text
OEM Delivery
    → System Description
    → System Extract
    → ECU Extract
    → Tier1 ECU Integration
    → DaVinci / tresos / ISOLAR
    → Vendor Validation / Generation
        ▲
        └── Autarx works here
```

## Who it serves

1. Tier1 system integration engineers
2. ECU AUTOSAR engineers
3. OEM ↔ Tier1 ARXML delivery flows
4. CI / automation / AI agents

## Capabilities

Autarx can:

- read ARXML files and directories as a Workspace
- index AUTOSAR identifiables with stable absolute paths
- classify elements into semantic kinds (extensible, unknown-safe)
- build forward and reverse reference graphs
- search, trace, and inspect workspaces
- run structural consistency checks (unresolved references, duplicate paths)
- emit deterministic JSON for scripts, CI and AI agents
- later: semantic diff, impact analysis, reviewed editing, vendor tool adapters

Autarx does **not**:

- generate production BSW / RTE / MCAL C code
- claim its validation replaces Vector / EB / ETAS vendor validation
- take certification responsibility for production configuration correctness

The product chain is: **Autarx analyses/modifies ARXML → Vendor Tool →
Vendor Validation → Vendor Generator.**

## Validation wording policy

`autarx validate` means structural checks only: XML correctness, workspace
integrity, broken references, duplicate paths, structural rules. It must never
be presented as "AUTOSAR compliant" or "production valid". Vendor validation
will arrive later as explicit vendor adapters (`autarx vendor validate`).

## Command surface

Current: `inspect` · `find` · `refs` · `trace` · `ecus` · `ecu` ·
`unresolved` · `info` · `modules` · `validate`

Planned: `diff` · `impact` · `vendor validate`

## Agent / automation stance

The CLI is the machine interface — deterministic exit codes, stable camelCase
JSON, additive changes only. A future AI layer must be built **on top of the
stable semantic tool API**; AI never edits XML directly.

## Engineering principles

Simple, stable, testable, maintainable, shippable — in that order. Core stays
UI-free; ViewModels and CLI commands contain no AUTOSAR domain logic; no large
dependencies without a quantified reason.
