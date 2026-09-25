# R5 寻路全库对拍（C# 移植 vs 上游）

> 本报告由 `tools/diagnostics/r5_path_sweep.py` 重建，不手写。
> 口径：同一份**声明地图**、同一算法（`find_path_initial(wall=True, has_enemy=True)`）逐格比 cost 与 connection。
> **不覆盖**真机识别状态（敌人/机关由识别提供），所以它证明的是**算法移植一致**，不是真机寻路一致。

- 关卡数：**1370**
- 逐格比较：**91978** 格
- **成本场不一致**：**0** 处（这是硬指标）
- 连接差异：**412** 处（**只允许等代价的多个最优前驱之间**；上游的择向依赖它自己 
  `set` 的迭代序、用对象身份哈希，本来就不保证跨实现一致）
- 路线最优性检查：两侧回溯代价都等于 cost（通过）
- 用时：C# 侧 1.0s（含进程启动）/ 上游侧 5.5s

## 连接差异（前 20 条，均为等代价择向）

| 关卡 | 位置 | 上游 | C# |
| --- | --- | --- | --- |
| campaign_hard\campaign_12_4 | connection@D1 | `C1` | `E1` |
| campaign_hard\campaign_14_4 | connection@H7 | `G7` | `I7` |
| campaign_main\campaign_10_3 | connection@D1 | `C1` | `E1` |
| campaign_main\campaign_10_3 | connection@G4 | `H4` | `F4` |
| campaign_main\campaign_11_2 | connection@J3 | `J2` | `J4` |
| campaign_main\campaign_11_3 | connection@H6 | `H7` | `H5` |
| campaign_main\campaign_11_4 | connection@E2 | `D2` | `F2` |
| campaign_main\campaign_12_1 | connection@A5 | `A6` | `A4` |
| campaign_main\campaign_12_2 | connection@B3 | `B4` | `B2` |
| campaign_main\campaign_12_4 | connection@D1 | `C1` | `E1` |
| campaign_main\campaign_13_1 | connection@C2 | `B2` | `D2` |
| campaign_main\campaign_13_2 | connection@F4 | `F5` | `F3` |
| campaign_main\campaign_13_4 | connection@F4 | `F5` | `F3` |
| campaign_main\campaign_13_4 | connection@K6 | `K7` | `K5` |
| campaign_main\campaign_14_4 | connection@H7 | `G7` | `I7` |
| campaign_main\campaign_15_2 | connection@F8 | `E8` | `G8` |
| campaign_main\campaign_15_3 | connection@H1 | `G1` | `I1` |
| campaign_main\campaign_15_3 | connection@H2 | `G2` | `I2` |
| campaign_main\campaign_15_4 | connection@H7 | `G7` | `I7` |
| campaign_main\campaign_15_4_121 | connection@H7 | `G7` | `I7` |

## 每关差异分布

| 差异数 | 关卡数 |
| --- | --- |
| 0 | 1017 |
| 1 | 302 |
| 2 | 43 |
| 3 | 8 |
