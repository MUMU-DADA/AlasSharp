# R5 决策层全库对拍（C# 钩子选择 vs 上游 `battle_function`）

> 本报告由 `tools/diagnostics/r5_decision_sweep.py` 重建，不手写。
> 基准是**上游真实代码**：导入关卡模块、把 `battle_function` 绑到替身实例上调用，看它选了哪个钩子。
> 只覆盖**默认变体**（`@Config.when` 在导入时求值，仓库配置下即默认变体）；两个变体由 shadow/loop 用例覆盖。

- 关卡数：**1437**
- 逐点比较：**6660** 次（每关 battle_count ∈ [0, 1, 2, 3, 4]）
- 一致：**6660**；不一致：**0**

## 跳过（如实列出原因，不当作通过）

| 原因 | 次数 |
| --- | --- |
| 上游导入/调用失败：AttributeError | 10 |
| 上游导入/调用失败：ImportError | 15 |
| 该模块没有 `Campaign` 类（关卡家族基类，不是关卡） | 330 |
| 该模块没有定义 `battle_<数字>` 钩子（章节基类，不是关卡） | 170 |

### 例子（每类最多 3 条）

- **该模块没有定义 `battle_<数字>` 钩子（章节基类，不是关卡）**
  - `campaign_hard/campaign_hard battle_count=0：该模块没有定义 `battle_<数字>` 钩子（章节基类，不是关卡）`
  - `campaign_hard/campaign_hard battle_count=1：该模块没有定义 `battle_<数字>` 钩子（章节基类，不是关卡）`
  - `campaign_hard/campaign_hard battle_count=2：该模块没有定义 `battle_<数字>` 钩子（章节基类，不是关卡）`
- **该模块没有 `Campaign` 类（关卡家族基类，不是关卡）**
  - `campaign_main/campaign_14_base battle_count=0：该模块没有 `Campaign` 类（关卡家族基类，不是关卡）`
  - `campaign_main/campaign_14_base battle_count=1：该模块没有 `Campaign` 类（关卡家族基类，不是关卡）`
  - `campaign_main/campaign_14_base battle_count=2：该模块没有 `Campaign` 类（关卡家族基类，不是关卡）`
- **上游导入/调用失败：ImportError**
  - `event_20200227_cn/c2 battle_count=0：cannot import name 'C2' from 'module.campaign.assets' (C:\Users\mumu\source\ALAS fork project\csharp\.runtime\engine\module\campaign\assets.py)`
  - `event_20200227_cn/c2 battle_count=1：cannot import name 'C2' from 'module.campaign.assets' (C:\Users\mumu\source\ALAS fork project\csharp\.runtime\engine\module\campaign\assets.py)`
  - `event_20200227_cn/c2 battle_count=2：cannot import name 'C2' from 'module.campaign.assets' (C:\Users\mumu\source\ALAS fork project\csharp\.runtime\engine\module\campaign\assets.py)`
- **上游导入/调用失败：AttributeError**
  - `event_20250520_cn/b3 battle_count=0：'Campaign' object has no attribute 'map'`
  - `event_20250520_cn/b3 battle_count=1：'Campaign' object has no attribute 'map'`
  - `event_20250520_cn/b3 battle_count=2：'Campaign' object has no attribute 'map'`
