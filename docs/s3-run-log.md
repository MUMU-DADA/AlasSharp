# S3 真机跑图记录（逐次留档）

> 这份表只记**真机跑过的**（`--run --allow-actions`），不是推演、不是 dry-run。
> 目的：把"覆盖到哪几张图"变成可核对的事实，避免再出现"以为跑过其实没跑"。
> 每行的证据都在 `%TEMP%\<日志>.log` 或 `docs/s3-entry-sequence.md` 里能对上。

> ⚠️ **账号换过（2026-09-23 00:10 用户告知"我换号了"）**
> 下面所有实测记录都是在**上一个账号**上做的，换号后需要重新取基线：
>
> - 旧号：解锁 1–12 + 14 章；本章图基本已三星（11-1/12-1/… 都是 `威胁排除 100%`）；
>   高难图用 `--fleet1 3 --fleet2 6`（用户规则），第二舰队为空。
> - 新号：**已探**（见下一节）—— 只有**第 1 章**，且换号当时只开了 1-1 / 1-2。
>
> 上一轮的 8-1/6-1 失败（`Image to detect is not in_map`、`CampaignNameError`）
> **很可能就是换号造成的** —— 当时画面已不是游戏内，判据自然不成立，先别当成缺陷。

## 新号基线（2026-09-23 00:2x）

`python tools/diagnostics/account_probe.py --chapters 1-16` 的结果：
**第 1 章 = 2 关 `['1-1','1-2']`；第 2–16 章全部 `GameTooManyClickError: CHAPTER_NEXT`（未解锁）**。
主界面：指挥官 `pgeS`、**Lv.20**、油 25000、"累计登录 2 天" → 全新号。

关键含义：新号可用面**只有第 1 章**，而 1-1 恰好是单行图（`No vertical line detected`，见文末"已知不能跑的图"）——
不过 1-1 已经是三星，**不挡进度**。

### 新号第一批真机记录（全部 `exit 0`、无 `WITHDRAW`）

| 关 | 场景 | 结果 | 用时 | BOSS 格 | 备注 |
| --- | --- | --- | --- | --- | --- |
| 1-2 | A | ✅ | 170.5s | E1 | 首次进图 18.6s；`map_init` 第 1 次 `Vanish point…` 失败、**第 2 次（挪机位重试）成功** |
| 1-3 | A（第 1 次） | ✅ | 171.3s | E1 | 打之前：**0% / 0 星** |
| 1-3 | B（`--clear-all`，第 2 次） | ✅ | 159.2s | E1 | `Enemy remain: []` → `Brute clear BOSS` |
| 1-3 | A（第 3 次） | ✅ | 177.8s | E1 | 打完：**100% / ★★★** |
| 1-4 | A | ❌（`battle_0` 报 `No battle executed` → 上游 `withdraw`） | 27.8s | — | 见下 |

### 1-4 为什么打不了（2026-09-23 00:52-00:55，已定位到"格距判错"）

现象：`map_init` 第 2 次（挪机位重试）**成功** ✓，但 `battle_0` 立刻报
`No battle executed` → `execute_a_battle` 十次无战果 → `ScriptError, No combat executed, Withdrawing`。

探针（`probe_71_view.py`，`stop_after='map_init'`）拿到的关键数：

| 项 | 值 | 应有 |
| --- | --- | --- |
| 视图检出格数 | **48–53 格**、`view_shape=[7,9]`（9 行） | 1-4 是 `G3` = **7×3＝21 格**（3 行） |
| 两个敌人在视图里的行号 | 第 **5** 行、第 **8** 行 | 3 行图里最多差 2 行 |
| 同一帧的模板匹配 | `tile_center: 0.636 (bad match)` | 其它图常见 0.9+ |
| 客户端画面 | 7 列 A–G × 3 行，敌人在 C1 / D3（与 `map_data` 的 `ME` 一致 ✓） | — |

读法：**格距被判成了约 1/3**（3 行 → 9 行）。一旦格距错，敌人落到的全局格就不是 `map_data` 里的
`ME` 格，`grid_info.update()` 把 `is_enemy` 丢掉（它只接受 `may_enemy` 格）→ 上游眼里"这张图没有敌人"
→ `battle_0` 无事可做 → 撤退。**这不是"敌人识别不出"，是整张网的间距错了。**

可疑诱因（待验证）：该图水面上画了**细网格**（截图里水纹下有细密的方格），
检测器的 Hough/模板可能锁到了那层细格，而不是瓦片边界。

> 注：新号当前**只解锁到 1-4**，而 1-1（单行图，检测器明确失效）已三星不挡路；
> 所以卡住进度的就是这张 1-4。定位手段已具备（`s3_probe_view` 能同时给视图与地图两侧的标志）。

### 1-3 的"可见变化"（这是最容易核对的验收证据）

| 时刻 | 威胁排除 | 击破护卫舰队 | 三星 |
| --- | --- | --- | --- |
| 跑之前（`stage_progress.py` 读到） | **0.0%** | 0/5 | 全 false |
| 场景 A 打完 1 次 | 100% | **2/5** | star_1 ✓ star_3 ✓ |
| 场景 B 打完 2 次 | 100% | **4/5** | 同上 |
| 场景 A 打完 3 次 | 100% | **5/5** | **star_2 ✓ → `map_is_3_stars: true`** |

读法（都是**游戏自己的面板**，`stage_progress.py` 读的）：
`[Map_info] 99%, star_1, star_2, star_3` / `map_is_100_percent_clear: true` / `map_is_3_stars: true`。
章节页上 1-3 从"**0% 无徽章**"变成"**金色 `Clear!` + ★★★**"，并且 **1-4 随之解锁**。

**副产品（对用户有用的规则）**：`击破护卫舰队 (N/5)` 是**跨出击累加**的 ——
1-3 每打一次只刷 2 支护卫舰队（`MAP.spawn_data` 里 enemy 合计 = 2），所以要**打 3 次**才够 5/5。
这解释了"为什么这张图打一遍拿不到三星"，也说明**重复刷图**本身就是拿星的手段。

### 新号上遇到的客户端弹层：「作战委托」教学（挡住导航，需人工/适配处理）

1-3 打完第一次后，关卡面板上弹出一组**多页教学气泡**（`作战委托系统已开放！` → `指挥官可通过自动委托的方式通关已攻略关卡！` …），
它把面板中区挡住，并且：

- 期间 `alashub goto page_campaign` 报 **`当前画面没有任何已建模页面（hit=），且自救 2 次仍无效`** ——
  上层导航直接被这层浮层挡住；
- 屏幕提示是"**点「作战委托」按钮**"（画着白色手指），按提示点 `(840,510)` 才走完教学；
- 走完后 `goto page_campaign` 恢复正常（`已到达 page_campaign（0 跳）`）。

上游没有这个浮层的模型（和"正在攻略中"弹窗、"低心情强制出击"弹窗同类，属**客户端专属弹层**）。
目前是**手动点掉**的；要自动化得再加一条垫片（判据可用面板中区出现蓝色气泡 + 白色手指图标）。
另外它只在"首次打完一张新图"时出现一次，**不影响已攻略图的重复刷**。

**范围约定**：只测账号已解锁的章节（旧号是 1–14 章；**15 章及以后不测**）。

## 换号 / 新号后的操作顺序（runbook）

1. **确认已登录**：游戏能停在战役页（`alashub goto page_campaign`）。
   标题/登录页上**不要**让自动化乱点（会误触"更换服务器"）。
2. **探可达范围**：
   `python tools/diagnostics/account_probe.py --chapters 1-16`
   → 逐章列出"入口表里认出来的关卡"，并落盘 `data/account_probe.json`。
3. **找能验收的图**：对候选关读游戏自带进度
   `python tools/diagnostics/stage_progress.py --chapter campaign.campaign_main.campaign_8_1 --no-goto`
   → 优先挑**还没三星 / `威胁排除 < 100%`** 的图：只有这种图，"全清"才会在游戏界面上留下**可见变化**
   （已三星的图跑完看不出差别，只能靠日志证明打过 BOSS）。
4. **确认舰队槽位**：`--fleet1` / `--fleet2` / `--submarine` 都是配置项，不是从地图推出来的。
   旧号是"高难图用 3 / 6"，新号要重新确认（`Fleet_Fleet2 = 0` 表示不用第二舰队）。
5. **两套流程各跑一次**并逐行记录到上面的表：默认（BOSS 一刷出来就打）与 `--clear-all`
   （先清光小怪再打 BOSS）。

命令行（两套战斗流程的区别见 `docs/s3-entry-sequence.md` 末章）：

```powershell
# 场景A：BOSS 一刷出来就打
alashub campaign <章模块> --run --allow-actions --fleet1 3 --fleet2 6 --repeat --max-rounds 20 --max-seconds 1500
# 场景B：先清光小怪再打 BOSS
alashub campaign <章模块> --run --allow-actions --clear-all --fleet1 3 --fleet2 6 --repeat --max-rounds 20 --max-seconds 1500
```

| 图 | 行数 | 场景 | 结果 | 用时 | BOSS 格 | 收尾 | 证据 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2-1 | 4 | A | ✅ 全清 | — | — | 回到章节页 | 章节页徽章 `Clear!` + ★★★（`data/_campaign125.png`） |
| 2-1 | 4 | A（对照跑） | ✅ | **160.9s**（4 轮） | — | `In stage.`，`campaign_end=True` | `%TEMP%\batch_21a.log` |
| 2-1 | 4 | B（`--clear-all`） | ✅ | **238.6s**（7 轮） | D4 | `Enemy remain: []` → `Brute clear BOSS` | `%TEMP%\batch_21b.log` |
| 3-1 | 5 | A | ✅ | **164.4s** | F1 | `In stage.`，`campaign_end=True`；3-1 有自己的 `battle_3` 钩子 | `%TEMP%\batch_c.log` |
| 11-1 | 6 | A | ✅ | 360.6s | F3 | `In stage.`，`campaign_end=True`，无 `WITHDRAW` | `%TEMP%\hard11_full.log` |
| 11-1 | 6 | A（复核：BOSS 颜色垫片已关） | ✅ | 340.8s | G6 | 同上 | `%TEMP%\hard11_noshim.log` |
| 11-1 | 6 | A（半途续打：`battle_count=6`） | ✅ | 53.6s（单场） | F3 | 同上 | `tools/diagnostics/oneoff/resume_boss.py` |
| 11-1 | 6 | B（`--clear-all`） | ✅ | 397.6s | A2 | 同上；关键行 `Enemy remain: []` → `Brute clear BOSS` | `%TEMP%\hard11_clearall.log` |
| 12-1 | 6 | A | ✅ | 312.2s | H5 | 同上 | `%TEMP%\batch_a.log` |
| 12-1 | 6 | B（`--clear-all`） | ✅ | 356.9s | H5 | 同上；`Enemy remain` 一路收到 `[]` 才 `Brute clear BOSS` | `%TEMP%\batch_b.log` |
| 10-1 | 6 | A | ✅ | 355.3s | G3 | 同上 | `%TEMP%\batch_a.log` |
| 10-1 | 6 | B（`--clear-all`） | ✅ | **391.0s**（9 轮） | G4 | `Enemy remain: []` → `Brute clear BOSS`；8 只小怪全清 | `%TEMP%\batch_101b.log` |
| 14-1 | 7 | A | ✅ | 530.6s | A7 | 同上；该图有自己的 `battle_5` 钩子（`campaign_14_1.py:84`），流程是 `battle_0 → battle_5 → battle_6` | `%TEMP%\batch_a.log` |

**跨关连续**：上面三关是**同一个进程**里连续驱动的（`campaign A,B,C`），两处"复位回战役页"
都是 `尝试1 success=True`，没有人工干预 —— 这是"常驻"所需的状态不跨进程丢的实证。

## 每张图的规则数据（`MAP.spawn_data`，累计口径）

| 图 | 形状 | 累计小怪 | BOSS 回合 | `may_boss` 候选格数 |
| --- | --- | --- | --- | --- |
| 2-1 | 6×4 | 6 | **2** | — |
| 10-1 | 7×6 | 8 | 6 | 2 |
| 11-1 | 8×6 | 7 | 6 | 4（`H1/A2/F3/G6`，实测分别刷在 F3、A2、G6） |
| 12-1 | 8×6 | 7 | 6 | 2（实测 H5） |
| 14-1 | 8×7 | 8 | 6 | 3 |

含义：场景 A 在"BOSS 回合"就打 BOSS（2-1 只要 2 场就刷 BOSS，11/12/14-1 要 6 场）；
场景 B 不看回合数，一直清到 `Enemy remain: []` 才打 BOSS。

## 两套流程的对照（同一张图 2-1，A/B 各跑一次）

2-1 是最能说明差别的一张：**BOSS 第 2 回合就刷出来**。

| 场景 | 日志关键行 | 轮次 | 用时 |
| --- | --- | --- | --- |
| A（默认） | `BATTLE_2 → Using function: battle_2`（该图自己的 BOSS 分支） | 4 | 160.9s |
| B（`--clear-all`） | `BATTLE_2 → Using function: clear_all` → `Enemy remain: [E1, C3, E3, F3]` → `Clear enemy: C3` | 7 | 238.6s |

**同一张图、同一个时刻**：第 2 回合 BOSS 已经刷出来（B 的日志里 `Boss found: [D4]` 就是证据），
A 直接去打 BOSS，B 继续清小怪，直到 `Enemy remain: []` 才 `Brute clear BOSS`。
这正是用户说的"两个不同的场景，都有用"。

## 已知不能跑的图

| 图 | 症状 | 证据 |
| --- | --- | --- |
| 1-1 | **单行图**（7 格一行）：上游检测器 `No vertical line detected`，5 档降阈值重试都无效 | `data/fixtures/subchapter_1_1.png`（真图内帧）+ `probe_backends.py` |
| 7-1 | **三行图，识别到一半就进不去战斗**：`map_init` 一次成功一次失败（已加"挪机位重试"缓解），进图后反复 `Arrive B1 (is_fleet)` 却始终不触发战斗；视图只检出 **12–15 格**（该图应为 24 格） | `%TEMP%\run_71*.log`，并见下面的复盘 |

### 7-1 复盘（2026-09-22 深夜 → 09-23 凌晨，两轮定位）

1. **旧结论的证据是错的**：`data/fixtures/inmap_7-1.png` 其实是**主界面**，不是图内帧 ——
   所以"7-1 识别不了"这条一直没有有效证据。
2. **第一轮真机复测**：`map_init` 第一次报 `MapDetectionError: Vanish point and distant point too close`
   （`vanish_point == distant_point == (654, -1425)`），挪机位后第二次成功 → 进了 `BATTLE_0`
   （真打了一场）。但随后开始空转：每 ~23 秒一次 `Arrive B1 (is_fleet)`，**从未** `Combat preparation`。
3. **第二轮定点探针**（`probe_71_view.py`：`s3_run_plan(stop_after='map_init')` + 连续 `s3_probe_view`）
   把两个猜测都排除了：
   - **信息条不是原因** —— 探针里 `info_bar_count` 全程 **0**，屏幕上是干净的；
     调上游 `handle_info_bar()` 后复测，格数一点没变。
   - **真正卡在检测**：`map_init` 三次尝试分别是
     `Vanish point and distant point too close` → `No vertical line detected` → `No vertical line detected`，
     之后每一次 `update()` 都失败（`View` 连 `grids` 都没建起来）。
4. **现场帧离线对照（`data/_71_inmap.png`，真图内帧）**：

   | 后端 | 结果 |
   | --- | --- |
   | `homography` | ✗ `Vanish point and distant point too close`；线族统计：水平 7 条（5 内部 / 2 边界）、**垂直 10 条但只有 2 条内部、0 条边界** |
   | `perspective` | ✓ 能检出，但**多判**：43 格 / `shape=[7,5]`，该图应为 24 格 / `[7,2]` |

5. 结论：**7-1 暂列不可用**，与 1-1 同栏。要修得动检测器本身（垂直内部线只拟合出 2 条），
   不是"换个机位/换个后端/清个信息条"能解决的 —— 三个办法这一轮都实测排除了。

> 注：困难 1-4（3 行、21 格）用真图内帧**能**正常识别，`8-1` 实际是 **4 行**（`shape=(9,3)`）——
> 所以"≤3 行 = 不支持"这条旧概括不成立，别再拿行数当判据。
