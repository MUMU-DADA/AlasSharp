# R5 目标选择扫描（C# 选择器 vs 上游 `Filter`）

> 本报告由 `tools/diagnostics/r5_selection_sweep.py` 重建，不手写。
> 覆盖范围：`clear_filter_enemy` 的**过滤器 + 排序 + preserve** 语义（上游 `Filter` DSL）；
> 过滤器串取自库里的真实用法（5 个不同串）加两个优先级预设；状态是**确定性随机**生成的。

- 状态数：**200**（seed=7）
- 用例数：**2166**
- 不一致：**421**

## 不一致（前 20 条）

| 用例 | 参数 | 差异 |
| --- | --- | --- |
| state0-f0-preserve0 | `filter=1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C preserve=0` | C# 'A2' vs 上游 'C1' |
| state0-f0-preserve2 | `filter=1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C preserve=2` | C# None vs 上游 'A2' |
| state0-f1-preserve1 | `filter=1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M preserve=1` | C# 'A2' vs 上游 'B2' |
| state0-f1-preserve2 | `filter=1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M preserve=2` | C# None vs 上游 'B1' |
| state0-f2-preserve0 | `filter=1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C preserve=0` | C# 'A2' vs 上游 'C1' |
| state0-f2-preserve1 | `filter=1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C preserve=1` | C# None vs 上游 'B1' |
| state0-f3-preserve0 | `filter=1L > 1M > 2L > 2M > 3L > 3M > 1E > 2E > 3E > 1C > 2C > 3C preserve=0` | C# 'A2' vs 上游 'B1' |
| state0-f3-preserve1 | `filter=1L > 1M > 2L > 2M > 3L > 3M > 1E > 2E > 3E > 1C > 2C > 3C preserve=1` | C# None vs 上游 'A2' |
| state0-f3-preserve2 | `filter=1L > 1M > 2L > 2M > 3L > 3M > 1E > 2E > 3E > 1C > 2C > 3C preserve=2` | C# None vs 上游 'C1' |
| state0-f4-preserve0 | `filter=1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M preserve=0` | C# 'A2' vs 上游 'B1' |
| state0-f4-preserve1 | `filter=1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M preserve=1` | C# None vs 上游 'A2' |
| state0-S1_enemy_first | `filter=S1_enemy_first preserve=0` | C# 'A2' vs 上游 'C1' |
| state1-f0-preserve2 | `filter=1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C preserve=2` | C# 'F4' vs 上游 'B3' |
| state1-f1-preserve1 | `filter=1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M preserve=1` | C# 'A4' vs 上游 'C1' |
| state1-f1-preserve2 | `filter=1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M preserve=2` | C# None vs 上游 'A4' |
| state1-f2-preserve0 | `filter=1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C preserve=0` | C# 'C1' vs 上游 'B2' |
| state1-f2-preserve2 | `filter=1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C preserve=2` | C# 'F4' vs 上游 'A4' |
| state1-f3-preserve2 | `filter=1L > 1M > 2L > 2M > 3L > 3M > 1E > 2E > 3E > 1C > 2C > 3C preserve=2` | C# 'F4' vs 上游 'B2' |
| state1-f4-preserve2 | `filter=1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M preserve=2` | C# 'F4' vs 上游 'B2' |
| state1-S3_enemy_first | `filter=S3_enemy_first preserve=0` | C# 'F4' vs 上游 'B2' |
