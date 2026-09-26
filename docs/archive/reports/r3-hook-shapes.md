# R3 钩子形态分类（决定谁值得迁移）

> 本页由 `tools/diagnostics/r3_hook_shapes.py` 从关卡 IR + 章节源码**静态**生成，**不手写**。
> 分桶是启发式：标签给出**判断依据**（super 调用数 / 调用了哪些 self 方法 / 引用哪些模块常量 / 有无控制流），
> 便于人工复核，而不是让读者相信一个结论。

## 分桶含义

| 标签 | 含义 | 对 R3 的意义 |
| --- | --- | --- |
| `pure_delegate` | 体里只有 `super().X(...)` | **没有可迁移的东西** |
| `data_only` | 不调用 `self.*`，只做数据/常量改写 | 是**章节自己的数据**（属上游关卡规则），不该搬进 C# |
| `self_calls` | 调用其它 `self.*` 方法 | 这才是**引擎能力**候选，需逐个看依赖闭包与对拍成本 |
| `not_in_module` | IR 说覆盖了该钩子，但源码里没有定义 | 继承来的；如实列出，不猜 |

## 逐钩子

| 钩子 | 覆盖章节 | pure_delegate | data_only | chapter_local | **engine_calls** | not_in_module | 例 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `map_data_init` | 15 | 0 | 15 | 0 | 0 | 0 | `campaign_14_4.py`(data_only) |
| `combat_status` | 8 | 0 | 8 | 0 | 0 | 0 | `ht3.py`(data_only) |
| `get_map_clear_percentage` | 8 | 8 | 0 | 0 | 0 | 0 | `a2.py`(pure_delegate) |
| `in_sight` | 4 | 0 | 2 | 0 | 2 | 0 | `b3.py`(engine_calls) |
| `_expected_end` | 3 | 0 | 3 | 0 | 0 | 0 | `campaign_hard.py`(data_only) |
| `clear_boss` | 3 | 0 | 0 | 2 | 1 | 0 | `campaign_hard.py`(engine_calls) |
| `handle_clear_mode_config_cover` | 3 | 2 | 1 | 0 | 0 | 0 | `t4.py`(data_only) |
| `map_init` | 3 | 0 | 3 | 0 | 0 | 0 | `campaign_16_3.py`(data_only) |
| `before_boss` | 2 | 0 | 2 | 0 | 0 | 0 | `b2.py`(data_only) |
| `brute_clear_boss` | 2 | 0 | 0 | 2 | 0 | 0 | `b2.py`(chapter_local) |
| `_campaign_ocr_result_process` | 1 | 0 | 1 | 0 | 0 | 0 | `sp.py`(data_only) |
| `catch_camera_repositioning` | 1 | 0 | 1 | 0 | 0 | 0 | `t4.py`(data_only) |
| `execute_actions` | 1 | 0 | 0 | 0 | 1 | 0 | `sp.py`(engine_calls) |

## 结论（用数据说话）

- **有引擎能力候选（出现 `self_calls`）的钩子：3 个** —— `clear_boss`、`execute_actions`、`in_sight`
- 只做数据改写（`data_only`）的钩子：9 个 —— 这些留在上游，不搬进 C#。
- 纯委托（`pure_delegate`）：2 个 —— 没有工作量。

> 与 `docs/archive/reports/r3-candidates.md`（按覆盖数排序）**配合使用**：覆盖数决定"影响面"，
> 形态决定"值不值得做、做了能不能对拍"。上一轮 `map_data_init` 就是覆盖数第一但形态不统一。

复现：`python tools/diagnostics/r3_hook_shapes.py`。
