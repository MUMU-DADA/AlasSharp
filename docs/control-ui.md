# 本地控制界面

这是本地接口原型，尚不具备远程服务器或桌面发行版能力。统一 UI 的技术方向、
AzurPilot 视觉参照与迁移门槛见 [R4 统一 UI 技术调研](r4-ui-architecture.md)。

构建 Release 后运行 `alashub control`，在浏览器打开输出的本机地址（默认
`http://127.0.0.1:8765/`）。端口可用 `--port` 指定。服务只绑定回环地址；写操作
需要页面从本机服务取得的随机令牌，不接受跨站写请求。

界面编辑普通任务队列 JSON，然后以三种会话模式之一运行：

- `dry_run` 是默认值，不配置设备；用于核对队列、任务前置条件和工件。
- `read_only` 配置只读设备会话，不给任务动作授权。
- `actions` 必须在本次请求明确确认；仍由运行时和宿主检查动作授权。界面不解释
  单个地图、页面或任务域的规则。

运行调用现有 `QueueExecution.RunFile`。停止请求走同一 `stop_file` 原语，**在任务
边界生效**；正在执行的上游出击不会被中途打断。`--resume` 只继承任务身份、前序
任务和会话参数都匹配的完成项。页面中的结论、证据和历史运行来自
`RunReport`、逐任务工件与会话日志，不另判通关。完整的静态分享视图仍可用
`python tools/report_html.py <运行目录>` 生成。

运行中界面直接展示已落盘的逐任务工件；`queue.json` 与完整报告在队列结束后
才出现。历史运行可从“运行记录”切换，切回“当前运行”查看本次结果。
运行目录由 `QueueExecution` 的会话启动回调给出，不通过扫描新目录猜测归属。
切换历史记录时丢弃已过期的响应，避免慢请求覆盖用户的新选择。

默认队列草稿和停止请求放在项目忽略目录 `.runtime/control/`，运行工件放在其
`runs/` 子目录。可用 `--workspace` 和 `--artifacts` 把两者分别移到别的本地目录。
这些目录可能包含任务输入、设备配置和原始证据，不应入库或直接对外分享。

离线验收：

```powershell
dotnet build Alas.sln -c Release
python tools/diagnostics/verify_control.py
python tools/diagnostics/verify_control_ui.py --browser
```

该回归通过真实本地 HTTP 入口执行 dry-run 队列、读取报告并请求边界停止，断言
设备配置次数为零；它不证明任何真机动作的业务效果。
