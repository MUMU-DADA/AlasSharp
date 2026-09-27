# 结果合同 sortie-result/1（R0）

一次出击的结论只有一份定义，就是这份合同。新产品由 **Alas.Engine** 产生结果，
`CampaignResumeTask` 在完整结算证据成立时才判通关；已确认撤退为 `withdrawn`，不算任务成功。
Python 规则脚本只作离线 oracle，产品裁决由 `Alas.Engine.Contracts.SortieContract` 承担，
由 `tools/diagnostics/verify_result_contract.py` **逐例对拍**，不一致就算失败。
脚本构建 `tools/diagnostics/result_contract_reference`，仅链接 Engine 合同源码，
不加载 Server 或设备宿主。缺少裁决文件或参考程序构建失败均不能通过。

> 为什么值得单独做一层合同：`CampaignEnd` 只表示"本次出击结束"——上游**撤退也抛它**。
> 早期只要有人写 `cleared = campaign_end`，撤退就会被记成通关，而且事后从日志里看不出来。

## 一、结果词表

| `outcome` | 含义 | 允许被当成通关 |
| --- | --- | --- |
| `cleared` | 本次出击击败 BOSS 并成功结算 | ✅ 且必须带结算证据 |
| `withdrawn` | 撤退（主动/上游自动），离开地图 | ❌ |
| `defeated` | 拿到败方战果（C/D） | ❌ |
| `ended_unknown` | 出击结束了，但证据不足以说明为什么 | ❌（**不是错误，但绝不能算通关**） |
| `error` | 报错中断（地图初始化失败、上游异常等） | ❌ |
| `incomplete` | 没打完：轮次/时间上限到顶，或上游返回但没给结束信号 | ❌ |
| `refused` | 安全联锁未开（真跑没带 `allow_actions`） | ❌ |

`dry_run=true` 的文档允许没有 `outcome`（它只描述"打算怎么跑"）。

## 二、字段

| 字段 | 说明 |
| --- | --- |
| `contract` | 恒为 `sortie-result/1`；别的值直接判 `contract_version_mismatch` |
| `outcome` / `cleared` | `cleared` **只由 `outcome` 推出**，不接受两个字段各说各话 |
| `campaign_end` | 仅表示"收到过 CampaignEnd"；**单独不能证明任何事** |
| `end_reason` / `stop_reason` / `reason` | 结束原因；`incomplete` 必须给合法的限额理由 |
| `end_evidence` | 结算证据链：`battle_rank` / `rank_source` / `combat_status` / `stage_observed` / `withdrawn` / `call_path` |
| `failure` | 导致原生运行终止的失败；逐步观测时取首处失败：`step` / `error` / `traceback_tail` / `frame` |
| `failure_frames` | 本次落盘的全部失败帧路径（`failure.*` 与各步 `failure_frame` 都必须在里面登记） |
| `contract_violations` | Engine 记录的违例码；消费方仍会重新裁决，不信任调用方自报值 |
| `steps` | Engine 操作轨迹（准备、进图、战斗等步骤） |
| `dry_run` | 为真时不得出现任何驱动游戏的操作步 |

## 三、不变量（违例码）

| 违例码 | 触发条件 |
| --- | --- |
| `contract_version_mismatch` | `contract` 不是 `sortie-result/1` |
| `outcome_missing` | 非 dry-run 却没有 `outcome` |
| `unknown_outcome` | `outcome` 不在词表里 |
| `cleared_flag_mismatch` | `cleared` 与 `outcome == 'cleared'` 不一致 |
| `cleared_without_settlement_evidence` | 判通关但**没有** `end_evidence`（campaign_end 单字段声称通关就落在这里） |
| `cleared_requires_win_rank` | 结算战果不是 S/A/B |
| `cleared_requires_rank_source` | 战果没有来源模板（`BATTLE_STATUS_` / `EXP_INFO_`） |
| `cleared_requires_combat_status` | 调用栈里没有 `Combat.combat_status` |
| `cleared_requires_stage_observed` | 调用栈里没有 `EnemySearchingHandler.handle_in_stage` |
| `cleared_requires_no_withdrawal` | 撤退路径却判通关 |
| `cleared_requires_executed_battle` | 一次无错误的 `execute_a_battle` 都没有 |
| `withdrawn_requires_withdrawal_evidence` | 判撤退但既没有撤退帧、原因里也没有 withdraw |
| `defeated_requires_loss_rank` | 判战败但战果不是 C/D |
| `ended_unknown_requires_campaign_end` | 判"结束但说不清"却没有 CampaignEnd |
| `error_requires_failure` | 判报错却没有任何失败步骤 |
| `error_requires_traceback` | 失败没有调用栈尾部 |
| `error_step_without_traceback` | 某个失败步骤没有调用栈尾部 |
| `incomplete_requires_limit_reason` | 判"没打完"但理由不在 `round_limit` / `time_limit` / `upstream_returned_without_clear` / `stopped_after_map_init` 里 |
| `refused_requires_reason` | 判拒绝执行却没给理由 |
| `failure_frame_not_listed` | 存了失败帧却没登记进 `failure_frames` |
| `failure_frame_missing_on_disk` | 登记的失败帧文件不存在 |
| `dry_run_must_not_execute` | dry-run 文档里出现驱动游戏的操作步 |

失败帧存在性只在能判的时候判：绝对路径直接查；相对路径需要 `artifact_root`；都没有就不算违例。

原生继承流程自行恢复的异常保留在 `steps`，不会覆盖之后的正常结算或执行限额。真正逃出 `Campaign.run()` 的异常
必须得到 `outcome=error`，即使此前已观察到结算；原结算证据仍保留，顶层 `error/failure` 指向终端异常。
这些组合沿用现有词表与不变量，生产者场景纳入 Python/C# 的 38 例对拍。

## 四、跨关复位的定义

同一进程连续跑多关时，"上一关"的状态不能变成"这一关"的结论：

| 环节 | 定义 | 实现 |
| --- | --- | --- |
| 设备记录 | 每次出击前清空 Engine 会话内的动作/失败状态 | `Alas.Engine.Runtime.EngineSession` |
| 上一局残留 | 若仍在图内，先执行通用撤退再导航；清理动作写入当前批次工件 | `CampaignPreparation` / `CampaignWithdrawal` |
| 清理撤退的结论 | 清理造成的结束信号不参与本局结果判定 | `CampaignResumeTask` + `SortieContract` |
| 导航期撤退 | 导航状态残留按 Engine 的通用页面恢复流程记录，不回退 Python 宿主 | `UiNavigator` / `CampaignPreparation` |
| 结果证据 | 每关独立一份 Engine 工件；上一关的证据不会进入下一关 | `TaskQueue` 批次目录 |
| 账号状态 | 由 Engine 规则和会话持有，不复制旧宿主状态 | `Alas.Engine.Rules` / `EngineSession` |

## 五、怎么被强制

1. **Engine 生成**：`CampaignResumeTask` 生成结果并把 `cleared` 从 `outcome` 推出，
   违例写进工件（不吞掉、不抛异常，结果本身要留给调用方）。
2. **Engine 裁决**：战役任务使用 `SortieContract.Violations`，
   有违例就让任务失败；工件保存整份结果文档。
3. **跨语言对拍**：`python tools/diagnostics/verify_result_contract.py`
   —— Engine 结果夹具和 20 条反例必须被正确裁决，两侧逐例相同。
4. **静态守卫**：`python tools/diagnostics/verify_architecture.py`
   —— 生产代码出现 `cleared = ... campaign_end` 直接失败；两侧词表/违例码漂移也直接失败；
   并要求 `docs/result-contract.md`、`tools/sortie_contract.py`、Engine 合同源文件都在。
5. **离线单命令**：`dotnet run --project tools/diagnostics/result_contract_reference -- <用例.json> <工件目录> <裁决.json>`。

## 六、实机证据

`docs/archive/reports/result-evidence.md`（由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 与
`tools/diagnostics/evidence/` 的脱敏运行工件重建）
把每条归档记录的战果点击、`In stage.`、`CAMPAIGN END`、撤退事件逐条对上：
4 条真实通关全部可解释、2 起真实撤退（进图前清理 / 导航期）全部可解释、0 条自相矛盾。

结构化归档另有一条本局 `withdrawn` 记录（`20260923T093800`）：`withdraw` 步骤、上游调用链与
返回章节页证据共同支持撤退结论。审计复用本合同裁决，并交叉核对单关工件、批次索引与会话日志；
`verify_real_records.py` 要求缺失、篡改和结论分歧均验收失败。本轮只补归档，不改生产判定。
原件仍保留在本机忽略目录；入库副本隐藏绝对路径与设备序列号，清单分别保存原件和副本校验和。
这不代表七个合同结果都有真机覆盖；离线各分支继续由 `verify_result_contract.py` 验证。
