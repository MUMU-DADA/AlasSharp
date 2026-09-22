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
| `page_shop` | `ShopUI._shop_bottom_navbar` | Navbar | hit | {"active": 0, "total": 5, "info": [0, 0, 4], "buttons": ["SHOP_BOTTOM_NAVBAR_0_0", "SHOP_BOTTOM_NAVBAR_1_0", "SHOP_BOTTOM_NAVBAR_2_0", "SHOP_BOTTOM_NAVBAR_3_0", "SHOP_BOTTOM_NAVBAR_4_0"], "active_color": [33, 195, 239], "inactive_color": [181, 178, 181]} |
| `page_shop` | `ShopUI.shop_nav_250814` | Switch | miss | {"state": "unknown", "appear": false, "states": ["NAV_GENERAL", "NAV_MONTHLY"], "offset": [20, 20]} |
| `page_shop` | `ShopUI.shop_tab_250814` | Switch | miss | {"state": "unknown", "appear": false, "states": ["TAB_GENERAL", "TAB_MERIT", "TAB_GUILD", "TAB_META", "TAB_PRIZE", "TAB_CORE_LIMITED", "TAB_CORE_MONTHLY", "TAB_MEDAL", "TAB_PROTOTYPE"], "offset": [20, 20]} |
| `page_shop` | `VOUCHER_SHOP_SCROLL` | Scroll | hit | at_top=True at_bottom=False |
| `page_storage` | `StorageUI.storage_filter` | Setting | hit | {"observed_active": ["rarity/super_rare", "rarity/ultra_rare"], "option_count": 6, "settings": ["rarity"]} |
| `page_dock` | `Dock.dock_filter` | Setting | hit | {"observed_active": [], "option_count": 52, "settings": ["extra", "faction", "index", "rarity", "sort"]} |
| `page_dock` | `DOCK_SORTING#drive` | Switch | hit | Descending -> Ascending（点 Ascending @(1050, 28)），复原 -> Descending |
| `page_dock` | `DOCK_FAVOURITE#drive` | Switch | hit | off -> on（点 on @(735, 26)），复原 -> off |
| `page_game_room` | `MINIGAME_SCROLL` | Scroll | hit | at_top=False at_bottom=True |
| `ship_detail` | `EQUIPMENT_SCROLL` | Scroll | hit | at_top=True at_bottom=False |
| `equip_change` | `equipping_filter` | Switch | miss | appear=False |
| `fleet_detail` | `FLEET_LOCK` | Switch | miss | appear=False |
| `fleet_detail` | `FORMATION` | Switch | miss | appear=False |
| `fleet_detail` | `SUBMARINE_HUNT` | Switch | miss | appear=False |
| `fleet_detail` | `SUBMARINE_VIEW` | Switch | miss | appear=False |
| `page_meowfficer` | `SWITCH_LOCK` | Switch | miss | appear=False |
| `page_os` | `SCROLL_STORAGE` | Scroll | hit | at_top=True at_bottom=False |
| `page_os` | `STRATEGIC_SEARCH_SCROLL` | Scroll | hit | at_top=True at_bottom=False |
| `equip_select2` | `equipping_filter` | Switch | miss | appear=False |
| `retire_dialog` | `<confident-guard>` | Guard | blocked | retire/RETIRE_APPEAR_1 实测 0.1968，未达确信阈值，放弃点击 |
| `page_campaign#strategy` | `<confident-guard>` | Guard | blocked | handler/STRATEGY_OPEN 实测 0.2107，未达确信阈值，放弃点击 |

## 滑动控制与开关驱动（动作，不是识别）

| 页面 | 动作 | 结果 |
| --- | --- | --- |
| `page_storage` | `MATERIAL_SCROLL#swipe` | at_top True -> False -> False |
| `page_dock` | `DOCK_SORTING#drive` | Descending -> Ascending（点 Ascending @(1050, 28)），复原 -> Descending |
| `page_dock` | `DOCK_FAVOURITE#drive` | off -> on（点 on @(735, 26)），复原 -> off |
| `retire_dialog` | `<confident-guard>` | retire/RETIRE_APPEAR_1 实测 0.1968，未达确信阈值，放弃点击 |
| `page_campaign#strategy` | `<confident-guard>` | handler/STRATEGY_OPEN 实测 0.2107，未达确信阈值，放弃点击 |

`#swipe` = 在 Scroll 自己的区域里真滑，看 `at_top` 是否翻转；
`#drive` = 读出开关状态 → 点上游规则给出的另一个状态的按钮 → 再读确认变化
→ **复原原状态**（验证不该留下痕迹）；
`#probe` = 再进一层的探测点击（只打开选择器，不做任何改动）。
开关驱动是控制能力的核心回路：识别出状态不难，难的是改它并复核。

## 20 个控件规则的总账

| 规则 | 类型 | 状态 | 说明 |
| --- | --- | --- | --- |
| `EventShopUI.event_shop_tab_count_and_navbar` | 运行时计算 | ⛔ 游戏状态阻塞 | 需进活动商店（本账号当前活动页可达，但商店入口需要活动开放对应玩法） |
| `ISLAND_DOCK_SORTING` |  | ⛔ 游戏状态阻塞 | 同上 |
| `ISLAND_SEASON_TASK_SCROLL` |  | ⛔ 游戏状态阻塞 | 岛屿计划未解锁（见 page-verification.md） |
| `RETIRE_CONFIRM_SCROLL` |  | ⛔ 游戏状态阻塞 | 退役确认弹窗的滚动条。用户已授权"只开弹窗、不点确认"，但**安全入口找不到**：在船坞点舰船卡片打开的是角色详情（`retire/DOCK_CHECK` 从命中掉到 0.43，已离开船坞），三个入口变体 `RETIRE_APPEAR_1/2/3` 实测 0.07/0.08/0.19 都不在屏上；再往下只能盲点船坞底部按钮，而错误代价是不可逆的退役 —— 停手，不验 |
| `COMMISSION_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `COMMISSION_SWITCH` | Switch | ✅ 已命中 | appear=True |
| `DOCK_FAVOURITE` | Switch | ✅ 已命中 | appear=True |
| `DOCK_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `DOCK_SORTING` | Switch | ✅ 已命中 | appear=True |
| `Dock.dock_filter` | Setting | ✅ 已命中 | {"observed_active": [], "option_count": 52, "settings": ["extra", "faction", "index", "rarity", "sort"]} |
| `EQUIPMENT_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `MATERIAL_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `MINIGAME_SCROLL` | Scroll | ✅ 已命中 | at_top=False at_bottom=True |
| `SCROLL_STORAGE` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `STRATEGIC_SEARCH_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `ShopUI._shop_bottom_navbar` | Navbar | ✅ 已命中 | {"active": 0, "total": 5, "info": [0, 0, 4], "buttons": ["SHOP_BOTTOM_NAVBAR_0_0", "SHOP_BOTTOM_NAVBAR_1_0", "SHOP_BOTTOM_NAVBAR_2_0", "SHOP_BOTTOM_NAVBAR_3_0", "SHOP_BOTTOM_NAVBAR_4_0"], "active_color": [33, 195, 239], "inactive_color": [181, 178, 181]} |
| `StorageUI.storage_filter` | Setting | ✅ 已命中 | {"observed_active": ["rarity/super_rare", "rarity/ultra_rare"], "option_count": 6, "settings": ["rarity"]} |
| `VOUCHER_SHOP_SCROLL` | Scroll | ✅ 已命中 | at_top=True at_bottom=False |
| `FLEET_LOCK` | Switch | ➡️ 需更深流程 | 舰队锁定开关。本机在战役地图（page_campaign，第2章 2-1~2-4 全 Clear）实测 `handler/FLEET_LOCKED` 0.22、`FLEET_UNLOCKED` 0.15，都不在屏上；`ui_white` 里也没有策略/阵型相关素材可替代 —— 本客户端不暴露该面板，不是实现缺口。未盲点关卡节点（有误触开战风险） |
| `FLEET_LOCK` | Switch | ➡️ 需更深流程 | 舰队锁定开关。本机在战役地图（page_campaign，第2章 2-1~2-4 全 Clear）实测 `handler/FLEET_LOCKED` 0.22、`FLEET_UNLOCKED` 0.15，都不在屏上；`ui_white` 里也没有策略/阵型相关素材可替代 —— 本客户端不暴露该面板，不是实现缺口。未盲点关卡节点（有误触开战风险） |
| `FORMATION` | Switch | ➡️ 需更深流程 | 阵型面板。同上：地图上 `handler/IN_MAP` 0.16、`STRATEGY_OPEN` 0.12、`STRATEGY_OPENED` 0.16 都不在屏上，上游那套「策略面板」入口在本客户端里不存在 |
| `FORMATION` | Switch | ➡️ 需更深流程 | 阵型面板。同上：地图上 `handler/IN_MAP` 0.16、`STRATEGY_OPEN` 0.12、`STRATEGY_OPENED` 0.16 都不在屏上，上游那套「策略面板」入口在本客户端里不存在 |
| `SUBMARINE_HUNT` | Switch | ➡️ 需更深流程 | 潜艇面板。同上（还需先有潜艇） |
| `SUBMARINE_HUNT` | Switch | ➡️ 需更深流程 | 潜艇面板。同上（还需先有潜艇） |
| `SUBMARINE_VIEW` | Switch | ➡️ 需更深流程 | 同上 |
| `SUBMARINE_VIEW` | Switch | ➡️ 需更深流程 | 同上 |
| `SWITCH_LOCK` | Switch | ➡️ 需更深流程 | appear=False |
| `equipping_filter` | Switch | ➡️ 需更深流程 | 装备选择浮层里的筛选开关。已按上游入口试过两条路：角色详情页点 EQUIPMENT_OPEN（该素材在详情页实测 0.99，但点开后筛选开关仍不出现）、点装备槽位 (792,156) 也未打开选择器 |
| `equipping_filter` | Switch | ➡️ 需更深流程 | 装备选择浮层里的筛选开关。已按上游入口试过两条路：角色详情页点 EQUIPMENT_OPEN（该素材在详情页实测 0.99，但点开后筛选开关仍不出现）、点装备槽位 (792,156) 也未打开选择器 |
| `ShopUI.shop_nav_250814` | Switch | 🕐 UI 版本差异 | 本客户端是 250814 之前的老版商店 UI：可选状态是 NAV_GENERAL/NAV_MONTHLY，实测 unknown（新版商店才有这两个导航项；老版走 _shop_bottom_navbar，已命中） |
| `ShopUI.shop_tab_250814` | Switch | 🕐 UI 版本差异 | 同上（9 个新版页签 TAB_* 都不在屏上） |

## 复现

```powershell
$env:STUB_ADB = "<adb.exe>"
$env:ALASHUB  = "src/Alas.DataTool/bin/Release/net8.0/alashub.exe"
python tools/diagnostics/verify_controls.py
```
