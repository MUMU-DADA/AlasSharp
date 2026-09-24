# R3 第一项的上游轨迹：`map_data_init`

> 本页由 `tools/diagnostics/r3_map_data_init.py` 生成，**不手写**。

## 这个钩子是什么（读上游源码得来）

```python
def map_data_init(self, map_):
    super().map_data_init(map_)
    if not self.map_is_clear_mode:
        for override_grid in OVERRIDE:
            # Set may_enemy, but keep may_ambush
            self.map[override_grid.location].may_enemy = override_grid.may_enemy
```

带该钩子的章节 **15** 个（`docs/archive/reports/r3-candidates.md` 里覆盖数居首）。

## 轨迹抽取结果（如实）

- 静态抽出 `OVERRIDE` 的章节：**0** / 15
- 未能静态抽出：**15**（原因逐条列在下面）

| 章节 | 原因 |
| --- | --- |
| `campaign/campaign_main/campaign_14_4.py` | OVERRIDE 不是字面量列表（Call） |
| `campaign/event_20211125_cn/t4.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20220915_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20230223_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20231026_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20231221_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20240425_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20240521_cn/a1.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20240521_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20240815_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20241024_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20241121_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20250912_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20251023_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |
| `campaign/event_20260326_cn/sp.py` | 该章节源码里没有模块级 OVERRIDE |

## 这轮得到的结论（对 R3 的意义）

- **`map_data_init` 不是一次统一迁移**：15 个章节里没有一个是「静态可抽的 OVERRIDE 列表」这一种形态 —— 它们各自引用别的常量、或在模块内以别名定义，形态并不统一。
- 因此 R3 对它的正确做法不是"实现一个 map_data_init"，而是**先把每个章节的钩子体分类**（纯数据改写 / 引用常量 / 其它行为），再判断哪些属于"通用能力"、哪些只是章节自己的数据。
- 路线 R3 的纪律照旧：**对拍不完整就继续走上游宿主** —— 现在这一项就不该往下走，因为连"输入是什么"都还没能静态固定下来。

复现：`python tools/diagnostics/r3_map_data_init.py`。
