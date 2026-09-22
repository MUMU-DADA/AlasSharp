# 页面规则的合成正对照

**这不是真机命中。** 真机命中见 `page-verification.md`（29/53）。
正对照的做法是：把每条页面规则的 check 素材**自己的模板图**贴到**它自己的 area** 上，
再跑一次上游的 `ui_page_appear` —— 规则此时应当返回真。

它证明的是"规则是活的"：素材文件能加载、区域与模板配对正确、判定方向没写反。

为什么值得单独做：受账号进度/活动/客户端版本所限，有 12 个页面在真机上到不了。
这些页面是"到不了"还是"规则本身坏了"，光靠真机验证分不清 —— 正对照把它们分开：
正对照过不了的规则一定是实现问题（素材路径错、模板空、区域写错），必须查。

生成：`tools/diagnostics/verify_positive_control.py`；数据 `data/positive_control.json`。

## 结果

| 项 | 数量 |
| --- | --- |
| 总计（上游 Page 数） | 53 |
| 正对照通过 | **52** |
| 跳过（无 check 素材 / 素材区域超出画面） | 1 |
| 失败 | 0 |

## 失败项（实现问题，必须查）

无。53 条页面规则在正对照下全部返回真（除合成实体 `page_unknown`）。

## 跳过项

| 页面 | 原因 |
| --- | --- |
| `page_unknown` | Page(None)：没有 check 素材（合成实体） |

## 控件规则（模块级 Switch）的正对照

做法：对开关的**每个状态**，单独把该状态的 check 素材贴到它自己的区域，
再调上游 `Switch.get()` —— 应当正好返回那个状态名。

| 项 | 数量 |
| --- | --- |
| 模块级规则总数 | 20 |
| Switch 正对照通过 | **10** |
| 跳过（Scroll：判定依赖颜色/掩码，贴模板图构造不出来） | 10 |
| 失败 | 0 |

通过的开关（含真机上到不了的）：

| 开关 | 每个状态贴图后的 get() 结果 |
| --- | --- |
| `COMMISSION_SWITCH` | daily→daily；urgent→urgent |
| `equipping_filter` | on→on；off→off |
| `FLEET_LOCK` | on→on；off→off |
| `FORMATION` | line_ahead→line_ahead；double_line→double_line；diamond→diamond |
| `SUBMARINE_HUNT` | on→on；off→off |
| `SUBMARINE_VIEW` | on→on；off→off |
| `ISLAND_DOCK_SORTING` | Ascending→Ascending；Descending→Descending |
| `SWITCH_LOCK` | lock→lock；unlock→unlock |
| `DOCK_SORTING` | Ascending→Ascending；Descending→Descending |
| `DOCK_FAVOURITE` | on→on；off→off |

注意 `equipping_filter` / `FLEET_LOCK` / `FORMATION` / `SUBMARINE_HUNT` /
`SUBMARINE_VIEW` / `ISLAND_DOCK_SORTING` / `SWITCH_LOCK` 这几条在真机上到不了，
但正对照全过 —— 说明它们的**状态判定是活的**，缺的只是游戏走到那一屏的条件。

10 个 Scroll 无法用贴图构造（`at_top`/`at_bottom` 比的是滚动条颜色掩码）；
其中 6 个已在真机上命中过（见 `controls.md`），剩 4 个受阻塞。

## 与真机结果的关系

正对照通过但真机没验过的页面共 18 个 —— 它们都是受外部条件阻塞的：

`page_channel`、`page_coalition`、`page_hospital`、`page_island`、`page_island_manage`、`page_island_map`、`page_island_order`、`page_island_phone`、`page_island_season`、`page_island_shop`、`page_island_storage`、`page_island_technology`、`page_island_transport`、`page_raid`、`page_rpg_city`、`page_rpg_stage`、`page_rpg_story`、`page_sp`

也就是说：**这些页面的规则本身是好的，缺的只是"让游戏走到那一屏"的条件**
（账号解锁岛屿/大舰队/指挥喵/大型作战、或对应类型的活动在跑、或客户端版本支持）。

## 复现

```powershell
python tools/diagnostics/verify_positive_control.py
```
