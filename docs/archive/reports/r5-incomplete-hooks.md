# R5 不完整钩子普查（`plan_complete=false` 的成因与先决条件）

> 本报告由 `tools/diagnostics/r5_incomplete_hooks.py` 重建，不手写。
> 这些钩子**有真实语句**但导出器表示不了；引擎侧会**拒绝执行并报原因**（tier C 守卫），
> 所以是「少做」而不是「做错」。本报告只说清还差什么。

- 导出里的钩子条目：**3019**
- `plan_complete=false` 且上游**有 ≥2 条语句**的：**53**（其中 `battle_*` **9**、变体/其它 **44**）
- 棘轮基线：**9**（只允许下降）

## `if` 的形态分布（条件 / 语句体）

口径：这些是**不完整钩子体内**所有的 `if`，包含那些**本身支持**的形态
（`self_call` + 纯 `return True`）——不完整的成因在别的语句上。要看的行是
`local_name`、`not_self_call/if`、`other/*` 这几类。

| 条件形态 | 语句体形态 | 次数 |
| --- | --- | --- |
| self_call | return | 13 |
| other | expr | 10 |
| other | assign+expr+return | 6 |
| self_call | assign | 6 |
| compare | assign | 5 |
| compare | return | 4 |
| other | assign | 2 |
| other | return | 2 |
| other | expr+return | 2 |
| local_name | assign+expr+raise | 1 |
| other | for | 1 |
| local_name | assign+if | 1 |

## 例子

- `campaign_hard/campaign_hard` clear_boss：`if grids` → 体内有 assign+expr+raise
- `campaign_main/campaign_14_4` map_data_init：`if not self.map_is_clear_mode` → 体内有 for
- `campaign_main/campaign_15_1` battle_function：`if self.config.MAP_CLEAR_ALL_THIS_TIME and self.bat` → 体内有 assign+expr+return
- `campaign_main/campaign_15_2` battle_function：`if self.config.MAP_CLEAR_ALL_THIS_TIME and self.bat` → 体内有 assign+expr+return
- `campaign_main/campaign_7_2` battle_0：`if self.fleet_2_step_on(FLEET_2_STEP_ON, roadblocks` → 体内有 return
- `campaign_main/campaign_7_2` battle_0：`if self.fleet_at(A3, fleet=2) and A1.enemy_scale !=` → 体内有 assign
- `campaign_main/campaign_7_2` battle_0：`if self.fleet_at(G3, fleet=2)` → 体内有 assign
- `campaign_main/campaign_7_2` battle_0：`if self.clear_roadblocks([ROAD_MAIN], strongest=Tru` → 体内有 return
- `campaign_main/campaign_7_2` battle_0：`if self.clear_enemy(scale=(3,))` → 体内有 return
- `campaign_main/campaign_7_2` battle_0：`if self.clear_potential_roadblocks([ROAD_MAIN], str` → 体内有 return

## 先决条件（按出现频次）

1. **局部变量 + `if <局部变量>:` + 分支体**：`boss = self.map.select(is_boss=True)` 这类「观察」，
   以及 `branch` 步骤（条件为局部变量或一次原语调用，体内是步骤序列）；
2. **局部变量的实参引用**：`check_accessibility(boss[0], fleet='boss')` 里的 `boss[0]`；
3. **运行期标志**：`self.map_is_clear_mode` 由上游 handler 层设置（`module/handler/fast_forward.py` 的 `handle_fast_forward`），语义是`map_has_clear_mode and config.Campaign_UseClearMode` —— **已实现**（默认没开快进 → 确定为假；开了但还没识别到 `map_has_clear_mode` → 阻塞报原因）。**更正**：本报告此前写成「上游快照里只有使用、没有定义」，那是本机 grep 用错参数（`-Include` 在递归下漏扫 `module/handler/`）造成的误判；快照里该文件与完整仓库哈希一致；
4. 其它形态（`compare` 条件、`for` 循环、`raise` 体）另计，需要单独设计，不要硬塞进上面的结构。

## 已经量化过、结论是「先不做」的两条路

| 设想 | 量化结果 | 为什么不做 |
| --- | --- | --- |
| 实例属性状态（`self.X = …` / `if self.X:`） | 只差这一项就能变完整的钩子：**0 / 101** | 读到的属性要么**全库没有写过**（`self.map_is_clear_mode`），要么这些钩子还被别的语句挡着 |
| 复合条件（`and`/`or` 组合已有条件形态） | 解锁钩子数：**0**；唯一可达的复合计划 `event_20200312_cn/sp3:handle_in_stage` 还卡在未实现的 `appear` | 不完整钩子里的复合式**每一个**都含 `self.map_is_clear_mode` 读取 |

结论：剩下的不完整钩子主要卡在**上游定义或原语的缺失**（`self.map_is_clear_mode` 72 处、`appear`、`self.fleet_at(...)`、`self.map.weight_data` 改写等），不是计划语言的表达力。继续加语言特性收益已经很低；要么拿到 `map_is_clear_mode` 的定义证据，要么把精力放到设备路径。

