# 界面识别验证记录（真机导航）

判定口径：从主界面按上游 `module/ui/page.py` 的页面图逐段导航，每段点击后用 **全量
页面扫描**（`ui_rules_sweep`）看目标页规则是否在该页上真正返回真。
「可驱动」（不抛异常）不算通过 —— 只有**在它自己的页面上命中**才算。

设备：MuMu 模拟器 1280x720 @ `127.0.0.1:16384`（国服，新主界面 UI）。
生成脚本：`tools/diagnostics/report_pages.py`（数据源 `docs/page-verification.json`，
由 `tools/diagnostics/verify_pages.py` 逐批累积）。

## 汇总

| 类别 | 数量 | 含义 |
| --- | --- | --- |
| 已验证命中 | 29 | 在该页上规则返回真，且离开该页后不再命中 |
| 受游戏状态阻塞 | 5 | 页面可达性被账号/活动状态挡住，非识别缺陷 |
| 未验证（原因已定位） | 19 | 依赖阻塞页或上游没有入边 |
| 未分类 | 0 | 需要继续排查 |
| 合计 | 53 | 上游 `page.py` 的全部 Page |

## 已验证命中

| 页面 | 该页实际命中的规则 |
| --- | --- |
| `page_academy` | `page_academy` |
| `page_archives` | `page_archives` |
| `page_battle_pass` | `page_battle_pass` |
| `page_build` | `page_build` |
| `page_campaign` | `page_campaign` |
| `page_campaign_menu` | `page_campaign_menu` |
| `page_commission` | `page_commission` |
| `page_daily` | `page_daily` |
| `page_dock` | `page_dock` |
| `page_dorm` | `page_dorm` |
| `page_dormmenu` | `page_dormmenu` |
| `page_event` | `page_event` |
| `page_exercise` | `page_exercise` |
| `page_fleet` | `page_fleet` |
| `page_game_room` | `page_game_room` |
| `page_mail` | `page_mail` |
| `page_main` | `page_main`, `page_main_white` |
| `page_main_white` | `page_main`, `page_main_white` |
| `page_meta` | `page_meta` |
| `page_mission` | `page_mission` |
| `page_munitions` | `page_munitions`, `page_shop`, `page_supply_pack` |
| `page_research` | `page_research` |
| `page_reshmenu` | `page_reshmenu` |
| `page_reward` | `page_reward` |
| `page_shipyard` | `page_shipyard` |
| `page_shop` | `page_munitions`, `page_shop`, `page_supply_pack` |
| `page_storage` | `page_storage` |
| `page_supply_pack` | `page_munitions`, `page_shop`, `page_supply_pack` |
| `page_tactical` | `page_tactical` |

## 受游戏状态阻塞（页面不可达）

| 页面 | 原因与证据 |
| --- | --- |
| `page_event_list` | NG-nochange（按钮不在屏上：无活动时活动一览入口不出现） |
| `page_guild` | 游戏状态阻塞（账号未加入大舰队，MAIN_GOTO_GUILD 落到舰队选择页，上游未建模该页） |
| `page_island` | 游戏状态阻塞（点击岛屿计划入口 0.9999 分，菜单关闭退回主界面＝功能未解锁） |
| `page_meowfficer` | 游戏状态阻塞（点击指挥喵入口 0.9894 分确认按钮在屏，但菜单关闭退回主界面＝功能未解锁） |
| `page_os` | NG-nochange（大型作战入口在屏 0.9990，点击无反应，等 6 秒仍无变化＝未解锁） |

## 未验证但原因已定位

| 页面 | 原因 |
| --- | --- |
| `page_channel` | 上游页面图里**没有入边**，只有出边（`page_channel.link(GOTO_MAIN...)`）；世界频道是临时浮层，ALAS 从不导航进去。实体本身可驱动，但无法由导航到达 |
| `page_coalition` | 活动类型决定：`CAMPAIGN_MENU_GOTO_EVENT` 按当前活动指向 event/sp/raid/coalition/rpg/hospital 之一；本机当前活动是普通活动，只命中 page_event |
| `page_hospital` | 同上（需 20250327 医院活动在跑） |
| `page_island_manage` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_map` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_order` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_phone` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_season` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_shop` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_storage` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_technology` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_island_transport` | 依赖 page_island（本机岛屿计划未解锁，见 blocked） |
| `page_private_quarters` | 宿舍菜单里没有该入口：实测 `DORMMENU_GOTO_PRIVATE_QUARTERS` 资产分 0.0627 （菜单只显示学院/后宅/指挥喵/岛屿计划四张卡） |
| `page_raid` | 同上（需 raid_20260827 类活动在跑） |
| `page_rpg_city` | 同上 |
| `page_rpg_stage` | 同上（需 raid_20240328 类 RPG 活动在跑） |
| `page_rpg_story` | 同上 |
| `page_sp` | 同上（需活动带 SP 关卡） |
| `page_unknown` | `Page(None)` —— 合成实体，没有 check 按钮，不是真实画面 |

## 复现方式

```powershell
$env:STUB_ADB = "<adb.exe 路径>"
$env:SEGMENTS = '[{"from":"page_main","button":"ui/MAIN_GOTO_REWARD","expect":"page_reward"}]'
python tools/diagnostics/verify_pages.py      # 逐段导航并记录
python tools/diagnostics/report_pages.py      # 重新生成本文件
```

## 验证器里三个必须保留的安全约束

1. **起点页必须在屏幕上。** 资产里的坐标是写死的，`asset_button_center` 不做检测；
   在错误的页面上盲点会点到别的东西（实测误点出「个人信息」页）。
2. **点击要落在模板匹配到的实际位置。** 上游 `Button.button` 在 match 后返回
   `_button_offset`，ALAS 的 appear+click 点的就是这个点，不是资产里的标称中心。
3. **同名资产要按实测分择优。** 本机跑的是新版主界面：`ui/MAIN_GOTO_X` 旧模板实测
   ≤ 0.25（早已不在屏上），真正在屏的是 `ui_white/MAIN_GOTO_X_WHITE`（0.94~0.997）。
   点旧坐标只会点到空气 —— 早期批次里 `MAIN_GOTO_CAMPAIGN` 就是这么"无变化"的。

   实测分数用**上游 `Button.match` 自身二分反解**（`match` 的语义是 `sim > similarity`，
   单调），不复制匹配算法，避免诊断口径与真实判定两套实现漂移。
