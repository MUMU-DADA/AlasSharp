# S3 入口序列：实测记录与卡点诊断

> 目标：让 C# 通过**宿主协议**驱动上游的战役实现（tier A 的 9 个调用），而不是用 C# 重写战斗逻辑。
> 复现脚本：`tools/diagnostics/s3_probe_campaign.py`；相关 op：`s3_campaign_init` / `s3_campaign_info` / `s3_campaign_call`。

## 已打通的链路（每步都是上游实现，C# 只发指令）

| 步骤 | 结果 | 说明 |
| --- | --- | --- |
| 探针实例化 `Campaign(cfg, device)` | ✅ | MRO：Campaign → CampaignBase → CampaignUI → Map → Fleet → Camera → AutoSearchCombat |
| `s3_campaign_init(1-1)` | ✅ | 含**种一帧**（334 ms）与配置绑定 |
| `campaign_ensure_chapter(1)` | ✅ | 158–2114 ms，**上游代码完成真实导航** |
| `campaign_get_entrance('1-1')` | ✅ | 返回值需 `store='ENTRANCE'` 写回实例属性 |
| `enter_map(@ENTRANCE, 'normal')` | ❌ | 操作游戏 62 s 后 `GameStuckError: Wait too long` |

## 三个"ALAS 方法的前提在我们这里不成立"的坑（都已修）

1. **`device.image` 需要先种一帧**：ALAS 的方法假定它已存在，而它只在 `screenshot()` 之后才有。
   少了这一步，第一个动作就报 `AttributeError: 'Device' object has no attribute 'image'`。
   → `s3_campaign_init` 里补一次 `device.screenshot()`。
2. **需要 `store=` 接住返回值**：上游很多方法**靠返回值传对象**——
   `campaign_get_entrance('1-1')` 返回的 Button 要赋给 `self.ENTRANCE`；
   类默认的 `ENTRANCE` 是**空 Button**，直接传它报 `not enough values to unpack (expected 4, got 0)`。
3. **配置必须跟着章节走**：不设 `Campaign_Name` 时，实例化 2-1 却仍读上一次的 `12-4`；
   `bind('Campaign')` 还会把截图后端重置成 `auto`（会去跑性能基准）。

## `enter_map` 卡点的精确诊断（纠正一个错误猜测）

我先前猜"大概率又是客户端 UI 版本差异"，**数据推翻了它**。在活着的舰队选择浮层上量素材：

| 素材 | 匹配 | 分数 |
| --- | --- | --- |
| `map/FLEET_1_CLEAR`（清空第一舰队） | ✅ | **0.9952** |
| `map/FLEET_1_CHOOSE`（选择） | ✅ | **0.9924** |
| `map/FLEET_PREPARATION`（立刻前往） | ✅ | 0.9929 |
| `map/FLEET_2_CLEAR` | ❌ | 0.1730 |
| `map/SUBMARINE_CLEAR` | ❌ | 0.1858 |

素材全都对得上；不存在的两个按钮是**因为本账号第二舰队与潜艇舰队都是空的**
（画面：可出击舰队数 1/2、潜艇舰队数 0/0）。

而日志显示 `Using fleet: [1, 2, 0]`（要用第一、第二两支舰队），随后进入
`map_fleet_preparation.fleet_preparation() → clear()`，**等一组永远不会出现的按钮**：

```
WARNING Waiting for {'FLEET_2_CLEAR', 'FLEET_PREPARATION', 'FLEET_2_ADVICE', 'DAILY_CHECK',
                     'SUBMARINE_ADVICE', 'FLEET_1_ADVICE', 'POPUP_CANCEL', 'POPUP_CONFIRM_WHITE',
                     'SUBMARINE_CLEAR', 'FLEET_1_CLEAR'}
```

**结论：问题是「要用的舰队 ≠ 账号里存在的舰队」——配置/账号状态不匹配，与 UI 无关。**
修法方向：让舰队选择与账号一致（配置只用一支舰队，或账号补上第二舰队），
而不是去改素材或遮罩。

## 安全要点（本项目自己的教训）

- 进战斗前用**上游配置键**关掉自动行为：`Campaign_UseClearMode=False`、`Campaign_UseAutoSearch=False`
  （比点 UI 开关可靠；实测读回 `ClearMode=False`）。本项目曾因误开自律寻敌把一场战斗打完。
- `s3_campaign_call` 有**硬性安全联锁**：`battle*/clear*/enter_map/run/fleet*/goto/map_*/...`
  必须显式 `allow_actions=true` 才执行。
- 两次实测都**没有真正进入战斗、油量未变**（24831），卡住后均以点 X 关闭浮层收尾。

## 入口序列的调用顺序要求（实测新增）

`campaign_ensure_chapter(chapter)` **必须在 `campaign_get_entrance(name)` 之前**调用：
入口节点坐标依赖当前选中的章节，顺序反了会拿到**空 Button**：

```
漏掉 ensure_chapter 时：ENTRANCE.button = "()"            → enter_map 报 not enough values to unpack
先 ensure_chapter(1)：  ENTRANCE.button = (120,475,159,514) → 正常
```

（这也解释了此前一次"ENTRANCE 是空 Button"的困惑：不是返回值/赋值的问题，是**前一步没做**。）

## 客户端素材状态（活画面实测）

| 素材 | 分数 | 含义 |
| --- | --- | --- |
| `map/MAP_PREPARATION` | **1.0000** | 关卡进场面板正常 |
| `map/FLEET_1_CHOOSE` | 0.9923 | 「选择」正常 |
| `map/FLEET_1_CLEAR` | 0.9952 | 「清空」正常 |
| `map/FLEET_PREPARATION` | 0.9928 | 「立刻前往」正常 |

## 尚未验证的假设：点「选择」不展开下拉

`FleetOperator` 的文档说明 `choose` 是"打开/关闭下拉菜单"的按钮：ALAS 点它 → 展开舰队下拉（`FLEET_1_BAR`）
→ `parse_fleet_bar()` 读下拉找序号 → 点对应项。若点了不展开，就会反复点 → `GameTooManyClickError`。

**本轮没能验证**：实验开始时画面状态已经漂移（当时并不在 `page_campaign`），序列没走完，
`FLEET_1_BAR` 的分数是在浮层未打开时量的（0.07，无意义）。
下次要做这个实验，必须先确认状态、再量「点选择前 / 点选择后」的 `FLEET_1_BAR`。

## 协议侧待改：Button 坐标过不了 JSON

`json_default` 会把 numpy 整数元组序列化成**字符串**：`"(np.int64(120), np.int64(475), ...)"`，
C# 拿不到可用坐标。两个方向：
1. 让 `json_default` 把数值元组/ndarray 转成数组（协议层修）；
2. **点击一律让 ALAS 自己执行**（`device.click(@ENTRANCE)` 已验证可用，62.5 ms）——
   这条更符合"动作交给上游"的架构，也绕开了序列化问题。
