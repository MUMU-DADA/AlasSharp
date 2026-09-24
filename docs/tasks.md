# 任务队列与业务域

新业务域先在 `Alas.Core/Tasks/` 实现 `ITaskRunner.Kind / Preconditions / Run`，
再由 `QueueExecution` 注册并交给 `TaskQueue`。CLI 和前端不解释业务输入或复制调度状态机。

## 请求与结果

```json
{"tasks":[{"id":"observe","kind":"observe","required":false,
  "input":{"seconds":2,"tick_seconds":0.5}}]}
```

`id` 在队列内唯一；`input` 只能是 JSON 对象或 `null`，由该域校验字段、类型和范围。
未知字段、错误类型或越界值不得静默改成默认值。每个任务保留输入、边界状态、前置条件与证据。

| 任务结论 | 含义 |
| --- | --- |
| `succeeded` | 达成任务定义的结果，证据范围由该域说明 |
| `failed` | 执行失败，或 required 任务未满足前置条件 |
| `skipped` | 非 required 前置不满足、续跑已完成项、前序失败或取消导致未运行 |
| `refused` | 被执行联锁拒绝 |
| `dry_run` | 只读计划，没有真实动作 |

队列顺序执行，默认失败即停。队列结论使用 `succeeded / failed / cancelled / dry_run / partial`；
任务没有运行与任务运行失败必须区分。取消、续跑身份与工件规则见[运行时](runtime.md)。

## 执行方式

```powershell
alashub queue --file queue.json --artifacts runs
alashub queue --file queue.json --run --read-only-device --serial <设备> --screenshot adb --control ADB
alashub queue --file queue.json --run --allow-actions --serial <设备> --screenshot adb --control ADB
```

第一条为默认 dry-run；后两条分别显式授权只读设备和动作会话。JSON 不能把 dry-run 或只读会话提升为动作会话。
`run`、`goto` 已弃用，只提供迁移提示。

## 已注册任务

| kind | 职责与主要输入 | 授权边界 |
| --- | --- | --- |
| `campaign_batch` | `chapters` 为完整上游模块名；支持舰队、轮次、时间、全清及 `mode=normal/hard` | 真跑须动作会话，逐关结果过 `sortie-result/1` |
| `account_state` | 只读页面、在图状态；`capture=true` 从设备取帧，存盘帧与现场帧来源明确区分 | 抓设备帧须设备会话 |
| `observe` | `seconds`、`tick_seconds`；可加 `map=main/os` | 只读设备，tick 边界取消 |
| `navigate` | `to`、`rounds`；`max_hops` 已停用，出现即拒绝 | 动作会话，调用上游 `UI.ui_ensure()` |
| `os_state` | `capture`、`detect=map/globe`；海域网格与球面探针 | 只读，不执行寻敌或战斗 |
| `os_action` | `task` 为上游 `opsi_*` 绑定任务，另需 `confirm`、`allow_actions` | 动作会话，原生调度 |
| `event_state` | `folder_prefix`、`only_complete`、`limit`，读取导出章节目录 | 离线清点，不代表已通关 |
| `task_catalog` | 从上游 `args.json` 枚举周期任务与分组 | 只读 |
| `task_schedule` | 读取任务启用状态和下次时间 | 只读，不自行重算上游调度 |
| `config_get` | 读取指定 `keys` | 只读，不能提交本地账号配置 |
| `periodic_plan` | `task/tasks`，勘察上游命令与原生方法绑定 | 只读，不构造设备业务对象 |
| `periodic_preflight` | `task`、`confirm`、`allow_actions`，检查计划与放行条件 | `executes=false`，放行不等于执行 |
| `periodic_run` | 按上游配置执行任务，可使用一次性配置覆盖 | 动作会话及任务确认均须满足 |
| `tool_run` | `instance`、`task` 来自上游 `get_available_func()`；独立工具原生分派 | 动作会话、`allow_actions=true`、`confirm=task` |
| `scheduler_run` | `instance`，运行原生连续调度循环 | 动作会话、设备、工件目录、`allow_actions=true`、`confirm=instance` |

具体字段与约束以各 `*Task.cs` 的输入校验和对应离线回归为准。
`TaskEnd`、绑定、`opsi_*`、活动参数均由上游 `AzurLaneAutoScript.run()` 调度处理，
不能通过“猜一个类然后调用 run”替代；`native_success=true` 只证明原生调度返回。

## 周期任务授权边界

`periodic_run` 同时要求动作会话、`input.allow_actions=true`，以及 `input.confirm` 与 `input.task` 完全一致。
`periodic_preflight` 只检查放行条件，不执行任务。`overrides` 经上游 `config.override()` 作用于本次任务对象；
上游对下次调度时间等状态的正常写入仍会发生。涉及领取、购买或补给时，先核对任务配置与实际资源消耗路径。
原生调度返回 False、抛出异常或记录根因后转为 SystemExit 时，周期任务都保留错误调用栈、
原生日志位置及已保存的失败帧；SystemExit 不退出共享宿主，也不将上一个任务的证据带入下一次执行。

独立工具走 `AzurLaneAutoScript(instance).run(method, skip_first_screenshot=True)`，配置与设备按上游实际访问延迟构造；
工具自行绑定任务配置，不套用周期任务的 `Scheduler.Command`。工具正常返回只证明原生执行完成，不能推导领取或通关。
周期任务、连续调度与独立工具都在动作授权后进入共享数值兼容上下文，不依赖先跑战役或地图探针；
此前关卡显式开启的全清覆盖在该上下文内暂停，返回或异常后恢复，任务仍使用自己的上游配置。
取消在工具返回后的队列边界生效；持续运行的守护工具不会被强杀。尚未迁入当前引擎的工具明确拒绝，不以 UI 菜单代替注册表。

连续调度直接调用上游 `loop/get_next_task/wait_until/run`，保留任务排序、首次重启跳过、配置重载和失败处理。
Core 将取消写成当前任务独享的 `stop.request`，由上游循环、等待或任务切换检查响应；不打断正在执行的战斗。
每次原生分派记录 `dispatch-*.json`，运行状态写 `state.json`，均在队列的 `scheduler-<id>/` 下。
正常停止记为取消，不能当作全部任务成功；失败优先保留。服务器维护等待仍沿用上游重试，停止延迟可能包含该等待。

## 战役与活动

```json
{"tasks":[{"id":"sortie","kind":"campaign_batch","required":true,
  "input":{"chapters":["campaign.campaign_main.campaign_1_1"],
    "max_seconds":1500,"max_rounds":20,"clear_all":false,
    "fleet1":1,"fleet2":0,"submarine":0}}]}
```

章节差异由上游 `MAP / Config / Campaign.run()` 消费；IR 完整度不等于可运行或通关。
`input.mode` 可选 `normal/hard`，省略或 null 沿用账号模式；显式模式经 `config.override()` 只作用于本次加载，
不写入账号的模式字段。章节 Config 合并、导航钩子及运行时仍可按上游语义调整它；工件分别保留请求模式与实际模式。
首次困难开荒使用主线章节模块和 `mode=hard`；上游每日 `hard` 任务要求已解锁周回，不应拿未满足此前提的执行替代开荒流程。
`alashub plan-queue --out events.json --only-complete --limit 5` 生成普通任务队列；
加 `--capture-after` 可在每关后插入实时账号状态任务，证明返页，不改变通关判据。

## 识别与导航边界

导航、待机恢复和弹窗由上游页面图、素材及 `ui_additional()` 处理，不允许逐界面独立适配。
存盘帧的页面识别直接调用同一个上游 `UI.ui_page_appear()`；识别异常独立记录，不折算为未命中。
控件清单包含原生子类和延迟声明，不能以静态清单、构造成功或模板正对照替代实际操作结果。
延迟属性返回的容器会展开其中的原生控件并分别识别；Scroll 无命中时位置为未知，任何子控件异常均保留且不计整体命中。
`account_state` 的页面、在图判据或配置读取发生异常时记为 `failed/upstream_error`，并保留部分观测与错误；正常未命中仍可成功返回只读状态。
`changed=false` 只是 `ui_ensure()` 返回值，不能据此断言没有点击。只读状态任务不会主动退出待机。
识别异常先核对截图颜色、上游素材、配置和完整调用链，再核对功能是否解锁。

地图 `MapDetectionError` 是正常负样本；构造、其他加载异常、预测和逐格语义抽取错误属于执行故障，
不能算作“不是地图”。逐格失败保留 `detected_raw/grid_count`，船标志未知不能当成零；OS 遮罩必须复位。
大世界无“自律寻敌”代表账号前提未满足，不能据此添加页面旁路。

## 验证与完成度

`verify_runtime.py` 覆盖通用队列、导航和观测；各域由 `verify_account_state.py`、`verify_os_state.py`、
`verify_os_action.py`、`verify_event_state.py`、`verify_task_catalog.py`、`verify_task_schedule.py`、
`verify_config_get.py`、`verify_periodic_plan.py`、`verify_native_tools.py` 等离线检查覆盖。
`verify_native_scheduler.py` 对真实原生循环使用合成依赖；`verify_scheduler_control.py` 验证 Core 接单、常驻宿主、停止、关闭及工件。
`verify_native_runtime_compat.py` 为三种执行入口分别启动新进程，验证真实上游数值计算、拒绝路径和全清选项隔离。
地图故障边界由 `verify_map_detect_failures.py` 验证。
`verify_upstream_coverage.py` 覆盖当前全部关卡、四服素材、页面、导航图、控件声明与调度绑定；
源依赖损坏也会失败，证据范围见[全量规则报告](archive/reports/upstream-coverage.md)。
该检查的控件段为四服分别启动干净进程，`verify_native_control_factories.py` 的合成反馈执行任务内原生工厂与控制循环；
已发现但无执行夹具的新增工厂明确失败，不以清单登记充当操作验证。

当前真实样本与剩余缺口统一见[路线](architecture-roadmap.md)，不在此复制阶段状态。
[结果审计](archive/reports/result-evidence.md)与[队列审计](archive/reports/queue-evidence.md)
保留可核对事实；领取、购买、战斗和目标完成不能只凭调度返回认定。
