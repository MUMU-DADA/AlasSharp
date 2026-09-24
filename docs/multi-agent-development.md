# 多 agent 本地协作规范

本规范适用于本项目的并行编码与审阅。`master` 工作树由主 agent 集成；每个并行任务在独立 Git worktree 和 `codex/<任务>` 分支完成。项目的上游规则、结果合同、隐私和真机证据边界仍以 `AGENTS.md` 为准。

## 任务分配

主 agent 派发任务时写清目标、基线提交、允许修改的文件、不得修改的文件、验收命令及交付形式。同一时间一份文件只有一个写入者；跨模块接口由主 agent 先定合同，再分配互不重叠的实现。发现任务范围需要扩大或基线已变化时，子 agent 先报告，不自行修改别人的文件。

轻量 agent 优先处理文档与代码事实核对、静态检查、独立夹具或局部离线回归。可给它只读审计复杂模块，但上游调度、S3 战役流程、页面/地图规则、`sortie-result/1`、素材导出结构和设备动作的最终实现与判断由主 agent 复核。没有可独立验收的切片时不为凑并行而拆任务。

## 主 agent 与子 agent 的交互

主 agent 先创建工作树，再通过协作工具的 `spawn_agent` 派发可读的纯文本任务。任务消息至少包含以下字段；子 agent 在收到后回复已理解的范围和当前分支，避免在错误目录开工：

```text
目标：一个可独立验收的具体结果
工作树与分支：.worktrees/docs-audit，codex/docs-audit
基线：派发时的提交哈希
仅可修改：明确的文件或模块
验收：要运行的命令与预期判据
限制：无设备、无主工作树修改、无推送；其他任务专属边界
交付：提交哈希、文件清单、测试结果、未验证项
```

子 agent 在完成一个可检查的阶段或遇到阻塞时，用 `send_message` 向主 agent 报告证据和下一步；不直接向用户提问，也不等待用户回答。主 agent 用 `send_message` 及时纠偏，已结束的任务需要补修时用 `followup_task`；用户改变方向时由主 agent 通知或中断相关 agent。主 agent 使用 `wait_agent` 等待任务结果，避免反复读取未变化的状态。若任务消息不可读或只有不透明编码，子 agent 立即报告，主 agent 重发可读文字，不能猜测任务内容。

交付以 worktree 中的 Git 状态和提交为准。子 agent 的最终消息写明分支、提交哈希、具体改动、实际测试输出摘要和剩余风险；原始设备日志与个人路径不放进消息或提交。主 agent 自己检查 `git status`、`git show` 和改动文件，再逐项集成并复验，不能只凭子 agent 声称通过。面向用户的进度、冲突和结果由主 agent 统一说明。

### 第三方 agent：共享文件信箱

无法调用内置协作工具的第三方 agent 使用**主工作树**的 `.runtime/agent-mailbox/<任务>/` 通信；先读该目录 `inbox/` 的任务消息。路径相对主工作树，不是各自 worktree；可通过 `git worktree list --porcelain` 定位。

- 主 agent 只写 `inbox/<递增序号>.md`；第三方 agent 只写 `outbox/<递增序号>.md` 和 `status.json`。消息用 UTF-8，先写临时文件再原子重命名；不覆盖对方文件，收件后回执引用消息编号。
- 任务沿用上述目标、基线、范围、验收和交付字段。回执含已理解边界、分支、当前提交、完成项、验证命令/证据相对路径、阻塞和下一步；`status.json` 至少含 `updated_at`、`state`、`last_inbox`、`branch`、`commit`，状态为 `working`、`blocked`、`ready_for_review` 或 `done`。
- 第三方 agent 开工前、每个阶段完成或阻塞时检查信箱并更新状态；长任务至少每 5 分钟一次。主 agent 约每 20 分钟监收，在阶段完成或失败时复查，优先推进自身任务；重复未变的状态无需打扰用户。未收到回执不能宣称对方已开始。
- 所有编码仍在分配的独立 worktree，文件信箱只是通信通道。主 agent 复核实际差异和证据后给出修改要求或集成回执；第三方 agent 不接触后端、设备或其他负责范围之外的文件，除非任务明确分配。
- 通信文件、截图和日志留在忽略目录，不加入提交。任务结束且证据已妥善保留后，再清理信箱和工作树；不提前删除活动 agent 的消息。

## 工作树隔离

从项目根目录创建，任务名用小写字母、数字和短横线。`.worktrees/` 已忽略，工作树内的 `.runtime/`、`data/`、`runs/` 也不入库。

```powershell
git status --short --branch
git worktree list --porcelain
git worktree add -b codex/docs-audit .worktrees/docs-audit HEAD
git -C .worktrees/docs-audit status --short --branch
```

一个 worktree 只交给一个写入者。子 agent 只在自己的目录编辑、测试和提交；不在共享 `master`、其他 worktree 或仓库外的上游克隆里写入。主 agent 的未提交改动不会自动出现在新 worktree，任务依赖这些改动时先由主 agent 提供可引用的提交。不要通过复制工作目录同步代码。

各 worktree 的构建输出和临时文件留在自己的忽略目录；`Directory.Build.props` 已将 NuGet 包固定到各自的 `.runtime/nuget/packages`，避免写入用户级缓存。离线包源也须放在该 worktree 的忽略目录，不能把主工作树的本机配置或账号资料复制过去。原始设备截图、日志、账号配置不得复制到子工作树或提交。脱敏证据需说明来源与脱敏范围，结果判据不能为归档而改动。

## 设备与共享资源

设备操作只由主 agent 持有，一个时间窗口只能有一个设备会话。开始前确认用户没有操作账号，结束后检查进程已退出、原始证据在本地忽略目录，并按运行前快照核对配置副作用。子 agent 的测试默认无设备；其测试命令若会连接模拟器、改配置或启动上游任务，必须交给主 agent 串行执行。

并行任务不能同时重建同一份生成文档、导出数据或共享依赖缓存。共享生成物由主 agent 在集成后统一重建。长任务由主 agent 跟踪其进程句柄直到完成，不能因一次轮询超时另起同一任务。

## 交付与集成

子 agent 提交使用具体的中文 Conventional Commit 标题与正文，并报告提交哈希、改动文件、实际执行的测试和未验证项；不推送、不合并、不改写历史。主 agent 在集成前检查差异是否遵守上游规则和文件归属，逐个合并或择取提交，冲突由主 agent 处理，不要求两个 agent 在同一工作树抢修。

集成后的最低检查是受影响模块的回归、`git diff --check`、`tools/diagnostics/verify_architecture.py` 和 `tools/diagnostics/verify_privacy.py`；改动结果合同、导出结构或稳定战役链时按 `AGENTS.md` 追加对应专项检查。隐私扫描覆盖本轮改动与当前已跟踪文件，原始设备证据只留在忽略目录。真机失败、空目标和用户现场观察分开记录，不用一个成功队列代替实际效果。

确认任务已停止、分支已集成且 worktree 无未提交改动后，主 agent 才清理对应工作树；不使用强制删除，也不清理其他人仍在使用的目录。
删除前检查目录内的 junction/symlink：共享运行时和设备配置的链接只移除链接本身，不能递归进入目标。
完成后移除临时通信文件与任务草稿；有审计用途的日志、截图和原始工件保留在本地忽略目录，不能为清理路径改写证据或哈希。

```powershell
git -C .worktrees/docs-audit status --porcelain
git worktree remove .worktrees/docs-audit
git branch -d codex/docs-audit
```

用户暂时离线时，优先推进已授权、可离线验证的任务。无法从代码与证据判断的行为保留为未验证，不擅自扩大设备动作或发布范围；待用户回来后只汇报具体结果和真正需要其决定的事项。
