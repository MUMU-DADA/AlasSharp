# R5 不完整钩子普查（`plan_complete=false` 的成因与先决条件）

> 本报告由 `tools/diagnostics/r5_incomplete_hooks.py` 重建，不手写。
> 这些钩子**有真实语句**但导出器表示不了；引擎侧会**拒绝执行并报原因**（tier C 守卫），
> 所以是「少做」而不是「做错」。本报告只说清还差什么。

- 导出里的钩子条目：**3019**
- `plan_complete=false` 且上游**有 ≥2 条语句**的：**167**（其中 `battle_*` **118**、变体/其它 **49**）
- 棘轮基线：**118**（只允许下降）

## `if` 的形态分布（条件 / 语句体）

口径：这些是**不完整钩子体内**所有的 `if`，包含那些**本身支持**的形态
（`self_call` + 纯 `return True`）——不完整的成因在别的语句上。要看的行是
`local_name`、`not_self_call/if`、`other/*` 这几类。

| 条件形态 | 语句体形态 | 次数 |
| --- | --- | --- |
| self_call | return | 247 |
| other | if | 57 |
| other | expr | 36 |
| other | expr+return | 9 |
| other | return | 9 |
| other | assign+expr+return | 8 |
| self_call | assign | 8 |
| other | assign+if | 6 |
| compare | return | 6 |
| other | assign+expr+if | 6 |
| compare | expr | 5 |
| other | for | 5 |

## 例子

- `campaign_hard/campaign_12_4` battle_0：`if self.battle_count >= 3` → 体内有 expr
- `campaign_hard/campaign_12_4` battle_0：`if self.clear_roadblocks([road_main])` → 体内有 return
- `campaign_hard/campaign_12_4` battle_0：`if self.clear_potential_roadblocks([road_main])` → 体内有 return
- `campaign_hard/campaign_hard` clear_boss：`if grids` → 体内有 assign+expr+raise
- `campaign_main/campaign_12_4` battle_0：`if self.battle_count >= 3` → 体内有 expr
- `campaign_main/campaign_12_4` battle_0：`if self.clear_roadblocks([road_main])` → 体内有 return
- `campaign_main/campaign_12_4` battle_0：`if self.clear_potential_roadblocks([road_main])` → 体内有 return
- `campaign_main/campaign_14_2` battle_0：`if not self.picked_flare and H7.is_accessible and A` → 体内有 expr
- `campaign_main/campaign_14_2` battle_0：`if self.clear_roadblocks([road_A5, road_H7], weakes` → 体内有 return
- `campaign_main/campaign_14_2` battle_0：`if self.clear_filter_enemy(self.ENEMY_FILTER, prese` → 体内有 return

## 先决条件（按出现频次）

1. **局部变量 + `if <局部变量>:` + 分支体**：`boss = self.map.select(is_boss=True)` 这类「观察」，
   以及 `branch` 步骤（条件为局部变量或一次原语调用，体内是步骤序列）；
2. **局部变量的实参引用**：`check_accessibility(boss[0], fleet='boss')` 里的 `boss[0]`；
3. 其它形态（`compare` 条件、`for` 循环、`raise` 体）另计，需要单独设计，不要硬塞进上面的结构。

