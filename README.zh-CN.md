# Autarx

**面向 OEM → Tier1 / ECU 集成流程的 AUTOSAR Integration Workbench。**

Autarx 直接读取 AUTOSAR 工程数据，帮助工程师完成 **交付包检查、搜索、引用追踪、语义对比、ECU 影响分析、基础校验，以及 CLI / CI / AI 自动化**。

它的目标不是替代 DaVinci Configurator、EB tresos、ETAS ISOLAR，也不承担量产 BSW/RTE/MCAL 代码生成。

[English](README.md) · 中文

---

## 产品定位

第一阶段主要服务：

- Tier1 系统集成工程师
- ECU AUTOSAR 工程师
- 接收 OEM System Description / System Extract / ECU Extract 的团队
- 需要处理多文件 ARXML 交付包、通信数据和 ECUC 配置数据的团队

Autarx 解决的是 **OEM 数据交付 → Tier1/ECU 集成 → Vendor 工具校验/生成** 中间的工程效率问题。

```text
OEM 交付
System / ECU Extract / ARXML Package
                 |
                 v
              Autarx
      inspect / find / refs / trace
       semantic diff / ECU impact
         validation / report
                 |
                 v
          已理解/已审核的数据
                 |
        +--------+---------+
        |                  |
        v                  v
     DaVinci           tresos / ISOLAR
        |                  |
        +--- Vendor Validation / Generation ---> 量产代码
```

**产品边界：** Autarx 可以调用 Vendor 工具进行最终校验和生成，但 Autarx Core 本身不自研量产 BSW/RTE/MCAL Generator。

详细范围见 [docs/PRODUCT.md](docs/PRODUCT.md)。

## 为什么做 Autarx

- **OEM 交付很难快速理解**：工程信息散落在大量 ARXML、Package、Reference、Mapping 中。
- **XML Diff 不等于工程 Diff**：供应商真正想知道的是“这次 OEM 改动是否影响我的 ECU”。
- **Reference 很难手工追踪**：需要快速回答“谁引用它”“它最终指向哪里”。
- **Vendor 工具是最终权威，但并非所有分析都值得先启动重工具链**。
- **自动化缺少统一入口**：GUI、CLI、CI、AI 应该共享同一套确定性的语义能力。

## 当前能力

仓库仍处在早期阶段，但方向已经调整为“Raw ARXML → Workspace/Semantic Index → Domain Projection”。

- **ARXML Parser**：基于 `XmlReader` 的流式解析
- **Multi-file Workspace**：支持单个 `.arxml` 或递归读取目录
- **Semantic Object Index**：基于 `SHORT-NAME` 建立 AUTOSAR identifiable 索引
- **Reference Graph**：索引简单 `*-REF` / `*-TREF` 引用关系
- **ECUC Projection**：保留现有 Module → Container → Parameter/Reference 模型
- **ECUC Structural Validation**：现有 ARX001–ARX006 规则
- **JSON-first CLI**：供脚本、CI、Agent 使用
- **Avalonia GUI Shell**：GUI 只做前端，领域逻辑留在 Core

## CLI

现有 ECUC 命令继续保留：

```bash
autarx info Mcu_Can.arxml
autarx modules Mcu_Can.arxml --json
autarx validate Mcu_Can.arxml
```

新的 Workspace/Semantic 命令可作用于单个文件或整个 ARXML 目录：

```bash
# 按 SHORT-NAME、路径片段或 AUTOSAR Element 类型查找
autarx find VehicleSpeed ./oem-delivery
autarx find ECU-INSTANCE ./oem-delivery --json

# 查看匹配对象的入向/出向引用
autarx refs /Can/Can/CanGeneralConfiguration ./project
autarx refs VehicleSpeed ./oem-delivery --json
```

后续重点命令：

```text
inspect
trace
diff
impact --ecu <name>
```

## 架构方向

```text
                    Autarx.Core
                         |
             +-----------+-----------+
             |                       |
        Raw ARXML Model         Workspace Index
                               Object / Reference
             |                       |
             +-----------+-----------+
                         |
                 Semantic Projection
          System / ECU / Communication / ECUC
                         |
              +----------+----------+
              |                     |
         Autarx.Cli             Autarx.Gui
              |                     |
            CI / AI              Avalonia
                         |
                  Vendor Adapters
             DaVinci / tresos / ISOLAR
```

**ECUC 只是一个 Projection，而不是整个产品的根模型。**

这样未来才能正确支持：

- System Description
- System Extract
- ECU Extract
- ECU / Network Topology
- Signal / PDU / Frame / Cluster
- SWC Deployment
- ECUC

详细设计见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 我们明确不做什么

当前阶段不做：

- 完整 AUTOSAR BSW 实现
- 量产 BSW / RTE / MCAL C Code Generator
- DaVinci / tresos / ISOLAR 替代品
- 完整 OEM E/E Architecture Authoring Suite
- 把 AI 当作工程正确性的来源

这些边界是为了让产品保持为独立开发者/小团队可以逐步完成的商业软件，而不是滑向“再造 Vector / EB”。

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
dotnet build Autarx.slnx
dotnet test

dotnet run --project src/Autarx.Cli -- find Mcu tests/Autarx.Tests/Fixtures/minimal.arxml
dotnet run --project src/Autarx.Gui
```

## 近期路线

近期优先级是 **先把 AUTOSAR 工程理解透，再做编辑**：

1. Multi-file Workspace + Reference Resolution
2. OEM Delivery Inspection + System/ECU Projection
3. Semantic Diff
4. ECU Impact Analysis
5. Communication Trace
6. Vendor Validation Adapter
7. Reviewable Edit / Patch
8. AI Plan → Review → Apply

详细路线见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## License

Proprietary — © TuringLambdaAI, all rights reserved. See [LICENSE](LICENSE).
