# R5 寻路全库对拍（C# 移植 vs 上游）

> 本报告由 `tools/diagnostics/r5_path_sweep.py` 重建，不手写。
> 口径：同一份**声明地图**，每种地图跑**三种配置**（默认 `has_ambush=False,has_enemy=True`、
> `has_ambush=True`、`has_enemy=False`），逐格比 `cost` 与 `connection`。
> **不覆盖**真机识别状态（敌人/机关由识别提供），所以它证明的是**算法移植一致**，不是真机寻路一致。

- 用例数：**4110**（关卡 × 配置；配置见下表）
- 逐格比较：**275934** 格
- **成本场不一致（无伏击配置）**：**0** 处（硬指标：这类配置下 cost 与迭代序无关）
- 有伏击配置的**收敛伪影**：**38** 处（上游前沿终止 + set 迭代序；方向不定，不是移植错误）
- 连接差异：**992** 处（**只允许等代价的多个最优前驱之间**；上游的择向依赖它自己 
  `set` 的迭代序、用对象身份哈希，本来就不保证跨实现一致）
- 路线最优性检查：两侧回溯代价都等于 cost（通过）
- 用时：C# 侧 1.9s（含进程启动）/ 上游侧 15.2s

## 有伏击配置的收敛伪影（前 20 条，信息项）

| 用例 | 位置 | 上游 | C# |
| --- | --- | --- | --- |
| campaign_main\campaign_13_2#有伏击 | cost@G3 | `34` | `43` |
| campaign_main\campaign_13_2#有伏击 | cost@H3 | `44` | `49` |
| campaign_main\campaign_13_2#有伏击 | cost@I3 | `45` | `50` |
| campaign_main\campaign_13_2#有伏击 | cost@G4 | `33` | `42` |
| campaign_main\campaign_13_2#有伏击 | cost@H4 | `43` | `52` |
| campaign_main\campaign_13_2#有伏击 | cost@I4 | `44` | `51` |
| campaign_main\campaign_13_2#有伏击 | cost@I5 | `43` | `34` |
| campaign_main\campaign_13_3#有伏击 | cost@F1 | `32` | `23` |
| campaign_main\campaign_13_3#有伏击 | cost@H1 | `43` | `44` |
| campaign_main\campaign_15_3#有伏击 | cost@J2 | `35` | `40` |
| campaign_main\campaign_15_3#有伏击 | cost@I3 | `35` | `40` |
| campaign_main\campaign_4_1#有伏击 | cost@F5 | `45` | `52` |
| campaign_main\campaign_5_1#有伏击 | cost@H2 | `31` | `40` |
| event_20210225_tw\a1#有伏击 | cost@J3 | `85` | `90` |
| event_20210225_tw\c1#有伏击 | cost@J3 | `85` | `90` |
| event_20210415_tw\sp2#有伏击 | cost@G1 | `51` | `54` |
| event_20210415_tw\sp2#有伏击 | cost@H1 | `50` | `55` |
| event_20210415_tw\sp2#有伏击 | cost@I1 | `51` | `56` |
| event_20210624_tw\a1#有伏击 | cost@H3 | `44` | `37` |
| event_20210624_tw\a1#有伏击 | cost@H4 | `45` | `38` |

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
| 有伏击 | 91978 | 206 |
| 默认 | 91978 | 412 |

## 每关差异分布

| 差异数 | 关卡数 |
| --- | --- |
| 0 | 3225 |
| 1 | 767 |
| 2 | 98 |
| 3 | 17 |
| 4 | 2 |
| 8 | 1 |
