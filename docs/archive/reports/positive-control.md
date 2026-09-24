# 页面规则的合成正对照

**这不是真机命中。** 真机命中见 `page-verification.md`（覆盖数见 `status.md`）。
正对照的做法是：把每条页面规则的 check 素材**自己的模板图**贴到**它自己的 area** 上，
再跑一次上游的 `ui_page_appear` —— 规则此时应当返回真。

它证明的是"规则是活的"：素材文件能加载、区域与模板配对正确、判定方向没写反。

本次清单中有 18 个页面没有历史真机命中证据。
正对照只能证明合成输入上的原生判定；真实画面、导航入口和业务结果仍需各自验证。

生成：`tools/diagnostics/verify_positive_control.py`；数据 `data/positive_control.json`。

## 结果

| 项 | 数量 |
| --- | --- |
| 总计（上游 Page 数） | 53 |
| 正对照通过 | **52** |
| 跳过（无 check 素材 / 素材区域超出画面） | 1 |
| 失败 | 0 |

## 失败项（实现问题，必须查）

无。本次 52 条页面规则返回真，1 条跳过。

## 跳过项

| 页面 | 原因 |
| --- | --- |
| `page_unknown` | Page(None)：没有 check 素材（合成实体） |

## 控件规则（模块级 Switch）的正对照

做法：对开关的**每个状态**，单独把该状态的 check 素材贴到它自己的区域，
再调上游 `Switch.get()` —— 应当正好返回那个状态名。

| 项 | 数量 |
| --- | --- |
| 模块级规则总数 | 31 |
| Switch 正对照通过 | **15** |
| 跳过（颜色掩码或子类原生识别流程不适用模板贴图） | 16 |
| 失败 | 0 |

合成正对照通过的开关：

| 开关 | 每个状态贴图后的 get() 结果 |
| --- | --- |
| `MODE_SWITCH_1` | normal→normal；hard→hard |
| `MODE_SWITCH_2` | hard→hard；ex→ex |
| `MODE_SWITCH_20241219` | combat→combat；story→story |
| `ASIDE_SWITCH_20241219` | part1→part1；part2→part2；sp→sp；ex→ex |
| `COMMISSION_SWITCH` | daily→daily；urgent→urgent |
| `equipping_filter` | on→on；off→off |
| `FLEET_LOCK` | on→on；off→off |
| `FORMATION` | line_ahead→line_ahead；double_line→double_line；diamond→diamond |
| `SUBMARINE_HUNT` | on→on；off→off |
| `SUBMARINE_VIEW` | on→on；off→off |
| `ISLAND_DOCK_SORTING` | Ascending→Ascending；Descending→Descending |
| `SWITCH_LOCK` | lock→lock；unlock→unlock |
| `fleet_lock` | on→on；off→off |
| `DOCK_SORTING` | Ascending→Ascending；Descending→Descending |
| `DOCK_FAVOURITE` | on→on；off→off |

## 控件跳过或失败

| 规则 | 结果 | 原因 |
| --- | --- | --- |
| `COMMISSION_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `EQUIPMENT_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `HOSPITAL_TAB` | skip | 原生子类有独立识别流程，模板贴图不构成该流程的正样本 |
| `CLEAR_MODE` | skip | 原生子类有独立识别流程，模板贴图不构成该流程的正样本 |
| `AUTO_SEARCH` | skip | 原生子类有独立识别流程，模板贴图不构成该流程的正样本 |
| `ISLAND_SEASON_TASK_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `MINIGAME_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `SCROLL_STORAGE` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `STRATEGIC_SEARCH_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `OS_SHOP_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `DOCK_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `RETIRE_CONFIRM_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `MEDAL_SHOP_SCROLL_250814` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `VOUCHER_SHOP_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `EVENT_SHOP_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |
| `MATERIAL_SCROLL` | skip | 判定依赖颜色/掩码，贴模板图构造不出来 |

## 与真机结果的关系

正对照通过但没有历史真机命中证据的页面共 18 个：

`page_channel`、`page_coalition`、`page_hospital`、`page_island`、`page_island_manage`、`page_island_map`、`page_island_order`、`page_island_phone`、`page_island_season`、`page_island_shop`、`page_island_storage`、`page_island_technology`、`page_island_transport`、`page_raid`、`page_rpg_city`、`page_rpg_stage`、`page_rpg_story`、`page_sp`

未覆盖原因需查对应现场证据；不能由合成模板命中推断真实客户端兼容或导航可达。

## 复现

```powershell
python tools/diagnostics/verify_positive_control.py
```
