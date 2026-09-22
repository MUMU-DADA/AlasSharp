# 地图识别：手工分析与踩坑记录（**不要放进生成物**）

`docs/map-detection.md` 是 `tools/diagnostics/verify_map_detection.py` 的**生成物**，
每次跑验收都会被整体重写 —— 实测踩过一次，手工补的两节被整段删掉 ✗。
所以人工写的分析、复核、踩坑一律放这里，生成物末尾只留一个指回本文件的指针。

## BOSS 认不出来？**先怀疑自己的取证链路**（2026-09-22 更正，含一次我自己的错误结论）

原始症状：清完小怪、只剩 BOSS 时上游 `Full scan find boss.` → `No boss found.` →
`battle_6` 的 `if boss:` 分支跳过 → 十次无战果 → `withdraw()`（用户实测"全清完小怪就主动撤退"）。

### 我当时的（错误）结论

我存了一帧（BOSS 已刷出），量到"BOSS 格子上游红色判据 -0.382 ✗ / 蓝色判据 0.981 ✓"，
于是判定**本客户端把 BOSS 眼睛画成了蓝色**，并加了一版"蓝色眼睛"垫片。
那一版垫片**是错的**：它建立在"存盘 PNG 的颜色 = 引擎给上游的颜色"这个前提上，而这个前提不成立。

### 链路错在哪（这个坑值得单独记）

```
引擎截图 E（ALAS 约定 RGB）
  → cv2.imwrite(path, E)      # cv2 把数组当 BGR 写 ⇒ 文件色相对真实屏幕 R/B 互换
  → load_image(path)          # PIL 忠实读文件 ⇒ 又是一次 R/B 互换
```

两次叠加在一次分析里，红与蓝正好看反。用设备裸 `adb exec-out screencap -p` 当真值一比就清楚了：

| 区域 | 真值 RGB | 引擎给上游的图 E |
| --- | --- | --- |
| "立即前往"按钮 | (247.2, 222.4, 157.7) | (245.7, 219.3, 149.3) ✓ 一致（黄） |
| 存盘 PNG 读回 | — | (157.7, 222.4, 247.2) ✗ R/B 互换 |

**结论：引擎给上游的图是对的（RGB，与设备真值一致）**，问题只在我的存盘/读回那一段。

### 正确的复核方式与结论

在**引擎真正交给上游的那张图 E** 上跑上游原版 `predict_boss`：

```powershell
python tools/diagnostics/oneoff/probe_boss_channel.py data/_map_now3.png
#   === 文件色 ===  上游原版 predict_boss 认出的 BOSS 格: []
#   === 引擎 E ===  上游原版 predict_boss 认出的 BOSS 格: [((3, 2), 'BO')]   ← 原版就能认出来
```

也就是说：**本客户端的 BOSS 图标本来就是上游期望的红色，`predict_boss()` 一直能工作**。
"只剩 BOSS 却撤退"的真正原因在别处：

1. **BOSS 所在格没进过相机视野** —— 上游 `full_scan()` 只走 `MAP.camera_data`
   （11-1 是 `['D3','E4']`），而 BOSS 可能刷在别的 `MB` 格（11-1 有 4 个：`H1/A2/F3/G6`，
   两次实测分别刷在 F3 和 A2）；
2. **扫描时机早于 BOSS 刷新** —— BOSS 在第 6 回合才出现，紧接着就扫是扫不到的。

执行器每轮调一次**上游自带、但上游自己没调用过**的 `full_scan_find_boss()`
（`camera.py:530`，它会依次把相机对准每一个 `may_boss` 格），正好补上第 1 条。

### 仍然成立的两条硬约束（与颜色无关）

`module/map_detection/grid_info.py:220-225` 只接受**声明为 `MB` 的格**：

```python
if info.is_boss:
    if not self.is_land and self.may_boss:   # ← map_data 里必须是 MB
        self.is_boss = True
    else:
        return False                          # ← 否则丢掉
```

所以 BOSS 刷在哪个格都行，但必须是 `map_data` 里标了 `MB` 的那几个之一 —— 11-1 实测
`may_boss = [H1, A2, F3, G6]`，两次真机分别落在 **F3**、**A2** ✓（离线对齐校验：
`probe_boss_global.py`）。

### 现在的做法

- `apply_boss_icon_color_compat()` **默认关闭**（保留为"某章图标真不是红色时"的一键对照，没有被证实需要就不开）；
- `op_device_screencap` 落盘前做 `RGB2BGR`，**存盘 PNG 从此与真实屏幕一致**
  （`load_image(png)` 读回来 == 引擎的 E）；`device_capture_set` 也统一成"宿主当前图 = E"，
  并显式处理 `raw=True` 时后端绕过 `BGR2RGB` 的情况。

### 复现与回归

```powershell
# 通道顺序自检（与设备裸 adb 截图对比，全程不落盘读回）
python tools/diagnostics/oneoff/probe_channel_order.py
# 在引擎约定的图上复核 BOSS 判据
python tools/diagnostics/oneoff/probe_boss_channel.py data/_map_now3.png
# 从"只剩 BOSS"的半途状态接着打（不消耗小怪那几场）
python tools/diagnostics/oneoff/resume_boss.py --chapter campaign.campaign_main.campaign_11_1 \
    --battle-count 6 --fleet1 3 --fleet2 6
```

真机记录（11-1，两套战斗流程各跑通一次，均 `exit 0`、无 `WITHDRAW`）：
`Full scan find boss.` → **`Boss found: [F3]`**（另一局是 `[A2]`）→ `BATTLE_6` →
`Using function: battle_6` → `Is boss: [F3]` → `<<< CLEAR BOSS >>>` → 战斗 →
回到章节页（`In stage.`，出击正常收尾）。事后 11-1 的关卡信息面板为
**威胁排除 100%**、三个条件全亮、章节页徽章是 `Clear!` + `COMPLETELY ELIMINATED` + ★★★。


## 复核：哪些"不支持"是真的（2026-09-22 夜，离线逐帧）

起因：跑 `probe_backends.py` 时发现"7-1 识别不了"这条结论**站不住** ——
`data/fixtures/inmap_7-1.png` 根本不是图内帧，而是**主界面**（秘书舰/宿舍背景那张 `page_main`）。
拿它去测地图识别，当然报 `No vertical line detected`。

`map_detect`（含 5 档降阈值重试）在存盘帧上的实测：

| 帧 | 内容 | homography | perspective |
| --- | --- | --- | --- |
| `subchapter_1_1.png` | 1-1，**7 格单行**（真图内帧 ✓） | FAIL `No vertical line detected` | FAIL |
| `map_hard_1_4.png` | 困难 1-4，**3 行 21 格** | **OK shape=[6,2]=21 格** | OK 但 [6,4]=35 格（多判一行）✗ |
| `inmap_7-1.png` | ⚠️ **其实是主界面，不是图内帧** | FAIL（无意义） | FAIL（无意义） |
| `inmap_2-2.png`（对照） | 2-2，4 行 24 格 | OK [6,4]=35 格 | OK [6,4]=35 格 |
| `inmap_3-1.png`（对照） | 3-1，4 行 28 格 | **OK [6,3]=28 格** ✓ | OK 但 [6,5]=42 格（多判一行）✗ |
| `inmap_3-2.png`（对照） | 3-2，4 行 32 格 | **OK [7,3]=32 格** ✓ | OK 但 [7,4]=40 格（多判一行）✗ |

结论：
1. **`perspective` 后端在多行图上会多判一行**（3-1/3-2/1-4 都是），所以默认仍必须是 `homography` ✓
   —— 这与既有记录一致；
2. **困难 1-4（3 行）现在是能识别的**（doc 前半段"困难图未能检出"那句已过时：那是加 5 档降阈值重试之前的结论）；
3. **真正确认失效的只剩"单行图"（1-1）**，而且那一帧是真图内帧；
4. **7-1 / 8-1 / 1-2 的"不支持"没有有效证据**（fixture 是错的或缺失），要重新抓真图内帧才能下结论。

复现：

```powershell
python tools/diagnostics/oneoff/probe_backends.py          # 两后端 × 六帧并排
python tools/diagnostics/oneoff/probe_backends.py --frames data/fixtures/inmap_7-1.png --backends homography
```

## 单行图 1-1：卡在"峰→线"，且不是阈值问题（`map_detect_trace` 逐段实测）

对真图内帧 `data/fixtures/subchapter_1_1.png` 跑 `map_detect_trace`：

| 线族 | 峰像素 | 过掩膜后 | Hough 拟合出的线 | Hough 原始输出 |
| --- | --- | --- | --- | --- |
| `inner_h`（内部水平） | 1310 | 1310 | **2** | 6 |
| `inner_v`（内部垂直） | **784** | 780 | **0** | **0** |
| `edge_h`（边界水平） | 2398 | 1382 | 4 | 6 |
| `edge_v`（边界垂直） | 849 | 629 | 1 | 1 |

读法：垂直方向**有 784 个峰像素**，但 Hough 连一条线都拟合不出（阈值降到 40 也是 `hough_raw=0`）。
Hough 是按"一条线上有多少共线像素"投票的 —— 784 个像素若散在很多短段/纹理上，
每条线都凑不够票数。也就是说：**单行图的"垂直峰"主要是瓦片纹理噪声，不是格线**。
这解释了为什么"降阈值 / 降峰参数 / 放大"三招都无效（既有结论在这里得到机制层面的解释）：
要修得换思路（例如改用边界线推几何，或对单行图另做一条检测路径），
而收益只有 1-1 这一张（1-2/1-3/1-4 都是多行图，不受影响），所以**先记为已知限制**。

## 三行图 / 7-1：真正的判据是"视图检出多少格"，不是"行数"（2026-09-22 夜，真机）

起因：跑 7-1 时 `map_init` **一次成功、一次失败**，失败信息是
`MapDetectionError: Vanish point and distant point too close`（日志里
`vanish_point == distant_point == (654, -1425)`）。

机制：三行图的网格线在透视里近于平行，机位不巧时两个消失点算到同一处 → 几何退化。
**这是机位相关的，不是"这张图不支持"** —— 所以 `map_init` 加了"失败就换个机位重试"
（设备级滑动，见下），实测第 2 次即通过。

但进图之后仍然跑不动，症状与机制都在日志里：

| 观察 | 值 | 含义 |
| --- | --- | --- |
| 视图检出格数 | **12–15 格** | 该图应为 24 格（8x3）→ 目标格映射错位 |
| 屏幕上的信息条 | 「已切换到第三舰队」 | 挡住地图区域，`handle_info_bar()` 只在 `map_init` 里调过 |
| 循环日志 | 每 ~23s 一次 `Arrive B1 (is_fleet)` | 反复"到达"却**从未**出现 `Combat preparation` |
| 时长 | 空转 6 分钟后手动收尾 | 用上游 `withdraw()` 干净退出 ✓ |

对照：困难 1-4（3 行、21 格）用真图内帧**能**正常识别（`shape=[6,2]`=21 格）。
所以**"≤3 行"不是干净的判据**，真正的差别是"视图能不能把该图的格数检全"。

**关于"挪机位"的一个坑**（连踩两次，值得记）：
`map_swipe()` 读 `view.center_offset`、`_map_swipe()` 读 `view.swipe_base`，而这两个属性
都是 `View.load()` **成功之后**才设置的 —— 检测已经失败了，所以这两条路必然报
`AttributeError`，相机一步都没动、三次重试失败得一模一样 ✗。
最后用**设备级滑动**（`device_swipe`，不依赖任何识别结果）才真正挪动了机位 ✓。

## 客户端弹窗：低心情"强制出击"确认框（会让整轮卡死 180s）

症状（真机，连打多场后）：点完「战斗准备」，游戏弹出

> 信息：第三舰队中「威廉·D·波特」「关岛」「苏维埃同盟」「莫斯科」处于低心情状态，
> 强制出击将降低好感且获得经验减半　[取消] [确定]

然后上游 `combat_preparation()` 一直等战斗 UI，180s 后 `GameStuckError: Wait too long`，
出击被中断（日志：`Wait too long / Waiting for {GAME_TIPS4, PAUSE, ...}`）。

**上游其实有这个弹窗的处理器**：`handle_combat_low_emotion()`（`handler/info_handler.py:187`），
被 `combat_preparation()` 主循环调用。但它第一行就是

```python
if not self.emotion.is_ignore:      # is_ignore = 'ignore' in config.Emotion_Mode
    return False                    # 默认 Emotion_Mode='calculate' → 永远 False
```

也就是说：**这是配置问题，不是缺处理器**。`Emotion.Mode` 的选项是
`[calculate, ignore, calculate_ignore]`，把它设成含 `ignore` 的档位，上游就会点「确定」继续。

本项目的做法：`op_s3_campaign_init` 默认设 `Emotion_Mode='calculate_ignore'`
（照常计算心情 + 忽略弹窗），可用参数覆盖。**代价如实记**：强制出击确实会扣，
实测战果页三艘船显示 `EXP -588`。

验证状态要如实说：配置已生效（读回 `Emotion_Mode='calculate_ignore'`、`emotion.is_ignore=True` ✓），
但**端到端还没触发过** —— 随后用 2-1 复跑时 ALAS 读到的心情是 115/119（不算低），弹窗没出现。

## 困难图 1-4 的那节标题已过时

`docs/map-detection.md` 里"困难图（1-4）：未能检出"那节是**加 5 档降阈值重试之前**的结论；
现在用同一张 `data/fixtures/map_hard_1_4.png`（真图内帧）能识别出 `shape=[6,2]`＝21 格 ✓，
详见上面的两后端对照表。这节标题由生成器输出，改不了，特此备注。
## 小图卡点的机制：内线族为 0，而**边界线族在同一帧上能检出 5 条垂直线**

承接上文（1-4 失败帧 vs 好帧）。进一步量了 Hough 的角度分布，先证伪了"角度过滤器把线滤掉"
这个猜测，再拿到真正的机制：

| 帧 | `inner_v`（内部垂直） | `edge_v`（边界垂直） | `inner_h` |
| --- | --- | --- | --- |
| **失败帧** | peaks=**1458** → hough_raw=**0**、lines=**0** | peaks=1321 → **hough_raw=5、lines=5**，theta=[0°,168°] | peaks=2218 → lines=3 |
| 好帧 | peaks=1809 → lines=**1**，theta=178° | peaks=1406 → lines=5，theta=[0°,168°] | peaks=2288 → lines=4 |

两条读数（`hough_threshold=75`、`theta_threshold=18°`、`pad=0`；edge 族 `pad=665`）：

1. **`inner_v.hough_raw=0`** —— 是 `cv2.HoughLines` 自己一条都没投出来，**不是我先前猜的角度过滤** ✗。
2. **同一帧的 `edge_v` 却有 5 条**（theta 0°/168°）—— 垂直方向的**真实格线存在且可检**，
   只是它们落在**边界线族**里；`inner_v` 那 1458 个峰像素是**散乱的纹理噪声**（不共线 ⇒ 每条的票数都不够）。

而上游单应后端的做法是：垂直族只取 `inner_v`，`inner_v` 为空就直接
`Vanish point and distant point too close` → 整张图判定失败。

**这就是 1-1 / 1-4 / 7-1 / 8-1 这些"小图不能跑"的共同机制**（1-1 更极端：
垂直方向 784 个峰像素、Hough 拟合出 0 条线；7-1 现场是 `Vertical: 10 (2 inner, 0 edge)`）。

**修的方向（下轮的入口）**：让垂直族在 `inner_v` 不足时**回退到 `edge_v`**
（同一帧上它稳定给 5 条），而不是直接判失败。改动位置在单应后端的线族装配处
（`module/map_detection/homography.py` 的 `load`/`find_lines` 一线），按铁律用垫片包，不改上游文件。
判据要有下限：只有当 `inner_v` 极少（0–1 条）且 `edge_v ≥ 2` 时才回退，避免影响本来正常的图。

