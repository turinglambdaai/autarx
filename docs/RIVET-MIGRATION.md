# Autarx × Rivet Migration (R0–R8)

> 分支 `experiment/autarx-rivet`。基线 v1.0.3（tag `1.0.3`，.NET 10 + Avalonia）。
> 本文档是重建的唯一计划文件；行为与 JSON 契约以 v1.0.3 为参照（同 Taskly
> main 模式：旧实现归档为契约快照，新实现逐步替换）。
>
> 决策出处：2026-10-01 终审（推翻同日「不重做、优先 M9」评估）。产品尚无
> 正式用户，可大胆重构；动机 = dogfood 反哺 Rivet 库质量。ARCHITECTURE.md
> 「Racket pre-1.0 不承载商业产品」的拒绝理由被有意推翻；M9（.NET 商业化
> 加固）冻结。

## 目标终态

| 层 | v1.0.3（.NET） | Rivet 版 |
|---|---|---|
| 领域核心 | Autarx.Core（C#） | `racket/autarx/`（Racket CS） |
| CLI | Autarx.Cli（System.Text.Json） | Racket 直连核心（不经 RVT1） |
| MCP | Mcp/McpServer（stdio JSON-RPC） | Racket 直连核心（不经 RVT1） |
| GUI | Autarx.Gui（Avalonia） | Rivet 平台宿主薄壳（macOS 首发SwiftUI，Windows/Linux 随后） |
| 分发 | self-contained single-file zip | `raco rivet build` staged 布局 + zip/installer |

**不变量（契约，逐字节兼容）：**

- CLI JSON：camelCase 键、字段声明顺序、缩进、string enum（`"communicationCluster"`）
- 退出码：0 成功 · 1 空结果/有 findings · 2 用法/文件/解析错误/歧义 · 3 崩溃
- 校验规则码 ARX001–ARX006、影响分析规则码 ARX-IMP-\*：append-only
- Identity = AUTOSAR 绝对 SHORT-NAME 路径；歧义是一等公民结果（exit 2 列候选）
- Unknown 元素优雅降级，永不导致解析失败
- patch 管线：plan（只读+强制语义 diff）→ apply（备份+manifest）→ undo
- MCP：无 raw-write 工具，全部 tools/call 审计到 `<workspace>/.autarx/audit.jsonl`
- FNV-1a 内容哈希：算法与遍历序逐字节复刻（保证哈希值跨实现一致，测试锁值）
- 排序：全部 Ordinal（Racket `string<?` 即 codepoint 序，天然一致）

**与 Taskly 的关键差异：** autarx 的主交付面是 CLI/MCP（无状态批处理进程），
不是常驻 GUI 后端。因此 CLI 与 MCP 直接 `require` Racket 领域核心；RVT1
RPC 面（`app/backend.rkt`）只为 GUI 壳存在，是 R6 新增面，不是迁移面。

## 给 Rivet 带来的新框架面（dogfood 产出）

Taskly 不需要、autarx 需要的能力，缺口一律 PR 到 `turinglambdaai/rivet`
（先在 `docs/RIVET-LIB-BACKLOG.md` 记一笔）：

1. **长操作进度/取消** — workspace 索引与 patch apply 是秒级长操作；RPC
   需要进度事件与取消语义（对照：rivet `changed` 事件是单向通知）
2. **懒加载树子节点** — GUI 的 workspace 树按需展开子包（ARXML 包层级深、
   单包可达数万子节点，一次性下发不可行）
3. **大报表分页** — diff/impact 的 findings 列表分页拉取（10M 行交付的
   diff 可达数千条）

## 停止规则（R0 出口条件）✅ 已裁决（2026-10-02）

合成 1.2M 行 / 65MB ECUC（`scripts/gen-bench-ecuc.rkt`，5 万容器×3 参数×1 引用，
85 万元素、5 万引用），同机 Release/预热三次取稳态：

| 实现 | 索引耗时 | 比率 |
|---|---|---|
| .NET 10（WorkspaceIndex.Build） | ~444 ms | 1× |
| Racket CS 9.3（build-workspace-index，首版） | ~2,420 ms | 5.4× |
| Racket CS 9.3（FNV halves 零分配 + 排序去分配） | **~1,110 ms** | **2.5×** |

**2.5× < 5× → 继续 R1–R8。** 关键优化：FNV-1a 哈希以双 32 位 fixnum 半部运算
（bignum 乘法+掩码占原耗时 60%）；引用排序比较器去除每次比较的列表分配。
哈希值与 .NET 逐对象一致（fixture 11/11 对拍相同），跨实现 diff 契约成立。

## 里程碑

### R0 — 解析地基 + 停止规则裁决
`racket/autarx/arxml.rkt`（手写流式解析器：LocalName 匹配、属性表、
text 拼接、行号追踪；DtdProcessing=Ignore 等价）、`arxml-writer.rkt`
（canonical 序列化：2 空格缩进、属性 Ordinal 排序、根 xmlns/xsi 重构）、
`ecuc.rkt`（模块/容器/参数/引用投影）、`release.rkt`（schemaLocation →
release 三正则）。**合成大文件基准测试 → 裁决。**

### R1 — 语义索引
`classify.rkt`（exact + 后缀规则，SemanticKind 11 值）、`index.rkt`
（WorkspaceIndexBuilder 的单文件/目录扫描、文档统计、包遍历、嵌套链、
duplicate path、file error 容错）。

### R2 — 引用图
\*-REF/\*-TREF 捕获（DEFINITION-REF 排除、绝对路径过滤）、resolve
（绝对路径 / 短名 / 歧义）、outgoing/incoming、trace BFS（方向×深度）、
unresolved、summary。

### R3 — diff + impact
`diff.rkt`（对象/引用/文件三级变更 + 属性级解释：SHORT-NAME →
DEFINITION-REF 叶段 → 元素名三档兄弟键 + 出现序后缀；延迟重读源文件）、
`impact.rkt`（联合图闭包、ARX-IMP-\* 规则、通信影响计数）。

### R4 — comm + validation
`comm.rkt`（CHANNEL-REF → ECU∪cluster、frame×PDU 映射对象、SIGNAL-REF
归属、孤岛、ECUC comm 模块名表）、`validate.rkt`（ARX001–006）。

### R5 — vendor + patch
`vendor.rkt`（davinci/tresos/isolar 探测：HOME 变量 → bin → PATH；
invocation 构造；进程执行与 error/warning 行启发式归一化）、
`patch.rkt`（四种 op 的 plan/rewrite/materialize/diff/apply/undo，
`.autarx/` 备份 + `patch-history.jsonl`）。

### R6 — CLI + MCP
`cli.rkt`（16 子命令、全局选项、JSON 契约逐字段、退出码）、
`mcp.rkt`（stdio JSON-RPC 2.0、工具注册表、审计日志）。**此二进制是
Racket 可执行，直连核心。**

### R7 — macOS 壳
`app/backend.rkt`（define-rpc：open_workspace / workspace_summary /
find_objects / get_object / refs / trace / ecus / comm / diff / impact /
validate / vendor_list / patch_plan / patch_apply / patch_undo +
`progress` 事件——Rivet 新框架面的第一个消费者）+ SwiftUI 宿主
（workspace 打开 → 摘要仪表、树浏览、trace/diff/impact 视图、patch 审阅）。

### R8 — 打包 + 收尾
`raco rivet build`（macOS app + CLI 产物）、`autarx update`（latest.json
feed + SHA-256 + swap）移植、GitHub Actions 矩阵（macOS 首发，
Windows/Linux 宿主排期）、删除 `src/` `tests/` .NET 树、README/ARCHITECTURE
改写、tag + release。

## 目录（目标）

```
autarx/
├── rivet.rktd              Rivet 应用清单（R7 起）
├── app/backend.rkt         RPC 面（R7；CLI/MCP 不经过它）
├── racket/
│   ├── autarx/             领域核心（R0–R5）+ json/cli/mcp（R6）
│   └── tests/              契约测试（对齐 .NET 测试套件逐条移植）
├── macos-host/             SwiftUI 壳（R7）
├── shared/fixtures/        合成 workspace fixtures（从 tests/Fixtures 上移共用）
└── scripts/bench-ecuc.rkt  R0 停止规则基准
```

## 风险登记

| 风险 | 缓解 |
|---|---|
| Racket XML 流式解析性能不达标 | R0 停止规则提前裁决；侧写热点（哈希、字符串拼接）；优化手段：bytes 级解析、避免逐字符 port 读 |
| FNV-1a 哈希复刻出错 → diff 误报 | 哈希单测锁死固定输入→固定值（与 .NET 输出对拍） |
| JSON 字段顺序漂移破坏 agent 契约 | 用 `jsexpr` 有序写出（手写 serializer，不依赖 hash 序） |
| Windows 上 vendor .bat 经 cmd.exe 的差异 | R8 前仅 macOS 交付，vendor 命令在 Windows 宿主排期内补 |
