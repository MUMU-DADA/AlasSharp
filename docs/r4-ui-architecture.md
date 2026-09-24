# 统一 UI 技术方案

目标：尽量共享 C# 界面代码，覆盖原生桌面、远程网页和纯服务器模式，外观接近 AzurPilot。
**桌面不能使用浏览器壳或 WebView 承载主要 UI。首选候选为 Avalonia，备选为 Uno Skia；服务使用 ASP.NET Core 10 / Kestrel。**
已接通共享界面和桌面/WASM 构建入口。任务配置、实例、报告及调度启停已通过能力接口接入 Core；其余页面按功能切片验收，演示数据仅用于离线预览。完整状态见[路线](architecture-roadmap.md)。

## 方案比较

| 方案 | 桌面与网页共用方式 | 结论 |
| --- | --- | --- |
| Avalonia | C#/AXAML、ViewModel、主题共享；桌面原生窗口 + Skia，网页 WebAssembly + CanvasKit/WebGL | 首选：本项目没有 WinUI 存量界面，适合自定义上游风格；网页首载、输入与无障碍须实测 |
| Uno Platform + Skia | C#/WinUI XAML 共享；Skia Desktop 与 WebAssembly 使用统一绘制 | 可行备选；适合已有 WinUI 资产的项目。若追求一致外观，各端应统一 Skia，不能把 WinAppSDK 原生渲染与它混为同一路径 |
| MAUI / Blazor Hybrid | MAUI 本身没有同一 UI 的浏览器目标；Hybrid 共享 Razor 但桌面使用 WebView | 不满足本轮约束，且 MAUI 官方桌面范围不含 Linux |
| React + Electron / Tauri | 网页直接复用，桌面依赖 Chromium 或系统 WebView | 不满足无浏览器壳要求 |

这里的“原生”指系统窗口与原生图形绘制，不要求每个控件都是 WinUI/AppKit/GTK 控件，也不要求 Native AOT。
系统原生控件会随平台改变外观；统一自绘更符合展示一致和自定义主题的目标。共享源码仍需分别发布桌面与 WASM 产物。

## 代码与运行模式

```mermaid
flowchart LR
  Shared[共享 AXAML / ViewModel / 主题 / 能力接口] --> Desktop[Avalonia 原生桌面]
  Shared --> Browser[Avalonia WebAssembly 网页]
  Desktop --> Runtime[Alas.Core/Runtime]
  Browser --> Client[HTTP 客户端]
  Client --> API[Kestrel API 与事件流]
  API --> Runtime
  Runtime --> Engine[上游 Python 与设备后端]
```

- `Alas.UI` 保存共享界面、交互、主题和客户端状态；`Alas.Contracts` 保存传输信封，`Alas.Client` 提供 HTTP 客户端，两者不引用设备或 Python 实现。
- `UI.Desktop` 负责窗口、平台文件能力及同进程 Core 生命周期；业务调用不经过本机 HTTP 中转。
- `UI.Browser` 使用同一共享界面；文件、剪贴板、下载和页面地址由浏览器适配层处理，不能直接访问服务器文件系统。
- `Server` 独立发布，只运行 Kestrel、业务运行时和设备依赖，并托管预构建 WASM 静态文件；不启动 Avalonia 桌面、浏览器、显示服务或 Node.js。
- 桌面和网页使用相同能力合同，分别由直接调用和网络适配器实现。队列、授权、停止、日志及结论留在 Core；桌面关闭通过 Core 等待上游停止边界，浏览器断开不停止服务端任务。
- 网页用 HTTP 命令/查询；SSE 已有合同回归，尚待接入页面。远程开放前完成认证、HTTPS 与来源检查。

WASM 在访问者浏览器中绘制界面，服务端不按用户渲染桌面画面。这与远程桌面串流或在桌面嵌入 WebView 不同。
浏览器无法加载现有 CPython/ADB，不能把 `Alas.Core` 整体打进 WASM；服务端事实仍按 `TaskOutcome` 和 `sortie-result/1` 展示。

## 还原 AzurPilot 的成本

已核对上游提交 `f67259dcd` 的 `App.tsx`、`styles/tokens.css` 与组件依赖。它是 React/CSS 界面，不能直接转换成 AXAML。

| 范围 | 实施与难度 |
| --- | --- |
| 顶栏、侧栏、任务导航、总览卡片、表单、弹窗 | 中等：建立共享布局和 ControlTheme，按上游主题参数还原，避免每页复制样式 |
| 明暗主题、字体、图标、圆角、间距、窄屏抽屉 | 中等：资源字典与响应式布局；固定可分发字体，核对中英文字形、DPI 和浏览器缩放 |
| 高频日志、大表格、统计图 | 中高：虚拟化与批量更新；ECharts 需用共享绘图/图表组件重做，不能只在网页保留另一套实现 |
| CodeMirror 编辑器、液态玻璃、背景模糊 | 高：需要替代控件或自定义渲染；若要求这些效果也高度一致，须在选型原型中验证，不能默认等价或嵌 WebView 绕过 |

可以以接近上游的布局、主题和交互为验收目标；没有双端截图与实际交互对照前，不承诺像素完全一致、固定复用比例或性能数字。
复用上游设计/素材时保留 GPL-3.0 与对应素材许可；不复制开发设备截图或账号数据。

## 平台边界

Avalonia 官方列出 Windows、macOS、Linux 的 x64/ARM64 目标，但 OS 版本与架构支持等级不同；不能把目标存在当成本项目已支持。
Linux 桌面当前主要走 X11；12.1 起的原生 Wayland 为实验性。普通服务器不依赖这些显示后端，优先验证 glibc 发行版。
网页目标要求 WASM/WebGL；首载包含 .NET 与绘图库，需测冷/热启动、内存与日志刷新。官方浏览器无障碍仍标为部分支持。
中文输入法、复制选择、上传下载、键盘焦点、触控和断线恢复必须逐浏览器验证；AOT 会增加构建与下载成本，按实测决定。

**当前自动化服务仍有 Windows 假设**：`PythonHost` 使用 `kernel32`、`python3*.dll`、Windows venv 布局及路径分隔符。
Linux/macOS 需补对应 libpython 和依赖布局；x64/ARM64 还需匹配 Python、OpenCV、ONNX Runtime、PyAV 及设备后端。
服务器以实际可用的 ADB 设备连接为准，不能承诺 Windows 专属模拟器管理能力。UI 选型不改变宿主替换门槛，也不引入 Python.NET。

## 实施门槛

1. 先做共享 Avalonia 原型：上游风格的导航、总览、配置、日志及代表性复杂效果，同时发布 Windows x64 桌面和 WASM。
2. 用相同数据、主题、尺寸及字体对照两端，验证中文输入、滚动、图表/编辑器、缩放和首载；有不可接受缺口时再以同一场景比较 Uno Skia。
3. 固定服务合同并迁入 Kestrel，保留真实 HTTP dry-run、零设备调用、边界停止及退出落盘验证；客户端不新增业务状态机。
4. 验证 Windows x64 与 Linux x64/ARM64 无显示环境服务，再逐项验收 macOS 与其余桌面架构。UI、服务、自动化宿主分别记录通过范围。
5. SDK、WASM 工具链与 NuGet 缓存放项目忽略目录；发行包验证脱离源码目录、普通权限和离线启动，生产资源不依赖 CDN。

## 原型与无窗口验证

### UI 隔离模式

桌面以 `--ui-only` 启动；网页地址加 `?ui-only=1`（已有参数时加 `&ui-only=1`）。开关在平台组合入口生效：
不构造 `DirectCoreBackend` / `BrowserControlBackend`，不启动 Python、设备或控制工作区，也不向控制 API 发请求。
默认启动保持原来的真实后端模式；连接失败不会自动变成模拟状态。切换模式需关闭并重新启动窗口或重新加载网页。

```powershell
# 先运行 build_ui.ps1；以下命令由使用者启动真实窗口
./.runtime/dotnet/dotnet.exe ./src/Alas.UI.Desktop/bin/Release/net10.0/Alas.UI.Desktop.dll --ui-only
```

隔离模式一直显示提示条，实例卡带“演示”标识。主页、总览、任务配置、配置管理、设置和统计使用同一套视图、
ViewModel 及能力接口，但数据源替换为 `SimulatedUiBackend`；创建/删除、保存、启停只修改当前内存样本。
主题与资源卡偏好也使用独立内存存储，重启恢复初始样本。显式文件导入/导出仍使用客户端文件选择器；导入数据不会提交到后端。
脚本校验明确提示隔离模式不执行校验，不伪造业务有效性；更新、认证、远程服务等尚未接通的能力仍保持不可用。

后台状态轮询关闭。提示条的“+100 模拟日志”提供确定、无设备的渲染负载，沿正常快照消费、筛选和滚动链进入总览，
缓存保持最近 400 条。任务表单为明确标注的通用样本，不能据其渲染时间推断实际大表单或自动化后端性能。
`UiOnlyChecks` 用抛异常的真实工厂证明隔离入口不求值，并覆盖全部模拟能力、逐任务导航、真实鼠标/键盘、启停、
宽窄布局和离树重挂接；Headless 过程未加载 Core/网络客户端。不显示真实窗口；浏览器现场和 GPU 性能仍需单独测量。

### 日志性能验收

`Alas.UI.Headless` 的 `--perf` 入口运行共享视图性能场景，再运行显式隔离入口；`--perf-ui-only` 只运行后者。
可追加输出目录参数，JSON/文本报告应写入本地忽略目录。构建与测量串行执行，前后比较固定构建、入口、数据及预热过程。

```powershell
./.runtime/dotnet/dotnet.exe ./src/Alas.UI.Headless/bin/Release/net10.0/Alas.UI.Headless.dll --perf .runtime/ui-perf
./.runtime/dotnet/dotnet.exe ./src/Alas.UI.Headless/bin/Release/net10.0/Alas.UI.Headless.dll --perf-ui-only .runtime/ui-only-perf
```

日志通过 `LogViewport` 按视口和数据项身份复用控件；滚动仍由外层原生 `ScrollViewer` 管理。
跟随请求按帧合并，并处理变高换行、缩放、用户上滚、暂停、换模型和离树后的旧回调；
Shell/Home 的后端订阅随可视树挂接和离开成对管理，重挂接补读状态，隔离模式仍不启动轮询。
回归检查逐样本验证实现行数、缓存、控件回收、最新行实际可见和订阅释放，性能报告记录样本量、耗时与分配量，未设置任意耗时通过线。

当前共享视图套件 22 个场景、67 项结构断言通过；显式隔离入口另有宽度 1280/390 × 跟随/暂停共 4 个场景，
每场景预热 20 次、正式测量 20 次，通过正常内存快照消费链而非直接追加 ViewModel。
独立串行样本中每次更新中位分配约 1.35–1.89 MB，仍有优化空间；不能与直接追加日志的样本混比，
也不能据 Headless 推断真实窗口、浏览器或 GPU 性能。倒序暂停时头部插入的既有锚点漂移仍未修复。

`Alas.UI.slnx` 独立于 CLI 方案，使用 Avalonia 12.1.3、.NET 10。
`Alas.UI` 的同一 AXAML/样式/ViewModel 供 Desktop 与 Browser 引用。已集成页面及剩余功能统一记在迁移路线；离线演示和真实 Core 数据必须明确区分。中文字体内置 Noto CJK 2.004（OFL），来源见字体目录。
配置管理与首页共用创建/导入表单，删除保留 revision 校验，配置导出只写 values；桌面与网页共享 Avalonia 文件选择能力。保存先请求选择器，再读取内容并写入；JSON/CSV/PNG、取消与读取失败已离线验证，真实浏览器文件选择器未验收。
主题使用编译期类型化资源字典，偏好使用无反射 JSON 读写；升级兼容旧版 PascalCase 字段与自定义配色。Headless 已覆盖六主题外壳尺寸、Legacy 内容栏、窄屏输入、任务搜索、资源卡四列/两列布局，以及偏好往返和自定义配色删除；双端现场视觉一致性仍待验收。
系统/远程设置共用 Core 部署能力和一条草稿队列；Headless 覆盖真实输入、布尔类型、旧响应隔离、异步回执、焦点保持及窄屏命中。草稿序列化不依赖反射，但平台存储尚未注入。更新和登录组件具有局部状态/交互验收，生产后端尚未接通；资源选择组件与上游即时保存/拖拽仍有差距，不能将页面存在视为业务完成。

Windows + PowerShell 7 的构建入口（不会启动桌面或浏览器窗口）：

```powershell
./tools/build_ui.ps1 -Bootstrap -Publish  # 首次联网准备项目内 SDK、WASM 工作负载及包源
./tools/build_ui.ps1 -Publish             # 后续仅使用项目内工具链与离线包源
```

SDK 固定为 10.0.401，依赖保存在 `.runtime/dotnet`、`.runtime/nuget`，不要求全局安装。
省略 `-Publish` 仍构建两端并运行 Headless；加上后生成 `.runtime/ui-publish/desktop-win-x64` 与 `browser/wwwroot`。
离线构建不查询在线漏洞公告；依赖安全公告需另行联网审计。UI 编译路径映射为中性路径，Release 关闭托管/原生调试符号。
发布后自动执行 `verify_ui_artifacts.ps1`，检查 DLL、WASM、JS 及解压后的资源是否包含个人路径，并检查网页入口引用是否齐全。

`Alas.UI.Headless` 使用 Avalonia 原生 Headless 窗口后端和 Skia 离屏渲染：鼠标点击、中文文本注入与双向绑定、JSON 错误反馈、主题往返、宽窄布局和 2,000 行日志滚动均已通过，进程正常退出。
该离线预览的日志场景仅实例化视口附近的少量行控件；数量随行高与尺寸变化，这证明虚拟化生效，不是性能基准。
显式隔离模式的完整快照链另按上述入口测量。离屏 PNG 留在 `.runtime/ui-headless`，常规测试有 90 秒退出上限。
自动化不得打开真实桌面或浏览器窗口；`Window.Show()` 在该测试中只连接内存窗口实现，不连接系统桌面。

尚未验收：真实浏览器/系统输入法、DPI、无障碍、首载与内存、复杂编辑器/玻璃效果、双端现场视觉一致性。
本地控制 API 已迁入 `Alas.Server` 的 Kestrel；既可由 `alashub control` 启动，也可由独立 `Alas.Server`
进程启动。独立入口可用 `--ui-root` 同源托管预构建 WASM，仍只监听回环并沿用 Host/Origin/令牌校验；
静态托管不提供远程认证或 HTTPS。编排收口至 `Alas.Core/Runtime/ControlWorkspace`，合同见[运行时](runtime.md)。
共享 HTTP 客户端已接入网页适配器；SSE 完整状态快照流通过真实服务离线回归，页面目前轮询运行状态。快照有游标、重连基准和慢订阅合并，不承诺审计事件重放。
独立服务入口和选定 UI 根目录的 HTTP/MIME/路径隔离已有无窗口回归；`tools/publish_server.ps1`
可生成指定 RID 的服务器并可复制预构建网页。WASM 实际浏览器加载、远程认证、HTTPS、Python/设备依赖
随包布局及其他平台/架构仍待实现或验证。Headless 结果不能替代这些结论。状态统一见[路线](architecture-roadmap.md)。

## 依据

2026-09-24 查阅 Avalonia 12.1.3 与 Uno 6.6.166 发布资料；Uno 使用稳定标签文档，避免把 master 的 7.0 行为当成已发布能力。

- Avalonia：[渲染方式](https://docs.avaloniaui.net/docs/welcome)、[支持矩阵](https://docs.avaloniaui.net/docs/supported-platforms)、[WASM 发布](https://docs.avaloniaui.net/docs/deployment/webassembly)、[无障碍边界](https://docs.avaloniaui.net/docs/app-development/accessibility)。
- Uno 6.6：[Skia 渲染](https://github.com/unoplatform/uno/blob/6.6.166/doc/articles/features/using-skia-rendering.md)、[桌面](https://github.com/unoplatform/uno/blob/6.6.166/doc/articles/features/using-skia-desktop.md)、[WASM 发布](https://github.com/unoplatform/uno/blob/6.6.166/doc/articles/uno-publishing-webassembly.md)。
- 上游：[布局](https://github.com/wess09/AzurPilot/blob/f67259dcd/frontend/src/app/App.tsx)、[主题参数](https://github.com/wess09/AzurPilot/blob/f67259dcd/frontend/src/styles/tokens.css)、[组件依赖](https://github.com/wess09/AzurPilot/blob/f67259dcd/frontend/package.json)。
- [MAUI 平台范围](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms?view=net-maui-10.0)、[Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/?view=aspnetcore-10.0)；旧浏览器壳调研仅留在[历史归档](archive/history/r4-ui-architecture-20260924.md)。
