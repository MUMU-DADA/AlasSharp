# 页面导航图（控制能力）验收记录

识图的另一半是**控制**：知道"我在哪一页"之后，要能按上游的页面图把设备带到目标页。
这里记录产品路径（C# → 进程内 CPython → 上游规则 → adb）在真机上的实际运行结果。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，**新版主界面**）。
以下真机 hop 是退役的直接 `goto` 命令留下的历史记录。当前导航应把目标写入
`navigate` 队列任务，并用 `alashub queue --file navigate.json --run --allow-actions --serial <serial>` 执行。

## 图从哪来

不导出、不重写：C# 启动时调一次 `ui_page_graph`，Python 宿主直接读上游
`module/ui/page.py` 的 `Page.all_pages` 与 `Page.links`，返回节点（页面 + check 资产）
与边（按钮资产 id → 目标页）。上游改连线，这里立刻跟着变。

同时做**资产 id 往返自检**：每个 id 反查回来必须还是同一个 `Button` 对象
（`RoundtripBad` 非空就拒绝导航）—— id 映射错了就会点到别的按钮，这比报错更危险。

```
[graph   ] nodes=53 edges=127 unmapped=0 roundtrip_bad=0
```

路径用**反向 BFS**（上游用 A*，但边权全为 1，结果等价且不需要启发函数）。

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
现在的规则是：

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

导航器一开始只会报错退出 —— 卡死。现在多一条最小可靠的自救：**按一次返回键**
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

1. **清理现场使用目标为 `page_main` 的 `navigate` 队列任务，不要循环按返回键。** 导航器只在
   "画面上没有任何已建模页面"时才按一次返回，且按完立刻重新感知；而手写清理循环
   不知道当前在哪一页，很容易在主界面上多按一次 —— 这正是事故的成因。
2. **主界面是"返回键的边界"。** 在 `page_main` 上按返回等于请求退出游戏，
   所以任何"多按几次确保退干净"的写法都是危险的；要么用导航器，要么先确认当前页。

## 复现

```powershell
alashub queue --file navigate.json --run --allow-actions --serial 127.0.0.1:16384
```

`navigate.json` 的内容为
`{"tasks":[{"id":"navigate","kind":"navigate","required":true,"input":{"to":"page_academy","max_hops":8,"rounds":1}}]}`。
这是当前复现入口；上面的 hop 数值来自旧入口，不代表新入口已完成真机回归。

输出里 `[hop N]` 的 score 与坐标就是点击依据；`(低置信)` 标记表示那一跳走的是
"标称坐标"分支。
