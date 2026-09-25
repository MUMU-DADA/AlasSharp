# 架构梳理与本轮讨论结论

> 本文档记录一次围绕**依赖边界**的完整梳理与决策：当前实现的入口与引用关系、上游 Python 的位置与角色、
> 静态规则与执行逻辑的分工，以及"先按现路线开发 → 完成后再做能力替换 → 最终只依赖上游静态规则"的路线。
>
> 所有结论都可由仓库内文件复核，证据以 `路径:行号` 或实测命令结果给出。
> 与既有规范的冲突点已在第 6、7 节标明（含"目标形态属于 R5 宿主替换评估"这一前提）。

## 0. 一句话结论

**C# 侧的产品能力集中在 `Alas.Core`；入口只有"服务端（兼 CLI）"与"UI（两个平台宿主）"两类；
但游戏行为本身仍由上游 ALAS 的 Python 逻辑执行。** 当前依赖 = 上游静态规则（已导出为 JSON，符合目标）
＋ 上游执行逻辑（通过进程内 CPython 宿主调用，**正是最终要替换掉的那一半**）。

## 1. 当前架构：项目与入口

### 1.1 项目引用图（实测，来源各 `*.csproj`）

```
Alas.Contracts      Library   （零依赖）
Alas.Core           Library   （零项目引用；包 SixLabors.ImageSharp）
Alas.Client         Library   → Alas.Contracts
Alas.UI             Library   → Alas.Contracts；包 Avalonia、Avalonia.Themes.Fluent
Alas.Server         Exe       → Alas.Core、Alas.Contracts；框架 Microsoft.AspNetCore.App
Alas.UI.Desktop     WinExe    → Alas.UI、Alas.Contracts、Alas.Core；包 Avalonia.Desktop
Alas.UI.Browser     exe       → Alas.UI、Alas.Client；包 Avalonia.Browser（TFM net10.0-browser）
Alas.UI.Headless    Exe       → Alas.UI；包 Avalonia.Headless、Avalonia.Skia（验收宿主）
```
- `Alas.Server` 与 `Alas.UI.*` 之间**零项目引用**：唯一的耦合是 `Alas.Contracts` 定义的线格式 + 运行期 HTTP。
- `src/Alas.DataTool/` 只剩 `bin/`、`obj/` 残留（项目已移出解决方案）。

### 1.2 两类可执行入口

| 入口 | 形态 | 职责 |
| --- | --- | --- |
| `Alas.Server.exe` | `Exe`（net10.0） | **双模式**：带命令字 → CLI/诊断；无命令或以 `-` 开头 → Kestrel 服务端 |
| `Alas.UI.Desktop.exe` | `WinExe` | 桌面原生窗口（进程内直连 Core） |
| `Alas.UI.Browser` | `exe`（net10.0-browser） | 浏览器端应用：编译为 WASM 模块 + 静态站点包，**没有 native exe**，宿主是浏览器 |

CLI/服务端判据：`src/Alas.Server/Program.cs:13-14`（首个参数不以 `-` 开头即当命令字）。
CLI 实际识别 **19** 个命令：`verify / list / show / imaging / matching / vision / campaign / map /
map-ir / capture / device / queue / plan-queue / report / runs / run / goto / contract / selftest-runtime`。
（"未知命令"提示只列了 13 个，漏 `goto / plan-queue / report / run / runs / selftest-runtime` —— 可修的小问题。）

架构检查 `tools/diagnostics/verify_architecture.py:389-392` 把可执行入口限定为
`Alas.Server`、`Alas.UI.Desktop`、`Alas.UI.Headless`；`Alas.UI.Browser` 的求值 `OutputType` 是小写 `exe`，
恰好不被该大小写敏感比较命中（覆盖缺口，未见拦截）。

### 1.3 `Alas.Contracts` 与 `Alas.Client` 的定位

- `Alas.Contracts`（`ControlModels.cs`，216 行 / 30 个公开类型 / 47 个 `required` / 28 条 `[JsonSerializable]`）：
  **控制 API 的接口标准**——请求响应信封、协议常量（`MaxRequestBodyBytes = 1 MiB`、
  `TokenHeader = "X-Alas-Token"`、`EventsContract = "control-state/1"`）、游标校验
  `IsEventCursor()`，以及源生成无反射 JSON 上下文。它是两侧唯一的线格式来源。
- `Alas.Client`（`ControlClient` + `.Deploy` + `.Events`）：合同的**客户端实现**——端点校验、禁重定向、
  30 秒期限（含响应体）、从 `/api/state` 取令牌并自动附加、错误信封转 `ControlApiException` /
  `ControlProtocolException`、SSE 快照流（游标 / 重连基准 / 空闲期限）。消费者：仅 `Alas.UI.Browser`
  与验收脚本 `tools/diagnostics/verify_control_client.py`（后者在桌面 .NET 上引用它做真实 HTTP 对拍）。

## 2. 界面侧：为什么还需要 `Alas.UI.Browser`

### 2.1 界面逻辑只有一份

| 项目 | `.axaml` | `.cs` | 内容 |
| --- | --- | --- | --- |
| `Alas.UI` | **22** | **63** | 页面、ViewModel、主题、交互（`App.axaml` 也在此） |
| `Alas.UI.Browser` | **0** | 4 | 入口 + 平台适配 |
| `Alas.UI.Desktop` | **0** | 5 | 入口 + 平台适配 |

`Alas.UI` 是**库**：没有 `Main`，只引用平台无关的 `Avalonia` + `Avalonia.Themes.Fluent`，
并通过三个静态工厂把平台相关实现交给宿主注入（`App.axaml.cs:17/23/24` → `:32-34`）：
`ThemeStoreFactory`、`ResourceStoreFactory`、`BackendFactory`。
两个宿主是对称的：桌面 = `Avalonia.Desktop` + `DirectCoreBackend`（进程内 Core）+
文件偏好；浏览器 = `Avalonia.Browser` + `BrowserControlBackend`（HTTP）+ localStorage。

### 2.2 远程 GUI 的三件套（Server 不渲染界面）

```
构建期  Alas.UI（样式/布局/VM）→ Alas.UI.Browser 编译 → wwwroot/（index.html + _framework/*.wasm）
        实测：Alas.UI.<hash>.wasm ≈ 15.7–16.8 MB（界面本体）；Alas.UI.Browser.<hash>.wasm ≈ 17 KB（薄宿主）
发布期  tools/publish_server.ps1 -IncludeUi 把 wwwroot 复制为 <server-publish>/ui/
运行期  浏览器执行 WASM 渲染界面；页面用 Alas.Client 回调 /api/*
```
服务端发布体量对照：**本体 2.22 MB**（exe+dll），**`ui/` 67.8 MB**；未给 `--ui-root` 时为纯 API 模式
（旧的内置单文件控制页已删除）。启动时若程序目录存在 `ui/index.html` 则自动同源托管
（`src/Alas.Server/Program.cs:46-51`），托管规则见 `src/Alas.Server/StaticUiFiles.cs`
（要求 index.html、拒绝链接/重解析点、按白名单扩展名、按内联脚本哈希下发 CSP、无目录浏览与 SPA 回退）。

`Alas.Server` 被架构检查强制**不得引用 UI/Avalonia**（`verify_architecture.py:518-520`），
这与文档口径一致：「`Server` 独立发布，只运行 Kestrel、业务运行时和设备依赖，并托管预构建 WASM 静态文件；
不启动 Avalonia 桌面、浏览器、显示服务或 Node.js」。

### 2.3 `Alas.UI.Browser` 不是"库"，也不是 WebView 套壳

- 它是**可执行应用**（MSBuild 求值 `OutputType=exe`，TFM `net10.0-browser`），只是可执行形态是
  "WASM 模块 + 静态站点包"；`net10.0` 项目**无法引用它**（实测 `error NU1201`）。
- 被禁止的方向是反向的（桌面用 WebView/Electron 承载网页）；本项目桌面是原生 Avalonia，
  浏览器端是同一份 C#/AXAML 编译到浏览器目标。宿主里 **0 个 `.axaml`**，没有第二份界面。

### 2.4 到 Core 的链路：两条主链路 + 一条 CLI

| 链路 | 路径 | 说明 |
| --- | --- | --- |
| 桌面（同进程） | `Alas.UI` → `DirectCoreBackend` → `new ControlWorkspace(...)`（`DirectCoreBackend.cs:36`） | 无 HTTP；`DirectCoreBackend.cs` 内不得出现 `ControlClient/HttpClient/http(s)://`/`/api/`（`:527-529`） |
| 浏览器 / API | `Alas.UI.Browser` → `BrowserControlBackend` → `ControlClient` → HTTP → `ControlServer` → `new ControlWorkspace(...)`（`ControlServer.cs:34`） | 两条链路在 `ControlWorkspace` **汇合，调用的方法完全相同**（State/Report/Instances/ReadHostJson/StartTask/StartScheduler/RequestStop/SaveQueueRequest/StartRun/BeginShutdown） |
| CLI | `Alas.Server.exe <命令>` → `Alas.Core.Diagnostics.DiagnosticCommands` → `Alas.Runtime`（如 `QueueExecution.RunFile`） | 进程内直跑，不经 HTTP、不经 UI |

隔离模式：`SimulatedUiBackend`（内存样本）刻意不接 Core，验收断言运行时不加载 `Alas.Core`/`Alas.Client` 程序集。

## 3. 上游 Python：位置、桥接与边界

### 3.1 位置

`src/Alas.Core/Diagnostics/ProjectPaths.cs:26-29` 是唯一的目录推导入口：

```
root = 从程序集目录向上找同时含 tools/ 与 src/ 的目录
data = ALAS_DATA ?? <root>/data
repo = ALAS_REPO ?? <root>/.runtime/engine      ← 上游 ALAS 检出
```
本机实测 `.runtime/engine`：真目录含 `module/`（385 个 py）、`campaign/`（134 章节 / 1437 个 py）、
`alas.py`、`deploy/ dev_tools/ doc/ item_template/ submodule/ tests/ webapp/ data/ log/ runs/`；
`.venv`、`assets`、`bin`、`config` 是 Junction（分别指向项目内 Python 环境、`vendor/upstream/assets`、
仓库外的上游克隆 `bin`、`.runtime/config`）。这些链接目标属本机布局，不入库。

### 3.2 桥接

| 角色 | 位置 |
| --- | --- |
| 进程内 CPython 宿主 | `src/Alas.Core/Vision/PythonHost.cs`（P/Invoke `python3xx.dll`；注释原文"路线甲的核心约定：**识图不重写**"） |
| 入口函数 | 默认 `"handle_line"`（`src/Alas.Core/Vision/PythonHost.cs:22` 的 `EntryFunction` 默认值） |
| 入口实现 | **本项目仓库**的 `tools/alas_vision.py:3793`（全文 3808 行 / 104 个函数；77 处 `import module.*`） |
| 接口边界 | `IVisionEngine`（实现：`InProcessVisionEngine`、`VisionWorker`；自检 `StubVisionEngine`） |

### 3.3 `tools/` 不是从上游搬来的

三条独立证据：
1. **内容级**：`tools/` 下 172 个 `.py` 与上游 1802 个 `.py` 逐字节比对 → **相同 0 个**；
2. **路径级**：上游检出**没有 `tools/` 目录**（172 个文件在上游无对应路径）；
3. **来源级**：`tools/alas_vision.py` 由本项目提交 `52a05c5 feat(vision): 进程内 CPython 宿主接通并验收…`
   （2026-09-22）引入，至今 125 次提交。

它们与上游的关系是**调用/解析**：`tools/*.py` 中 `import module.*` 共 284 处；
`upstream_config_export.py` / `upstream_map_export.py` 明确 "without importing game code"（只做静态解析）；
`native_scheduler.py` 自述 *"Adapter for the unmodified upstream scheduler; no task selection logic here"*。
`tools/` 构成：顶层 30 个脚本（桥接 / 静态导出 / 同步 / 夹具 / 合同 / 报告）+ `diagnostics/` 114 个（验收与证据审计）。
`cv2`/`numpy` 在 tools 内出现 76 处，集中在夹具与验收（验证算法），不是产品识别路径。

## 4. 上游自身的结构：声明式规则 + 执行逻辑

### 4.1 规则侧（`campaign/`，134 章 / 1437 py）

关卡文件是**声明**：

```python
from module.campaign.campaign_base import CampaignBase     # 绑定逻辑类
from module.map.map_base import CampaignMap
MAP = CampaignMap()
MAP.shape = 'G1'
MAP.camera_data = ['D1']
MAP.map_data = """
    SP -- -- -- -- ME MB
"""
MAP.spawn_data = [{'battle': 0, 'enemy': 1}, {'battle': 1, 'boss': 1}]
class Config:
    FLEET_2 = 0
    INTERNAL_LINES_FIND_PEAKS_PARAMETERS = { 'height': (120, 206), ... }
```

### 4.2 逻辑侧（`module/`，385 py）

```
device 33  os 28  webui 17  config 15  statistics 14  shop 13  base 12
island_handler 12  map_detection 11  research 10  os_handler 10  island 10
handler 10  combat 9  map 9  campaign 9  …
```

### 4.3 关键点

上游的"规则"**不是纯数据**，而是绑定在逻辑类上的 Python 声明（`MAP = CampaignMap()`、`class Config:`、
`class Xxx(CampaignBase)`）。要把它变成真正的静态形式，必须靠静态导出器（AST 解析、不导入游戏代码）——
**这一步本项目已经做了**：`data/assets.json`、`campaign_index.json`、`campaign/`、`schema/`、`manifest.json`，
C# 侧由 `src/Alas.Core/UpstreamData.cs` 的 `Catalog.Open(dir)` 与 `UpstreamModels.cs` 消费（**32 个消费点**）。

## 5. 目标形态与现状的差距

### 5.1 已经符合目标的部分（纯静态依赖）

上游静态规则与配置 → 导出 JSON → C# 只读消费（32 个消费点）；另有 `vendor/upstream/assets`
（素材逐字节镜像）与 `.runtime/config`（ALAS 账号/实例配置，当数据读）。

### 5.2 不符合目标的部分（运行时执行上游逻辑）

链路：`PythonHost` → `tools/alas_vision.py:handle_line` → 上游 `module.*`。`IVisionEngine` 的 **24 个方法**
就是"执行上游"的清单：

| 能力 | 接口方法 |
| --- | --- |
| 识别 | `AppearOn` / `AppearOnBatch` / `ButtonMatch` / `TemplateMatch` / `AssetButtonCenter` / `Ocr` |
| 页面 | `PageList` / `PageCurrent` / `PageGraph` / `PageAppear` |
| 账号与任务目录 | `AccountState` / `TaskCatalog` |
| 统计与策略 | `StatisticsReport` / `RefreshStatisticsLoot` / `MeowfficerReport` / `ClearMeowfficerReport` / `ValidateShopStrategy` |
| 设备与帧 | `ConfigureDevice` / `CaptureViaEngine` / `LoadScreenshot` / `SetScreenshot` / `ScaleScreenshot` |

Core 内调用点 **33 处**，分布：`Device/DeviceController`、`Tasks/ObserveTask`、`Runtime/AlasSession`、
`Runtime/CampaignBatchRunner`、`Tasks/OsStateTask`、`TaskCatalogTask`、`AccountStateTask`、
`Diagnostics/DeviceCheck`、`CaptureCheck` 等。另有最重的一条：**生产战役直接调原生
`CampaignRun.load_campaign()` + `Campaign.run()`**（`Campaign/BattlePlanRunner` + `EngineCall`、
`tools/s3_campaign_execution.py`）。

### 5.3 量化对照

| 维度 | 数值 |
| --- | --- |
| 静态规则消费点（符合目标） | 32 |
| 上游逻辑调用点（待替换） | 33 |
| 需替换的接口面 | `IVisionEngine` 24 个方法 |
| 对应上游逻辑规模 | `module/` 385 个 py（16 个子系统） |

### 5.4 与既有规范的关系

当前"继续走上游"是**写进规范的口径**，不是疏漏：
`AGENTS.md`「生产战役继续走上游 `CampaignRun.load_campaign()`、章节 `Config` 合并和原生 `Campaign.run()`」、
「视觉、页面和地图识别继续通过上游对象和 `IVisionEngine`」、「JSON 只用于离线展示、溯源和漂移校验」；
`docs/architecture-roadmap.md`「C# 图像实现只用于参考和对拍；未满足 R5 门槛不替换上游宿主」。
因此"最终只依赖上游静态规则"属于 **R5 宿主替换评估**的目标形态，需要独立门槛与证据。

## 6. 本轮确认的路线

**路线：现在按现有主线继续开发（R2 游戏自动化规则全量适配 + 业务验证等）→ 开发完成后再做能力适配替换
→ 最终实现只依赖上游的静态规则。**

### 6.1 必须守住的三个接缝（否则"以后替换"会变成重写）

| 接缝 | 现状 | 纪律 |
| --- | --- | --- |
| `IVisionEngine`（唯一上游边界） | 24 方法 / 33 调用点 | 新功能不要轻易新增接口方法；能静态化的不问 Python |
| 静态导出覆盖面 | 已导出 assets/campaign_index/campaign/schema/manifest | 凡能用规则表达的就**现在就导出**（页面图、任务目录、阈值参数、地图 IR…），它们是将来自研引擎的输入 |
| 夹具与基线 | 已有 `RecordingEngine`、`verify_map_ir.py`、`make_*_fixture.py`、`verify_result_contract.py` | 每次新增上游交互顺手录 fixture（脱敏）+ 记性能基线，否则将来无法证明"替换后等价" |

### 6.2 增量纪律

1. 只读 / 展示 / 校验类能力**优先静态消费**，不调 Python；
2. 新增上游调用必须留证据（fixture + 时延），落在 `tools/diagnostics/` 或 `data/fixtures/`；
3. 维护**能力归属矩阵**：每个能力标注【借引擎 / 已自研 / 纯静态】，随功能更新；
4. **按域替换**，不要"最后一把整体替换"——某域夹具齐 + 基线有 + 业务验收过后即可单独切换。

### 6.3 记录在案的风险

- **"开发完成"的判据要先定义**（功能面覆盖 vs 每域真实路径证据），否则无法判断"完成后替换"的起点；
- **有些规则只存在于运行时**（阈值/分支由代码算出而非声明）：这类要么补静态导出，要么注定自研逻辑，
  所以"最终只依赖静态规则"的**可达性需要先量化**；
- **别让上游语义成为本项目的隐含 spec**：`IVisionEngine` 的返回类型要当作"我们的接口"维护，
  而不是上游对象的镜像，否则替换会退化为重写功能层。

## 7. 待办与下一步产出

1. **能力归属矩阵**：逐 `IVisionEngine` 方法 + Core 33 个调用点，标注【借引擎 / 可静态化 / 必须自研】；
2. **静态化可达性评估**：按域给出"纯静态可达 / 需先补导出（列字段）/ 必须自研引擎"三档结论，
   并附依赖的上游模块与文件数；
3. 建议把这两份产出登记到 `docs/architecture-roadmap.md` 的 R5 章节，作为"先开发、后替换"的验收锚点。

**目标与流程已单独成文**：[上游引擎重写：目标与流程](UPSTREAM-ENGINE-REWRITE.md)
（含范围界定、"识图引擎不要求 C# 重写"这一例外、P0→P4 推进流程与验收判据）。

## 8. 本轮修正过的错误结论（留档）

| 我先前的说法 | 实测结果 |
| --- | --- |
| "没有任何脚本调用 `alashub control`" | **不准确**：`tools/diagnostics/verify_control.py:20,69` 与 `verify_control_client.py:21` 都用它起服务端；该结论后来随 `f1cad79` 的入口统一而自然消失 |
| "浏览器目标编译不过 AspNetCore（Kestrel）" | **错误**：`net10.0-browser` + `FrameworkReference Microsoft.AspNetCore.App` 可正常 restore/build；真正的阻断是 TFM 引用方向（`NU1201`）与运行时能力 |
| "`build.ps1` 只发布入口项目" | 补正：`-IncludeUi` 由 `tools/publish_server.ps1` 负责，`build.ps1` 的 `-Publish` 只发入口项目 |

## 9. 证据索引（按主题）

| 主题 | 位置 |
| --- | --- |
| 项目引用与类型 | 各 `src/*/*.csproj`；`dotnet list <proj> reference` |
| 入口双模式 | `src/Alas.Server/Program.cs:13-14`；命令分支 `src/Alas.Core/Diagnostics/Program.cs` |
| UI 宿主注入接缝 | `src/Alas.UI/App.axaml.cs:17/23/24/32-34`；`src/Alas.UI.Desktop/Program.cs`、`src/Alas.UI.Browser/Program.cs` |
| 静态托管与自动识别 ui/ | `src/Alas.Server/Program.cs:46-51`；`src/Alas.Server/StaticUiFiles.cs` |
| 桌面 / 浏览器链路 | `src/Alas.UI.Desktop/DirectCoreBackend.cs:36`；`src/Alas.UI.Browser/BrowserControlBackend.cs:18`；`src/Alas.Server/ControlServer.cs:34` |
| 合同与客户端 | `src/Alas.Contracts/ControlModels.cs`；`src/Alas.Client/ControlClient*.cs` |
| 架构边界检查 | `tools/diagnostics/verify_architecture.py:389-392`、`:518-520`、`:524`、`:527-535` |
| 上游位置解析 | `src/Alas.Core/Diagnostics/ProjectPaths.cs:23-48` |
| 上游桥接 | `src/Alas.Core/Vision/PythonHost.cs`；`tools/alas_vision.py:3793` |
| 静态导出与消费 | `tools/export_upstream_data.py`、`upstream_config_export.py`、`upstream_map_export.py`；`src/Alas.Core/UpstreamData.cs` |
| 构建入口 | 根目录 `build.ps1`；`tools/build_ui.ps1`；`tools/publish_server.ps1` |
