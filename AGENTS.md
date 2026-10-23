# AGENTS.md

Guidance for AI agents (and developers): how to understand, build, run and
change Autarx.

## What this is

Autarx is an AUTOSAR Integration Workbench (inspect, trace, diff and audit
OEM-to-supplier AUTOSAR delivery data), rebuilt on the Rivet application
framework: one Racket domain core (`racket/autarx/`), a JSON CLI + MCP
server linking the core directly, and native GUI hosts speaking RVT1.
`main` is the current Rivet development line. The archived .NET v1.0.3
implementation remains a historical behaviour/JSON reference in Git history;
do not develop against an old migration branch.

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

# 6) Current-platform product acceptance (contract → RVT1 → native → package → UI)
raco rivet acceptance --json
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
  this repo pins the reviewed `v0.12.0` release across CI, acceptance, and release.
- Agent 不得以单元测试、编译或截图单独宣称产品完成；涉及 GUI 的改动必须让
  `raco rivet acceptance --json` 在声明支持的平台通过，并保留原生控件交互与
  后端结果证据。
- `raco test racket/` must stay green on every change.

## 组合定位（Portfolio placement）

- 轨道与授权：轨道 1 商业支柱（B2B 专业工具）；授权形态：专有许可；商业归属：适合收费（收费节奏第三棒）。
- 组合归属唯一权威：[PORTFOLIO.md](https://github.com/turinglambdaai/turinglambdaai.github.io/blob/master/PORTFOLIO.md)（turinglambdaai.github.io 仓库）。改归属/授权先改权威档，再同步本节。
- 纪律：价格只进本仓 `PRICING.md`，且收费开始时才发布；README 与产品站不做价格预告。

## 产品交付与下一步（2026-10-23）

- 家族[交付标准](https://github.com/turinglambdaai/turinglambdaai.github.io/blob/master/docs/PRODUCT-STANDARD.md)与[组合策略](https://github.com/turinglambdaai/turinglambdaai.github.io/blob/master/PORTFOLIO.md)是共用政策；本仓具体行为以 `docs/ROADMAP.md` 为准，不复制五库依赖图。
- 当前执行入口：[产品审查 issue](https://github.com/turinglambdaai/autarx/issues/22)。以 M9 为当前队列：先记录合成大型 ARXML 的索引/diff/影响分析性能预算与基线，再做 Permit 接入。历史栈只作历史说明，不再启动迁移分支。
- 开工先检查干净工作树与远端，以 fast-forward-only 更新并记录 SHA；共享文件一次一个写入者，保留其他 checkout 的未提交工作。
- 每次只完成一个可验收切片，记录 owned files、依赖 pin、命令/平台/产物证据和回滚。已实现、已发布、实机验收分别陈述；不修改 requirements 或复用旧证据来制造通过。
