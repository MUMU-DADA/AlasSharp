# 验收总状态：上游界面与控件识别跑到什么程度

本页由 `tools/diagnostics/status.py` 从四份证据文件汇总生成，**不手写**。
每项的"为什么没通过"在对应专项文档里，本页只给总数与去处。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，新版主界面）。

## 一页结论

| 范围 | 总数 | 已通过 | 未通过/阻塞 | 明细 |
| --- | --- | --- | --- | --- |
| 页面规则（Page） | 53 | **29** | 5 受游戏状态阻塞 + 19 原因已定位 | `page-verification.md` |
| 控件规则（模块级 Switch/Scroll） | 20 | 9 | 11 | `controls.md` |
| cached_property 规则 | 6 | 3 | 3 | `controls.md` |
| 控制动作（滑动/开关驱动/探测） | 3 | 3 | 0 | `controls.md` |
| 控制原语（返回键/长按/滑动） | 4 | **4** | 0 | `primitives.md` |
| 文本输入（装备码流程） | 3 | **3** | 0 | `text-input.md` |
| 全量回归（产品路径导航） | 29 | **29** | 0 | `regression.md` |
| 页面规则合成正对照 | 53 | 52 | 1 跳过（`page_unknown` 无素材） | `positive-control.md` |
| 控件 Switch 合成正对照 | 20 | 10 | 10 跳过（Scroll 判定依赖颜色掩码） | `positive-control.md` |

（控件与页面条目在证据文件里含"动作行"，上表已把动作与规则分开计数；
页面规则里 `page_main_white` / `page_channel` / `page_unknown` 是上游图里**无入边**的
状态节点，只能验"同屏被检测到"，见 `regression.md`。）

## 已通过：页面 29 个

`page_academy`、`page_archives`、`page_battle_pass`、`page_build`、`page_campaign`、`page_campaign_menu`、`page_commission`、`page_daily`、`page_dock`、`page_dorm`、`page_dormmenu`、`page_event`、`page_exercise`、`page_fleet`、`page_game_room`、`page_mail`、`page_main`、`page_main_white`、`page_meta`、`page_mission`、`page_munitions`、`page_research`、`page_reshmenu`、`page_reward`、`page_shipyard`、`page_shop`、`page_storage`、`page_supply_pack`、`page_tactical`

## 受游戏状态阻塞（页面不可达，非识别缺陷）

| 页面 | 原因与证据 |
| --- | --- |
| `page_event_list` | NG-nochange（按钮不在屏上：无活动时活动一览入口不出现） |
| `page_guild` | 游戏状态阻塞（账号未加入大舰队，MAIN_GOTO_GUILD 落到舰队选择页，上游未建模该页） |
| `page_island` | 游戏状态阻塞（点击岛屿计划入口 0.9999 分，菜单关闭退回主界面＝功能未解锁） |
| `page_meowfficer` | 游戏状态阻塞（点击指挥喵入口 0.9894 分确认按钮在屏，但菜单关闭退回主界面＝功能未解锁） |
| `page_os` | NG-nochange（大型作战入口在屏 0.9990，点击无反应，等 6 秒仍无变化＝未解锁） |

## 原因已定位但未验证的 19 个页面

分三类（逐条原因见 `page-verification.md`）：

1. **依赖阻塞页**：9 个岛屿子页（岛屿计划未解锁）；
2. **活动类型不同**：raid / sp / coalition / hospital / rpg_* —— 都由
   `CAMPAIGN_MENU_GOTO_EVENT` 按当前活动指向，本机当前活动只命中 `page_event`；
3. **上游无入边或非真实画面**：`page_channel`（只有出边）、`page_unknown`（`Page(None)`）、
   `page_private_quarters`（宿舍菜单里没有该入口，实测资产分 0.06）。

## 还没验的控件（都是"到不了"，不是"判定错"）

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
python tools/diagnostics/regress_pages.py        # 29 个页面全量回归（约 5 分钟）
python tools/diagnostics/verify_controls.py      # 控件规则 + 滑动/开关驱动
python tools/diagnostics/verify_primitives.py    # 返回键/长按/滑动
python tools/diagnostics/report_pages.py         # 重建 page-verification.md
python tools/diagnostics/status.py               # 重建本文件
```
