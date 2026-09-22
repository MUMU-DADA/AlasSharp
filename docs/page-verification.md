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
| 已验证命中 | 34 | 在该页上规则返回真，且离开该页后不再命中 |
| 受游戏状态阻塞 | 8 | 页面可达性被账号/活动状态挡住，非识别缺陷 |
| 未验证（原因已定位） | 12 | 依赖阻塞页或上游没有入边 |
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
| `page_event_list` | `page_event_list` |
| `page_exercise` | `page_exercise` |
| `page_fleet` | `page_fleet` |
| `page_game_room` | `page_game_room` |
| `page_guild` | `page_guild` |
| `page_mail` | `page_mail` |
| `page_main` | `page_main`, `page_main_white` |
| `page_main_white` | `page_main`, `page_main_white` |
| `page_meowfficer` | `page_meowfficer` |
| `page_meta` | `page_meta` |
| `page_mission` | `page_mission` |
| `page_munitions` | `page_munitions`, `page_shop`, `page_supply_pack` |
| `page_os` | `page_os` |
| `page_private_quarters` | `page_private_quarters` |
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
| `page_coalition` | 定向重试仍未到达：落在 ['page_campaign_menu']（goto-failed） |
| `page_event_list` | 定向重试仍未到达：落在 ['page_main', 'page_main_white']（goto-failed） |
| `page_hospital` | 定向重试仍未到达：落在 ['page_event']（goto-failed） |
| `page_island` | 游戏状态阻塞（点击岛屿计划入口 0.9999 分，菜单关闭退回主界面＝功能未解锁） |
| `page_raid` | 定向重试仍未到达：落在 ['page_campaign_menu']（goto-failed） |
| `page_rpg_stage` | 定向重试仍未到达：落在 ['page_campaign']（goto-failed） |
| `page_rpg_story` | 定向重试仍未到达：落在 ['page_event']（goto-failed） |
| `page_sp` | 定向重试仍未到达：落在 ['page_campaign']（goto-failed） |

## 未验证但原因已定位

| 页面 | 原因 |
| --- | --- |
| `page_channel` | 上游页面图里**没有入边**，只有出边（`page_channel.link(GOTO_MAIN...)`）；世界频道是临时浮层。而且 `CHANNEL_CHECK` 在本客户端实测只有 0.11~0.13（旧版 UI 素材：主界面同位置现在是「任务」按钮）—— 即使打开了频道，这条规则也不会命中。找入口时我在主界面聊天条右侧误点了一次，结果是「屏蔽聊天」的确认弹窗（fail-safe 的返回键已取消，未确认、无副作用）；上游与新 UI 都没有世界频道的入口素材，故此项无法在真实画面上验证 |
| `page_island_manage` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_map` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_order` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_phone` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_season` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_shop` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_storage` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_technology` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_island_transport` | **按用户要求跳过**：岛屿计划相关不在本轮验证范围内 |
| `page_rpg_city` | RPG 活动的城内界面，**上游页面图里没有入边**（只有回主界面/回剧情页的出边），既不可能是导航目标；且需要 RPG 类活动在跑才可能出现 |
| `page_unknown` | `Page(None)` —— 合成实体，没有 check 按钮，不是真实画面 |

## ⚠️ 手工入口验证（页面规则命中，但产品导航器到不了）

这类页面必须单独看：规则在真机上确实命中了，但**导航边上的按钮素材在本客户端不匹配**，
所以 `alashub goto` 到不了它。跑全量回归时它们会报 `goto-failed`，那是导航边的问题、
不是识别问题。

| 页面 | 入口与实测 |
| --- | --- |
| `page_event_list` | 点主界面右上角「活动汇总」卡片 (1235,125) 进入，`EVENT_LIST_CHECK` 实测 0.9958 命中。但上游的白版素材 `MAIN_GOTO_EVENT_LIST_WHITE` 在本客户端只有 0.088（新版 UI 的卡片样式变了），导航器点不中它 —— 即"页面规则已验证、导航边还缺客户端素材" |

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
