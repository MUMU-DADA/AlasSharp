# 地图模型（S2 数据半边）跨语言对照

C# 的 `MapIR` 解析 vs **上游活对象**（导入章节模块读 `MAP`）。
两边用完全相同的字段顺序拼摘要，逐字符比 —— 不通过就是实现错了，没有第二种解释。

为什么必须这么验：`shape` 的解析规则很隐蔽（上游 `node2location()` 末行是`ord(node[0]) % 32 - 1, int(node[1:]) - 1`，网格数是 **shape+1**）；而且章节**没写 `camera_data` 时上游会自动生成**（`map_base.py:77` 用 `camera_2d((0,0,*shape), sight=(-3,-1,3,2))`），IR 里只有字面量。这类"差一 / 漏推导"弄错后所有坐标偏一格或整片缺相机点，且**不会以"识别不准"的形式暴露**。

脚本：`tools/diagnostics/verify_map_ir.py`；数据：`data/map_ir_crosscheck.json`。

摘要格式：`shapeX,shapeY | WxH | rows | tokens | weight | camera | spawnpts | spawn | battles`

口径说明：`camera` 与 `spawnpts` 按**集合**比（排序后拼）。上游 `camera_data` 是
`SelectedGrids`，顺序不影响用途；它那串顺序来自 CPython `set()` 的迭代顺序
（`camera_1d` 里 `[x for x in set(out) if ...]`），属于实现细节 —— 复刻它既脆弱又无意义。
排序只丢掉顺序，**成员仍必须逐项一致**，真错了照样查得出来。

## 结果：字段摘要 11 匹配 / 1 跳过；网格指纹 11/11 匹配

两类对照的含义不同：

- **字段摘要**：shape/map_data/weight/camera/spawn 等字段的规范化值 —— 防"差一/漏推导"；
- **网格指纹**：用上游 `GridInfo.decode` 与 C# 的移植版**逐格**解码 map_data，把整张地图的语义压成一行比 —— 防 token 语义抄错（例如上游 `decode` 会先 `text.upper()`，所以 `Me` 等同 `ME`；而 `--` 不在表里、所有标志为假）。

IR 文件 1437 个，其中 `*_base.json` **基类模块 63 个（不是章节，没有 MAP）**，真实章节 **1374** 个 —— 导出层把基类也当章节了，见下面的"顺带发现"。

| 章节 | 结果 | 网格 | C# 摘要 |
| --- | --- | --- | --- |
| `war_archives_20221222_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20240815_cn/d1.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D2,D6,H3,H7|spawnpts=D6|spawn=6|battles=6` |
| `event_20200917_cn/t3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20190321_en/c1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1600|camera=D2,D3,E2,E3|spawnpts=C1,D3|spawn=6|battles=6` |
| `campaign_main/campaign_13_1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=7|battles=7` |
| `war_archives_20190911_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `event_20200716_en/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=640|camera=D2,D6,E2,E6|spawnpts=|spawn=6|battles=6` |
| `war_archives_20201029_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220414_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `war_archives_20191010_en/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `campaign_sos/campaign_6_5.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1910|camera=D2,D4|spawnpts=D4|spawn=6|battles=6` |
| `event_20200521_en/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=0|camera=D2,D6,D7,F2,F6,F7|spawnpts=|spawn=7|battles=7` |

## 跳过原因

- `war_archives_20201029_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过

## 已知问题（全量跑出来的，逐条留证据）

### 1. 有章节的地图是**从别的章节拷贝**的，导出成空地图

`campaign_main/campaign_15_4_121.json`：IR 侧 `1x1 / rows=0 / tokens=0`，上游活对象是 `11x9 / 99 格 / weight=4950`。源码写的是：

```python
from .campaign_15_4 import MAP as MAP_15_4, Campaign as Campaign_15_4
MAP = copy.copy(MAP_15_4)      # ← 地图来自另一个章节
MAP.name = '15-4-121'
```

导出器（AST 抓字面量赋值）看不到这条链，于是导出了空地图。**这正是跨语言对照存在的意义** —— 这种错不会表现成"识别不准"，只会让引擎在一张空地图上做规划。

修法（留待改导出器时一起做）：导出器解析 `copy.copy(MAP_X)` / `from .X import MAP as MAP_X`，把被引用章节的地图复制过来；或至少记一个 `map.derived_from = "campaign_15_4"` 指针让消费方跟进。

### 2. 导出的"章节"里混着非章节、以及上游自己都导入不了的死模块

全量 1437 个 IR 文件里：

- **63 个 `*_base.py` 基类模块**（没有 `MAP` 对象）—— 导出层把基类当章节了，所以"章节数"应以 **1374** 为准；
- 另有若干章节**连上游自己都导入不了**（`ImportError: cannot import name ... from module.campaign.assets`），说明它们引用的素材名在当前上游已不存在（历史遗留的死章节）。

两者都会被"按 IR 文件数统计章节数"的地方算进去。跳过原因直方图：

| 跳过原因 | 数量 |
| --- | --- |
| 基类模块（无 MAP 对象），按设计跳过 | 1 |

## 复现

```powershell
alashub map-ir                              # C# 解析全部 IR，导出摘要与网格指纹
python tools/diagnostics/verify_map_ir.py   # 与上游活对象对照（固定随机种子）
$env:SAMPLE = "2000"                        # 跑全量（实测 1437 个文件约 10 秒）
```
