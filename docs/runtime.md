# 运行时与控制入口

产品业务编排位于 `src/Alas.Engine/`。桌面 UI → Alas.Engine；浏览器 UI → Alas.Server → Alas.Engine。Core 已退役，旧运行时说明仅作为[历史对照](archive/history/core-runtime.md)。

## 组成

| 模块 | 职责 |
| --- | --- |
| EngineSession | 同一队列惰性创建一套 C# 设备、页面/地图状态与纯视觉进程 |
| TaskQueue | 类型化 runner、输入验证、依赖、任务期限、失败即停、工件和断点 |
| EngineControlWorkspace | 单个活动队列、实例绑定、状态快照、边界停止与关闭 |
| EngineProfileStore / EngineSettingsWorkspace | Engine profile 事务、Engine 自有 JSON 设置和启动列表存储；只配置纯视觉 worker，不启动业务 Python |
| RunReport | 只读消费 Engine 队列快照及证据，不另行裁决通关 |
| Alas.Server / DirectEngineBackend | HTTP 传输或桌面进程内调用，业务仍在 Engine |

## 请求、停止与断点

`POST /api/run` 接收 `{queue:{tasks:[...]}, mode, instance?, serial?, max_seconds?, resume?, resume_directory?, continue_on_error?}`。mode 为 dry_run（默认）、read_only 或 actions；动作模式须显式 `confirm_actions=true`。每任务 `timeoutSeconds` 优先于公共 `max_seconds`；旧 `max_rounds` 仅接受兼容默认值 20，实际行为由 C# runner 规则定义。任务输入格式见[任务域](tasks.md)。

一次只执行一个队列。实例和设备必须一致；不得在同队列混用实例。纯 CV/OCR worker、ADB、OCR 模型默认从 `.runtime/engine/settings.json` 的 Engine 设置读取；`ALAS_CV_RUNTIME`、`ALAS_ADB` 和 `ALAS_OCR_MODELS` 仅作为显式环境覆盖，素材从 Engine 根目录的 assets 读取；业务规则、导航和任务调度不经过 worker。

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

地图初始化的潜艇定位写入任务证据 `submarine`，会话另存 `submarine-location.json`。其中区分观测、规则推定与中心假设；异常时保留已检查点及当前待检查点，下一任务不复用。报告检查文件存在、定位来源、观察顺序及其与任务结果的一致性。该证据只说明定位过程，不证明潜艇战斗或战役通关。

会话将各场潜艇呼叫另存 `submarine-calls.json`，任务边界清空列表和共享点击计时器。记录实际呼叫模式、加载/最后观察/确认帧及每次点击是否完成；准备失败时状态为失败、加载帧仍为空，后续异常不会丢弃已确认的图标。报告检查固定文件名、普通文件、有效帧顺序、禁用模式无动作以及呼叫状态一致性。`called_observed` 仅表示看见 CALLED 图标，不证明潜艇伤害或战役通关。CLI `--submarine-mode` 与任务 `submarineMode` 对应，五种模式与 BOSS 距离参数见[任务模型](tasks.md)。

BOSS 潜艇移动另存 `submarine-moves.json`，任务边界清空。每次保留 BOSS/起点/目标、尝试次数、最后阶段、选择帧及返回帧；失败不会补成完成。只有完成关闭策略并获取新相机帧才记录 `completed`，返回帧必须晚于选择帧；选点已在原位时走取消，也会记录其完整返回过程。报告校验固定文件名、普通文件、阶段与帧序；缺失/损坏工件使证据不完整。舰队页待命的 `confirmed` 和 `unavailable_clear_mode` 在任务结果中分开记录。完整失败工件只证明过程可追踪，不证明移动成功或战役通关。

地图移动识别步数不足后，`walk-recoveries.json` 保存当前舰队、原位置/目标、拒绝帧及此前交互、恢复帧、单步路径、已完成步数和阶段。`observed` / `recovering` / `walking` 保留失败停点；`redispatched` 表示已提交单步后遇到回合变化，`completed` 仅表示该恢复路径结束。报告校验帧序、路径末端和完成步数，缺失或损坏文件不计完整；会话在任务边界清空。信息条消失、恢复完成和工件完整均不证明战役通关。

普通行走超时单独保存 `walk-timeouts.json`：当前舰队/目标、超时观测帧、找边恢复帧、再次点击帧和输入是否完成。仅在新帧仍未到达时进入恢复；截图/识别调用阻塞或取消不作为可恢复行走超时。恢复后重新建立覆盖层基线并点击原目标，持续重试受任务期限约束。任务边界清空记录；恢复或点击中失败保留部分字段，报告校验文件和帧序。再次点击完成不表示到达，当前舰队标记确认也不表示战斗/战役成功。

地图移动轮询还按上游顺序处理猫攻击动画和延迟公会弹窗：C# 读取原有素材的颜色计数或按钮匹配并执行点击，猫攻击重置到达确认与行走超时，公会弹窗只重置行走超时；两者不会增加战斗、谜题或弹药计数，也不会触发相机重定位。处理项保留在到达结果，点击和异常沿现有任务动作日志/失败帧保存。四服各 144 条原生轨迹和共 96 组上游素材区域合成像素夹具已离线对拍，实际会话另验证失败工件与任务隔离；无真机动作或通关证据。

## 服务与桌面组合

Server 只运行控制 API 和预构建 Web UI 托管，不再转发旧 Core CLI。使用 `Alas.Engine.Cli --help` 查看新命令。Server 参数 `--root`、`--engine-root`、`--instance-store`、`--assets`、`--workspace`、`--artifacts`、`--ui-root` 和 `--port` 指定布局；旧上游仓库和 Python 宿主参数已移除。

API 仅监听回环，校验 Host/Origin，写操作需要本次服务的 X-Alas-Token。请求上限 1 MiB；无效输入 400、来源拒绝 403、工作区忙/关闭 409、未迁移能力 501。`POST /api/queue` 仅保存草稿；`POST /api/stop` 请求边界停止。

`Alas.Contracts` 定义信封，`Alas.Client` 提供 HTTP 客户端；共享 UI 不引用引擎、设备或服务端实现。桌面使用 DirectEngineBackend 直连，浏览器使用 HTTP 适配器。配置实例、Engine settings、PATCH、导入/创建/删除和启动列表均由 Engine 的 JSON 文件事务提供；保存启动列表不会执行任务。

单任务接口只接受已注册的 Engine `ITaskRunner.Kind`，并复用 `TaskQueue` 的输入校验、工件和结果合同；旧上游任务名不会自动映射。旧周期调度请求模型和 `/api/scheduler/start` 已删除，界面上的观察按钮提交同一 Engine 队列的只读 `observe` 任务。连续调度、统计、指挥喵报告/清理和策略校验还未迁移，明确抛 `EngineCapabilityUnavailableException`（HTTP 501）。这些能力仍须在 Engine 内实现，不能接回 Core 或 Python 服务。

SSE 保留 control-state/1 完整快照与游标合同，慢订阅仅保留待发最新状态，不承诺事件重放。静态托管不等于远程认证或 HTTPS；发布不自动打包素材、Python 或 ADB。

## Engine 设置

`GET/PATCH /api/settings` 与桌面 `EngineSettingsBackend` 共用 `EngineSettingsWorkspace`。设置位于 Engine 根目录的 `settings.json`，合同为 `engine-settings/1`，只包含 `AdbPath`、`VisionRuntime` 和 `OcrModelDirectory`。路径或命令均为字符串，空 OCR 目录禁用需要 OCR 的任务；执行文件命令名在 PATH 查找，相对路径以 Engine 根目录为基准。环境变量显式覆盖文件值，设置在提交下一队列时快照化，保存不影响正在运行的队列。CLI 仍使用自己的显式参数。

`startup.json` 使用独立 `engine-startup/1` 合同，只保存实例偏好；自动启动调度尚未实现，保存不执行设备操作。两种文件都通过跨工作区文件锁合并最新值并原子替换，拒绝未知字段、错误类型、损坏合同及重复键；不会静默回退或覆盖坏文件。旧 `config/deploy.yaml` 不读取、不迁移、不修改。Git/Python 安装、上游 OCR 服务及尚未实现的远程/Web 设置不在新设置页中；Web 监听端口仍由 Server 参数设置。

## 验证

Engine 的控制、停止、报告和命令检查使用 `dotnet run --project tests/Alas.Engine.Tests -c Release -- --control-workspace .runtime/checks/control-workspace` 及 `--campaign-command`。这是可执行测试项目，不能用无输出的 dotnet test 作为通过证据。

`dotnet run --project tests/Alas.Engine.Tests -- --settings .runtime/checks/engine-settings` 检查设置存储、并发/失败原子性、运行时接线和坏文件拒绝；`dotnet run --project tests/Alas.Control.Tests -- .runtime/checks/engine-settings-http` 使用合成根目录启动真实本机 HTTP 服务与类型化客户端，验证设置与启动偏好读写及令牌保护。两者不连接真实设备。

架构与隐私分别运行 tools/diagnostics/verify_architecture.py、verify_privacy.py。UI 用 Avalonia Headless 验收，不打开窗口。旧 Core/原生测试和真机样本仅证明原调用路径，尚未迁移的诊断脚本不能算新产品通过。最新缺口见[路线](architecture-roadmap.md)。
