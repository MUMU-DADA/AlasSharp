# 结果合同 sortie-result/1（R0）

一次出击的结论只有一份定义，就是这份合同。生产方是 **Python 宿主**（跑上游 `Campaign.run()`），
消费方是 **C# 运行时**（`Alas.Campaign.SortieContract`），两侧各实现一遍同一张规则表，
由 `tools/diagnostics/verify_result_contract.py` **逐例对拍**，不一致就算失败。

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
| `failure` | 第一处失败：`step` / `error` / `traceback_tail` / `frame` |
| `failure_frames` | 本次落盘的全部失败帧路径（`failure.*` 与各步 `failure_frame` 都必须在里面登记） |
| `contract_violations` | 生产方**自报**的违例码；消费方不信它，会自己再判一次 |
| `steps` | 上游操作轨迹（`prepare_campaign_navigation` / `enter_map` / `execute_a_battle` …） |
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

## 四、跨关复位的定义

同一进程连续跑多关时，"上一关"的状态不能变成"这一关"的结论：

| 环节 | 定义 | 实现 |
| --- | --- | --- |
| 设备记录 | 每次出击前清空 stuck / click 记录 | `s3_campaign_entry.clear_campaign_device_records` |
| 上一局残留 | 若仍在图内，**先撤退再导航**；这次撤退记为 `withdrew_previous_sortie=true` 的清理步 | `prepare_campaign_navigation` |
| 清理撤退的结论 | 清理造成的 `CampaignEnd` **不参与**本局结果判定 | `s3_campaign_entry` 文档串 + `verify_s3_plan.py` 回归 |
| 导航期撤退 | 上游导航内部自己撤退（客户端状态残留）时，把 `navigation_end` / `navigation_withdrawn` 记进 `ensure_campaign_ui` 步 | `op_s3_run_plan`（真机证据见 `result-evidence.md`） |
| 结果证据 | 每关独立一份文档；上一关的 `end_evidence` / `failure_frames` 不得出现在这一关 | 每关各自 `finalize_sortie_result` |
| 账号状态 | 数据（心情、石油、通关进度）由上游管；宿主不维护第二份 | 上游 `Emotion` / `CampaignRun` |

## 五、怎么被强制

1. **生产方自报**：`finalize_sortie_result` 末尾 `stamp()`，把 `cleared` 从 `outcome` 推出，
   有违例就写进 `contract_violations`（不吞掉、不抛异常，结果本身要留给调用方）。
2. **消费方裁决**：`alashub campaign` 每关跑 `SortieContract.Violations`，
   有违例就打印 `[合同]` 并让退出码非 0；`--artifacts <目录>` 同时落盘整份结果文档。
3. **跨语言对拍**：`python tools/diagnostics/verify_result_contract.py`
   —— 四类结果由替身真跑产出、20 条反例必须被拒绝、两侧裁决逐例相同。
4. **静态守卫**：`python tools/diagnostics/verify_architecture.py`
   —— 生产代码出现 `cleared = ... campaign_end` 直接失败；两侧词表/违例码漂移也直接失败；
   并要求 `docs/result-contract.md`、`tools/sortie_contract.py`、`SortieResult.cs` 都在。
5. **离线单命令**：`alashub contract --fixture <用例.json> [--artifacts <目录>] [--json <裁决.json>]`。

## 六、实机证据

`docs/result-evidence.md`（由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 重建）
把每条归档记录的战果点击、`In stage.`、`CAMPAIGN END`、撤退事件逐条对上：
4 条真实通关全部可解释、2 起真实撤退（进图前清理 / 导航期）全部可解释、0 条自相矛盾。

**已知缺口**：归档里没有"本局撤退被判为 `withdrawn`"的真机记录 —— 现有撤退都属于上一局/客户端状态清理。
补这一条需要一次真实出击后主动撤退（消耗石油、改变账号状态），留到设备在线且获授权时做；
离线侧该分支已由 `verify_result_contract.py` 的替身用例覆盖。
