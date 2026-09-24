# 验收总状态：上游界面与控件识别跑到什么程度

本页由 `tools/diagnostics/status.py` 从历史证据与当前上游清单汇总生成，**不手写**。
历史命中不证明当前宿主回归通过；原生加载/四服正对照另见 `upstream-coverage.md`。
每项的"为什么没通过"在对应专项文档里，本页只给总数与去处。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，新版主界面）。

## 一页结论

| 范围 | 总数 | 已通过 | 未通过/阻塞 | 明细 |
| --- | --- | --- | --- | --- |
| 页面规则（Page） | 53 | **34** | 7 导航未达且规则未命中 + 12 其余未验 | `page-verification.md` |
| 控件规则（模块级 Switch/Scroll） | 31 | 9 | 22 | `controls.md` |
| cached_property 规则 | 25 | 3 | 22 | `controls.md` |
| 原生任务内工厂声明 | 7 | 未单独执行 | 需原生任务状态与实机证据 | `upstream-coverage.md` |
| 控制动作（滑动/开关驱动/探测） | 3 | 3 | 0 | `controls.md` |
| 控制原语（返回键/长按/滑动） | 4 | **4** | 0 | `primitives.md` |
| 文本输入（装备码流程） | 3 | **3** | 0 | `text-input.md` |
| 全量回归（产品路径导航） | 34 | **29** | 5 | `regression.md` |
| 页面规则合成正对照 | 53 | 52 | 1 跳过（`page_unknown` 无素材） | `positive-control.md` |
| 控件 Switch 合成正对照 | 31 | 15 | 16 跳过（颜色掩码或子类原生判据） | `positive-control.md` |

（控件与页面条目在证据文件里含"动作行"，上表已把动作与规则分开计数；
页面规则历史命中与导航未达记录有 1 页重叠（不重复计入总数）；
`page_main_white` / `page_channel` / `page_unknown` 是上游图里**无入边**的
状态节点，只能验"同屏被检测到"，见 `regression.md`。）

## 页面规则曾命中：34 个

`page_academy`、`page_archives`、`page_battle_pass`、`page_build`、`page_campaign`、`page_campaign_menu`、`page_commission`、`page_daily`、`page_dock`、`page_dorm`、`page_dormmenu`、`page_event`、`page_event_list`、`page_exercise`、`page_fleet`、`page_game_room`、`page_guild`、`page_mail`、`page_main`、`page_main_white`、`page_meowfficer`、`page_meta`、`page_mission`、`page_munitions`、`page_os`、`page_private_quarters`、`page_research`、`page_reshmenu`、`page_reward`、`page_shipyard`、`page_shop`、`page_storage`、`page_supply_pack`、`page_tactical`

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

## 其余未验证的 12 个页面

逐条原因见 `page-verification.md`：

1. **依赖阻塞页**：9 个岛屿子页（岛屿计划未解锁）；
2. **上游无入边或非真实画面**：`page_channel`（只有出边）、
   `page_rpg_city`（只有出边且活动类型未开跑）、`page_unknown`（`Page(None)`）。

## 历史控件未验原因（不涵盖本次新增发现的全部声明）

| 规则 | 到不了的原因 |
| --- | --- |
| `FORMATION` / `SUBMARINE_HUNT` / `SUBMARINE_VIEW` / `FLEET_LOCK` | 出击前阵型面板、潜艇面板、舰队编辑浮层。本机 `page_fleet` 上 `equipment/FLEET_DETAIL` 实测 0.17 分（不在屏上），要进出击流程才行 —— 那条流程会消耗石油并影响账号，**需本人同意** |
| `equipping_filter` | 装备选择浮层。已试过两条上游入口（详情页点 `EQUIPMENT_OPEN`、点装备槽位），均未打开该浮层 |
| `RETIRE_CONFIRM_SCROLL` | 退役确认弹窗。哪怕只差一次误点就可能真的退役舰船，**故意不验** |
| `EventShopUI.event_shop_tab_count_and_navbar` | 需进活动商店（本机活动页可达，但商店入口未开放） |
| 岛屿 / 大世界 / 指挥喵相关控件 | 功能本身未解锁 |

## 未覆盖的控制原语

| 项 | 原因 |
| --- | --- |
| 长按的完整业务链 | 上游 `gems_farming` 那条真实用法要真的出击，未验；
原语本身已用上游自己的判据（`EQUIPMENT_OPEN`）验过 |
| uiautomator2 后端的文本输入 | 上游装备码用的是 `send_keys`（uiautomator2）。
我们验的是 adb 的 `input text`（产品当前只有这个后端），两者语义不同：
`input text` 不支持中文、不清空原内容 |

## 复现全部证据

```powershell
$env:STUB_ADB = "<adb.exe>"
python tools/diagnostics/regress_pages.py        # 已验证页面的产品导航回归
python tools/diagnostics/verify_controls.py --report-only  # 只读归档控件历史证据
python tools/diagnostics/verify_primitives.py    # 返回键/长按/滑动
python tools/diagnostics/report_pages.py         # 重建 page-verification.md
python tools/diagnostics/status.py               # 重建本文件
```
