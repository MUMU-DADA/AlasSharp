# 运行时与控制入口

业务编排位于 `src/Alas.Core/Runtime/`。CLI 和前端只传递请求、调用运行时、展示结果。
任务模型见[任务域](tasks.md)，战役结论见[结果合同](result-contract.md)。

## 组成

| 模块 | 职责 |
| --- | --- |
| `AlasSession` / `SessionOptions` | 宿主、设备后端、公共授权、日志和工件目录；同一会话只初始化一次 |
| `QueueExecution` | 队列解析、runner 注册、断点、停止文件及任务调度入口 |
| `ControlWorkspace` | 控制工作区、单队列运行快照、状态与关闭门禁；调用 `QueueExecution` |
| `SchedulerRunTask` / `native_scheduler.py` | 原生连续调度生命周期、边界停止及逐次分派工件；不复制上游调度状态机 |
| `native_telemetry.py` | 只读原生待运行/等待队列与资源记录，捕获 Rich 日志；不重新选择任务 |
| `Alas.Server/ControlServer` | Kestrel HTTP 传输、回环与同源/令牌检查；不引用 UI |
| `CampaignBatchRunner` | 逐关原生执行、合同裁决、批次工件与失败即停 |
| `SessionLog` / `RuntimeErrors` | 结构化日志、统一错误分类与释放 |
| `RunReport` | 只读汇总任务、批次、原始请求和证据完整性 |

## 会话与停止

默认 dry-run，不操作设备。只读设备会话使用 `--run --read-only-device`；
动作会话使用 `--run --allow-actions`。任务输入不能提升会话授权。

失败默认停止后续任务并记录跳过原因；继续执行必须显式使用 `--continue-on-error`。
Ctrl-C 与 `--stop-file` 请求在任务/关卡边界生效，不中断正在执行的上游出击；
观测可在 tick 边界取消。时间上限在上游操作边界检查，不能把超时当成通关。

需要结束本局战役时，在运行目录创建 `withdraw.request`。宿主在下一次战斗前调用
上游 `withdraw()`，结论为 `withdrawn`，与停止整个队列不同。

## 工件与断点

每次运行分配独立目录，每个任务包括失败、跳过项都有工件：

```text
<artifacts>/<运行批次>/
  queue.json / state.json     队列结论与断点身份
  task-<id>.json              输入、前置条件、结果和证据
  index.json / index-2.json   各战役批次的独立索引
  sortie-*.json               逐关合同、步骤和失败详情
  session-log.jsonl           会话结构化日志
  scheduler-<id>/state.json   原生调度阶段、上次观测的任务列表与资源
  scheduler-<id>/native-log.jsonl / logs.json  完整原生日志 / 最近 400 条展示快照
```

`--artifacts`、`--resume-state` 和 `--stop-file` 的相对路径在宿主启动前固定。
多批战役工件不得覆盖；任务通过 `index_artifact` 关联所属批次，混合结果在报告中记为 `mixed`。

`--resume` 只复用请求、会话参数及有序前序任务身份均相同的完成项。修改输入、重排前序任务、
从 dry-run 改为真跑时重新执行；仅在队尾追加任务可保留已有完成项。
`--resume-state <文件>` 必须配合 `--resume`；显式文件缺失、断点损坏或完成状态无效时，启动会话前报错。
旧 id-only 断点不证明请求相同，保守重跑。

Core 日志按运行批次隔离，在每批结束与会话释放时落盘；复用宿主不会带入上一批日志。强杀进程可能缺少完整日志。原始截图、账号配置、设备信息和日志只留在忽略目录；
归档只能做可重复脱敏，保留原件与副本哈希，不改写事实或原始证据来迎合检查。

原生日志按行落盘，展示快照最多每秒四次更新，并在阶段边界刷新。Core 读取工件即可显示运行状态，不并发进入繁忙宿主；UI 以来源内递增编号去重，清空仅影响显示。任务/资源是原生分派或等待边界的观测值，不能把它当作新的调度依据或游戏目标完成证据；未记录的资源显示等待同步。

按实例读取状态时，Core 另提供配置总览：合并配置中的已启用任务、资源记录与模拟器设置，首次运行前和其他实例占用宿主时也可读取，不启动宿主或修改配置。展示排序按 AzurPilot `RuntimeService.overview` 对拍；到期比较使用当前运行时的本机时钟，第三方 NTP 校时尚未接入。运行中以原生观测为准，结束后恢复配置总览；展示列表不参与任务选择。

## 报告与本地控制台

```powershell
alashub report --run <运行目录> --json <报告.json>
alashub runs --artifacts <工件根目录> --limit 10
alashub control --port 8765
python tools/report_html.py <运行目录>
```

报告保留完整任务输入、`required`、结果及原始证据；缺文件、坏 JSON、合同违例和索引矛盾写入 findings。
报告生成成功只证明可读取，不代表任务成功；dry-run 和跳过不能被展示成业务失败。
CLI 的 `[任务证据]` 摘要属于 `queue` 入口，单批 `campaign` 使用合同和批次输出。

本地控制台使用 .NET 10 Kestrel，仅监听 `127.0.0.1`，忽略环境中的 URL/端点覆盖配置。可由
`alashub control` 或独立的 `Alas.Server` 进程启动；独立进程用 `--root`、`--repo`、`--data`、
`--tools`、`--workspace`、`--artifacts` 和 `--port` 明确运行布局，避免从发布目录猜测源码根。
请求校验 Host/端口，带 Origin 的请求必须同源；原生客户端可不带 Origin。写操作要求本次服务的 `X-Alas-Token`。
页面编辑普通队列 JSON，
提供 dry-run、只读与显式动作授权三种模式，通过 `QueueExecution.RunFile` 执行。
运行中展示已写入的任务工件，结束后生成完整报告；停止使用同一边界停止原语。
草稿与工件默认位于 `.runtime/control/`，可用 `--workspace`、`--artifacts` 调整。
独立服务可用 `--ui-root <预构建 wwwroot>` 同源托管 Avalonia WASM；未指定时仍提供旧控制页。
静态目录必须有 `index.html`，拒绝目录浏览、路径穿越和链接逃逸，只读取有限 MIME 类型；服务仍只监听
回环地址，静态托管不等于远程认证或 HTTPS。业务依赖（`tools`、`data`、上游仓库和 Python 环境）
仍需由发布布局显式提供。
`tools/publish_server.ps1` 使用项目内 SDK 和 NuGet 缓存发布指定 RID 的服务器，可选复制已生成的
`.runtime/ui-publish/browser/wwwroot`；它不打包 Python、上游仓库或设备后端，启动时仍需显式传入这些路径。

关闭服务时先原子拒绝新运行/草稿、请求边界停止，再等待已接受的队列和日志落盘；HTTP 断连与 HTTP 关闭期限不会取消正在进行的上游出击。
停止先通过每次运行独立的内存信号进入现有边界取消入口，再写停止标记；标记写入失败会报告错误，仍等待队列退出，不能绕过落盘。
进程强杀仍不保证工件完整。`active.status=completed` 仅表示工作线程正常结束，业务结果必须读取 `report.queue_outcome`。

当前本地 API 合同（JSON，响应 `no-store`）：

| 接口 | 行为 |
| --- | --- |
| `GET /api/state` | `token`、`queue`、`active`、`report`、`live_tasks`、`recent_logs`、`runs` |
| `GET /api/events` | `control-state/1` SSE 完整状态快照，沿用同源/回环限制；可带 `Last-Event-ID` |
| `GET /api/report?stamp=` | 指定运行报告；标识只允许 ASCII 字母数字、`-`、`_`，不存在返回 404 |
| `POST /api/queue` | 保存 `{queue: ...}` 草稿，成功 200；不改变已接受运行的快照 |
| `POST /api/run` | `{queue, mode?, confirm_actions?, serial?, max_seconds?, max_rounds?, resume?, continue_on_error?}`；默认 dry-run，接受返回 202 |
| `POST /api/stop` | 请求边界停止，成功 200；没有活动队列返回 409 |
| `GET/PATCH /api/settings` | 部署设置 schema / `{values: {...}}` 字段事务；读取可带 `language` |
| `GET /api/startup?instance=`、`POST /api/startup` | 读取或保存 `{instance, enabled}` 启动运行列表，不立即执行任务 |

`mode` 仅允许 `dry_run`、`read_only`、`actions`，动作模式须 `confirm_actions=true`。
保存/运行请求必须为 JSON 对象，有 `Content-Length` 且不超过 1 MiB；不接受 chunked。
输入错误返回 400，授权/来源错误返回 403，已有队列或服务关闭中的写请求返回 409，错误对象包含 `error`。
一次服务只运行一个队列；任务输入、结果合同和授权语义仍由运行时决定。

`Alas.Contracts` 定义请求与状态信封，`Alas.Client/ControlClient` 提供共享 HTTP 客户端，两者不引用 UI、Core 或 Python。
客户端先 `GetStateAsync()` 获取本次服务令牌，再保存队列、开始运行或请求停止；`StartRunAsync()` 只确认接受，最终结论读取状态与报告。
队列、报告、任务证据保持原始 JSON；`runs` 是包含 `artifacts_root/exists/returned/runs` 的对象，不是裸数组。
序列化使用生成元数据，请求提供明确 UTF-8 字节长度。默认传输禁用重定向；注入自定义 `HttpClient` 时也必须禁用自动重定向和重试。
取消 HTTP 请求或释放客户端不会发送停止命令；网络错误可能发生在接单之后，应重新查询状态，不自动重放写请求。
服务重启后需重新读取状态获取令牌。普通 JSON 请求有覆盖响应头与正文的 30 秒期限，可在构造客户端时调整；超时不重发写请求。
当前没有幂等请求键。浏览器 UI 通过该客户端调用 Core；桌面在同一进程直接调用 Core。

部署设置由 `DeploySettingsWorkspace` 直接读写执行根目录的 `config/deploy.yaml`，不等待或启动 Python 宿主。
`tools/export_deploy_settings.py` 从 AzurPilot 声明、Windows/Unix 模板和五种翻译生成嵌入资源；`--check` 检查来源哈希和内容漂移，不读取个人部署文件。
8 个分组与字段类型沿用上游；密码只写不读，空密码保留原值，普通保存忽略 `Run`，启动运行通过独立接口修改。整批校验后按最新文件合并，原子替换；演示模式拒绝修改。
保留现有注释和未知字段，补写旧文件缺少的模板字段；拒绝换行注入。Windows 短暂读锁重试同一次替换，持续占用则保留原文件并报告失败。只读不触发上游部署器的地理镜像选择或自动改写。
此接口当前证明配置持久化，尚未完成启动运行消费、部署器迁移、远程服务、认证和更新/重启接线；旧 Python 部署器按自身模板重写时也可能丢失新增字段，须在接入其消费者时统一修复并验证。

## 上游服务缓存与单任务

任务编辑器按所选实例保存参数，单任务由 Core 读取上游 `Scheduler.Command` 后进入 `periodic_run` 队列。
实例导入只接收名称和 JSON 文本，暂存至 `config/import` 后显式创建实例；不会把暂存源列为运行实例。
实例列表排除损坏或缺少 `Alas` 段的文件；删除仍要求当前修订号并移动到备份目录。
设备初始化使用该实例配置和截图/输入后端；同一会话固定实例及设备初始化参数，变更时明确拒绝并要求关闭会话。
独立工具和连续调度器已有统一队列入口及离线回归，真机业务效果仍须分别验证。

统计和报告继续调用上游服务。用 `tools/cache_upstream_services.py --source <AzurPilot目录>`
缓存明确列出的 23 个依赖文件到 `.runtime/engine`，`--check` 校验来源与逐文件哈希；不同的已有文件会拒绝覆盖。
缓存清单保留在运行时目录，不复制账号配置、日志或设备证据。Lua 解析器按
`tools/upstream-services-requirements.txt` 下载到 `.runtime/wheels` 后用 `--no-index --find-links` 安装。

五类统计报告、指挥喵 JSON 报告/清理和策略校验有隔离数据回归；这不证明游戏任务已采集相应记录。
指挥喵报告是机器共享产物，清理会影响所有实例。掉落统计的 `azurstats` 实现仍待整体迁移，当前返回明确不可用原因。
当前 Python 宿主固定使用项目内 `.runtime/engine`；Core 拒绝不同执行根目录，防止读 A 配置却执行 B 仓库。

状态流由 `WatchStateAsync(lastCursor, token)` 消费：每条消息完整替换显示状态（含日志窗口），不能把 `recent_logs` 当增量追加。
游标为服务实例标识与递增观测版本。初次或过期/异实例游标返回 `reset`；游标仍是当前版本时返回 `snapshot`。断线/EOF 后调用方用最后游标重新订阅，客户端不自动重连或重放命令。
所有观察者共用一个至多每秒采样器，无订阅时不扫描；每订阅只缓存一份待发快照，慢客户端可跳过中间版本，不能据此还原全部事件。
服务每 15 秒发送空闲心跳，写入超过 10 秒的客户端被断开；客户端每次网络读取有默认 30 秒空闲期限。服务关闭立即结束订阅，再独立等待队列释放，不保证 SSE 最后一帧送达。
`recent_logs` 从每次运行的内存日志读取最近 80 条，包含运行中记录；它是 C# 运行时日志窗口，不代表完整 Python 控制台输出。最终 JSONL 和任务报告仍是完整审计入口。
观察工件时使用允许并发写入的文件共享方式，避免 Windows 读锁打断断点写入；半写 JSON 仍产生原有 `unreadable_artifact` 等发现，运行结束后重新查询最终证据。

## 验证

使用项目 Python 执行 `tools/diagnostics/` 下的以下脚本：

| 范围 | 验收脚本 |
| --- | --- |
| 会话、取消、输入和断点 | `verify_runtime.py`、`verify_cli_errors.py`、`verify_stop.py` |
| 工件与报告 | `verify_artifact_paths.py`、`verify_report.py`、`verify_report_html.py` |
| 控制服务 | `verify_control.py`、`verify_control_shutdown.py` |
| 共享客户端 | `verify_control_client.py`（真实 HTTP、禁用反射序列化、传输错误及取消） |
| 状态事件流 | `verify_control_events.py`（共享采样、游标、运行中日志、断流/重启及工件读取） |
| 旧控制页静态结构 | `verify_control_ui.py`（不打开浏览器） |
| 独立服务与静态网页 | `verify_server_static.py`（临时目录、无窗口） |
| 结构边界与隐私 | `verify_architecture.py`、`verify_privacy.py` |
| 上游服务与实例绑定 | `verify_upstream_services.py`、`verify_instance_device_binding.py`（隔离数据/替身，无设备） |
| 部署设置与启动列表 | `verify_deploy_settings.py`（上游原始函数对拍、并发/原子保存、真实 HTTP 与类型化客户端） |

控制台离线回归通过真实 HTTP 执行 dry-run，断言设备配置次数为零；不证明真实游戏任务效果。
关闭回归通过公开 shutdown token 实测延迟 POST 拒绝、停止标记 IO 失败和完整落盘；系统 Ctrl-C/SIGTERM 与长于 HTTP 关闭期限的实战仍需分别验收。
客户端回归验证已接受队列在客户端释放后完成、dry-run 结果原样保留、边界停止和全部工件，以及不跟随 307、不重试写请求；不代替 WASM 浏览器传输或系统输入验收。
状态流回归含 200 个 dry-run 任务的并发观测、慢订阅合并、采样失败恢复、实际服务重启和关闭；不证明中间状态无损重放，也未验证 WASM 流式传输。
真机记录见[队列审计](archive/reports/queue-evidence.md)。
