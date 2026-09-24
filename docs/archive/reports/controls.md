# 控件历史识别与动作记录

由 `tools/diagnostics/verify_controls.py` 只读归档；本次没有设备动作。
旧逐页动作驱动已退役：其中固定坐标、推测素材变体及手工点击顺序不再执行。
原始记录 `data/controls_verify.json` 保持不变，以下结果不改判、不补写未发生的验证。
原始文件 SHA-256：`6a1cf5ad5d6f69d73bffa3f18f3c6ab4c7a75ff00bd51cd4f68dd9594818b8f6`。

历史 33 条记录：hit=15、miss=13、blocked=5。
这些记录包含重复控件、动作及守卫；不代表同等数量的独立规则通过。
历史 hit 不能证明当前产品路径有效；miss/blocked 也不能直接归因为客户端版本或素材错误。

| 页面 | 模块 | 规则 | 类型 | 原判定 | 原始细节 |
| --- | --- | --- | --- | --- | --- |
| page_storage | module.storage.storage | MATERIAL_SCROLL | Scroll | hit | at_top=True at_bottom=False |
| page_storage |  | MATERIAL_SCROLL#swipe | Swipe | hit | at_top True -> False -> False |
| page_dock | module.retire.dock | DOCK_SCROLL | Scroll | hit | at_top=True at_bottom=False |
| page_dock | module.retire.dock | DOCK_SORTING | Switch | hit | appear=True |
| page_dock | module.retire.dock | DOCK_FAVOURITE | Switch | hit | appear=True |
| page_commission | module.commission.commission | COMMISSION_SCROLL | Scroll | hit | at_top=True at_bottom=False |
| page_commission | module.commission.commission | COMMISSION_SWITCH | Switch | hit | appear=True |
| page_fleet | module.handler.strategy | FORMATION | Switch | miss | appear=False |
| page_fleet | module.handler.strategy | SUBMARINE_HUNT | Switch | miss | appear=False |
| page_fleet | module.handler.strategy | SUBMARINE_VIEW | Switch | miss | appear=False |
| page_fleet | module.handler.fast_forward | FLEET_LOCK | Switch | miss | appear=False |
| page_shop | module.shop.ui | ShopUI._shop_bottom_navbar | Navbar | hit | {"active": 0, "total": 5, "info": [0, 0, 4], "buttons": ["SHOP_BOTTOM_NAVBAR_0_0", "SHOP_BOTTOM_NAVBAR_1_0", "SHOP_BOTTOM_NAVBAR_2_0", "SHOP_BOTTOM_NAVBAR_3_0", "SHOP_BOTTOM_NAVBAR_4_0"], "active_color": [33, 195, 239], "inactive_color": [181, 178, 181]} |
| page_shop | module.shop.ui | ShopUI.shop_nav_250814 | Switch | miss | {"state": "unknown", "appear": false, "states": ["NAV_GENERAL", "NAV_MONTHLY"], "offset": [20, 20]} |
| page_shop | module.shop.ui | ShopUI.shop_tab_250814 | Switch | miss | {"state": "unknown", "appear": false, "states": ["TAB_GENERAL", "TAB_MERIT", "TAB_GUILD", "TAB_META", "TAB_PRIZE", "TAB_CORE_LIMITED", "TAB_CORE_MONTHLY", "TAB_MEDAL", "TAB_PROTOTYPE"], "offset": [20, 20]} |
| page_shop | module.shop.shop_voucher | VOUCHER_SHOP_SCROLL | Scroll | hit | at_top=True at_bottom=False |
| page_storage | module.storage.ui | StorageUI.storage_filter | Setting | hit | {"observed_active": [], "option_count": 6, "settings": ["rarity"]} |
| page_dock | module.retire.dock | Dock.dock_filter | Setting | hit | {"observed_active": ["faction/meta"], "option_count": 52, "settings": ["extra", "faction", "index", "rarity", "sort"]} |
| page_dock | module.retire.dock | DOCK_SORTING#drive | Switch | hit | Descending -> Ascending（点 Ascending @(1050, 28)），复原 -> Descending |
| page_dock | module.retire.dock | DOCK_FAVOURITE#drive | Switch | hit | off -> on（点 on @(735, 26)），复原 -> off |
| page_game_room | module.minigame.minigame | MINIGAME_SCROLL | Scroll | hit | at_top=False at_bottom=False |
| ship_detail | module.equipment.equipment_change | EQUIPMENT_SCROLL | Scroll | hit | at_top=True at_bottom=False |
| equip_change | module.equipment.equipment_change | equipping_filter | Switch | miss | appear=False |
| fleet_detail | module.handler.fast_forward | FLEET_LOCK | Switch | miss | appear=False |
| fleet_detail | module.handler.strategy | FORMATION | Switch | miss | appear=False |
| fleet_detail | module.handler.strategy | SUBMARINE_HUNT | Switch | miss | appear=False |
| fleet_detail | module.handler.strategy | SUBMARINE_VIEW | Switch | miss | appear=False |
| page_meowfficer |  | SWITCH_LOCK |  | blocked | 页面不可达 |
| page_os |  | SCROLL_STORAGE |  | blocked | 页面不可达 |
| page_os |  | STRATEGIC_SEARCH_SCROLL |  | blocked | 页面不可达 |
| equip_select2 | module.equipment.equipment_change | equipping_filter | Switch | miss | appear=False |
| equip_select3 | module.equipment.equipment_change | equipping_filter | Switch | miss | appear=False |
| retire_dialog |  | <confident-guard> | Guard | blocked | retire/RETIRE_APPEAR_1 实测 0.1180，未达确信阈值，放弃点击 |
| page_campaign#strategy |  | <confident-guard> | Guard | blocked | handler/STRATEGY_OPEN 实测 0.3303，未达确信阈值，放弃点击 |

## 证据边界与复验

原生声明、构造及合成识别覆盖见 [全量规则报告](upstream-coverage.md)。
真实识别必须在对应原生任务状态下验证；开关/滚动动作还要核对变化及恢复后的状态。
历史记录没有独立恢复成功判据时，不从动作 hit 推断已经恢复。
新动作由 TaskQueue 调度原生任务，不在诊断脚本另建逐页流程。
仅重建本报告：`python tools/diagnostics/verify_controls.py --report-only`。

脱敏范围：只导出页面/模块/规则标识、原判定及原始细节；项目绝对路径替换为 `<project>`。
不发布设备配置、图像或账号信息，敏感字符串会阻止归档；原始文件与哈希保持不变。
