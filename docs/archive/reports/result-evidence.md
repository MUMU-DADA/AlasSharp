# 实机结果证据核对（R0）

> 本页由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 与 `tools/diagnostics/evidence/` 脱敏工件重新核对生成，**不手写**。
> 每条记录都能指回原始日志的行号；对不上就报矛盾，不做解释性兜底。

归档日志：17 份；出击记录：7 条；矛盾：0 条。

## 逐条记录

| 日志 | 关 | 声称结果 | 战果 | 结算行 | CAMPAIGN END | 撤退事件 | 裁决 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `s3_native_1_1_retry.log`:164 | 1-1 | outcome=error cleared=False | — | — | — | — | 一致 |
| `s3_native_1_4.log`:512 | 1-4 | outcome=cleared cleared=True | S | ✓ | ✓ | — | 一致 |
| `s3_native_2_1_clearall.log`:953 | 2-1 | outcome=cleared cleared=True | S | ✓ | ✓ | cleanup_previous_sortie | 一致 |
| `s3_native_final_batch.log`:365 | 1-1 | outcome=cleared cleared=True | S | ✓ | ✓ | — | 一致 |
| `s3_native_final_batch.log`:861 | 1-4 | outcome=cleared cleared=True | S | ✓ | ✓ | — | 一致 |
| `s3_native_small_and_clearall.log`:204 | 1-1 | outcome=error cleared=False | — | — | — | — | 一致 |
| `s3_native_small_and_clearall.log`:678 | 2-1 | outcome=error cleared=False | — | ✓ | — | navigation | 一致 |

## 解释

- `s3_native_1_1_retry.log`:164（1-1）：[错误    ] TypeError: unsupported operand type(s) for -: 'float' and 'NoneType'
- `s3_native_1_4.log`:512（1-4）：战果 S 已点击、In stage. 与 CAMPAIGN END 都在，最后一次战斗之后没有撤退
- `s3_native_2_1_clearall.log`:953（2-1）：战果 S 已点击、In stage. 与 CAMPAIGN END 都在，最后一次战斗之后没有撤退；进图前清理了上一局（1 次撤退调用）
- `s3_native_final_batch.log`:365（1-1）：战果 S 已点击、In stage. 与 CAMPAIGN END 都在，最后一次战斗之后没有撤退
- `s3_native_final_batch.log`:861（1-4）：战果 S 已点击、In stage. 与 CAMPAIGN END 都在，最后一次战斗之后没有撤退
- `s3_native_small_and_clearall.log`:204（1-1）：[错误    ] MapDetectionError: Image to detect is not in_map
- `s3_native_small_and_clearall.log`:678（2-1）：[错误    ] ScriptEnd: Campaign name error

## 撤退事件台账

| 日志 | 行 | 性质 | 依据 |
| --- | --- | --- | --- |
| `s3_native_2_1_clearall.log` | 47 | cleanup_previous_sortie | 进图前清理上一局（prepare_campaign_navigation） |
| `s3_native_small_and_clearall.log` | 244 | navigation | 章节导航期间上游自己调了 withdraw() |

## 结构化运行工件

脱敏归档保留调用链与结果字段，同时检查结果合同、index、队列任务与会话日志。战后返回章节页只在下一任务实时抓帧、识别页面、非地图状态及章节关联均一致时记为已核验。下表不把撤退当成通关；它与上面的历史控制台日志分别计数。

| 工件 | 会话时间 | 结果 | 本局撤退证据 | 战后章节页 | 裁决 |
| --- | --- | --- | --- | --- | --- |
| `tools/diagnostics/evidence/20260923T093800/sortie-1-1.json` | 2026-09-23T09:38:39.189+08:00 | withdrawn, cleared=False | withdraw 步骤 + 上游调用链 + 返回章节页 | 未核验 | 一致 |
| `tools/diagnostics/evidence/20260923T144951/sortie-1-1.json` | 2026-09-23T14:50:54.927+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260923T163008/sortie-a1.json` | 2026-09-23T16:33:09.227+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260923T171026/sortie-a2.json` | 2026-09-23T17:15:49.643+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260923T180804/sortie-a3.json` | 2026-09-23T18:12:39.675+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260923T194236/sortie-a1.json` | 2026-09-23T19:45:48.340+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260923T194236/sortie-a2.json` | 2026-09-23T19:50:19.808+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T024144/sortie-1-4.json` | 2026-09-25T02:44:08.498+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T035913/sortie-1-1.json` | 2026-09-25T04:00:34.210+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T053717/sortie-2-2.json` | 2026-09-25T05:40:06.905+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T073557/sortie-1-1.json` | 2026-09-25T07:37:00.280+08:00 | cleared, cleared=True | — | 未核验 | 一致 |
| `tools/diagnostics/evidence/20260925T083148/sortie-1-1.json` | 2026-09-25T08:33:12.252+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T155652/sortie-1-2.json` | 2026-09-25T15:58:44.654+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T194930/sortie-1-3.json` | 2026-09-25T19:51:27.449+08:00 | cleared, cleared=True | — | 已核验 | 一致 |
| `tools/diagnostics/evidence/20260925T195405/sortie-2-3.json` | 2026-09-25T19:56:59.429+08:00 | cleared, cleared=True | — | 已核验 | 一致 |

## 门槛与缺口

- 真实成功结算：4 条可解释（通过）
- 真实撤退证据：2 起可解释（通过）
- 本局撤退结构化真机记录：1 条可解释（通过）
- 记录自相矛盾或工件无法核验：0 条（通过）

## 已知缺口

- 导航期撤退目前只记在 `ensure_campaign_ui` 的步骤里（`navigation_end`/`navigation_withdrawn`），不再被静默吞掉；对应回归见 `verify_s3_plan.py`。

## 复现

```powershell
python tools/diagnostics/audit_real_records.py
```
