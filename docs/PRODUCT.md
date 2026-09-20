# Product Direction

Autarx is an **AUTOSAR Integration Workbench** for engineers who need to understand, compare, validate, and automate AUTOSAR engineering data across OEM and supplier boundaries.

The first target users are Tier-1 system integration engineers and ECU AUTOSAR engineers receiving OEM deliveries such as System Description, System Extract, ECU Extract, communication descriptions, and ECU configuration ARXML.

## Product promise

Autarx should make questions like these cheap to answer:

- What is in this OEM delivery?
- Which parts of the delivery affect my ECU?
- Where is this signal/PDU/SWC/ECU element defined?
- What references this object, and what does it reference?
- What changed between delivery V32 and V33 semantically?
- Which changes are relevant to `RadarFL` and which are unrelated vehicle changes?
- Is the ARXML structurally sound before I open a vendor configurator?
- Can the same analysis run in CI or from an AI agent through a stable CLI?

## Product boundary

Autarx is **not** intended to become a replacement for DaVinci Configurator, EB tresos, ETAS ISOLAR, or an OEM E/E architecture suite.

In particular, the core product does not own production BSW/RTE/MCAL code generation.

```text
OEM delivery / ARXML / communication data
                  |
                  v
             Autarx
     explore / query / trace
      diff / impact / validate
                  |
                  v
       reviewed ARXML / report
                  |
        +---------+---------+
        |                   |
        v                   v
     DaVinci             tresos / ISOLAR
        |                   |
        +--- vendor validation / generation ---> production code
```

Vendor validation and generation remain authoritative when a project depends on a vendor BSW stack.

## Core concepts

### Workspace

A workspace may contain one ARXML file or a directory/package of many ARXML files. Multi-file resolution is a first-class requirement because real OEM deliveries are not single-file documents.

### Semantic index

The engine builds shared indexes over identifiable AUTOSAR objects and references before applying domain-specific projections. ECUC is one projection, not the root model of the product.

Future projections include:

- System / ECU topology
- ECU Extract view
- Communication (Signal / PDU / Frame / Cluster / ECU)
- Software component deployment
- ECUC
- Diagnostics and security views where useful

### Analysis before editing

The early product prioritizes read-only understanding and analysis:

1. inspect
2. find
3. refs / trace
4. semantic diff
5. ECU impact analysis
6. validation
7. reports / CI

Editing is added only after the semantic model is trustworthy. AI is a client of the same command/semantic APIs, not the foundation of correctness.

## Primary workflows

### OEM delivery intake

Open a delivery package, identify AUTOSAR release and contained models, resolve references, select an ECU, and summarize relevant communication/system content.

### ECU impact analysis

Compare two OEM deliveries and answer what changed for one ECU. The long-term report should distinguish breaking, modified, added, removed, and unrelated changes.

### Reference trace

Trace an engineering object through the model, for example Signal -> PDU -> Frame -> channel/ECU or ECUC references through Com/PduR/CanIf/SecOC where the available data supports it.

### Vendor hand-off

When configuration changes eventually become supported, Autarx reviews and writes ARXML, then invokes vendor adapters for authoritative validation/generation rather than implementing a production code generator itself.

## Engineering principles

- One UI-free semantic engine shared by GUI, CLI, CI, and AI.
- CLI JSON is a stable automation contract.
- Keep raw ARXML representation separate from semantic projections.
- Preserve provenance: every semantic result should be traceable to source file/object.
- Prefer deterministic analysis over AI inference.
- Do not claim vendor-level or functional-safety validation for checks Autarx does not perform.
- Keep vendor integrations behind adapters.
- Product scope decisions should optimize for a small team/independent developer first.
