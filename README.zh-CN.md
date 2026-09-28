# Autarx

**面向 OEM-Tier1 集成数据的现代 AUTOSAR 工程工作台：检查、理解、比较、追溯、自动化。**
把 OEM 交付包或 ECU Extract 目录交给它，得到一个语义化工作区：对象清单、引用图、ECU 发现、未解析引用检查——全程可通过 agent 友好的 JSON CLI 脚本化。Autarx 位于厂商生成器**上层**；它不替代达芬奇、tresos 或 ISOLAR。

[![CI](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml/badge.svg)](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-12-9B4FBE?logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE)

中文 · [English](README.md)

---

## 为什么做 Autarx？

- **交付包是黑盒**——一个 OEM 交付是几十个 ARXML 文件：系统描述、ECU Extract、ECUC 捆绑混在一起；不打开完整厂商工具链，没人能回答"这里面有什么、它们拼不拼得起来"
- **ARXML 对 grep 不友好**——工程师在百万行 XML 树里手工翻找；把一个信号从 ECU 追回 Cluster，意味着跨文件人工解析几十个 REF 元素
- **没有自动化接口**——CI 无法 diff、追溯或体检交付包，因为没有厂商工具提供稳定的机器接口
- **Agent 帮不上忙**——LLM agent 依赖文本界面和结构化反馈，厂商工具两者皆无

Autarx 解决这些问题：一个无 UI 的语义引擎直接读取 ARXML，给人一个原生 Avalonia GUI，给脚本、CI 和 AI agent 一个 JSON 优先的 CLI。

## Autarx 是什么、不是什么

Autarx **可以**：读取 ARXML 工作区、以稳定绝对路径索引 identifiable、构建正/反向引用图、搜索、追溯、检查、输出确定性 JSON。

Autarx **不会**：替代达芬奇/tresos/ISOLAR、生成量产 BSW/RTE/MCAL 代码、宣称自己的校验能替代厂商校验。`validate` 只代表结构检查。产品链路是：**Autarx 分析 → 厂商工具 → 厂商校验 → 厂商生成器**。见 [docs/PRODUCT.md](docs/PRODUCT.md)。

## 功能

- **工作区语义索引**——每个包级 identifiable 获得稳定绝对路径（`/Vehicle/Signals/VehicleSpeed`）；身份绝不依赖 XML 行号
- **语义分类**——System、ECU、Cluster、Frame、PDU、Signal、SW 组件、Port、Interface、ECUC 模块；未知元素类型如实报告为 unknown，绝不导致解析失败
- **引用引擎**——基于 `*-REF`/`*-TREF` 的正反向图、跨文件解析、嵌套元素归因（`/Pdu_VehicleSpeed/VehicleSpeedMapping`）、重复路径检测、未解析引用发现
- **OEM 交付检查**——`inspect` 一次输出文件、大小、元素数、包数、AUTOSAR 命名空间/schema/release 和完整语义清单
- **ECU 发现**——`ecus` 列出 ECU 实例；`ecu <name>` 展示直接关系并显式标注 `direct` 关系置信度（不猜测语义）
- **语义 Diff**——`diff` 基于路径身份和内容哈希比较两版交付；属性级变化以 DEFINITION-REF 定位 ECUC 参数，"McuFrequency: 80000000 → 160000000" 是一等公民发现，而非行号噪声
- **ECU 影响分析**——`impact --ecu RadarFL` 通过两版交付引用图的并集闭包，把每个变化归类为"与本 ECU 相关/无关"；确定性 ARX-IMP-* 规则把"被引用对象被删""类型被改"判为 breaking
- **通信投影**——`comm` 从引用图推导 Cluster → Frame → PDU → Signal 链路，并报告未挂接的孤儿 Frame/PDU/Signal；只陈述结构，不虚构收发方向
- **厂商交接**——`vendor list` 探测达芬奇/tresos/ISOLAR 安装；`vendor validate` 中继厂商工具自身的运行结果（归一化诊断 + 保留原始输出）——这永远是厂商的结论，不是 Autarx 的
- **受控编辑**——`patch plan` 把每次修改先转成语义 diff 供审阅，未审阅不落盘；`patch apply` 保留备份和撤销清单；重命名会同步重写全工作区的入向引用
- **AI Agent 接口**——`autarx mcp` 启动 Model Context Protocol stdio 服务器暴露完整工具 API；不存在裸写文件工具，agent 无法绕过 plan/diff/审阅边界，且每次调用都进审计日志
- **在线自更新**——`autarx update` 检查随每个版本发布的 `latest.json` 更新清单，校验 SHA-256 后就地替换二进制；GUI 启动时静默检查，Help 菜单一键安装。未处理崩溃会在 `<TEMP>/autarx/crashes/` 留本地日志——不上传任何数据
- **Agent 友好 CLI**——camelCase JSON、确定性退出码、歧义显式上报而非猜测；现成的 CI 门禁命令链
- **Avalonia 工作区**——Windows/macOS/Linux 原生 GUI
- **自包含构建**——每平台单文件可执行，无需安装 .NET

## 命令行

```bash
# 检查整个 OEM 交付目录
autarx inspect ./OEM_Delivery/
autarx inspect ./OEM_Delivery/ --json

# 按 SHORT-NAME 子串搜索对象
autarx find VehicleSpeed ./OEM_Delivery/

# 谁引用谁
autarx refs VehicleSpeed ./OEM_Delivery/
autarx refs VehicleSpeed ./OEM_Delivery/ --incoming
autarx refs /Vehicle/Clusters/VehicleCan ./OEM_Delivery/ --outgoing --json

# 沿引用图追溯
autarx trace Pdu_VehicleSpeed ./OEM_Delivery/ --depth 3

# ECU 发现
autarx ecus ./OEM_Delivery/
autarx ecu ./OEM_Delivery/ Gateway --json

# 交付质量种子——存在发现时退出码为 1
autarx unresolved ./OEM_Delivery/

# 两版交付的语义 diff——有差异时退出码为 1
autarx diff ./V32/ ./V33/ --detail

# 我的 ECU 受了什么影响——有相关变化时退出码为 1
autarx impact ./V32/ ./V33/ --ecu RadarFL

# 通信链路与孤儿对象
autarx comm ./OEM_Delivery/ --cluster VehicleCan

# 厂商工具交接（探测 + 结果中继）
autarx vendor list
autarx vendor validate davinci ./Project.dvcfg

# 受控编辑：plan 只读预览，apply 保留备份，undo 撤销
autarx patch plan ./OEM_Delivery/ --file ops.json
autarx patch apply ./OEM_Delivery/ --file ops.json
autarx patch undo ./OEM_Delivery/

# AI agent 的 MCP stdio 服务器（JSON-RPC 2.0，带审计）
autarx mcp

# 自更新：检查并就地应用新版本
autarx update --check        # 只报告（有新版本时退出码 1）
autarx update                # 下载、校验 SHA-256、就地替换

# 单文件 ECUC 命令（仅结构校验——不是厂商校验）
autarx info Mcu.arxml
autarx modules Mcu.arxml --json
autarx validate Mcu.arxml
```

`autarx unresolved --json` 输出：

```json
[
  {
    "kind": "REQUIRED-INTERFACE-TREF",
    "sourcePath": "/Vehicle/SwCs/SpeedSensor",
    "sourceElementPath": "/Vehicle/SwCs/SpeedSensor/CalibrationIn",
    "targetPath": "/Vehicle/Interfaces/IF_Calibration",
    "sourceFile": "communication.arxml",
    "resolved": false
  }
]
```

## 安装

从 [Releases](https://github.com/turinglambdaai/autarx/releases/latest) 下载最新 zip——每个平台提供 GUI 包（`Autarx-gui-<platform>.zip`）与 CLI 包（`Autarx-cli-<platform>.zip`），自包含单文件可执行，无需安装 .NET：

| 平台 | 资产 |
|---|---|
| Windows x64 | `Autarx-{gui,cli}-windows-x64.zip` |
| macOS（Apple 芯片） | `Autarx-{gui,cli}-macos-arm64.zip` |
| Linux x64 | `Autarx-{gui,cli}-linux-x64.zip` |

每个 Release 同时附带 `SHA256SUMS` 校验清单和 Sigstore 构建溯源证明，运行前建议先校验：

```bash
sha256sum --ignore-missing --check SHA256SUMS
gh attestation verify Autarx-cli-linux-x64.zip -R turinglambdaai/autarx
```

macOS 构建未签名；首次启动前用 `xattr -cr Autarx`（GUI）或 `xattr -cr autarx`（CLI）移除 Gatekeeper 隔离标记。

## 快速开始

### 从源码构建

依赖：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
dotnet build Autarx.slnx
dotnet run --project src/Autarx.Gui
dotnet run --project src/Autarx.Cli -- inspect tests/Autarx.Tests/Fixtures/OemDelivery
```

### 测试

```bash
dotnet test
```

测试覆盖解析器、语义分类、工作区索引、引用图、汇总追溯、语义 diff、影响分析、通信投影、厂商适配器归一化、补丁 plan/apply/undo 往返和 MCP 协议——基于合成工作区与 DiffBefore/DiffAfter 交付对 fixture，包含刻意构造的未解析引用和歧义/重复路径。

## 项目结构

```text
autarx/
├── src/
│   ├── Autarx.Core/            # 语义引擎（无 UI 依赖）
│   │   ├── Models/             # SemanticObject、SemanticKind、引用、文档
│   │   ├── Parsing/            # ArxmlParser（流式）、EcucReader、release 解析
│   │   ├── Index/              # WorkspaceIndex 构建、引用图、trace、summary
│   │   ├── Diff/               # 语义 diff（路径身份 + 内容哈希）
│   │   ├── Impact/             # ECU 相关性闭包、ARX-IMP-* 规则
│   │   ├── Communication/      # Cluster→Frame→PDU→Signal 投影
│   │   ├── Vendor/             # 适配器探测 + 厂商交接中继
│   │   ├── Patch/              # plan/apply/undo、规范化 ARXML 写出
│   │   ├── Mcp/                # MCP stdio 服务器 + 工具注册表
│   │   └── Validation/         # ValidationEngine、ARX00NN 诊断
│   ├── Autarx.Cli/             # 工作区 + 单文件命令，--json
│   └── Autarx.Gui/             # Avalonia 工作台——工作区浏览器、trace、comm、diff
├── tests/Autarx.Tests/         # 单元测试 + 合成 fixture（ECUC 与非 ECUC）
├── docs/PRODUCT.md             # 定位、边界、非目标
└── docs/ROADMAP.md             # M0–M9
```

## 设计说明

- **Core 是语义引擎，不是 ECUC 解析器**——ECUC 只是投影之一；顶层模型（Workspace、SemanticObject、引用图）必须同样服务于系统描述和 ECU Extract
- **身份 = AUTOSAR 绝对路径**——跨改写稳定、为 diff 铺路；XML 行号绝不作为身份
- **Unknown 优雅降级**——未映射的元素类型照样索引、可引用、如实报告为 unknown，绝不是解析失败
- **歧义显式上报，绝不猜测**——重复 SHORT-NAME 返回明确的歧义结果并列出所有候选路径
- **CLI JSON 是机器契约**——camelCase 键、字符串枚举、确定性退出码、只做增量变更；脚本和 AI agent 依赖它

## 路线图

M0–M8 已全部落地：语义引擎、语义 diff、ECU 影响分析、通信追溯、厂商交接、受控编辑、AI agent 接口（MCP）。剩余为 M9 商业化加固（安装器、授权、遥测、性能画像）——见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 许可证

专有软件——© TuringLambdaAI，保留所有权利。见 [LICENSE](LICENSE)。
