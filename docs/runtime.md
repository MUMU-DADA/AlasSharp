# 运行时与控制入口

业务编排位于 `src/Alas.Core/Runtime/`。CLI 和前端只传递请求、调用运行时、展示结果。
任务模型见[任务域](tasks.md)，战役结论见[结果合同](result-contract.md)。

## 组成

| 模块 | 职责 |
| --- | --- |
| `AlasSession` / `SessionOptions` | 宿主、设备后端、公共授权、日志和工件目录；同一会话只初始化一次 |
| `QueueExecution` | 队列解析、runner 注册、断点、停止文件及任务调度入口 |
| `ControlWorkspace` | 控制工作区、单队列运行快照、状态与关闭门禁；调用 `QueueExecution` |
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
```

`--artifacts`、`--resume-state` 和 `--stop-file` 的相对路径在宿主启动前固定。
多批战役工件不得覆盖；任务通过 `index_artifact` 关联所属批次，混合结果在报告中记为 `mixed`。

`--resume` 只复用请求、会话参数及有序前序任务身份均相同的完成项。修改输入、重排前序任务、
从 dry-run 改为真跑时重新执行；仅在队尾追加任务可保留已有完成项。
`--resume-state <文件>` 必须配合 `--resume`；显式文件缺失、断点损坏或完成状态无效时，启动会话前报错。
旧 id-only 断点不证明请求相同，保守重跑。

日志在会话释放时落盘，强杀进程可能缺少完整日志。原始截图、账号配置、设备信息和日志只留在忽略目录；
归档只能做可重复脱敏，保留原件与副本哈希，不改写事实或原始证据来迎合检查。

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

本地控制台使用 .NET 10 Kestrel，仅监听 `127.0.0.1`，忽略环境中的 URL/端点覆盖配置。
请求校验 Host/端口，带 Origin 的请求必须同源；原生客户端可不带 Origin。写操作要求本次服务的 `X-Alas-Token`。
页面编辑普通队列 JSON，
提供 dry-run、只读与显式动作授权三种模式，通过 `QueueExecution.RunFile` 执行。
运行中展示已写入的任务工件，结束后生成完整报告；停止使用同一边界停止原语。
草稿与工件默认位于 `.runtime/control/`，可用 `--workspace`、`--artifacts` 调整。
它尚不支持远程部署；目标方案见[统一 UI](r4-ui-architecture.md)。

关闭服务时先原子拒绝新运行/草稿、请求边界停止，再等待已接受的队列和日志落盘；HTTP 断连与 HTTP 关闭期限不会取消正在进行的上游出击。
停止先通过每次运行独立的内存信号进入现有边界取消入口，再写停止标记；标记写入失败会报告错误，仍等待队列退出，不能绕过落盘。
进程强杀仍不保证工件完整。`active.status=completed` 仅表示工作线程正常结束，业务结果必须读取 `report.queue_outcome`。

当前本地 API 合同（JSON，响应 `no-store`）：

| 接口 | 行为 |
| --- | --- |
| `GET /api/state` | `token`、`queue`、`active`、`report`、`live_tasks`、`recent_logs`、`runs` |
| `GET /api/report?stamp=` | 指定运行报告；标识只允许 ASCII 字母数字、`-`、`_`，不存在返回 404 |
| `POST /api/queue` | 保存 `{queue: ...}` 草稿，成功 200；不改变已接受运行的快照 |
| `POST /api/run` | `{queue, mode?, confirm_actions?, serial?, max_seconds?, max_rounds?, resume?, continue_on_error?}`；默认 dry-run，接受返回 202 |
| `POST /api/stop` | 请求边界停止，成功 200；没有活动队列返回 409 |

`mode` 仅允许 `dry_run`、`read_only`、`actions`，动作模式须 `confirm_actions=true`。
保存/运行请求必须为 JSON 对象，有 `Content-Length` 且不超过 1 MiB；不接受 chunked。
输入错误返回 400，授权/来源错误返回 403，已有队列或服务关闭中的写请求返回 409，错误对象包含 `error`。
一次服务只运行一个队列；任务输入、结果合同和授权语义仍由运行时决定。

## 验证

使用项目 Python 执行 `tools/diagnostics/` 下的以下脚本：

| 范围 | 验收脚本 |
| --- | --- |
| 会话、取消、输入和断点 | `verify_runtime.py`、`verify_cli_errors.py`、`verify_stop.py` |
| 工件与报告 | `verify_artifact_paths.py`、`verify_report.py`、`verify_report_html.py` |
| 控制服务 | `verify_control.py`、`verify_control_shutdown.py` |
| 旧控制页静态结构 | `verify_control_ui.py`（不打开浏览器） |
| 结构边界与隐私 | `verify_architecture.py`、`verify_privacy.py` |

控制台离线回归通过真实 HTTP 执行 dry-run，断言设备配置次数为零；不证明真实游戏任务效果。
关闭回归通过公开 shutdown token 实测延迟 POST 拒绝、停止标记 IO 失败和完整落盘；系统 Ctrl-C/SIGTERM 与长于 HTTP 关闭期限的实战仍需分别验收。
真机记录见[队列审计](archive/reports/queue-evidence.md)。
