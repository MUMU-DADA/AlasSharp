# 界面识别验证记录（真机导航）

历史判定口径：旧逐段点击诊断通常从主界面进入目标页，点击后用 **全量
页面扫描**（`ui_rules_sweep`）看目标页规则是否在该页上真正返回真。
「可驱动」（不抛异常）不算通过 —— 只有**在它自己的页面上命中**才算。
少数手工进入后命中的规则单独列出，不能据此声称产品导航可达。

设备：MuMu 模拟器 1280x720 @ `127.0.0.1:16384`（国服，新主界面 UI）。
生成脚本：`tools/diagnostics/report_pages.py`（数据源 `docs/archive/reports/page-verification.json`，
由已退役的 `tools/diagnostics/verify_pages.py` 逐批累积，原始记录保持不变）。
旧驱动含本地变体择优与固定点击流程，不能证明当前上游原生导航通过；当前入口见 `regress_pages.py`。

## 汇总

| 类别 | 数量 | 含义 |
| --- | --- | --- |
| 已验证规则命中 | 34 | 历史真机画面上规则返回真；不等于当前账号可导航到 |
| 导航未达或状态受限记录 | 8 | 含导航素材问题与账号/活动门禁 |
| 两项重叠 | 1 | 规则曾命中，但另一次导航未达；已计在上述两项中 |
| 尚未命中且未列为阻塞（原因已定位） | 12 | 依赖阻塞页或上游没有入边 |
| 未分类 | 0 | 需要继续排查 |
| 合计 | 53 | 上游 `page.py` 的全部 Page |

上述两项是独立证据维度，不能相加当作页面总数；去重后全部 53 页都有分类，
其中只有已验证规则命中的页面有真机正样本。

## 已验证规则命中

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

## 导航未达或状态受限记录

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
所以导航任务到不了它。回归里的历史标签 `goto-failed` 表示导航失败，那是导航边的问题、
不是识别问题。

| 页面 | 入口与实测 |
| --- | --- |
| `page_event_list` | 点主界面右上角「活动汇总」卡片 (1235,125) 进入，`EVENT_LIST_CHECK` 实测 0.9958 命中。但上游的白版素材 `MAIN_GOTO_EVENT_LIST_WHITE` 在本客户端只有 0.088（新版 UI 的卡片样式变了），导航器点不中它 —— 即"页面规则已验证、导航边还缺客户端素材" |

## 归档与当前验证入口

```powershell
python tools/diagnostics/report_pages.py      # 重新生成本文件
python tools/diagnostics/verify_native_page_rules.py  # 离线原生判据对照
python tools/diagnostics/regress_pages.py     # 当前原生导航回归；会操作游戏
```

## 历史驱动与当前边界

旧驱动的白版素材推测、按分择优和固定点击顺序已删除；不再作为验证入口或可复用导航算法。
当前导航只通过队列调用上游 `UI.ui_ensure()`，识别使用原生 `UI.ui_page_appear()`。
本报告中的历史命中、导航失败和手工入口分别保留，不能由单帧分数推断整个界面操作成功。
