# 实机结果证据核对（R0）

> 本页由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 重新核对生成，**不手写**。
> 每条记录都能指回原始日志的行号；对不上就报矛盾，不做解释性兜底。

归档日志：14 份；出击记录：7 条；矛盾：0 条。

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

## 门槛与缺口

- 真实成功结算：4 条可解释（通过）
- 真实撤退证据：2 起可解释（通过）
- 记录自相矛盾：0 条（通过）

## 已知缺口

- 归档日志里**没有**以 `outcome=withdrawn` 收尾的一局：现有 2 起撤退都是进图前清理或导航期发生，属于"上一局/客户端状态"清理，不是本局结论。要补"本局撤退判为 withdrawn"的真机记录，需要一次真实出击后主动撤退；那是消耗石油且改变账号状态的动作用户未授权，留给设备在线时补。
- 导航期撤退目前只记在 `ensure_campaign_ui` 的步骤里（`navigation_end`/`navigation_withdrawn`），不再被静默吞掉；对应回归见 `verify_s3_plan.py`。

## 复现

```powershell
python tools/diagnostics/audit_real_records.py
```
