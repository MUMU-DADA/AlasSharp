# 页面识别全量回归（产品路径）

用产品队列的 `navigate` 任务对**已验证的每个页面**重跑一遍：既验证页面规则在各自页面上命中，
也验证上游 `UI.ui_ensure()` 的页面图、识别和点击流程本身没退化。
本次样本经产品队列的 `navigate` 任务采集。

为什么需要单独做这一遍：早先的页面验证是分批做的（诊断脚本按资产坐标导航），
后来导航换成了产品实现 —— 实现变了，"已验证"就必须重新证明。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。
脚本：`tools/diagnostics/regress_pages.py`；原始数据 `data/regress_pages.json`。
账号前提：按 `data/account_probe.json`，本次仅解锁 **1 章 / 2 关**（第 1 章）。未解锁功能的入口不可达，会直接反映在上面的通过数里 —— **换号后必须重记基线，不能与旧数字直接比较。**


## 结果：30 / 34 通过

| 页面 | 结果 | 耗时 | 导航输出 |
| --- | --- | --- | --- |
| `page_academy` | ok | 10.0s | [原生导航] phase=to_target destination=page_academy arrived=true final=page_academy changed=true elapsed_ms=5949.9 |
| `page_archives` | ok | 8.0s | [原生导航] phase=to_target destination=page_archives arrived=true final=page_archives changed=true elapsed_ms=5847.8 |
| `page_battle_pass` | ok | 9.2s | [原生导航] phase=to_target destination=page_battle_pass arrived=true final=page_battle_pass changed=true elapsed_ms=7044.4 |
| `page_build` | ok | 7.1s | [原生导航] phase=to_target destination=page_build arrived=true final=page_build changed=true elapsed_ms=4810.9 |
| `page_campaign` | ok | 8.0s | [原生导航] phase=to_target destination=page_campaign arrived=true final=page_campaign changed=true elapsed_ms=5899.4 |
| `page_campaign_menu` | ok | 6.2s | [原生导航] phase=to_target destination=page_campaign_menu arrived=true final=page_campaign_menu changed=true elapsed_ms=3675.6 |
| `page_commission` | ok | 8.4s | [原生导航] phase=to_target destination=page_commission arrived=true final=page_commission changed=true elapsed_ms=6107.3 |
| `page_daily` | ok | 8.1s | [原生导航] phase=to_target destination=page_daily arrived=true final=page_daily changed=true elapsed_ms=5914.3 |
| `page_dock` | ok | 6.9s | [原生导航] phase=to_target destination=page_dock arrived=true final=page_dock changed=true elapsed_ms=4709.7 |
| `page_dorm` | ok | 8.1s | [原生导航] phase=to_target destination=page_dorm arrived=true final=page_dorm changed=true elapsed_ms=5970.8 |
| `page_dormmenu` | ok | 7.3s | [原生导航] phase=to_target destination=page_dormmenu arrived=true final=page_dormmenu changed=true elapsed_ms=4900.2 |
| `page_event` | ok | 8.2s | [原生导航] phase=to_target destination=page_event arrived=true final=page_event changed=true elapsed_ms=5895.4 |
| `page_event_list` | ok | 7.0s | [原生导航] phase=to_target destination=page_event_list arrived=true final=page_event_list changed=true elapsed_ms=4711.4 |
| `page_exercise` | ok | 8.1s | [原生导航] phase=to_target destination=page_exercise arrived=true final=page_exercise changed=true elapsed_ms=5948.9 |
| `page_fleet` | ok | 6.8s | [原生导航] phase=to_target destination=page_fleet arrived=true final=page_fleet changed=true elapsed_ms=4692.6 |
| `page_game_room` | ok | 9.4s | [原生导航] phase=to_target destination=page_game_room arrived=true final=page_game_room changed=true elapsed_ms=7135.4 |
| `page_guild` | goto-failed | 189.4s | [原生导航] phase=to_target destination=page_guild arrived=false final=page_main_white changed= elapsed_ms=183607<br>[导航失败] kind=GameStuckError GameStuckError: Wait too long |
| `page_mail` | ok | 7.0s | [原生导航] phase=to_target destination=page_mail arrived=true final=page_mail changed=true elapsed_ms=4895.9 |
| `page_main` | ok | 5.7s | [原生导航] phase=to_target destination=page_main arrived=true final=page_main changed=true elapsed_ms=3503 |
| `page_main_white` | ok | 4.4s | [原生导航] phase=to_target destination=page_main arrived=true final=page_main changed=false elapsed_ms=2232.9 |
| `page_meowfficer` | goto-failed | 35.0s | [原生导航] phase=to_target destination=page_meowfficer arrived=false final=page_dormmenu changed= elapsed_ms=28994.3<br>[导航失败] kind=GameTooManyClickError GameTooManyClickError: Too many click between 2 buttons: MAIN_GOTO_DORMMENU_WHITE, DORMMENU_GOTO_MEOWFFICER |
| `page_meta` | ok | 8.0s | [原生导航] phase=to_target destination=page_meta arrived=true final=page_meta changed=true elapsed_ms=5848.8 |
| `page_mission` | ok | 6.8s | [原生导航] phase=to_target destination=page_mission arrived=true final=page_mission changed=true elapsed_ms=4698.5 |
| `page_munitions` | ok | 9.2s | [原生导航] phase=to_target destination=page_munitions arrived=true final=page_munitions changed=true elapsed_ms=7112.2 |
| `page_os` | goto-failed | 67.0s | [原生导航] phase=to_target destination=page_os arrived=false final=page_campaign_menu changed= elapsed_ms=61057.1<br>[导航失败] kind=GameTooManyClickError GameTooManyClickError: Too many click for a button: CAMPAIGN_MENU_GOTO_OS |
| `page_private_quarters` | goto-failed | 36.4s | [原生导航] phase=to_target destination=page_private_quarters arrived=false final=page_dormmenu changed= elapsed_ms=30309.8<br>[导航失败] kind=GameTooManyClickError GameTooManyClickError: Too many click between 2 buttons: MAIN_GOTO_DORMMENU_WHITE, DORMMENU_GOTO_PRIVATE_QUARTERS |
| `page_research` | ok | 8.0s | [原生导航] phase=to_target destination=page_research arrived=true final=page_research changed=true elapsed_ms=5916.7 |
| `page_reshmenu` | ok | 6.9s | [原生导航] phase=to_target destination=page_reshmenu arrived=true final=page_reshmenu changed=true elapsed_ms=4734.1 |
| `page_reward` | ok | 7.0s | [原生导航] phase=to_target destination=page_reward arrived=true final=page_reward changed=true elapsed_ms=4780.3 |
| `page_shipyard` | ok | 8.2s | [原生导航] phase=to_target destination=page_shipyard arrived=true final=page_shipyard changed=true elapsed_ms=5887 |
| `page_shop` | ok | 6.8s | [原生导航] phase=to_target destination=page_shop arrived=true final=page_shop changed=true elapsed_ms=4724.6 |
| `page_storage` | ok | 7.0s | [原生导航] phase=to_target destination=page_storage arrived=true final=page_storage changed=true elapsed_ms=4866.9 |
| `page_supply_pack` | ok | 7.0s | [原生导航] phase=to_target destination=page_supply_pack arrived=true final=page_supply_pack changed=true elapsed_ms=4732.6 |
| `page_tactical` | ok | 8.4s | [原生导航] phase=to_target destination=page_tactical arrived=true final=page_tactical changed=true elapsed_ms=6161.7 |

## 顺带发现：上游页面图里有"无入边"节点

上游图共 53 节点 / 127 边，其中 **4 个节点没有任何入边**：`page_channel`, `page_main_white`, `page_rpg_city`, `page_unknown`。

这类节点**不可能是导航目标**（没人能"走到"它），它们是：

- `page_main_white`：同一张主界面的另一种皮肤，与 `page_main` 同屏命中；
- `page_channel`：世界频道浮层，上游只定义了 check 素材、没有任何入口素材；
- `page_unknown`：`Page(None)`，合成实体；
- `page_rpg_city`：RPG 活动的城内界面，只有出边（回主界面/回剧情页），没有入边。

所以对它们只能验"同屏被检测到"（本脚本用它的同屏兄弟页做锚点），
不能验"能导航到"—— 这是上游的设计，不是缺陷。


## 未通过

- `page_guild`：goto-failed（命中=[]）
- `page_meowfficer`：goto-failed（命中=['page_dormmenu']）
- `page_os`：goto-failed（命中=['page_campaign_menu']）
- `page_private_quarters`：goto-failed（命中=['page_dormmenu']）

## 复现

```powershell
$env:STUB_ADB = "<adb.exe>"
python tools/diagnostics/regress_pages.py
```
