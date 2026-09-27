# AlasSharp

基于 .NET 10 的碧蓝航线自动化重写工程。产品业务后端为 `Alas.Engine`：桌面 UI 在进程内调用 Engine，浏览器 UI 经 Server 调用 Engine。`Alas.Core` 已删除。

目标是 **C# 执行全部非视觉逻辑，Python 仅保留必要的 CV/OCR 桥接**。规则文件与引擎一起迁为 C#，产品不运行导出的 JSON 计划或 Python 业务。产品入口已切换，完整规则和业务域仍在迁移，不能称为引擎重写完成。详见[路线](docs/architecture-roadmap.md)和[重写边界](docs/upstream-engine-rewrite.md)。

## 当前状态

- Engine 已有 C# 规则、设备、导航、地图状态、战役执行与任务队列；完整业务覆盖及新引擎真机通关仍未验收。
- Server 与桌面共用 Engine 的配置、部署设置、队列和报告能力。旧按任务名运行、连续调度、统计及策略服务尚未迁移，明确返回不可用。
- `Alas.Server` 提供控制 API 与 Web UI 托管；`Alas.Engine.Cli` 提供新引擎命令。旧 `Server verify/campaign/queue` 命令转发已取消。

禁止逐地图、逐界面独立适配。上游规则须忠实迁移，导出 JSON 仅用于离线展示、溯源和校验。通关结论只能使用 [`sortie-result/1`](docs/result-contract.md)。旧 Core 的真机记录不能算作 Engine 验收。完整规则见 [AGENTS.md](AGENTS.md)。

## 构建与运行

需 .NET 10 SDK；控制服务运行需 ASP.NET Core 10 运行时。设备执行另需 ADB、素材、带图像依赖的 Python 和 OCR 模型；Engine 启动纯视觉进程，不启动旧混合宿主。实例配置与素材仍从 `ALAS_REPO` 指定的数据根目录读取。

```powershell
$env:ALAS_REPO = "<配置与素材根目录>"
$env:ALAS_PYTHON = "<纯视觉 Python 可执行文件绝对路径>"
$env:ALAS_ADB = "<ADB 可执行文件路径>"
$env:ALAS_OCR_MODELS = "<OCR 模型目录>"
dotnet build Alas.sln -c Release
dotnet run --project src/Alas.Server -c Release -- --root .
```

Server 默认仅监听 `http://127.0.0.1:8765/`，队列 API 默认 dry-run。普通构建不包含网页；先运行 `./tools/build_ui.ps1 -Publish`，再运行 `./build.ps1 -Publish` 或 `./tools/publish_server.ps1`，发布目录才包含共享 Web UI。

日常构建使用 PowerShell 7，不启动窗口或访问设备：

```powershell
./build.ps1                                  # 增量构建主解决方案（Release）
./build.ps1 -Ui                              # 追加构建桌面、浏览器及 Headless
./build.ps1 -Publish                         # 发布目录，Server 包含预构建 Web UI
./build.ps1 -Publish -SelfContained          # 自带 win-x64 .NET 运行时
./tools/build_ui.ps1 -Bootstrap -Publish     # 首次准备 UI 依赖并离屏验收
```

NuGet 缓存位于 `.runtime/nuget/packages`，离线包源为 `.runtime/nuget/source`，模板见 [NuGet.config.example](NuGet.config.example)。本机配置与缓存不入库。

| 程序 | 位置 | 职责 |
| --- | --- | --- |
| `Alas.Server.exe` | `src/Alas.Server/bin/<配置>/net10.0/` | 控制 API 与静态网页托管 |
| `Alas.Engine.Cli.exe` | `src/Alas.Engine.Cli/bin/<配置>/net10.0/` | observe、navigate、run、campaign 命令 |
| `Alas.UI.Desktop.exe` | `src/Alas.UI.Desktop/bin/<配置>/net10.0/` | 原生桌面 UI |

`-Publish` 发布到 `.runtime/publish/`，不自动打包视觉 Python、OCR 模型、素材或 ADB。跨平台发布与真实窗口/浏览器现场仍须分别验收。

只测界面时使用桌面 `--ui-only` 或网页 `?ui-only=1`：内存模拟实例、无后台轮询，不启动 Engine、Python、设备或控制 API。详见[统一 UI](docs/r4-ui-architecture.md)。

## 离线验证

```powershell
python tools/diagnostics/verify_architecture.py
python tools/diagnostics/verify_privacy.py
dotnet run --project tests/Alas.Engine.Tests -c Release -- --campaign-command .runtime/checks/campaign-command
dotnet run --project tests/Alas.Engine.Tests -c Release -- --control-workspace .runtime/checks/control-workspace
```

测试项目是可执行验收程序，须用 `dotnet run`；`dotnet test` 不会执行这些检查。`tools/diagnostics/verify_all.py` 和 `tools/sync_all.py --verify` 只登记 Engine、导出合同、结果合同、架构和隐私检查；退役 Core/S3/R5 脚本不在默认执行链，旧通过记录不能证明新产品通过。

## 目录

| 目录 | 用途 |
| --- | --- |
| `src/Alas.Engine/` | C# 规则、业务运行时、设备、任务与纯视觉接口 |
| `src/Alas.Engine.Cli/` | Engine 命令入口 |
| `src/Alas.Server/` | 控制 API 与静态网页托管；业务编排在 Engine |
| `src/Alas.UI*` | 共享界面、桌面/WASM 入口及 Headless 验收 |
| `tools/` | 离线迁移、导出、对照与诊断工具；旧混合宿主已退役 |
| `vendor/upstream/` | 静态素材镜像与来源清单 |
| `docs/` | [核心文档](docs/README.md)；历史和报告在 `docs/archive/` |
| `.runtime/`、`data/`、`runs/` | 本地依赖、生成数据和原始运行材料，不入库 |

## 许可

本仓库及全部历史提交以 [GPL-3.0](LICENSE) 授权。上游为 [AzurLaneAutoScript](https://github.com/LmeSzinc/AzurLaneAutoScript)，UI 参照 [AzurPilot](https://github.com/wess09/AzurPilot)。分发时保留许可与来源并提供对应源码。
