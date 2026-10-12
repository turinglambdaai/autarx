# Changelog

All notable changes to autarx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-10-12

Real-delivery hardening release: every fix below was driven by running the
workbench against genuine OEM deliveries (hundreds of ARXML files, up to
322 MB) for the first time.

### Fixed

- **ECUC container values are identifiables** — nested
  `ECUC-CONTAINER-VALUE` elements are indexed with their absolute paths and
  take over reference attribution, so the `VALUE-REF`s that point at them
  resolve inside a single ECUC file. On a real 97 MB DaVinci ECUC this
  turns 49,068 unresolved references into 21,374 (the remainder are
  genuinely cross-file); the container count (45,186) matches the file's
  XML exactly
- **XSD revision numbers map to releases** — vendor files carrying
  `AUTOSAR_00044.xsd`-style schema locations reported the raw revision;
  known revisions now translate to the classic version (00043 → 4.3.0,
  00044 → 4.3.1, 00047 → 4.4.0, 00048 → 4.5.0, 00049 → 4.6.0,
  00050 → 4.7.0), unknown ones stay raw rather than guessing
- **Constants classify** — `CONSTANT-SPECIFICATION` elements report as the
  new `constant` semantic kind instead of `unknown` (539/539 classified on
  a real 322 MB constants delivery)
- **ECU membership recovered through NM config** — DaVinci-style system
  extracts carry no direct ECU↔cluster edge: the NM-CLUSTER holds the
  `COMMUNICATION-CLUSTER-REF` and each NM-NODE a controller ref into the
  ECU. The communication projection pairs the two by nested element-path
  prefix and rides the controller target back to its owning ECU, so real
  clusters list their ECUs (verified: 5/5 clusters on a real extract)
- `I-SIGNAL-I-GROUP` classifies as `signal` (the table carried a
  misspelled `I-SIGNAL-GROUP` that never matched)

### Changed

- Semantic kinds are append-only: `ecuc-container` and `constant` join the
  inventory, the CLI/JSON contracts and the GUI inventory rows

## [0.3.0] - 2026-10-12

In-app update release: the macOS workbench now updates itself through
the family's signed-feed updater, and the host moves to the current
Rivet generator line.

### Added

- Workbench in-app updater (`shared/spec/UPDATE.md`): a silent check
  shortly after launch (throttled to one attempt per 4 hours) plus a
  manual `Check for Updates…` (⌘U) menu entry. The offer panel
  downloads a portable app zip with progress, verifies the Ed25519
  manifest signature, the artifact checksum and the unpacked bundle
  version, swaps the bundle in place and relaunches — only copies under
  `/Applications` (or `~/Applications`) can self-update
- Signed workbench feed `update-app-manifest.json`: the release publish
  job generates and signs it (`rivet/distribution` wrapper format, key
  id `autarx-2026-10`, secret `UPDATE_ED25519_PRIVATE_KEY_B64`) and
  re-verifies the signature before the release ships; a missing secret
  publishes a warning and no manifest rather than an unsigned one
- Portable app zips `autarx-<version>-macos-<arch>-app.zip` — the
  updater's payload, beside the human-installer DMGs, with the usual
  `.sha256` sidecars

### Changed

- Rivet pin 2fcdd09 → 53487b3: generated Swift DTOs moved into the
  `RivetTypes` namespace (upstream #174); the host is migrated and back
  on the current generator line. The CLI feed (`update-manifest.json`
  + `latest.json` alias) is untouched — CLI builds up to 0.2.0 keep
  updating through their checksum channel

## [0.2.0] - 2026-10-11

Packaging-parity release: the asset set closes every gap that does not
require the still-in-development Windows and Linux GUI hosts.

### Added

- The macOS workbench ships an Intel build: `autarx-<version>-macos-x64.dmg`
  joins the Apple-silicon DMG (native per-arch Racket, no Rosetta)
- CLI zips for `macos-x64` and `linux-arm64` — the CLI now covers
  `{macos-arm64, macos-x64, linux-x64, linux-arm64, windows-x64}`, built on
  native per-arch runners
- Every release asset ships a per-asset `.sha256` sidecar beside the
  aggregated `SHA256SUMS`

### Changed

- The update feed is published as `update-manifest.json` (family
  convention) and carries the two new CLI platforms; a byte-identical
  `latest.json` rides along so pre-0.2.0 CLI builds keep self-updating —
  the alias will be dropped once the field has moved on

## [0.1.0] - 2026-10-10

Version epoch reset. The technical rewrite is settled, so the project enters
a 0.x functional-validation phase: the first release of the new epoch is
0.1.0. The 1.x-era releases and tags (v1.0.0, v1.1.0 of the Rivet rebuild)
were deleted from GitHub Releases and the tag list; the sections below stay
as the historical record of that era.

### Changed

- Release artifacts follow `autarx-<version>-<os>-<arch>.<ext>` (lowercase,
  with architecture): `autarx-0.1.0-macos-arm64.dmg` for the macOS
  workbench and `autarx-0.1.0-{macos-arm64,linux-x64,windows-x64}.zip` for
  the CLI. The old `Autarx-*-macos.dmg` and `autarx-cli-*` names are gone;
  the update-feed contract and release workflow were updated to match.
- The version is single-sourced on `rivet.rktd`. The CLI no longer carries
  a version literal — source checkouts read the repo manifest, distributed
  builds read `rivet-app-info.rktd`; a build that finds neither fails
  instead of reporting a stale version. `scripts/check-release-version.sh`
  (release-gate, taskly-style) enforces tag == rivet.rktd ==
  GeneratedBackend.swift and that the CHANGELOG announces the release.
- The rivet generator is pinned in CI/release workflows (the RivetTypes
  namespace change in rivet breaks this SwiftUI host until it is adopted).

## 1.1.0 - 2026-10-09

### Added

- **Self-updating CLI**: `autarx update` now has a live feed. Releases ship
  self-contained CLI zips (launcher + embedded Racket runtime) for macOS
  (arm64), Linux (x64) and Windows (x64), and a checksum-verified
  `latest.json` feed; check compares versions, apply downloads, verifies
  SHA-256 before touching anything, and swaps in place with
  `.autarx-update.old` backups. The feed is the contract documented in
  `racket/autarx/update.rkt`; the GUI leg stays manual (DMG drag-install)
  until the Windows/Linux hosts land.
- The CLI reports its true version: distributed builds carry
  `rivet-app-info.rktd` beside the launcher and read it at startup
  (source checkouts fall back to the rivet.rktd literal). The shipped 1.0.0
  launcher reported a stale internal constant.

### Fixed

- `autarx update` extraction works on Windows (PowerShell
  `Expand-Archive`) instead of assuming `unzip`.

## 1.0.0 - 2026-10-08

First release of the Rivet rebuild: the semantic engine, CLI, MCP server and
the native macOS workbench are one Racket codebase. Ships macOS (Apple
silicon, macOS 14+); the Windows and Linux hosts are in development.

### Added

- One Racket codebase replacing the .NET/Avalonia implementation (v1.0.3,
  archived as the behaviour oracle): JSON output, exit codes, rule codes and
  content hashes are contract-compatible — verified byte-level against the
  v1.0.3 CLI and by 101 ported contract tests. Indexing on a 1.2M-line ECUC
  workspace is ~2.5× the .NET baseline (R0 stop-rule passed).
- `autarx mcp` unchanged (12 tools, audit logging); CLI unchanged
- Native macOS workbench (SwiftUI) over the embedded Racket core via RVT1
- `progress` RPC event during long indexing/diff operations
- In-place self-update (`autarx update`): checksum-verified feed, previous
  binaries kept as `.autarx-update.old`

### Removed

- .NET/Avalonia implementation tree (`src/`, `tests/`, `Autarx.slnx`)

