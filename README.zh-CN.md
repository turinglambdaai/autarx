# Autarx

**跨平台 AUTOSAR ECU 配置工具。**
直接打开 ARXML——浏览 ECUC 模块配置、校验结构，并通过带 JSON 输出的 agent 友好 CLI 完成一切脚本化操作。不依赖厂商配置器。

[![CI](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml/badge.svg)](https://github.com/turinglambdaai/autarx/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-12-9B4FBE?logo=avaloniaui&logoColor=white)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Proprietary-red)](LICENSE)

中文 · [English](README.md)

---

## 为什么做 Autarx？

- **厂商配置器笨重且绑定平台**——EB tresos、达芬奇 Configurator、ISOLAR 把配置数据锁在 Windows GUI 和私有工程格式后面
- **ARXML 对 grep 不友好**——工程师在百万行 XML 树里手工翻找；不启动完整工具链，没人能回答"这里配置了哪些模块"
- **没有自动化接口**——CI 无法 diff 或校验 ECUC 配置，因为没有厂商工具提供稳定的机器接口
- **Agent 帮不上忙**——LLM agent 依赖文本界面和结构化反馈，厂商工具两者皆无

Autarx 解决这些问题：一个无 UI 的核心直接读取 ARXML，给人一个原生 Avalonia GUI，给脚本、CI 和 AI agent 一个 JSON 优先的 CLI。

## 功能（v0）

- **ARXML 引擎**——单遍流式解析器，容忍厂商命名空间前缀；为真实项目动辄千万行的 ECUC 文件而设计
- **ECUC 投影**——模块 → 容器 → 参数/引用模型，带汇总计数
- **结构校验**——ARX001–ARX006 规则（缺失引用、兄弟重名、孤儿参数），以 SHORT-NAME 路径报告
- **Agent 友好 CLI**——`info` / `modules` / `validate`，`--json` 输出，退出码确定
- **Avalonia 工作区**——Windows/macOS/Linux 原生 GUI：模块树、详情面板、状态栏
- **自包含构建**——每平台单文件可执行，无需安装 .NET

## 命令行

```bash
# 文件摘要
autarx info Mcu_Can.arxml
autarx info Mcu_Can.arxml --json

# 列出 ECUC 模块配置
autarx modules Mcu_Can.arxml --json

# 结构校验——发现错误时退出码为 1
autarx validate Mcu_Can.arxml
```

`autarx validate --json` 输出：

```json
{
  "errors": 0,
  "warnings": 0,
  "diagnostics": []
}
```

## 快速开始

### 从源码构建

依赖：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
git clone https://github.com/turinglambdaai/autarx.git
cd autarx
dotnet build Autarx.slnx
dotnet run --project src/Autarx.Gui
dotnet run --project src/Autarx.Cli -- info tests/Autarx.Tests/Fixtures/minimal.arxml
```

### 测试

```bash
dotnet test
```

测试覆盖解析器（命名空间剥离、属性、错误行号）、ECUC 读取器（容器、参数、引用、汇总）和校验规则——全部基于仓库内置的 fixture ARXML。

## 项目结构

```text
autarx/
├── src/
│   ├── Autarx.Core/            # ARXML 解析、ECUC 模型、校验（无 UI 依赖）
│   │   ├── Models/             # ArxmlElement 树、EcucModule/Container/Parameter/Reference
│   │   ├── Parsing/            # ArxmlParser（流式 XmlReader）、EcucReader（投影）
│   │   └── Validation/         # ValidationEngine、ARX00NN 诊断
│   ├── Autarx.Cli/             # agent 友好 CLI——info / modules / validate，--json
│   └── Autarx.Gui/             # Avalonia 工作区——模块树、详情、状态栏
├── tests/Autarx.Tests/         # 解析器 / ECUC / 校验测试套件 + fixture ARXML
└── docs/ROADMAP.md
```

## 设计说明

- 核心库无 UI 依赖；两个前端都是它的薄壳，CLI 的 JSON 输出是脚本和 AI agent 的稳定契约——只做增量变更
- ARXML 匹配基于 LocalName：厂商命名空间前缀不影响解析结果，正如真实世界的配置器彼此对待文件的方式
- 校验报告 SHORT-NAME 路径（`Mcu/McuGeneralConfiguration/…`）——AUTOSAR 工程师指认配置位置的真实方式
- 规则码 ARX001–ARX006 已在 [ARCHITECTURE.md](ARCHITECTURE.md) 记录且永不重编号——脚本和 agent 依赖它们

## 路线图

见 [docs/ROADMAP.md](docs/ROADMAP.md)：ARXML 读写回环、分模块分片加载、DBC 导入、厂商配置器集成、带 plan/apply/rollback 的 agent 层。

## 许可证

专有软件——© TuringLambdaAI，保留所有权利。见 [LICENSE](LICENSE)。
