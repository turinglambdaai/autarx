# Architecture

## Product architecture, not configurator architecture

Autarx is an AUTOSAR Integration Workbench. Its core responsibility is to understand engineering data across OEM and supplier boundaries, not to become a production BSW/RTE/MCAL generator.

The architecture therefore optimizes for:

- multi-file ARXML workspaces
- provenance-preserving semantic indexes
- reference tracing
- semantic diff and ECU impact analysis
- deterministic CLI/CI automation
- thin GUI and AI clients
- vendor-tool hand-off for authoritative validation/generation

See [docs/PRODUCT.md](docs/PRODUCT.md) for the product boundary.

## Stack

C# / .NET 10, Avalonia for the GUI. One UI-free core, multiple clients.

The repository stays single-language for now. A Rust core may be reconsidered only if profiling, portability, distribution, or library requirements justify the added FFI/process boundary. For an independent developer, reducing architectural surface area is currently more valuable than adding another implementation language.

Why this stack:

- **One core for GUI, CLI, CI and AI.** Domain logic does not live in the frontend.
- **Production-grade XML/JSON primitives.** `XmlReader` and `System.Text.Json` are sufficient for the current parser/automation surface.
- **Self-contained distribution.** Customers should not need a preinstalled runtime.
- **Avalonia keeps the desktop client native enough for engineering workflows** while preserving Windows/Linux/macOS reach.

## Layers

```text
+-------------------------------------------------------------+
| Clients                                                     |
|                                                             |
|  Autarx.Gui        Autarx.Cli        CI / AI / integrations |
+---------------------------+---------------------------------+
                            |
                            v
+-------------------------------------------------------------+
| Semantic / application layer                                |
|                                                             |
|  Query   Trace   Diff   Impact   Validation   Reports       |
|                                                             |
|  Domain projections:                                        |
|  System / ECU / Communication / SWC / ECUC                 |
+---------------------------+---------------------------------+
                            |
                            v
+-------------------------------------------------------------+
| Workspace                                                   |
|                                                             |
|  Documents   Identifiables   Reference graph   Provenance    |
+---------------------------+---------------------------------+
                            |
                            v
+-------------------------------------------------------------+
| Raw ARXML                                                   |
|                                                             |
|  Parser   source locations (planned)   round-trip support    |
+-------------------------------------------------------------+
                            |
                            v
+-------------------------------------------------------------+
| Vendor adapters (optional / isolated)                       |
|                                                             |
|  DaVinci   EB tresos   ETAS ISOLAR   OEM-specific tools    |
+-------------------------------------------------------------+
```

### Raw ARXML model

`ArxmlElement` is the loss-tolerant raw representation used by the parser. It must remain independent from ECUC-specific semantics.

Current matching is LocalName-based so namespace prefixes do not affect discovery. Namespace URI / AUTOSAR release awareness should be added explicitly rather than inferred from prefixes.

### Workspace

`ArxmlWorkspace` is the first shared semantic layer. It can load a file or an ARXML directory and builds:

- an identifiable-object index from `SHORT-NAME`
- AUTOSAR-like hierarchical paths
- simple reference edges from `*-REF` / `*-TREF`
- source-file provenance

This is intentionally generic. It exists so System Description, ECU Extract, communication and ECUC models do not each invent independent document/reference handling.

### Semantic projections

A projection turns raw indexed objects into domain concepts without owning the source document.

Planned projections include:

- **System / ECU** — `SYSTEM`, `ECU-INSTANCE`, clusters, connectors, mappings
- **Communication** — Signal / PDU / Frame / Cluster / ECU relationships
- **SWC** — components, ports, interfaces and deployment/mapping
- **ECUC** — module/container/parameter/reference configuration

The existing `EcucReader` is an early projection. It should gradually be adapted to consume workspace semantics, not become the foundation of every other feature.

### Query and trace

Search starts with deterministic object/path/type matching. Later query syntax should operate on semantic objects and graph relationships, not raw XPath-like XML positions.

`refs` is the first graph operation. `trace` will compose edges into engineering paths such as:

```text
Signal -> PDU -> Frame -> Cluster -> ECU
```

or, when ECUC data is present:

```text
Com -> PduR -> SecOC -> CanIf
```

### Diff and impact

Semantic diff compares engineering identities and properties rather than serialized XML lines.

Impact analysis is a higher-level operation over semantic diff plus the reference/deployment graph. The key initial use case is:

```text
OEM Delivery V32 + OEM Delivery V33 + target ECU
                    -> ECU impact report
```

The report should distinguish relevant changes from unrelated vehicle changes and retain provenance to the exact source objects/files.

### Validation

Validation is layered and claims must match the layer:

1. XML / parser validity
2. workspace consistency / broken references
3. deterministic Autarx semantic rules
4. optional vendor validation through adapters

Autarx must never present layer 1-3 as equivalent to a vendor's complete production configuration validation.

### Editing

Editing is intentionally later than analysis. Before adding broad write capability the engine needs stable identity, provenance, semantic diff and tests.

The intended flow is:

```text
plan -> review semantic patch -> write ARXML -> Autarx validate
     -> optional vendor validate -> vendor generate
```

Production code generation remains outside the core product boundary.

## Existing ECUC diagnostics

The current ECUC validation reports SHORT-NAME paths.

Rule codes are part of the CLI contract. Never renumber; append only.

| Code   | Severity | Rule |
| :----- | :------- | :--- |
| ARX001 | Error    | Module configuration missing DEFINITION-REF |
| ARX002 | Error    | Container missing DEFINITION-REF |
| ARX003 | Error    | Duplicate sibling container SHORT-NAME |
| ARX004 | Error    | Parameter has neither VALUE nor VALUE-REF |
| ARX005 | Warning  | Parameter DEFINITION-REF outside module definition |
| ARX006 | Error    | Module/container missing SHORT-NAME |

Future rule families should make their scope clear (workspace/system/communication/ECUC/vendor adapter) rather than mixing all checks into one validator.

## CLI contract

- `--json` emits camelCase keys.
- Exit codes remain deterministic.
- Machine-readable output changes are additive whenever possible.
- Human formatting is not a stable API; JSON is.
- Commands should work against workspaces/directories when their semantics are naturally multi-file.

## Testing strategy

Tests should grow from parser-level fixtures into scenario fixtures representing realistic deliveries:

- multi-file reference resolution
- duplicate short names in separate scopes
- System + ECU Extract examples
- communication chains
- semantic diff fixtures
- ECU impact fixtures
- vendor adapter contract tests without requiring proprietary tools in normal CI

Every semantic result should be traceable to its source ARXML and covered by deterministic tests before AI consumes it.
