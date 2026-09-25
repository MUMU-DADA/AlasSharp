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
Alas.Server queue --file queue.json --artifacts runs
Alas.Server queue --file queue.json --run --read-only-device --serial <设备> --screenshot adb --control ADB
Alas.Server queue --file queue.json --run --allow-actions --serial <设备> --screenshot adb --control ADB
```

第一条为默认 dry-run；后两条分别显式授权只读设备和动作会话。JSON 不能把 dry-run 或只读会话提升为动作会话。
`run`、`goto` 已弃用，只提供迁移提示。

## 已注册任务

| kind | 职责与主要输入 | 授权边界 |
| --- | --- | --- |
| `campaign_batch` | `chapters` 为完整上游模块名；支持舰队、轮次、时间、全清及 `mode=normal/hard` | 真跑须动作会话，逐关结果过 `sortie-result/1` |
| `account_state` | 只读页面、在图状态；`capture=true` 从设备取帧，存盘帧与现场帧来源明确区分 | 抓设备帧须设备会话 |
| `observe` | `seconds`、`tick_seconds`；可加 `map=main/os` 和完整 `chapter` 模块名 | 只读设备，tick 边界取消 |
| `navigate` | `to`、`rounds`；`max_hops` 已停用，出现即拒绝 | 动作会话，调用上游 `UI.ui_ensure()` |
| `os_state` | `capture`、`detect=map/globe`；海域网格与球面探针 | 只读，不执行寻敌或战斗 |
| `os_action` | `task` 为上游 `opsi_*` 绑定任务，另需 `confirm`、`allow_actions` | 动作会话，原生调度 |
| `event_state` | `folder_prefix`、`only_complete`、`limit`，读取导出章节目录 | 离线清点，不代表已通关 |
| `task_catalog` | 从上游 `args.json` 枚举周期任务与分组 | 只读 |
| `task_schedule` | 读取存盘任务启用值和下次时间 | 只读快照，未应用上游默认、锁定字段或迁移，不是有效调度计划 |
| `config_get` | 读取指定 `keys` | 只读，不能提交本地账号配置 |
| `periodic_plan` | `task/tasks`，勘察上游命令与原生方法绑定 | 只读，不构造设备业务对象 |
| `periodic_preflight` | `task`、`confirm`、`allow_actions`，检查计划与放行条件 | `executes=false`，放行不等于执行 |
| `periodic_run` | 按上游配置执行任务，可使用一次性配置覆盖 | 动作会话及任务确认均须满足 |
| `tool_run` | `instance`、`task` 来自上游 `get_available_func()`；独立工具原生分派 | 动作会话、`allow_actions=true`、`confirm=task` |
| `scheduler_run` | `instance`，运行原生连续调度循环 | 动作会话、设备、工件目录、`allow_actions=true`、`confirm=instance` |

具体字段与约束以各 `*Task.cs` 的输入校验和对应离线回归为准。
`TaskEnd`、绑定、`opsi_*`、活动参数均由上游 `AzurLaneAutoScript.run()` 调度处理，
不能通过“猜一个类然后调用 run”替代；`native_success=true` 只证明原生调度返回。

`task_schedule` 工件以 `semantics=stored_config` 标识原始快照，`NextRun` 不重算。
显式 `Scheduler.Enable` 只接受 JSON 布尔值；字符串、数字、显式 null 或容器使整个读取失败，并报告字段路径。
缺任务、缺 Scheduler 或缺 Enable 时，enable 为未知 null；只有缺 Scheduler 才计入 `no_scheduler_count`。
原始 false 即使对应上游锁定开关也保持 false，不能据此断言原生任务已禁用。过滤和截断前校验全部条目。
Core 校验来源、必需字段、计数和条目一致性；矛盾响应保留 `host_response` 并记为合同失败，不进入断点完成列表。

## 周期任务授权边界

`periodic_run` 同时要求动作会话、`input.allow_actions=true`，以及 `input.confirm` 与 `input.task` 完全一致。
`periodic_preflight` 只检查放行条件，不执行任务。`overrides` 先按当前原生配置的 `bound/args` 校验全部字段，
复用 `ConfigService` 的类型、选项、只读与组合规则，再经 `parse_value()` 转为原生值；例如开关必须是 JSON 布尔值，
日期字符串按原生语义转为 datetime。任何无效项都在 `config.override()` 与获取设备之前拒绝，不能部分应用。
有效覆盖只作用于本次任务对象；配置构造器的正常迁移、上游对下次调度时间等状态的正常写入仍会发生。
涉及领取、购买或补给时，先核对任务配置与实际资源消耗路径。
原生返回 `decision=ran` 只有在任务名、实例、动作授权、确认、构造、运行、`native_success` 及目标模块/类/方法/命令全部与请求一致，且没有错误字段时，才记为任务成功；矛盾或缺失字段统一记为 `contract_violation`，原始响应和 `response_violations` 原样进入工件。失败任务不会写入队列断点的 `completed`，因此恢复时不会被跳过。
原生调度返回 False、抛出异常或记录根因后转为 SystemExit 时，周期任务都保留错误调用栈、
原生日志位置及已保存的失败帧；SystemExit 不退出共享宿主，也不将上一个任务的证据带入下一次执行。

独立工具走 `AzurLaneAutoScript(instance).run(method, skip_first_screenshot=True)`，配置与设备按上游实际访问延迟构造；
工具自行绑定任务配置，不套用周期任务的 `Scheduler.Command`。工具正常返回只证明原生执行完成，不能推导领取或通关。
工具的成功响应也校验请求身份、授权、运行标记和原生计划/实际目标的一致性；错误或矛盾响应记为 `contract_violation`，
保留原始证据，不能进入断点完成列表。方法映射仍来自原生注册表，Core 不另建工具映射表。
周期任务、连续调度与独立工具都在动作授权后进入共享数值兼容上下文，不依赖先跑战役或地图探针；
此前关卡显式开启的全清覆盖在该上下文内暂停，返回或异常后恢复，任务仍使用自己的上游配置。
上下文同时让嵌套 `ModuleBase` 设备工厂复用会话设备，并逐任务恢复 `DaemonBase` 覆盖的卡死/点击检测方法；
连续调度每次分派结束即恢复，不能等整个循环结束。设备无关的工具保持惰性获取。
取消在工具返回后的队列边界生效；持续运行的守护工具不会被强杀。尚未迁入当前引擎的工具明确拒绝，不以 UI 菜单代替注册表。
工具的失败摘要包含原生调用栈尾部；设备配置恢复失败会追加到原始错误，不能覆盖先前根因和失败帧。

连续调度直接调用上游 `loop/get_next_task/wait_until/run`，保留任务排序、首次重启跳过、配置重载和失败处理。
每轮调度按原生独立进程语义初始化类级囤积状态，结束或异常时恢复调用前状态；同轮配置重载和逐任务分派不重置它。
`verify_scheduler_hoarding.py` 用真实 Config 和调度循环覆盖连续运行、原状态为 False 及异常恢复共 5 组；
固定时钟下观察应有的 300 秒等待，不实际睡眠、不操作设备。
Core 将取消写成当前任务独享的 `stop.request`，由上游循环、等待或任务切换检查响应；不打断正在执行的战斗。
每次原生分派记录 `dispatch-*.json`，运行状态写 `state.json`，均在队列的 `scheduler-<id>/` 下。
正常停止记为取消，不能当作全部任务成功；失败优先保留。服务器维护等待仍沿用上游重试，停止延迟可能包含该等待。
失败分派的根因与调用栈同时保存在逐分派工件和队列可见的汇总中，汇总登记最后失败分派编号与所有已保存失败帧。
外层 `SystemExit`、配置恢复或工件收尾错误不能抹掉原生原因；每次调度独立收集，后续会话不继承旧失败证据。

## 战役与活动

活动清点与队列生成共用结构筛选：条目须同时具有导出的 `CampaignPresent` 和 `MapPresent`。
辅助 Config/基类模块保留在导出索引用于溯源，但不能生成出击任务；`only_complete` 是此后另加的计划完整度筛选。
候选结构正确不代表活动当前开放、账号可达或缺失的上游依赖已恢复，运行仍交给原生加载器判定。

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
`Alas.Server plan-queue --out events.json --only-complete --limit 5` 生成普通任务队列；
加 `--capture-after` 可在每关后插入实时账号状态任务，证明返页，不改变通关判据。

## 识别与导航边界

导航、待机恢复和弹窗由上游页面图、素材及 `ui_additional()` 处理，不允许逐界面独立适配。
存盘帧的页面识别直接调用同一个上游 `UI.ui_page_appear()`；识别异常独立记录，不折算为未命中。
控件清单包含原生子类和延迟声明，不能以静态清单、构造成功或模板正对照替代实际操作结果。
延迟属性返回的容器会展开其中的原生控件并分别识别；Scroll 无命中时位置为未知，任何子控件异常均保留且不计整体命中。
`account_state` 的页面、在图判据或配置读取发生异常时记为 `failed/upstream_error`，并保留部分观测与错误；正常未命中仍可成功返回只读状态。
队列的 `boundary_state` 在设备会话中读取已存在上游设备的最后缓存帧，在离线会话中读取宿主帧，
以 `frame_source=device_cached/host_frame` 区分；没有设备缓存就记为不可用，不回退到旧离线帧。
边界读取不创建设备、不新增抓帧或点击，只证明最后观测状态；需要现场状态仍显式提交 `account_state(capture=true)`。
显式加载或抓帧失败会清除旧宿主帧，避免随后把旧画面当作成功读取。
`changed=false` 只是 `ui_ensure()` 返回值，不能据此断言没有点击。只读状态任务不会主动退出待机。
导航通过参数校验并取得会话设备后，在任务入口清空上一任务的 stuck/click 记录；任务内部继续使用原生检测。
离线回归直接运行原生 UI、Device 和 Timer，覆盖旧记录隔离及当前导航的卡死/重复点击报错，不代表真机页面验收。
导航异常的调用栈尾部同时进入错误摘要和逐段证据；原生 `SystemExit` 作为失败返回，不退出共享宿主。
存在运行工件目录时，Core 为每段导航分配独立失败帧路径，仅在失败后保存设备已有的最后一帧，
标记为 `last_cached_device_frame`，不重新截图或点击；无缓存时明确 unavailable。保存错误追加并保留原始导航原因，
已有文件不覆盖，所有保存成功的帧登记在该段 `failure_frames` 中。路径由运行时决定，任务输入不能注入路径或未知字段。
识别异常先核对截图颜色、上游素材、配置和完整调用链，再核对功能是否解锁。
`observe` 的可选 `chapter` 随 `map` 透传给宿主 `_map_config(chapter)`，由原生章节 Config 合并决定识别参数，
不用导出 JSON 重建规则。省略时沿用通用配置；工件记录所用章节，未检出与执行异常仍分别计数。

地图 `MapDetectionError` 是正常负样本；构造、其他加载异常、预测和逐格语义抽取错误属于执行故障，
不能算作“不是地图”。逐格失败保留 `detected_raw/grid_count`，船标志未知不能当成零；OS 遮罩必须复位。
大世界无“自律寻敌”代表账号前提未满足，不能据此添加页面旁路。

## 验证与完成度

`verify_runtime.py` 覆盖通用队列、导航和观测；各域由 `verify_account_state.py`、`verify_os_state.py`、
`verify_os_action.py`、`verify_event_state.py`、`verify_task_catalog.py`、`verify_task_schedule.py`、
`verify_config_get.py`、`verify_periodic_plan.py`、`verify_native_tools.py` 等离线检查覆盖。
`verify_account_state_cache.py` 覆盖原生设备/宿主帧分离、无缓存与失败失效，以及 Core 在动作、只读设备和 dry-run 会话中的来源参数。
`verify_periodic_overrides.py` 使用原生默认配置、绑定和 dispatcher，验证错误覆盖在设备前拒绝、原生值转换、
混合输入原子性、继承字段及已归档任务输入兼容；设备和末端领域方法是替身，不读取账号配置或证明真机业务完成。
`verify_periodic_run_result.py` 通过真实 Core 队列覆盖周期任务和大世界动作的 52 个一致性/失败断点场景；
原生成功响应必须完整且与请求、目标一致，冲突响应保留宿主证据并拒绝进入 `completed`。该检查只验证分派合同，
不把 `native_success` 当作领取、购买或战役通关。
`verify_native_dispatch_catalog.py` 从原生参数和工具目录发现全部入口，使用真实 ConfigUpdater、配置绑定、
`AzurLaneAutoScript.run()` 与任务方法，对照领域签名和 AST 核对调用参数；每个入口覆盖正常返回、普通 False、
TaskEnd 和重试异常，并验证原生 Restart 配置写入、设备恢复及失败证据隔离。
领域类/函数和设备是离线替身，配置来自默认值并限制在临时目录，不读取账号配置；
它不验证领域构造器或内部流程，不把调度通过当作业务完成，见[全量分派报告](archive/reports/native-dispatch.md)。
`verify_native_scheduler.py` 对真实原生循环使用合成依赖；`verify_scheduler_control.py` 验证 Core 接单、常驻宿主、停止、关闭及工件。
`verify_native_runtime_compat.py` 为三种执行入口分别启动新进程，验证真实上游数值计算、拒绝路径和全清选项隔离。
`verify_native_campaign_runtime.py` 补验任务内首次战役调用的舰队/相机/结算兼容，使用真实原生加载器和 run；
16 项检查覆盖继承配置、MAP 身份和正常/异常/嵌套作用域恢复，战斗端点仍为替身，不证明通关。
`verify_native_tool_devices.py` 再深入全部工具的真实构造器，包括函数型入口的内部构造和 DaemonBase，
仅替换末端 run 与物理设备构造；验证任务配置初始化、显式设备/内部工厂共用会话、未配置时拒绝、
正常/异常退出及原有卡死/多次点击检测恢复。它仍不执行工具业务动作。
`verify_native_task_devices.py` 保留原生生产规划器与嵌套扫描器构造器，16 个场景验证缓存有无、正常/异常、
继承/自定义检测方法和连续两次调度，I/O 端点使用替身。`verify_screenshot_auto.py` 保留原生配置绑定、截图分派和基准写回，
验证自动选择结果不被永久 `auto` 覆盖重置、显式选择保持及下一任务重载后设备复用；不测真实后端速度。
缺省导航/抓帧入口同样只临时覆盖会话设备参数；6 组/18 个原生观测与 14 个实例绑定场景通过。
S3 与地图配置从当前设备所属实例重新加载原生配置，不带入上一任务的临时覆盖；无设备时才使用默认实例。
S3 在原生加载器和首帧前核对设备身份，截图/控制后端不写回账号；四个原生配置/加载器场景验证此边界。
真实两任务队列复验后账号配置字节不变；此前后端写回的失败断言及配置前后副本保留，并经无并发修改比对还原。
`verify_native_tools.py` 的 47 个 Core 队列场景验证工具响应合同和失败工件。
`task_catalog` 从所选上游的 `task.yaml` 读取完整分组成员，与生成的 `args.json` 任务集合核对；
Core 工件的 `group_tasks` 不受展示用 `limit` 截断。缺文件、结构错误和集合漂移均失败，保留可读到的证据。
`verify_task_catalog.py` 对照当前 9 组/68 个成员，并覆盖四类损坏来源和真实 Core 工件；目录读取不证明任务业务执行。
地图故障边界由 `verify_map_detect_failures.py` 验证。
`verify_upstream_coverage.py` 覆盖当前全部关卡、四服素材、页面、导航图、控件声明与调度绑定；
源依赖损坏也会失败，证据范围见[全量规则报告](archive/reports/upstream-coverage.md)。
该检查的控件段为四服分别启动干净进程，`verify_native_control_factories.py` 的合成反馈执行任务内原生工厂与控制循环；
已发现但无执行夹具的新增工厂明确失败，不以清单登记充当操作验证。

当前真实样本与剩余缺口统一见[路线](architecture-roadmap.md)，不在此复制阶段状态。
[结果审计](archive/reports/result-evidence.md)与[队列审计](archive/reports/queue-evidence.md)
保留可核对事实；领取、购买、战斗和目标完成不能只凭调度返回认定。
