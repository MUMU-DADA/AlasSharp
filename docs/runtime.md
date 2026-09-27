# 运行时与控制入口

产品业务编排位于 `src/Alas.Engine/`。桌面 UI → Alas.Engine；浏览器 UI → Alas.Server → Alas.Engine。Core 已退役，旧运行时说明仅作为[历史对照](archive/history/core-runtime.md)。

## 组成

| 模块 | 职责 |
| --- | --- |
| EngineSession | 同一队列惰性创建一套 C# 设备、页面/地图状态与纯视觉进程 |
| TaskQueue | 类型化 runner、输入验证、依赖、任务期限、失败即停、工件和断点 |
| EngineControlWorkspace | 单个活动队列、实例绑定、状态快照、边界停止与关闭 |
| EngineProfileStore / DeploySettingsWorkspace | Engine profile 事务、部署 schema 和启动列表存储，不启动 Python |
| RunReport | 只读消费 Engine 队列快照及证据，不另行裁决通关 |
| Alas.Server / DirectEngineBackend | HTTP 传输或桌面进程内调用，业务仍在 Engine |

## 请求、停止与断点

`POST /api/run` 接收 `{queue:{tasks:[...]}, mode, instance?, serial?, max_seconds?, resume?, resume_directory?, continue_on_error?}`。mode 为 dry_run（默认）、read_only 或 actions；动作模式须显式 `confirm_actions=true`。每任务 `timeoutSeconds` 优先于公共 `max_seconds`；旧 `max_rounds` 仅接受兼容默认值 20，实际行为由 C# runner 规则定义。任务输入格式见[任务域](tasks.md)。

一次只执行一个队列。实例和设备必须一致；不得在同队列混用实例。纯 CV/OCR worker、ADB、OCR 模型通过 `ALAS_CV_RUNTIME`（绝对路径）、`ALAS_ADB` 和 `ALAS_OCR_MODELS` 配置，素材从 Engine 根目录的 assets 读取；业务规则、导航和任务调度不经过 worker。

控制入口的停止与关闭只设置边界信号，不中断当前任务；后续任务记 skipped / stop_requested_at_boundary。关闭先拒绝新请求，再等待已接受队列收尾。HTTP 取消或断开不会停止队列。Engine CLI 的取消令牌和任务期限可中断当前任务，须保留失败证据，不能把它当作正常结算。

失败默认停止后续任务；显式 continue_on_error 才继续，依赖仍须满足。断点要求任务序列、会话指纹和完成项工件哈希一致，只有 succeeded 可跳过。dry-run 不能续跑真实状态。控制 API 通过 resume_directory 指定目录，CLI 使用 `--resume <目录>`。

## 工件与报告

```text
<artifacts>/<随机运行 id>/
  queue.json                 原始类型化任务数组
  run.json                   engine-queue/1 当前尝试、结果与完成标记
  state.json                 成功任务及证据哈希（真跑）
  attempt-<id>/
    0000-<task-id>/
      request.json / task.json / actions.json
      ...                    帧与域内证据
    summary.json             本次尝试最终结果
```

run.json 原子替换，报告只展示当前尝试；断点跳过项指向以前的成功工件，不把多次尝试重复统计。报告检查请求、任务快照、动作文件及登记的失败帧；截图文件名相对于该任务目录解析，并核对任务边界中的帧编号、图像哈希、动作次数和失败帧登记。缺失或被修改的截图不计为完整证据。dry-run 与 succeeded 分开计数。未完成快照显示 running，缺失/损坏证据保留 findings，不能据工作线程结束判断业务成功。战役结果直接保留 runner 的 sortie-result/1 证据，报告不从单字段推断通关。

运行 id 为随机值，最近运行按快照修改时间排序。`GET /api/report?stamp=` 返回报告，`GET /api/state` 包含 active、report、live_tasks、recent_logs 和 runs；当前 recent_logs 尚无新引擎实时日志接线。原始账号、设备、日志和截图只留忽略目录，入库证据须脱敏。

## 服务与桌面组合

Server 只运行控制 API 和预构建 Web UI 托管，不再转发旧 Core CLI。使用 `Alas.Engine.Cli --help` 查看新命令。Server 参数 `--root`、`--repo`、`--workspace`、`--artifacts`、`--ui-root` 和 `--port` 指定布局；旧 data/tools 参数仅保留解析兼容，不用于执行旧宿主。

API 仅监听回环，校验 Host/Origin，写操作需要本次服务的 X-Alas-Token。请求上限 1 MiB；无效输入 400、来源拒绝 403、工作区忙/关闭 409、未迁移能力 501。`POST /api/queue` 仅保存草稿；`POST /api/stop` 请求边界停止。

`Alas.Contracts` 定义信封，`Alas.Client` 提供 HTTP 客户端；共享 UI 不引用引擎、设备或服务端实现。桌面使用 DirectEngineBackend 直连，浏览器使用 HTTP 适配器。配置实例、schema、PATCH、导入/创建/删除和部署设置仍由 C# 文件事务提供；保存启动列表不会执行任务。

单任务接口只接受已注册的 Engine `ITaskRunner.Kind`，并复用 `TaskQueue` 的输入校验、工件和结果合同；旧上游任务名不会自动映射。旧周期调度请求模型和 `/api/scheduler/start` 已删除，界面上的观察按钮提交同一 Engine 队列的只读 `observe` 任务。连续调度、统计、指挥喵报告/清理和策略校验还未迁移，明确抛 `EngineCapabilityUnavailableException`（HTTP 501）。这些能力仍须在 Engine 内实现，不能接回 Core 或 Python 服务。

SSE 保留 control-state/1 完整快照与游标合同，慢订阅仅保留待发最新状态，不承诺事件重放。静态托管不等于远程认证或 HTTPS；发布不自动打包素材、Python 或 ADB。

## 验证

Engine 的控制、停止、报告和命令检查使用 `dotnet run --project tests/Alas.Engine.Tests -c Release -- --control-workspace .runtime/checks/control-workspace` 及 `--campaign-command`。这是可执行测试项目，不能用无输出的 dotnet test 作为通过证据。

架构与隐私分别运行 tools/diagnostics/verify_architecture.py、verify_privacy.py。UI 用 Avalonia Headless 验收，不打开窗口。旧 Core/原生测试和真机样本仅证明原调用路径，尚未迁移的诊断脚本不能算新产品通过。最新缺口见[路线](architecture-roadmap.md)。
