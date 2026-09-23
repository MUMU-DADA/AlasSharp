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

脱敏归档保留调用链与结果字段，同时检查结果合同、index 与会话日志。下表不把撤退当成通关；它与上面的历史控制台日志分别计数。

| 工件 | 会话时间 | 结果 | 本局撤退证据 | 裁决 |
| --- | --- | --- | --- | --- |
| `tools/diagnostics/evidence/20260923T093800/sortie-1-1.json` | 2026-09-23T09:38:39.189+08:00 | withdrawn, cleared=False | withdraw 步骤 + 上游调用链 + 返回章节页 | 一致 |

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
