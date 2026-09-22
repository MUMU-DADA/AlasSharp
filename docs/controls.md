# 控件识别与滑动控制验证记录

判定口径与页面验证一致：**在它自己的页面上命中**才算通过。
"可驱动"（不抛异常）不算 —— 那是 S1 阶段的结论。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，新版主界面）。
脚本：`tools/diagnostics/verify_controls.py`（导航走产品路径 `alashub goto`，
顺带回归 Navigation 实现）；原始数据 `data/controls_verify.json`。

## 本批实际运行结果

| 页面 | 规则 | 类型 | 结果 | 细节 |
| --- | --- | --- | --- | --- |
| `page_storage` | `MATERIAL_SCROLL` | Scroll | hit | at_top=True at_bottom=False |
| `page_storage` | `MATERIAL_SCROLL#swipe` | Swipe | hit | at_top True -> False -> False |
| `page_dock` | `DOCK_SCROLL` | Scroll | hit | at_top=True at_bottom=False |
| `page_dock` | `DOCK_SORTING` | Switch | hit | appear=True |
| `page_dock` | `DOCK_FAVOURITE` | Switch | hit | appear=True |
| `page_commission` | `COMMISSION_SCROLL` | Scroll | hit | at_top=True at_bottom=False |
| `page_commission` | `COMMISSION_SWITCH` | Switch | hit | appear=True |
| `page_fleet` | `FORMATION` | Switch | miss | appear=False |
| `page_fleet` | `SUBMARINE_HUNT` | Switch | miss | appear=False |
| `page_fleet` | `SUBMARINE_VIEW` | Switch | miss | appear=False |
| `page_fleet` | `FLEET_LOCK` | Switch | miss | appear=False |

## 滑动控制

`MATERIAL_SCROLL` 的拖拽区域实测是**右侧滚动条** `[1257, 94, 1263, 585]`。
在它自己的区域里向上滑 3 次 → `at_top` 由 `True` 变 `False`；再向下滑 6 次回顶。
这一步同时验了两件事：`input swipe` 这条控制链路真的能驱动游戏，
且上游 Scroll 规则会随画面变化翻转判定（不是永远返回同一个值）。

## 20 个控件规则的总账

| 规则 | 类型 | 状态 | 说明 |
| --- | --- | --- | --- |
| `ISLAND_DOCK_SORTING` |  | ⛔ 游戏状态阻塞 | 同上 |
| `ISLAND_SEASON_TASK_SCROLL` |  | ⛔ 游戏状态阻塞 | 岛屿计划未解锁（见 page-verification.md） |
| `SCROLL_STORAGE` |  | ⛔ 游戏状态阻塞 | 大型作战未解锁 |
| `STRATEGIC_SEARCH_SCROLL` |  | ⛔ 游戏状态阻塞 | 同上 |
| `SWITCH_LOCK` |  | ⛔ 游戏状态阻塞 | 指挥喵未解锁 |
| `COMMISSION_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `COMMISSION_SWITCH` | Switch | ✅ 已命中 | appear=True |
| `DOCK_FAVOURITE` | Switch | ✅ 已命中 | appear=True |
| `DOCK_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `DOCK_SORTING` | Switch | ✅ 已命中 | appear=True |
| `MATERIAL_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `MATERIAL_SCROLL#swipe` | Swipe | ✅ 已命中 | at_top True -> False -> False |
| `EQUIPMENT_SCROLL` |  | ➡️ 需更深流程 | 需进「舰船详情 → 装备」浮层，不是页面图里的独立页 |
| `FLEET_LOCK` | Switch | ➡️ 需更深流程 | 舰队编辑浮层里的锁定开关（不是 page_fleet 本身） |
| `FORMATION` | Switch | ➡️ 需更深流程 | 出击前「阵型」面板 |
| `RETIRE_CONFIRM_SCROLL` |  | ➡️ 需更深流程 | 需进退役确认弹窗 |
| `SUBMARINE_HUNT` | Switch | ➡️ 需更深流程 | 潜艇面板（需先有潜艇） |
| `SUBMARINE_VIEW` | Switch | ➡️ 需更深流程 | 同上 |
| `VOUCHER_SHOP_SCROLL` |  | ➡️ 需更深流程 | 需切到商店的兑换页签（页签本身是 ShopUI 的 Switch 规则） |
| `equipping_filter` |  | ➡️ 需更深流程 | 同上（装备筛选开关在装备浮层里） |
| `MINIGAME_SCROLL` |  | 🔵 待验 | page_game_room 已验证可达，小游戏内滚动待验 |

## 复现

```powershell
$env:STUB_ADB = "<adb.exe>"
$env:ALASHUB  = "src/Alas.DataTool/bin/Release/net8.0/alashub.exe"
python tools/diagnostics/verify_controls.py
```
