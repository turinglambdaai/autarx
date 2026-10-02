# AGENTS.md

Guidance for AI agents (and developers): how to understand, build, run and
change Autarx.

## What this is

Autarx is an AUTOSAR Integration Workbench (inspect, trace, diff and audit
OEM-to-supplier AUTOSAR delivery data), rebuilt on the Rivet application
framework: one Racket domain core (`racket/autarx/`), a JSON CLI + MCP
server linking the core directly, and native GUI hosts speaking RVT1.
Branch `experiment/autarx-rivet` is the development line; the archived
.NET implementation (v1.0.3, `main`) is the behaviour and JSON oracle.

- Migration plan + stop-rule record: `docs/RIVET-MIGRATION.md`
- Product boundaries: `docs/PRODUCT.md`

## Quick commands

```bash
# 0) Prerequisite: Racket CS 9.x with rivet linked
cd ../rivet && raco pkg install --auto --no-docs --name rivet --link file://$PWD

# 1) Domain + contract tests (101 cases, RVT1 smoke included)
raco test racket/

# 2) CLI against a fixture
racket racket/autarx/cli.rkt inspect shared/fixtures/OemDelivery --json

# 3) Build the staged app (embedded backend + macOS SwiftUI host)
raco rivet build
raco rivet dev

# 4) MCP server (newline-delimited JSON-RPC 2.0 on stdio)
racket racket/autarx/backend.rkt   # RVT1 transport (GUI/dev)
# mcp: racket racket/autarx/cli.rkt mcp

# 5) Large-file benchmark (R0 stop-rule reproduction)
racket scripts/gen-bench-ecuc.rkt -o "$TMPDIR/autarx-bench" -n 50000
```

## Contracts (read before changing behaviour; change both sides together)

- **CLI JSON**: camelCase keys in declaration order, null omitted, string
  enums, 2-space indent; exit codes 0/1/2/3 with per-command variants
  (diff: 0 identical/1 differs; vendor validate: 0 clean/1 tool failed/
  2 autarx-side; patch: 1 plan invalid). Byte-verified against v1.0.3.
- **Rule codes**: ARX001–ARX006 (validation) and ARX-IMP-\* (impact) are
  append-only, never renumbered.
- **Content hashes**: FNV-1a subtree hashes are a cross-implementation
  contract (UTF-16 code units, 2^64 wrap; see `hash.rkt`).
- **MCP**: 12 tools, indented payloads, compact error envelopes with
  explicit `"id":null`; every tools/call appends to
  `<workspace>/.autarx/audit.jsonl`.
- **Patch pipeline**: plan is read-only and produces a mandatory semantic
  diff; apply writes `.autarx/` backups + `patch-history.jsonl`; no raw
  write surface exists anywhere.
- **RPC surface** (`racket/autarx/backend.rkt` define-rpc): open/close
  workspace, summary, find/list objects, refs, trace, ecus, unresolved,
  diff, impact, communication, validate, patch plan/apply + `progress`
  event. Changing a signature requires regenerating host clients
  (`raco rivet build`).

## Layout

```
autarx/
├── rivet.rktd              Rivet manifest (backend/module/entry/protocol)
├── racket/autarx/          domain core + cli.rkt + mcp.rkt + backend.rkt
├── racket/tests/           101 contract tests + RVT1 smoke
├── macos-host/             SwiftUI workbench (SwiftPM; builds via rivet)
├── shared/fixtures/        synthetic deliveries (ECUC + System/Comm)
├── scripts/                bench generator
└── docs/                   RIVET-MIGRATION / PRODUCT / site
```

## Conventions

- Domain logic lives in `racket/autarx/` only; hosts render, CLI/MCP translate.
- Unknown AUTOSAR element types degrade to `unknown` — never a parse
  failure. Ambiguity is a first-class outcome, never silently resolved.
- Rivet framework gaps go upstream (issue/PR to turinglambdaai/rivet);
  this repo pins a commit.
- `raco test racket/` must stay green on every change.
