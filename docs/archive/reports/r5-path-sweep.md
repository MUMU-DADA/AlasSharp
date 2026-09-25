# R5 寻路全库对拍（C# 移植 vs 上游）

> 本报告由 `tools/diagnostics/r5_path_sweep.py` 重建，不手写。
> 口径：同一份**声明地图**，每种地图跑**三种配置**（默认 `has_ambush=False,has_enemy=True`、
> `has_ambush=True`、`has_enemy=False`），逐格比 `cost` 与 `connection`。
> **不覆盖**真机识别状态（敌人/机关由识别提供），所以它证明的是**算法移植一致**，不是真机寻路一致。

- 用例数：**4110**（关卡 × 配置；配置见下表）
- 逐格比较：**275934** 格
- **成本场不一致（无伏击配置）**：**0** 处（硬指标：这类配置下 cost 与迭代序无关）
- **逐格编码不一致**（上游 `GridInfo.encode()`，即 `Filter` 用的 `grid.str`）：**0** 处 —— 覆盖「令牌 → 标志 → 编码」整条链路
- 有伏击配置的**收敛伪影**：**52** 处（**全部是「上游偏大」**：C# relax 到不动点，给出的是真正最短距离，不可能比上游更贵；更贵会被判为硬失败）
- 连接差异：**990** 处（**只允许等代价的多个最优前驱之间**；上游的择向依赖它自己 
  `set` 的迭代序、用对象身份哈希，本来就不保证跨实现一致）
- 路线最优性检查：两侧回溯代价都等于 cost（通过）
- **路线对拍**（C# `FindPath` vs 上游 `_find_path`）：比较 **32877** 条 —— 完全相同 32739，等代价择向不同但**两侧都最优** 138，不合法 0
- 用时：C# 侧 2.3s（含进程启动）/ 上游侧 15.8s

## 有伏击配置的收敛伪影（前 20 条，信息项）

| 用例 | 位置 | 上游 | C# |
| --- | --- | --- | --- |
| campaign_main\campaign_13_2#有伏击 | cost@F3 | `50` | `44` |
| campaign_main\campaign_13_2#有伏击 | cost@H3 | `44` | `35` |
| campaign_main\campaign_13_2#有伏击 | cost@I3 | `45` | `36` |
| campaign_main\campaign_13_2#有伏击 | cost@I4 | `44` | `35` |
| campaign_main\campaign_13_2#有伏击 | cost@I5 | `43` | `34` |
| campaign_main\campaign_13_3#有伏击 | cost@F1 | `32` | `23` |
| campaign_main\campaign_13_3#有伏击 | cost@G1 | `42` | `33` |
| campaign_main\campaign_13_3#有伏击 | cost@H1 | `43` | `34` |
| campaign_main\campaign_13_3#有伏击 | cost@F2 | `31` | `24` |
| campaign_main\campaign_15_3#有伏击 | cost@J1 | `41` | `36` |
| campaign_main\campaign_4_2#有伏击 | cost@C5 | `24` | `19` |
| campaign_main\campaign_5_1#有伏击 | cost@G2 | `44` | `41` |
| campaign_main\campaign_6_2#有伏击 | cost@F6 | `45` | `40` |
| event_20200326_cn\b1#有伏击 | cost@A5 | `64` | `59` |
| event_20201029_cn\sp3#有伏击 | cost@H6 | `83` | `76` |
| event_20210325_cn\a3#有伏击 | cost@I8 | `80` | `66` |
| event_20210325_cn\c3#有伏击 | cost@I8 | `80` | `73` |
| event_20210527_tw\b1#有伏击 | cost@A5 | `64` | `59` |
| event_20210527_tw\d1#有伏击 | cost@A5 | `64` | `59` |
| event_20210624_tw\a1#有伏击 | cost@H3 | `44` | `37` |

## 连接差异（前 20 条，均为等代价择向）

| 关卡 | 位置 | 上游 | C# |
| --- | --- | --- | --- |
| campaign_hard\campaign_12_4#默认 | connection@D1 | `C1` | `E1` |
| campaign_hard\campaign_12_4#不考虑敌人 | connection@D1 | `C1` | `E1` |
| campaign_hard\campaign_14_4#默认 | connection@H7 | `G7` | `I7` |
| campaign_hard\campaign_14_4#不考虑敌人 | connection@H7 | `G7` | `I7` |
| campaign_main\campaign_10_3#默认 | connection@D1 | `C1` | `E1` |
| campaign_main\campaign_10_3#默认 | connection@G4 | `H4` | `F4` |
| campaign_main\campaign_10_3#有伏击 | connection@G4 | `H4` | `F4` |
| campaign_main\campaign_10_3#不考虑敌人 | connection@D1 | `C1` | `E1` |
| campaign_main\campaign_10_3#不考虑敌人 | connection@G4 | `H4` | `F4` |
| campaign_main\campaign_11_2#默认 | connection@J3 | `J2` | `J4` |
| campaign_main\campaign_11_2#不考虑敌人 | connection@J3 | `J2` | `J4` |
| campaign_main\campaign_11_3#默认 | connection@H6 | `H7` | `H5` |
| campaign_main\campaign_11_3#不考虑敌人 | connection@H6 | `H7` | `H5` |
| campaign_main\campaign_11_4#默认 | connection@E2 | `D2` | `F2` |
| campaign_main\campaign_11_4#有伏击 | connection@E2 | `D2` | `F2` |
| campaign_main\campaign_11_4#不考虑敌人 | connection@E2 | `D2` | `F2` |
| campaign_main\campaign_12_1#默认 | connection@A5 | `A6` | `A4` |
| campaign_main\campaign_12_1#不考虑敌人 | connection@A5 | `A6` | `A4` |
| campaign_main\campaign_12_2#默认 | connection@B3 | `B4` | `B2` |
| campaign_main\campaign_12_2#有伏击 | connection@B3 | `B4` | `B2` |

## 每种配置的结果

| 配置 | 逐格比较 | 差异 |
| --- | --- | --- |
| 不考虑敌人 | 91978 | 412 |
| 有伏击 | 91978 | 218 |
| 默认 | 91978 | 412 |

## 每关差异分布

| 差异数 | 关卡数 |
| --- | --- |
| 0 | 3216 |
| 1 | 779 |
| 2 | 91 |
| 3 | 16 |
| 4 | 7 |
| 5 | 1 |
