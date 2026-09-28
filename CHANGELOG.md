# Changelog

All notable changes to autarx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.2] - 2026-09-28

### Added

- Online self-update: `autarx update` checks the release feed and applies newer builds in place (checksum-verified, previous binaries kept as `.autarx-update.old`); `--check` reports availability for CI, `--json` for agents.
- Update feed asset: the release pipeline publishes `latest.json` (version, per-platform download URLs, SHA-256, sizes) with every release.
- GUI update menu: quiet startup check plus Help → Check for Updates / Install Update.
- Local crash logs: unhandled failures in CLI and GUI write a log under `<TEMP>/autarx/crashes/` (exit code 3); nothing is sent anywhere.

## [1.0.1] - 2026-09-27

### Fixed

- MCP error responses no longer drop `"id": null`, restoring the JSON-RPC 2.0 contract.

## [1.0.0] - 2026-09-27

### Added

- Workspace semantic index and OEM delivery inspection (M1/M2): System Description, System Extract, ECU Extract, ECUC and communication ARXML.
- Semantic diff and ECU impact analysis (M3/M4).
- Communication projection and vendor hand-off (M5/M6).
- Reviewed patching, MCP agent layer and the Avalonia workbench GUI (M7/M8).
- Self-contained single-file builds for Windows, macOS (Apple silicon) and Linux.

[Unreleased]: https://github.com/turinglambdaai/autarx/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/turinglambdaai/autarx/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/turinglambdaai/autarx/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/turinglambdaai/autarx/releases/tag/v1.0.0
