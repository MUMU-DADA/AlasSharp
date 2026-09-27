# 当前架构边界

本文只描述当前产品组合。旧 Core 的依赖调查已归档到 `docs/archive/history/`，不再作为开发入口或运行时实现。

## 产品执行图

```text
桌面 UI ────────────────┐
                        ├──> Alas.Engine ──> C# 规则、状态、设备、导航、任务和结果合同
浏览器 UI ─> Alas.Server ┘                    │
                                              └──> 独立 CV/OCR worker（只处理图像观测）
```

`Alas.Engine` 是唯一业务执行核心。它不引用 `Alas.Core`、旧任务 runner、计划解释器或上游业务宿主。`Alas.Server` 只负责 HTTP、认证、配置事务和静态网页托管；桌面 UI 通过 `DirectEngineBackend` 在进程内调用 Engine；浏览器 UI 通过 `Alas.Client` 调用 Server。`Alas.Contracts` 只承载传输合同，不能包含业务编排。

`src/Alas.Core/` 仍可能作为历史源码快照存在，但没有产品解决方案引用、项目引用、发布输入或运行时加载。`verify_architecture.py` 会检查这些边界，发布检查还要确认输出目录没有 `Alas.Core.dll`。

## Engine 内部职责

| 层 | 责任 |
| --- | --- |
| `Rules/` | 类型化的 Config、MAP、页面、素材和章节规则；保留来源、继承和服务器变体 |
| `Runtime/` | `EngineSession`、地图状态/相机/寻路、战斗、导航、设备会话、配置和工件 |
| `Tasks/` | `ITaskRunner`、`TaskQueue`、战役/观测/导航任务及输入校验 |
| `Imaging/` | `IVision` 和纯 CV/OCR worker；不接受任务名、地图名或业务操作 |
| `Contracts/` | `sortie-result/1` 的 C# 权威裁决及离线夹具检查器 |

规则 JSON、素材清单和 Python 上游代码只用于构建、来源追踪、漂移校验和离线 oracle。产品运行时不解释 JSON 计划，也不把静态摘要当成地图或页面规则。

## 依赖合同

产品项目的引用必须保持下表；新增项目必须先说明它属于哪个执行边界：

| 项目 | 允许引用 |
| --- | --- |
| `Alas.Engine` | 无产品项目引用 |
| `Alas.Engine.Cli` | `Alas.Engine` |
| `Alas.Server` | `Alas.Engine`、`Alas.Contracts` |
| `Alas.UI.Desktop` | `Alas.Engine`、`Alas.UI`、`Alas.Contracts` |
| `Alas.UI.Browser` | `Alas.UI`、`Alas.Client` |
| `Alas.UI` | `Alas.Contracts` |
| `Alas.Client` | `Alas.Contracts` |

任何产品源码出现 `Alas.Core`、`tools/alas_vision.py`、`s3_campaign_*` 或计划解释入口都应被架构检查拒绝。未迁移能力由 Engine 明确返回不可用，不能回退旧流程。

## 验证入口

```text
dotnet build Alas.sln --no-restore
dotnet build Alas.Engine.slnx --no-restore
<仓库 Python 环境>/python tools/diagnostics/verify_architecture.py
<仓库 Python 环境>/python tools/diagnostics/verify_result_contract.py
dotnet run --project tests/Alas.Engine.Tests -- --control-workspace .runtime/checks/control-workspace
```

真机成功必须由新 Engine 产生成功结算、返回章节页和完整工件；旧 Core 或上游原生路径的成功记录只作对照，不能替代产品验收。
