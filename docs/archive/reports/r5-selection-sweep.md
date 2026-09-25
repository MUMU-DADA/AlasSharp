# R5 目标选择扫描（C# 选择器 vs 上游 `Filter`）

> 本报告由 `tools/diagnostics/r5_selection_sweep.py` 重建，不手写。
> 覆盖范围：`clear_filter_enemy` 的**过滤器 + 排序 + preserve** 语义（上游 `Filter` DSL）；
> 过滤器串取自库里的真实用法（5 个不同串）加两个优先级预设；状态是**确定性随机**生成的。

- 状态数：**200**（seed=7）
- 用例数：**2166**
- 不一致：**0**
