# R5 复合原语严格扫描

> 由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。
> 上游使用原生 Map/Fleet 属性及方法；C# 执行注册原语，保留 bool/None 返回。
> 动作只录制，计数反馈为显式替身假设；不证明设备效果、真实通关或完整状态等价。

- 状态数：**20**（seed=13）
- 用例数：**4080**（17 个原语 × 12 种配置）
- 完整比较通过：**3399**
- 失败或未验证：**681**
- 核对返回值及类型、全部动作及参数、计数/舰队/弹药/拾取列表与地图状态字段。
- 不豁免动作前缀、切队顺序、同权目标或潜艇目标；原生失败、缺结果也令检查失败。
- 原生寻路超过每例 3 秒诊断预算记为未验证，不改变上游结束判据。
- 未运行的慢原语：fleet_2_rescue

| 差异类别（可重叠） | 次数 |
| --- | --- |
| actions | 558 |
| csharp_error | 15 |
| native_unverified | 15 |
| state | 248 |
| value | 33 |

## 失败样本（最多 30 条）

| 用例 | 差异 |
| --- | --- |
| state0-clear_boss-c0 | actions |
| state0-clear_boss-c1 | actions |
| state0-clear_boss-c2 | actions |
| state0-clear_boss-c3 | actions |
| state0-clear_boss-c4 | actions |
| state0-clear_boss-c5 | actions |
| state0-clear_boss-c6 | actions |
| state0-clear_boss-c7 | actions |
| state0-clear_boss-c8 | actions, state |
| state0-clear_boss-c9 | actions, state |
| state0-clear_boss-c10 | actions |
| state0-clear_boss-c11 | actions |
| state0-brute_clear_boss-c0 | actions |
| state0-brute_clear_boss-c1 | actions |
| state0-brute_clear_boss-c2 | actions |
| state0-brute_clear_boss-c3 | actions |
| state0-brute_clear_boss-c4 | actions |
| state0-brute_clear_boss-c5 | actions |
| state0-brute_clear_boss-c6 | actions |
| state0-brute_clear_boss-c7 | actions |
| state0-brute_clear_boss-c8 | actions |
| state0-brute_clear_boss-c9 | actions |
| state0-brute_clear_boss-c10 | actions |
| state0-brute_clear_boss-c11 | actions |
| state0-clear_potential_boss-c0 | actions |
| state0-clear_potential_boss-c1 | actions |
| state0-clear_potential_boss-c2 | actions |
| state0-clear_potential_boss-c3 | actions |
| state0-clear_potential_boss-c4 | actions |
| state0-clear_potential_boss-c5 | actions |

集合并集的身份哈希顺序可能造成目标差异；此扫描不据此豁免。需以具体候选集合、
状态与原生顺序证据继续定位；当前失败不能宣称为全量语义一致。
