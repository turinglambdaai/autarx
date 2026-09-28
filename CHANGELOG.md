# Changelog

All notable changes to autarx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/turinglambdaai/autarx/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/turinglambdaai/autarx/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/turinglambdaai/autarx/releases/tag/v1.0.0
