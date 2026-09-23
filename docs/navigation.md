# 页面导航验收记录

当前 `navigate` 产品路径由 C# 队列任务调用视觉宿主的 `ui_ensure`，再由上游
`UI.ui_ensure()` / `UI.ui_goto()` / `ui_additional()` 处理页面判定、路径、点击与附加界面。
C# 只负责动作授权、多回合、段间取消与工件；不按页面另选边或加坐标规则。
输入是 `{"to":"page_academy","rounds":1}`；`max_hops` 已停用，出现即拒绝。
每段返回 `destination`、`arrived`、`final_page`、`changed`、`elapsed_ms` 和失败详情，
不提供逐跳分数或坐标。迁移前的真机证据不能证明当前原生队列已完成相同路径；
当前原生队列的真机结果如下。
`changed` 是上游 `ui_ensure()` 的返回值；它为 `false` 时，
`ui_get_current_page()` 仍可能已点击 Home 处理未知画面，不能据此断言没有设备动作。

2026-09-23 新原生队列从主界面到 `page_tactical` 两轮、轮间返主界面及最终返页均成功，
末帧 `account_state` 命中主界面；原始工件留在本地忽略目录
`data/mainline-device/20260923T230711-native_nav_roundtrip/`。另一轮请求 `page_os` 时，
原生 UI 点击上游入口后设备进入 OS 海域，但 `page_os` 检查未到达，最终触发
`GameStuckError`；任务记 `failed`，后续任务按失败即停跳过，原始工件留在
`data/mainline-device/20260923T230833-native_nav_os_page/`。从该 OS 海域再请求
`page_main`，上游 `ui_get_current_page()` 点击 `GOTO_MAIN` 并成功返页，见本地
`data/mainline-device/20260923T231308-native_nav_from_os/`。这组证据证明通用恢复有效，
但 `page_os` 目标页语义在当前客户端未由原生 `UI.ui_ensure()` 闭环；未加专用分支。

## 迁移前 C# 导航器的历史记录

以下保留旧实现的现场证据与失效分析，所有 hop、阈值、选边和返回自救均不是当前生产规则。
旧产品路径为 C# → 进程内 CPython → 上游规则 → adb。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，**新版主界面**）。
以下真机 hop 是退役的直接 `goto` 命令留下的历史记录。

## 图从哪来

旧实现不导出、不重写：C# 启动时调一次 `ui_page_graph`，Python 宿主直接读上游
`module/ui/page.py` 的 `Page.all_pages` 与 `Page.links`，返回节点（页面 + check 资产）
与边（按钮资产 id → 目标页）。上游改连线，这里立刻跟着变。

同时做**资产 id 往返自检**：每个 id 反查回来必须还是同一个 `Button` 对象
（`RoundtripBad` 非空就拒绝导航）—— id 映射错了就会点到别的按钮，这比报错更危险。

```
[graph   ] nodes=53 edges=127 unmapped=0 roundtrip_bad=0
```

旧 C# 路径用**反向 BFS**（上游用 A*，但边权全为 1，结果等价且不需要启发函数）。

## 运行 1：`goto page_meta`（低置信一跳）

起点 `page_reshmenu`：

```
[path    ] page_meta: page_main -> page_reshmenu -> page_meta
[hop 1   ] on=page_reshmenu click ui/RESHMENU_GOTO_META score=0.0941(低置信) at (1112,264) -> page_meta
[result  ] success=True final=page_meta
```

## 运行 2：`goto page_academy`（多跳 + 变体择优）

起点 `page_meta`：

```
[path    ] page_academy: page_main -> page_dormmenu -> page_academy
[hop 1   ] on=page_meta       click ui/GOTO_MAIN                  score=0.2490(低置信) at (1240,10) -> page_main,page_main_white
[hop 2   ] on=page_main       click ui_white/MAIN_GOTO_DORMMENU_WHITE score=0.9958      at (562,679) -> page_dormmenu
[hop 3   ] on=page_dormmenu   click ui/DORMMENU_GOTO_ACADEMY      score=0.9877      at (298,537) -> page_academy
[result  ] success=True final=page_academy
```

三跳里三种情况都出现了：低置信回到主界面、白版变体进宿舍菜单、常规高分点击进学院。
每一跳后都重新感知并用新画面重新规划，落错页面不会一路错下去。

## 两条从上游代码里读出来的语义（都推翻了我先写的实现）

### 1. 上游不校验"要点的按钮是否出现"

`module/ui/ui.py` 的 `ui_goto`：

```python
if self.appear(page.check_button, offset=offset, interval=5):
    button = page.links[page.parent]
    self.device.click(button)          # 只看当前页的 check，不校验 button 自身
```

而且循环开头有 `GOTO_MAIN.clear_offset()` —— 正是为了清掉别处 `appear` 留下的偏移，
让点击回到资产声明的**标称坐标**。

我先写的版本给按钮实测分设了 0.85 的硬闸门，结果把 `ui/RESHMENU_GOTO_META`
（实测 0.0941，新版界面换了皮但坐标没变）判成"无可用出边"而失败 —— **违背上游语义**。
旧实现当时的规则是：

| 实测分 | 点击位置 | 依据 |
|---|---|---|
| ≥ 0.85 | 模板匹配到的实际位置 | 上游 appear+click 的语义（`Button.button` 匹配后即 `_button_offset`） |
| < 0.85 | 资产标称坐标 | 上游 `ui_goto` 的语义（低分时 `minMaxLoc` 峰值是随机的，拿它当点击目标等于乱点） |

### 2. 但"选哪条边"必须比上游更严

上游按 `Page.iter_pages()` 的**定义顺序**取第一个出现的页面，再点它的出边。
本机新版主界面下 `page_main` 与 `page_main_white` **同时命中**，而上游先遍历到
`page_main`，会点旧版 `ui/MAIN_GOTO_*` 的坐标 —— 实测那些资产只有 ≤0.25 分、
早已不在屏上，点了等于点空气（`MAIN_GOTO_CAMPAIGN` 就是这么"无变化"的）。

所以候选边取"当前所有命中页的出边"，每条边再评估它的界面版本候选
（`ui/X` 与上游声明的 `ui_white/X_WHITE`，按可解析性过滤），**按实测分择优**。
这一条是刻意的偏离，理由是真机证据。

## 未建模画面的自救（按返回键）

上游页面图只覆盖 53 个 Page，而游戏里到处是**不在图里的浮层**：角色详情、个人信息、
舰队编辑……实测在船坞长按舰船卡片就会进「角色详情」，此时没有任何页面规则命中。

旧导航器一开始只会报错退出，后来加入一次返回键自救
（`UnmodeledRecoveryBudget`，默认 2 次，不占跳数预算）。

实测（正是上面那个场景）：停在角色详情页时

```
[path    ] page_dock: page_main -> page_dock
[hop 0   ] on= click <BACK 自救> score=0.0000(低置信) at (0,0) -> page_dock
[result  ] success=True final=page_dock
```

`[hop 0]` 那一行标记为低置信、分数 0，就是为了让人一眼看出"这一步不是点击，是自救"，
而不是把它混进正常的跳数里假装成功。

## ⚠️ 返回键的边界：主界面上连按会弹「确认退出游戏?」

**事故记录（一次真机操作失误）**：验证装备筛选浮层时，我的清理脚本在退出浮层时
**循环按了 3 次返回键**，结果在回到主界面后又多按了一次，弹出
「确认退出游戏?」（取消/确定）。当时用上游的 `POPUP_CANCEL_WHITE` 素材去定位取消按钮，
结果它也是旧版素材（实测 0.17，不在屏上），最后按截图上量出的坐标 (474,510) 点了取消，
弹窗关闭、游戏未退出、无副作用。

固化下来的两条规矩：

1. **清理现场使用目标为 `page_main` 的 `navigate` 队列任务，不要循环按返回键。** 旧导航器
   曾在无页面命中时尝试一次返回；手写清理循环不知道当前在哪一页，容易在主界面多按一次。
2. **主界面是"返回键的边界"。** 在 `page_main` 上按返回等于请求退出游戏，
   所以任何"多按几次确保退干净"的写法都是危险的；要么用导航器，要么先确认当前页。

## 当前复现入口

```powershell
alashub queue --file navigate.json --run --allow-actions --serial 127.0.0.1:16384
```

`navigate.json` 的内容为
`{"tasks":[{"id":"navigate","kind":"navigate","required":true,"input":{"to":"page_academy","rounds":1}}]}`。
当前输出按原生调用段显示 `arrived`、`final_page`、`changed` 与 `elapsed_ms`；
上方 `[hop N]` 仅是旧 `goto` 的历史日志，不是新入口的点击依据或回归结果。
