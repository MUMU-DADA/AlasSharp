# 识别特异性矩阵（从 29 页回归证据挖出）

逐页验证只证明"规则在自己的页面上为真"；它证明不了**规则不会在别人的页面上乱为真**。
而恒真/乱真的规则会让导航到处误判 —— 这一页就是查这个。

数据来源：`data/regress_pages.json`。回归脚本每到一个页面都跑一次全量页面判定，
所以"29 页 × 53 条规则"的命中矩阵已经在证据里，本页不重新跑设备。
生成：`tools/diagnostics/analyze_specificity.py`。

## 汇总

| 类别 | 数量 | 含义 |
| --- | --- | --- |
| 精确命中 | 22 | 只在自己的页面上命中（最理想） |
| 共命中 | 7 | 同一屏多条规则同时命中（见下） |
| 从未命中 | 5 | 29 个可达页面上一次都没命中 |

## 共命中（要确知是上游设计，不是误判）

| 规则 | 同时命中的页面 |
| --- | --- |
| `page_campaign_menu` | `page_campaign_menu`, `page_os` |
| `page_dormmenu` | `page_dormmenu`, `page_private_quarters` |
| `page_main` | `page_event_list`, `page_main`, `page_main_white`, `page_meowfficer` |
| `page_main_white` | `page_event_list`, `page_main`, `page_main_white`, `page_meowfficer` |
| `page_munitions` | `page_munitions`, `page_shop`, `page_supply_pack` |
| `page_shop` | `page_munitions`, `page_shop`, `page_supply_pack` |
| `page_supply_pack` | `page_munitions`, `page_shop`, `page_supply_pack` |

已知的两组共命中都是上游设计：

- `page_main` / `page_main_white`：同一张主界面的两种皮肤，本来就同时成立；
- `page_shop` / `page_munitions` / `page_supply_pack`：商店是同一套界面的子页签，
三条 check 同时为真（区分当前页签要靠 `ShopUI` 的 Navbar/Switch 规则）。

## 疑似误判（命中了自己以外的页面）

| 规则 | 意外命中的页面 |
| --- | --- |
| `page_campaign_menu` | `page_os` |
| `page_dormmenu` | `page_private_quarters` |
| `page_munitions` | `page_shop`, `page_supply_pack` |
| `page_shop` | `page_munitions`, `page_supply_pack` |
| `page_supply_pack` | `page_munitions`, `page_shop` |

## 从未命中（含受阻塞页面）

这些规则在 29 个可达页面上一次都没命中 —— 对**受游戏状态阻塞**的页面来说，
这是能拿到的最强证据：**它们至少不是恒真规则**（恒真的规则会在任何页面上乱命中）。
反过来说，如果某条这里列出的规则本该在可达页面上命中，那它就是漏检，需要排查。

未在回归里命中、且属于"已验证页面"的规则（即漏检嫌疑）：

- `page_event_list`, `page_guild`, `page_meowfficer`, `page_os`, `page_private_quarters`

其余未命中的规则（本就不可达，属正常）：`page_channel`, `page_coalition`, `page_event_list`, `page_hospital`, `page_island`, `page_raid`, `page_rpg_stage`, `page_rpg_story`, `page_sp`, `page_unknown`
