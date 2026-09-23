# 页面识别全量回归（产品路径）

用 `alashub queue --file` 的 `navigate` 任务对**已验证的每个页面**重跑一遍：既验证页面规则在各自页面上命中，
也验证导航器（运行时取自上游的页面图 + 变体择优 + 未建模画面自救）本身没退化。
当前存档是退役直接导航入口的历史样本；需重新运行脚本验证队列入口。

为什么需要单独做这一遍：早先的页面验证是分批做的（诊断脚本按资产坐标导航），
后来导航换成了产品实现 —— 实现变了，"已验证"就必须重新证明。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。
脚本：`tools/diagnostics/regress_pages.py`；原始数据 `data/regress_pages.json`。
账号前提：按 `data/account_probe.json`，本次仅解锁 **1 章 / 2 关**（第 1 章）。未解锁功能的入口不可达，会直接反映在上面的通过数里 —— **换号后必须重记基线，不能与旧数字直接比较。**


## 结果：29 / 34 通过

| 页面 | 结果 | 耗时 | 跳数 | 导航输出 |
| --- | --- | --- | --- | --- |
| `page_academy` | ok | 8.7s | 2 | [hop 1   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9959 at (562,679) -> page_dormmenu<br>[hop 2   ] on=page_dormmenu click ui/DORMMENU_GOTO_ACADEMY score=0.9877 at (298,537) -> page_academy |
| `page_archives` | ok | 11.5s | 3 | [hop 1   ] on=page_academy click ui_white/GOTO_MAIN_WHITE score=0.2408(低置信) at (1227,32) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_CAMPAIGN_WHITE score=0.9447 at (1192,508) -> page_campaign_menu<br>[hop 3   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_WAR_ARCHIVES score=0.9983 at (260,612) -> page_archives |
| `page_battle_pass` | ok | 14.3s | 4 | [hop 1   ] on=page_archives click ui/WAR_ARCHIVES_GOTO_CAMPAIGN_MENU score=0.9863 at (42,390) -> page_campaign_menu<br>[hop 2   ] on=page_campaign_menu click ui/GOTO_MAIN score=0.2448(低置信) at (1240,10) -> page_main,page_main_white<br>[hop 3   ] on=page_main click ui_white/MAIN_GOTO_REWARD_WHITE score=0.9919 at (20,234) -> page_reward<br>[hop 4   ] on=page_reward click ui/REWARD_GOTO_BATTLE_PASS score=0.1820(低置信) at (599,166) -> page_battle_pass |
| `page_build` | ok | 8.5s | 2 | [hop 1   ] on=page_battle_pass click ui_white/GOTO_MAIN_WHITE score=0.9689 at (1224,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_BUILD_WHITE score=0.9375 at (1031,680) -> page_build |
| `page_campaign` | ok | 11.4s | 3 | [hop 1   ] on=page_build click ui_white/GOTO_MAIN_WHITE score=0.3264(低置信) at (1228,12) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_CAMPAIGN_WHITE score=0.9447 at (1192,508) -> page_campaign_menu<br>[hop 3   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_CAMPAIGN score=0.9990 at (350,327) -> page_campaign |
| `page_campaign_menu` | ok | 5.5s | 1 | [hop 1   ] on=page_campaign click ui/BACK_ARROW score=0.9977 at (57,54) -> page_campaign_menu |
| `page_commission` | ok | 11.3s | 3 | [hop 1   ] on=page_campaign_menu click ui/GOTO_MAIN score=0.2448(低置信) at (1240,10) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_REWARD_WHITE score=0.9919 at (20,234) -> page_reward<br>[hop 3   ] on=page_reward click ui_white/REWARD_GOTO_COMMISSION_WHITE score=0.9716 at (467,278) -> page_commission |
| `page_daily` | ok | 11.4s | 3 | [hop 1   ] on=page_commission click ui/GOTO_MAIN score=0.2309(低置信) at (1238,6) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_CAMPAIGN_WHITE score=0.9447 at (1192,508) -> page_campaign_menu<br>[hop 3   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_DAILY score=0.8008(低置信) at (737,612) -> page_daily |
| `page_dock` | ok | 8.6s | 2 | [hop 1   ] on=page_daily click ui/GOTO_MAIN score=0.9499 at (1240,32) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_DOCK_WHITE score=0.9938 at (249,680) -> page_dock |
| `page_dorm` | ok | 11.5s | 3 | [hop 1   ] on=page_dock click ui/GOTO_MAIN score=0.9215 at (1242,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu<br>[hop 3   ] on=page_dormmenu click ui/DORMMENU_GOTO_DORM score=0.9897 at (474,540) -> page_dorm |
| `page_dormmenu` | ok | 8.6s | 2 | [hop 1   ] on=page_dorm click ui/DORM_GOTO_MAIN score=0.9995 at (51,45) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu |
| `page_event` | ok | 11.8s | 3 | [hop 1   ] on=page_dormmenu click ui/DORMMENU_GOTO_MAIN score=0.9877 at (294,216) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_CAMPAIGN_WHITE score=0.9447 at (1192,508) -> page_campaign_menu<br>[hop 3   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_EVENT score=0.9990 at (755,222) -> page_event |
| `page_event_list` | goto-failed | 26.7s | 8 | [hop 1   ] on=page_event click ui_white/GOTO_MAIN_WHITE score=0.3038(低置信) at (1228,12) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1324(低置信) at (635,152) -> page_main,page_main_white<br>[hop 3   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1327(低置信) at (635,152) -> page_main,page_main_white<br>[hop 4   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1324(低置信) at (635,158) -> page_main,page_main_white<br>[hop 5   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1321(低置信) at (635,157) -> page_main,page_main_white<br>[hop 6   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1324(低置信) at (635,152) -> page_main,page_main_white<br>[hop 7   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1327(低置信) at (635,152) -> page_main,page_main_white<br>[hop 8   ] on=page_main click ui/MAIN_GOTO_EVENT_LIST score=0.1326(低置信) at (635,159) -> page_main,page_main_white |
| `page_exercise` | ok | 8.4s | 2 | [hop 1   ] on=page_main click ui_white/MAIN_GOTO_CAMPAIGN_WHITE score=0.9447 at (1192,508) -> page_campaign_menu<br>[hop 2   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_EXERCISE score=0.9969 at (1058,612) -> page_exercise |
| `page_fleet` | ok | 8.4s | 2 | [hop 1   ] on=page_exercise click ui/GOTO_MAIN score=0.2335(低置信) at (1240,10) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_FLEET_WHITE score=0.9974 at (1061,508) -> page_fleet |
| `page_game_room` | ok | 14.5s | 4 | [hop 1   ] on=page_fleet click ui/GOTO_MAIN score=0.9976 at (1242,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu<br>[hop 3   ] on=page_dormmenu click ui/DORMMENU_GOTO_ACADEMY score=0.9877 at (298,537) -> page_academy<br>[hop 4   ] on=page_academy click ui/ACADEMY_GOTO_GAME_ROOM score=0.9750 at (1088,361) -> page_game_room |
| `page_guild` | goto-failed | 20.4s | 6 | [hop 1   ] on=page_game_room click ui/GAME_ROOM_GOTO_MAIN score=0.9975 at (1228,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_GUILD_WHITE score=0.9487 at (1188,680) -><br>[hop 0   ] on= click <BACK 自救> score=0.0000(低置信) at (0,0) -> page_main,page_main_white<br>[hop 4   ] on=page_main click ui_white/MAIN_GOTO_GUILD_WHITE score=0.9489 at (1188,680) -><br>[hop 0   ] on= click <BACK 自救> score=0.0000(低置信) at (0,0) -> page_main,page_main_white<br>[hop 6   ] on=page_main click ui_white/MAIN_GOTO_GUILD_WHITE score=0.9489 at (1188,680) -> |
| `page_mail` | ok | 8.2s | 2 | [hop 0   ] on= click <BACK 自救> score=0.0000(低置信) at (0,0) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIL_ENTER_WHITE score=0.9587 at (1052,37) -> page_mail |
| `page_main` | ok | 5.3s | 1 | [hop 1   ] on=page_mail click ui_white/GOTO_MAIN_WHITE score=0.9979 at (1225,36) -> page_main,page_main_white |
| `page_main_white` | ok | 2.5s | 0 | — |
| `page_meowfficer` | goto-failed | 26.9s | 8 | [hop 1   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu<br>[hop 2   ] on=page_dormmenu click ui/DORMMENU_GOTO_MEOWFFICER score=0.9894 at (692,540) -> page_main,page_main_white<br>[hop 3   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9961 at (562,679) -> page_dormmenu<br>[hop 4   ] on=page_dormmenu click ui/DORMMENU_GOTO_MEOWFFICER score=0.9894 at (692,540) -> page_main,page_main_white<br>[hop 5   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9959 at (562,679) -> page_dormmenu<br>[hop 6   ] on=page_dormmenu click ui/DORMMENU_GOTO_MEOWFFICER score=0.9894 at (692,540) -> page_main,page_main_white<br>[hop 7   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu<br>[hop 8   ] on=page_dormmenu click ui/DORMMENU_GOTO_MEOWFFICER score=0.9894 at (692,540) -> page_main,page_main_white |
| `page_meta` | ok | 8.5s | 2 | [hop 1   ] on=page_main click ui_white/MAIN_GOTO_RESHMENU_WHITE score=0.9941 at (720,680) -> page_reshmenu<br>[hop 2   ] on=page_reshmenu click ui/RESHMENU_GOTO_META score=0.0941(低置信) at (1112,264) -> page_meta |
| `page_mission` | ok | 8.3s | 2 | [hop 1   ] on=page_meta click ui/GOTO_MAIN score=0.2490(低置信) at (1240,10) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_MISSION_WHITE score=0.9957 at (876,680) -> page_mission |
| `page_munitions` | ok | 14.4s | 4 | [hop 1   ] on=page_mission click ui/GOTO_MAIN score=0.2272(低置信) at (1242,28) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu<br>[hop 3   ] on=page_dormmenu click ui/DORMMENU_GOTO_ACADEMY score=0.9877 at (298,537) -> page_academy<br>[hop 4   ] on=page_academy click ui/ACADEMY_GOTO_MUNITIONS score=0.9783 at (1090,199) -> page_munitions,page_shop,page_supply_pack |
| `page_os` | goto-failed | 26.8s | 8 | [hop 1   ] on=page_munitions click ui_white/GOTO_MAIN_WHITE score=0.9452 at (1223,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_CAMPAIGN_WHITE score=0.9448 at (1192,508) -> page_campaign_menu<br>[hop 3   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_OS score=0.9990 at (755,448) -> page_campaign_menu<br>[hop 4   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_OS score=0.9990 at (755,448) -> page_campaign_menu<br>[hop 5   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_OS score=0.9990 at (755,448) -> page_campaign_menu<br>[hop 6   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_OS score=0.9990 at (755,448) -> page_campaign_menu<br>[hop 7   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_OS score=0.9990 at (755,448) -> page_campaign_menu<br>[hop 8   ] on=page_campaign_menu click ui/CAMPAIGN_MENU_GOTO_OS score=0.9990 at (755,448) -> page_campaign_menu |
| `page_private_quarters` | goto-failed | 27.0s | 8 | [hop 1   ] on=page_campaign_menu click ui/GOTO_MAIN score=0.2448(低置信) at (1240,10) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu<br>[hop 3   ] on=page_dormmenu click ui/DORMMENU_GOTO_PRIVATE_QUARTERS score=0.0627(低置信) at (1074,485) -> page_main,page_main_white<br>[hop 4   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9961 at (562,679) -> page_dormmenu<br>[hop 5   ] on=page_dormmenu click ui/DORMMENU_GOTO_PRIVATE_QUARTERS score=0.0627(低置信) at (1074,485) -> page_main,page_main_white<br>[hop 6   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9960 at (562,679) -> page_dormmenu<br>[hop 7   ] on=page_dormmenu click ui/DORMMENU_GOTO_PRIVATE_QUARTERS score=0.0627(低置信) at (1074,485) -> page_main,page_main_white<br>[hop 8   ] on=page_main click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958 at (562,679) -> page_dormmenu |
| `page_research` | ok | 11.5s | 3 | [hop 1   ] on=page_dormmenu click ui/DORMMENU_GOTO_MAIN score=0.9877 at (294,216) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_RESHMENU_WHITE score=0.9941 at (720,680) -> page_reshmenu<br>[hop 3   ] on=page_reshmenu click ui/RESHMENU_GOTO_RESEARCH score=0.9849 at (385,343) -> page_research |
| `page_reshmenu` | ok | 8.4s | 2 | [hop 1   ] on=page_research click ui/GOTO_MAIN score=0.2246(低置信) at (1244,28) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_RESHMENU_WHITE score=0.9941 at (720,680) -> page_reshmenu |
| `page_reward` | ok | 8.4s | 2 | [hop 1   ] on=page_reshmenu click ui/GOTO_MAIN score=0.2275(低置信) at (1240,8) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_REWARD_WHITE score=0.9919 at (20,234) -> page_reward |
| `page_shipyard` | ok | 11.5s | 3 | [hop 1   ] on=page_reward click ui/REWARD_GOTO_MAIN score=0.0781(低置信) at (830,608) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_RESHMENU_WHITE score=0.9941 at (720,680) -> page_reshmenu<br>[hop 3   ] on=page_reshmenu click ui/RESHMENU_GOTO_SHIPYARD score=0.9754 at (646,336) -> page_shipyard |
| `page_shop` | ok | 8.4s | 2 | [hop 1   ] on=page_shipyard click ui/GOTO_MAIN score=0.2176(低置信) at (1244,28) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_SHOP_WHITE score=0.8323(低置信) at (91,680) -> page_munitions,page_shop,page_supply_pack |
| `page_storage` | ok | 8.4s | 2 | [hop 1   ] on=page_munitions click ui_white/GOTO_MAIN_WHITE score=0.9452 at (1223,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_STORAGE_WHITE score=0.9964 at (404,680) -> page_storage |
| `page_supply_pack` | ok | 8.4s | 2 | [hop 1   ] on=page_storage click ui/GOTO_MAIN score=0.9309 at (1240,32) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_SHOP_WHITE score=0.8323(低置信) at (91,680) -> page_munitions,page_shop,page_supply_pack |
| `page_tactical` | ok | 11.2s | 3 | [hop 1   ] on=page_munitions click ui_white/GOTO_MAIN_WHITE score=0.9452 at (1223,34) -> page_main,page_main_white<br>[hop 2   ] on=page_main click ui_white/MAIN_GOTO_REWARD_WHITE score=0.9919 at (20,234) -> page_reward<br>[hop 3   ] on=page_reward click ui_white/REWARD_GOTO_TACTICAL_WHITE score=0.9881 at (467,416) -> page_tactical |

## 顺带发现：上游页面图里有"无入边"节点

上游图共 53 节点 / 127 边，其中 **4 个节点没有任何入边**：`page_main_white`, `page_rpg_city`, `page_unknown`, `page_channel`。

这类节点**不可能是导航目标**（没人能"走到"它），它们是：

- `page_main_white`：同一张主界面的另一种皮肤，与 `page_main` 同屏命中；
- `page_channel`：世界频道浮层，上游只定义了 check 素材、没有任何入口素材；
- `page_unknown`：`Page(None)`，合成实体；
- `page_rpg_city`：RPG 活动的城内界面，只有出边（回主界面/回剧情页），没有入边。

所以对它们只能验"同屏被检测到"（本脚本用它的同屏兄弟页做锚点），
不能验"能导航到"—— 这是上游的设计，不是缺陷。


## 未通过

- `page_event_list`：goto-failed（命中=['page_main', 'page_main_white']）
- `page_guild`：goto-failed（命中=[]）
- `page_meowfficer`：goto-failed（命中=['page_main', 'page_main_white']）
- `page_os`：goto-failed（命中=['page_campaign_menu']）
- `page_private_quarters`：goto-failed（命中=['page_dormmenu']）

## 复现

```powershell
$env:STUB_ADB = "<adb.exe>"
python tools/diagnostics/regress_pages.py
```
