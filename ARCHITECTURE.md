# Architecture

## Stack

C# / .NET 10, Avalonia for the GUI. One UI-free core, two frontends.

Why this stack:

- **One core, two frontends from day one.** The GUI and the CLI are thin
  shells over `Autarx.Core`; neither contains domain logic.
- **The BCL XML/JSON stack is production-grade** — streaming `XmlReader`
  chews through the 10M+ line ECUC files real projects ship, and
  `System.Text.Json` powers the CLI's machine interface.
- **Self-contained single-file distribution** — customers install nothing.
- **IP protection is tractable** — unlike JS-based UI stacks, the shipping
  artifact is compiled IL that can be obfuscated when the product goes to
  market.
- Alternatives considered: Electron/TypeScript (weakest IP protection, no
  precedent in our toolchain) and our own Racket stack (glaze/tessera are
  promising but pre-1.0 — a commercial product should not ride on framework
  risk).

## Layering

```text
Autarx.Core   ARXML parse → ECUC model → validation. No UI dependencies.
   ├── Autarx.Cli   info / modules / validate, --json output
   └── Autarx.Gui   Avalonia workspace (MVVM)
```

## ARXML strategy

- Matching is **LocalName-based**: namespace prefixes never affect what is
  parsed. Vendor tools disagree about prefixes; LocalName is what survives
  interchange.
- One streaming pass builds an `ArxmlElement` tree. ECUC modules are then
  located by whole-tree scan, so files with modules directly under
  `AR-PACKAGE/ELEMENTS` or wrapped in `ECUC-VALUE-COLLECTION` / `ECUC-VALUES`
  all work.
- Planned (M1): write-back that preserves untouched subtrees byte-for-byte,
  and per-module chunked loading for huge files.

## Diagnostics

Structural validation reports SHORT-NAME paths
(`Mcu/McuGeneralConfiguration/...`) — how AUTOSAR engineers actually refer to
configuration locations.

Rule codes are part of the CLI contract. **Never renumber; append only.**

| Code   | Severity | Rule |
| :----- | :------- | :--- |
| ARX001 | Error    | Module configuration missing DEFINITION-REF |
| ARX002 | Error    | Container missing DEFINITION-REF |
| ARX003 | Error    | Duplicate sibling container SHORT-NAME |
| ARX004 | Error    | Parameter has neither VALUE nor VALUE-REF |
| ARX005 | Warning  | Parameter DEFINITION-REF outside module definition |
| ARX006 | Error    | Module/container missing SHORT-NAME |

## CLI JSON contract

- `--json` emits camelCase keys, indented.
- Exit codes: `0` success, `1` validation errors found, `2` usage/file/parse
  error. Deterministic, documented in `--help`.
- Additive changes only — scripts and AI agents parse this output.

## Testing

Parser, ECUC reader, and validation suites run against a checked-in fixture
(`tests/Autarx.Tests/Fixtures/minimal.arxml`). CI builds, tests, and runs the
CLI against the fixture as a smoke test on every push.
