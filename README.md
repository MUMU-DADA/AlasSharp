# AlasSharp

基于 .NET 10 的碧蓝航线自动化迁移工程。C# 负责会话、任务队列和运行报告；
游戏识别、页面操作与战役流程继续调用上游 ALAS。当前通过 CPython C API 桥接，未采用 Python.NET。

重写目标是 **C# 执行全部非视觉逻辑，Python 仅保留必要的 CV/OCR 桥接**。
目前仍是迁移中的混合运行时，不能称为 C# 引擎重写完成；实际调用归属和替换顺序见
[引擎重写](docs/upstream-engine-rewrite.md)。规则文件与引擎一起迁成 C#，钩子直接编译执行，逐步退役 JSON 计划解释链；静态数据与溯源校验保留。Python 测试/导出工具不属于产品业务执行依赖。

## 当前状态

- 战役已接通上游完整加载与原生运行流程，已有主线、活动成功结算和撤退证据。
- 通用队列、断点续跑、只读观测、原生导航和周期任务入口已实现；各域验证范围见[路线](docs/architecture-roadmap.md)。
- `Alas.Server` 提供 Core 自动化命令、本地控制 API 与预构建 Web UI；桌面 UI 独立运行。Avalonia 桌面/WASM 共用界面已接入 Core/网络能力，并有不调用后端的 UI 隔离模式；验收范围详见[统一 UI](docs/r4-ui-architecture.md)。

禁止逐地图、逐界面独立适配。JSON 导出仅用于展示、溯源和校验；通关结论只能使用
[`sortie-result/1`](docs/result-contract.md)。完整开发边界见 [AGENTS.md](AGENTS.md)。

## 构建与运行

当前启动方式适用于 Windows，需 .NET 10 SDK（运行控制服务需 ASP.NET Core 10 运行时），以及上游 ALAS 仓库内已装好依赖的 `.venv`。
宿主从 `.venv/pyvenv.cfg` 定位 CPython DLL 与依赖；跨平台发布仍待验收。NuGet 缓存位于项目内
`.runtime/nuget/packages`；离线包放入 `.runtime/nuget/source`，配置模板见
[NuGet.config.example](NuGet.config.example)。本机配置与依赖缓存不入库。

```powershell
$alas = "<上游仓库>"
$env:ALAS_REPO = $alas
$py = Join-Path $alas ".venv/Scripts/python.exe"
& $py tools/export_upstream_data.py --repo $alas
& $py tools/verify_export.py --repo $alas
dotnet build src/Alas.Server/Alas.Server.csproj -c Release
& ./src/Alas.Server/bin/Release/net10.0/Alas.Server.exe verify
& ./src/Alas.Server/bin/Release/net10.0/Alas.Server.exe --root .
```

普通 `dotnet build` 产物只提供 Core 命令和控制 API。先运行 `./tools/build_ui.ps1 -Publish`，
再运行 `./build.ps1 -Publish` 或 `./tools/publish_server.ps1`，发布目录才包含共享 Web UI。
此时控制台默认地址为 `http://127.0.0.1:8765/`，队列 API 默认 dry-run。队列格式与动作授权见
[任务说明](docs/tasks.md)，控制台和停止方式见[运行时](docs/runtime.md)。

改完代码后的日常构建用根目录 `build.ps1`（PowerShell 7，默认增量：不清理输出、没改动的项目不重编、
还原过期才执行，重复构建只花几秒；不启动窗口、不访问设备）：

```powershell
./build.ps1                                                   # 增量构建 Alas.sln（Release）
./build.ps1 -Project src/Alas.Server/Alas.Server.csproj        # 只构建服务端入口
./build.ps1 -Ui                                               # 追加构建共享 UI 解决方案 Alas.UI.slnx
./build.ps1 -Publish                                          # 发布目录，Server 包含预构建 Web UI
./build.ps1 -Publish -SelfContained                           # 发布成自带 win-x64 运行时、可拷走的目录
./build.ps1 -Clean                                            # 非增量：先清理再重建
```

可直接运行的程序由各入口项目生成，构建结束脚本会打印它们的路径：

| 程序 | 位置 | 说明 |
| --- | --- | --- |
| `Alas.Server.exe` | `src/Alas.Server/bin/<配置>/net10.0/` | Core 命令、控制 API 与 Web UI 托管 |
| `Alas.UI.Desktop.exe` | `src/Alas.UI.Desktop/bin/<配置>/net10.0/` | 桌面界面，需 `-Ui` |

这些 exe 是框架依赖的 apphost，运行时需要本机有 .NET 10 运行时；`-Publish` 会把入口项目发布到
`.runtime/publish/<程序名>/`，Server 发布需要先生成 `.runtime/ui-publish/browser/wwwroot`；
`-SelfContained` 再额外带上 win-x64 运行时（每个约 108 MB），
可以拷到没装 .NET 的机器上运行。

共享 UI 的发布与 Headless 验收仍在 `tools/build_ui.ps1`，服务端发布仍在 `tools/publish_server.ps1`。

共享 UI 使用 PowerShell 7：首次 `./tools/build_ui.ps1 -Bootstrap -Publish`，以后
`./tools/build_ui.ps1 -Publish` 使用项目内离线缓存；构建桌面/WASM 并运行 Headless，不打开窗口。
只测界面时，桌面启动参数加 `--ui-only`，网页地址加 `?ui-only=1`：使用内存模拟实例，关闭后端轮询，
界面操作不启动或调用 Core、Python、设备和控制 API。启动示例和验证边界见[UI 隔离模式](docs/r4-ui-architecture.md#ui-隔离模式)。

## 验证与上游同步

以下使用已配置的项目 Python；不会操作设备：

```powershell
& ./.runtime/venv314/Scripts/python.exe tools/diagnostics/verify_architecture.py
& ./.runtime/venv314/Scripts/python.exe tools/diagnostics/verify_privacy.py
& ./.runtime/venv314/Scripts/python.exe tools/diagnostics/verify_all.py --docs-only
& ./.runtime/venv314/Scripts/python.exe tools/sync_all.py --verify
```

`--docs-only` 执行离线检查并重建归档报告，设备步骤跳过；省略该参数会包含设备动作。
上游更新使用 `tools/sync_all.py --update --verify`，同步成功不等于业务功能已验收。

## 目录

| 目录 | 用途 |
| --- | --- |
| `src/Alas.Core/` | 上游数据模型、视觉桥接、运行时与任务域 |
| `src/Alas.Core/Diagnostics/` | 诊断命令与离线自检，由 Server 转发 |
| `src/Alas.Server/` | Kestrel 控制 API、Web UI 静态托管与唯一服务入口；运行编排继续在 Core |
| `src/Alas.UI*` | 共享界面、桌面/WASM 入口与原生 Headless 验证 |
| `tools/` | 导出、Python 宿主、同步与诊断工具 |
| `vendor/upstream/` | 上游静态素材镜像，来源和哈希见其中清单 |
| `docs/` | [8 份核心文档](docs/README.md)；历史和详细报告在 `docs/archive/` |
| `.runtime/`、`data/`、`runs/` | 本地依赖、生成数据和原始运行材料，不入库 |

## 许可

本仓库及全部历史提交以 [GPL-3.0](LICENSE) 授权。
上游为 [AzurLaneAutoScript](https://github.com/LmeSzinc/AzurLaneAutoScript)，
UI 参照 [AzurPilot](https://github.com/wess09/AzurPilot)。分发时保留许可与来源并提供对应源码。
