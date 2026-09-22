# 地图模型（S2 数据半边）跨语言对照

C# 的 `MapIR` 解析 vs **上游活对象**（导入章节模块读 `MAP`）。
两边用完全相同的字段顺序拼摘要，逐字符比 —— 不通过就是实现错了，没有第二种解释。

为什么必须这么验：`shape` 的解析规则很隐蔽（上游 `node2location()` 末行是`ord(node[0]) % 32 - 1, int(node[1:]) - 1`，网格数是 **shape+1**）；而且章节**没写 `camera_data` 时上游会自动生成**（`map_base.py:77` 用 `camera_2d((0,0,*shape), sight=(-3,-1,3,2))`），IR 里只有字面量。这类"差一 / 漏推导"弄错后所有坐标偏一格或整片缺相机点，且**不会以"识别不准"的形式暴露**。

脚本：`tools/diagnostics/verify_map_ir.py`；数据：`data/map_ir_crosscheck.json`。

摘要格式：`shapeX,shapeY | WxH | rows | tokens | weight | camera | spawnpts | spawn | battles`

## 结果：36 匹配 / 4 跳过 / 共 40

IR 文件 1437 个，其中 `*_base.json` **基类模块 63 个（不是章节，没有 MAP）**，真实章节 **1374** 个 —— 导出层把基类也当章节了，见下面的"顺带发现"。

| 章节 | 结果 | C# 摘要 |
| --- | --- | --- |
| `war_archives_20221222_cn/a2.json` | ✅ 匹配 | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20240815_cn/d1.json` | ✅ 匹配 | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D2,D6,H3,H7|spawnpts=D6|spawn=6|battles=6` |
| `event_20200917_cn/t3.json` | ✅ 匹配 | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20190321_en/c1.json` | ✅ 匹配 | `7,4|8x5|rows=5|tokens=40|weight=1600|camera=D2,D3,E2,E3|spawnpts=D3,C1|spawn=6|battles=6` |
| `campaign_main/campaign_13_1.json` | ✅ 匹配 | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=7|battles=7` |
| `war_archives_20190911_cn/a3.json` | ✅ 匹配 | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `event_20200716_en/a3.json` | ✅ 匹配 | `7,7|8x8|rows=8|tokens=64|weight=640|camera=D2,D6,E2,E6|spawnpts=|spawn=6|battles=6` |
| `war_archives_20201029_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220414_cn/d3.json` | ✅ 匹配 | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D2,D6,D7,F2,F6,F7|spawnpts=F7,D7|spawn=7|battles=7` |
| `war_archives_20191010_en/sp2.json` | ✅ 匹配 | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `campaign_sos/campaign_6_5.json` | ✅ 匹配 | `7,5|8x6|rows=6|tokens=48|weight=1910|camera=D2,D4|spawnpts=D4|spawn=6|battles=6` |
| `event_20200521_en/d3.json` | ✅ 匹配 | `8,8|9x9|rows=9|tokens=81|weight=0|camera=D2,D6,D7,F2,F6,F7|spawnpts=|spawn=7|battles=7` |
| `war_archives_20230525_cn/ht5.json` | ✅ 匹配 | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=7|battles=7` |
| `war_archives_20210916_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260813_cn/a3.json` | ✅ 匹配 | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D4,D7|spawnpts=D7|spawn=5|battles=5` |
| `war_archives_20220915_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20250814_cn/sp.json` | ✅ 匹配 | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5|spawnpts=E3|spawn=8|battles=8` |
| `war_archives_20230525_cn/ht6.json` | ✅ 匹配 | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G6,D6|spawn=7|battles=7` |
| `event_20200521_en/a2.json` | ✅ 匹配 | `9,5|10x6|rows=6|tokens=60|weight=0|camera=D2,D4,G2,G4|spawnpts=|spawn=0|battles=0` |
| `war_archives_20190911_cn/c3.json` | ✅ 匹配 | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `war_archives_20210819_cn/c2.json` | ✅ 匹配 | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `event_20220210_cn/c3.json` | ✅ 匹配 | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=6|battles=6` |
| `event_20210415_tw/sp3.json` | ✅ 匹配 | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=G5,D5|spawn=6|battles=6` |
| `war_archives_20200820_cn/a1.json` | ✅ 匹配 | `7,5|8x6|rows=6|tokens=48|weight=480|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20191031_en/a4.json` | ✅ 匹配 | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20201029_cn/sp2.json` | ✅ 匹配 | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=C2|spawn=5|battles=5` |
| `war_archives_20200903_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220915_cn/c1.json` | ✅ 匹配 | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20211028_tw/c3.json` | ✅ 匹配 | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20241219_cn/d1.json` | ✅ 匹配 | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D2|spawn=6|battles=6` |
| `event_20210610_tw/sp2.json` | ✅ 匹配 | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=D2,D4,H2,H4|spawnpts=D4|spawn=5|battles=5` |
| `event_20230223_cn/d3.json` | ✅ 匹配 | `9,8|10x9|rows=9|tokens=90|weight=4500|camera=E3,E7,F3,F7|spawnpts=E7|spawn=7|battles=7` |
| `event_20220728_cn/b1.json` | ✅ 匹配 | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G4|spawnpts=D2|spawn=6|battles=6` |
| `event_20230914_cn/b2.json` | ✅ 匹配 | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2,D2|spawn=6|battles=6` |
| `event_20200917_cn/t1.json` | ✅ 匹配 | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=H5,D5|spawn=5|battles=5` |
| `event_20250227_cn/b2.json` | ✅ 匹配 | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E2,E6,G2,G6|spawnpts=G2,E2|spawn=6|battles=6` |
| `war_archives_20220224_cn/b2.json` | ✅ 匹配 | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=E2|spawn=6|battles=6` |
| `event_20240912_cn/b2.json` | ✅ 匹配 | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E2,E6,H2,H6|spawnpts=E2|spawn=6|battles=6` |
| `event_20200521_en/c3.json` | ✅ 匹配 | `10,6|11x7|rows=7|tokens=77|weight=0|camera=F2,F4,F6|spawnpts=|spawn=0|battles=0` |
| `event_20201229_cn/b2.json` | ✅ 匹配 | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D2|spawn=6|battles=6` |

## 跳过原因

- `war_archives_20201029_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20210916_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20220915_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20200903_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过

## 顺带发现：导出层把基类模块当成了章节

`campaign/**/*.py` 里有 63 个 `*_base.py`（`campaign_2_base`、`campaign_base` 之类），它们是**基类模块，没有 `MAP` 对象**，但导出层把它们也导成了"章节"。

后果：凡是以 IR 文件数统计"章节数"的地方都会虚高 —— 实际是 **1374 个真实章节**。
本脚本按设计跳过它们（`AttributeError: module ... has no attribute MAP` 就是这么来的）。
要不要在导出层过滤掉，等下一轮改导出器时一起处理（改动会牵动 IR 数量与既有校验口径，不适合顺手改）。

## 复现

```powershell
alashub map-ir                              # C# 解析全部 IR 并导出摘要
python tools/diagnostics/verify_map_ir.py   # 与上游活对象对照（固定随机种子）
```
