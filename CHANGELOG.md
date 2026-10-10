# Changelog

All notable changes to autarx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

