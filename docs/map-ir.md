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

## 结果：字段摘要 1367 匹配 / 70 跳过；网格指纹 1367/1367 匹配

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
| `war_archives_20230525_cn/ht5.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=7|battles=7` |
| `war_archives_20210916_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260813_cn/a3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D4,D7|spawnpts=D7|spawn=5|battles=5` |
| `war_archives_20220915_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20250814_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5|spawnpts=E3|spawn=8|battles=8` |
| `war_archives_20230525_cn/ht6.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6,G6|spawn=7|battles=7` |
| `event_20200521_en/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=0|camera=D2,D4,G2,G4|spawnpts=|spawn=0|battles=0` |
| `war_archives_20190911_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `war_archives_20210819_cn/c2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `event_20220210_cn/c3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=6|battles=6` |
| `event_20210415_tw/sp3.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=D5,G5|spawn=6|battles=6` |
| `war_archives_20200820_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=480|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20191031_en/a4.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20201029_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=C2|spawn=5|battles=5` |
| `war_archives_20200903_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220915_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20211028_tw/c3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20241219_cn/d1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D2|spawn=6|battles=6` |
| `event_20210610_tw/sp2.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=D2,D4,H2,H4|spawnpts=D4|spawn=5|battles=5` |
| `event_20230223_cn/d3.json` | ✅ 匹配 | ✅ | `9,8|10x9|rows=9|tokens=90|weight=4500|camera=E3,E7,F3,F7|spawnpts=E7|spawn=7|battles=7` |
| `event_20220728_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G4|spawnpts=D2|spawn=6|battles=6` |
| `event_20230914_cn/b2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20200917_cn/t1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D5,H5|spawn=5|battles=5` |
| `event_20250227_cn/b2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E2,E6,G2,G6|spawnpts=E2,G2|spawn=6|battles=6` |
| `war_archives_20220224_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=E2|spawn=6|battles=6` |
| `event_20240912_cn/b2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E2,E6,H2,H6|spawnpts=E2|spawn=6|battles=6` |
| `event_20200521_en/c3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=0|camera=F2,F4,F6|spawnpts=|spawn=0|battles=0` |
| `event_20201229_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D2|spawn=6|battles=6` |
| `event_20201229_cn/sp.json` | ✅ 匹配 | ✅ | `12,9|13x10|rows=10|tokens=130|weight=6500|camera=F6,G8,I6|spawnpts=D8|spawn=8|battles=8` |
| `event_20250814_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20230914_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20190221_en/b1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `event_20211028_tw/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `event_20220428_cn/b1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=6|battles=6` |
| `event_20200611_en/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=0|camera=D2,D5,E2,E5|spawnpts=|spawn=5|battles=5` |
| `event_20230914_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D6|spawn=6|battles=6` |
| `event_20221222_cn/b2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=F2|spawn=6|battles=6` |
| `war_archives_20220224_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=7|battles=7` |
| `campaign_main/campaign_1_1.json` | ✅ 匹配 | ✅ | `6,0|7x1|rows=1|tokens=7|weight=0|camera=D1|spawnpts=D1|spawn=2|battles=2` |
| `event_20210624_tw/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20210429_tw/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20210916_cn/a1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E5|spawn=5|battles=5` |
| `war_archives_20230525_cn/t3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `campaign_main/campaign_2_3.json` | ✅ 匹配 | ✅ | `5,4|6x5|rows=5|tokens=30|weight=643|camera=D3|spawnpts=D1,D3|spawn=4|battles=4` |
| `event_20210624_tw/a2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `event_20210225_tw/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20200521_en/a3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=0|camera=D2,D5,H2,H5|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220210_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20210429_tw/a4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20210325_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=E2,E5|spawnpts=E2,E5|spawn=6|battles=6` |
| `event_20210819_cn/sp.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=8|battles=8` |
| `war_archives_20210325_cn/ds1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `event_20211028_tw/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `campaign_main/campaign_3_3.json` | ✅ 匹配 | ✅ | `5,4|6x5|rows=5|tokens=30|weight=1101|camera=D3|spawnpts=D3|spawn=4|battles=4` |
| `war_archives_20220324_cn/sp2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20220818_cn/sp4.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D6,E3,F6|spawnpts=E7|spawn=6|battles=6` |
| `event_20200917_cn/ht3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20200917_cn/t2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D3,D5,E3,E5|spawnpts=E5|spawn=5|battles=5` |
| `war_archives_20180607_cn/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=E3,E4|spawnpts=|spawn=5|battles=5` |
| `event_20240521_cn/d2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=E2,E5|spawnpts=F5|spawn=7|battles=7` |
| `event_20240521_cn/d1.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,F3,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20200806_cn/sp1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=480|camera=D3,D4|spawnpts=D3,D4|spawn=5|battles=5` |
| `event_20200423_cn/d2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=0|camera=D2,D5,G2,G5|spawnpts=|spawn=0|battles=0` |
| `event_20240229_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D5,F3,F5|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20220324_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20191031_en/d1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20240425_cn/isp1.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `event_20220728_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,E6|spawnpts=E2|spawn=6|battles=6` |
| `event_20210121_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20220210_cn/b2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=6|battles=6` |
| `war_archives_20210624_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=7|battles=7` |
| `war_archives_20210225_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D4,H2,H4|spawnpts=H4|spawn=7|battles=7` |
| `event_20231221_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,E6|spawnpts=F2|spawn=5|battles=5` |
| `campaign_main/campaign_14_1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=3030|camera=D2,D5,E2,E5|spawnpts=E5|spawn=7|battles=7` |
| `event_20210225_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D4,H2,H4|spawnpts=H4|spawn=7|battles=7` |
| `war_archives_20210624_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20190911_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20211014_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20211125_cn/tss3.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C3|spawnpts=C3|spawn=1|battles=1` |
| `event_20200820_cn/d3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `event_20240829_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20210527_tw/a2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `war_archives_20200603_cn/sp1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H5|spawn=5|battles=5` |
| `war_archives_20220728_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=7|battles=7` |
| `event_20211125_cn/t1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20241219_cn/c3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D2,D5,D7,H2,H5,H7|spawnpts=D6|spawn=6|battles=6` |
| `campaign_main/campaign_8_1.json` | ✅ 匹配 | ✅ | `8,2|9x3|rows=3|tokens=27|weight=991|camera=D1,F1|spawnpts=D1,F1|spawn=5|battles=5` |
| `war_archives_20210624_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20200917_cn/ts2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=1|battles=1` |
| `campaign_sos/campaign_10_5.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4,F4|spawn=7|battles=7` |
| `war_archives_20211229_cn/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20180607_cn/a4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E3,E5|spawnpts=|spawn=6|battles=6` |
| `war_archives_20210527_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,D6|spawn=6|battles=6` |
| `war_archives_20190911_cn/as1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200521_en/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=0|camera=D2,D6,D7,F2,F6,F7|spawnpts=|spawn=6|battles=6` |
| `event_20240725_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=5|battles=5` |
| `war_archives_20210325_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20201029_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20210121_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `war_archives_20200917_cn/ht4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4910|camera=D2,D5,D7,H2,H5,H7|spawnpts=D2,D7|spawn=6|battles=6` |
| `event_20210225_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220915_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D4,D6,E3|spawnpts=D2|spawn=5|battles=5` |
| `event_20240229_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=5|battles=5` |
| `event_20240521_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=5430|camera=D3,D6,H3,H6|spawnpts=D7|spawn=6|battles=6` |
| `event_20231221_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D6,H3,H6|spawnpts=D6,H6|spawn=6|battles=6` |
| `campaign_main/campaign_9_3.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=500|camera=D3,E4|spawnpts=E4|spawn=6|battles=6` |
| `event_20251218_cn/sp.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=F2|spawn=8|battles=8` |
| `war_archives_20200903_cn/sp1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3825|camera=D3,D5,H3,H5|spawnpts=D2|spawn=5|battles=5` |
| `event_20200917_cn/hts2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=1|battles=1` |
| `campaign_main/campaign_9_1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2820|camera=E3|spawnpts=C1,C3|spawn=6|battles=6` |
| `war_archives_20240725_cn/ht3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `event_20210225_cn/a3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E3,E6|spawnpts=E6|spawn=5|battles=5` |
| `event_20200521_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=0|camera=D2,D5,H2,H5|spawnpts=|spawn=0|battles=0` |
| `event_20220915_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D4,D6,E3|spawnpts=D2|spawn=5|battles=5` |
| `event_20210325_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `event_20260417_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20200723_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D3,D5,F2,F5|spawnpts=D3|spawn=4|battles=4` |
| `campaign_main/campaign_16_base_aircraft.json` | ⏭️ AttributeError: module 'campaign.campaign_main.campaign_16_base_aircraft' has no attribute 'MAP' | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20200917_cn/t6.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `event_20250912_cn/sp.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3200|camera=D4,F4|spawnpts=F4|spawn=8|battles=8` |
| `event_20260908_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20241219_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210121_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=7|battles=7` |
| `war_archives_20230223_cn/d3.json` | ✅ 匹配 | ✅ | `9,8|10x9|rows=9|tokens=90|weight=4500|camera=E3,E7,F3,F7|spawnpts=E7|spawn=7|battles=7` |
| `event_20201126_cn/sp.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=E3,E4|spawnpts=E4|spawn=8|battles=8` |
| `event_20231221_cn/a2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20240521_cn/b2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=E2,E5|spawnpts=F5|spawn=6|battles=6` |
| `campaign_sos/campaign_7_5.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1872|camera=D2,D4|spawnpts=D4|spawn=6|battles=6` |
| `event_20250520_cn/a3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=3355|camera=D5,D7,F5,F7|spawnpts=D5,F5|spawn=5|battles=5` |
| `war_archives_20210225_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20260226_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D6,D8,F4,F6,F8|spawnpts=D2|spawn=6|battles=6` |
| `event_20260520_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G3|spawn=6|battles=6` |
| `war_archives_20210422_cn/d2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D5,D7,F3,F7|spawnpts=D2|spawn=7|battles=7` |
| `war_archives_20190314_en/sp3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=F4|spawn=6|battles=6` |
| `event_20260520_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20251218_cn/c3.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=F3,F6|spawnpts=F6|spawn=6|battles=6` |
| `event_20220414_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=6|battles=6` |
| `event_20251218_cn/b3.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=F3,F6|spawnpts=F6|spawn=6|battles=6` |
| `war_archives_20201012_cn/sp1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `event_20230223_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20221124_cn/th5.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=7|battles=7` |
| `campaign_main/campaign_1_3.json` | ✅ 匹配 | ✅ | `5,2|6x3|rows=3|tokens=18|weight=0|camera=C1|spawnpts=C1|spawn=3|battles=3` |
| `event_20240521_cn/b1.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,F3,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20260520_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `campaign_main/campaign_9_2.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=570|camera=D3,E3|spawnpts=D3|spawn=6|battles=6` |
| `event_20200507_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=0|camera=D3,D5|spawnpts=|spawn=5|battles=5` |
| `event_20220324_cn/sp2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20200507_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=0|camera=D3,D6|spawnpts=|spawn=6|battles=6` |
| `event_20210624_tw/b3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F2,F6,F8,I2,I6,I8|spawnpts=F8,I8|spawn=6|battles=6` |
| `war_archives_20220428_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D7,F4,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `war_archives_20200820_cn/d3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=619|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `war_archives_20230223_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D5,E2,E5|spawnpts=D6|spawn=5|battles=5` |
| `event_20220324_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20241121_cn/ttl5.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `event_20220224_cn/a1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20210422_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,G2|spawn=6|battles=6` |
| `war_archives_20190321_en/b1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=7|battles=7` |
| `war_archives_20220210_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20241121_cn/ttl3.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `war_archives_20230525_cn/hts1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `event_20220224_cn/sp.json` | ✅ 匹配 | ✅ | `9,8|10x9|rows=9|tokens=90|weight=4500|camera=D2,D6,G2,G6|spawnpts=G6|spawn=8|battles=8` |
| `event_20231026_cn/t3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D3|spawn=6|battles=6` |
| `event_20201126_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20250520_cn/d2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=3660|camera=D4,D6,G4,G6|spawnpts=D4|spawn=7|battles=7` |
| `event_20230223_cn/b3.json` | ✅ 匹配 | ✅ | `9,8|10x9|rows=9|tokens=90|weight=4500|camera=E3,E7,F3,F7|spawnpts=E7|spawn=6|battles=6` |
| `event_20241219_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210325_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20210225_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D4,H2,H4|spawnpts=H4|spawn=6|battles=6` |
| `war_archives_20190314_en/sp1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2000|camera=D2,D3,E2,E3|spawnpts=D3|spawn=5|battles=5` |
| `event_20260226_cn/sp.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D5,E2,E5|spawnpts=D2|spawn=8|battles=8` |
| `event_20230817_cn/sp.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D6,E3,F6|spawnpts=D6|spawn=8|battles=8` |
| `war_archives_20200312_cn/sp3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4,F2|spawn=6|battles=6` |
| `event_20230817_cn/d3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F3,G6,H4|spawnpts=G8|spawn=7|battles=7` |
| `war_archives_20200917_cn/t4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4910|camera=D2,D5,D7,H2,H5,H7|spawnpts=D2,D7|spawn=6|battles=6` |
| `event_20260813_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G2,G5|spawnpts=D3|spawn=6|battles=6` |
| `war_archives_20240725_cn/ht1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20260326_cn/ht3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E3,E6|spawnpts=E6|spawn=7|battles=7` |
| `war_archives_20181026_en/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20230525_cn/ht2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210527_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G6|spawn=7|battles=7` |
| `war_archives_20190911_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20260520_cn/sp.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D6,D8|spawnpts=D6|spawn=8|battles=8` |
| `event_20200917_cn/ts1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=E4|spawn=4|battles=4` |
| `event_20250912_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=2795|camera=E2,E6,G4|spawnpts=G6|spawn=7|battles=7` |
| `war_archives_20220526_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=6|battles=6` |
| `war_archives_20240725_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20230817_cn/b1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `event_20240521_cn/c1.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D7,F3,F7|spawnpts=|spawn=5|battles=5` |
| `event_20211028_tw/a1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D2,D3|spawn=5|battles=5` |
| `war_archives_20220915_cn/b2.json` | ✅ 匹配 | ✅ | `12,5|13x6|rows=6|tokens=78|weight=3900|camera=D4,E3,G3,G4|spawnpts=H3|spawn=6|battles=6` |
| `event_20220414_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H2,H5|spawn=7|battles=7` |
| `war_archives_20211028_cn/c2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `event_20221222_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=5|battles=5` |
| `event_20260226_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D4,D6,E3|spawnpts=D4|spawn=5|battles=5` |
| `event_20260625_cn/ht3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,F2,F5|spawnpts=F5|spawn=7|battles=7` |
| `event_20240425_cn/sp4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=C3,C6,F3,F6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20210819_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20211014_cn/sp1.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=A3,A5|spawnpts=A3|spawn=5|battles=5` |
| `war_archives_20191031_en/b3.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D2,D6|spawnpts=D6|spawn=6|battles=6` |
| `event_20220915_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,E5|spawnpts=D7|spawn=6|battles=6` |
| `event_20220915_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20241024_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20220224_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=E2|spawn=7|battles=7` |
| `war_archives_20210819_cn/c1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=2100|camera=C2,C5|spawnpts=C2,C5|spawn=5|battles=5` |
| `war_archives_20220526_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D3,D6|spawn=5|battles=5` |
| `event_20240912_cn/d1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D3,D6,G2,G6|spawnpts=E2|spawn=6|battles=6` |
| `event_20230914_cn/a2.json` | ✅ 匹配 | ✅ | `10,4|11x5|rows=5|tokens=55|weight=2750|camera=D3,F3,H3|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20230525_cn/hts2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=7|battles=7` |
| `war_archives_20201029_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=C2|spawn=5|battles=5` |
| `war_archives_20200917_cn/ht3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D5|spawn=6|battles=6` |
| `event_20210624_cn/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=8|battles=8` |
| `event_20260520_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `event_20210225_tw/b2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=6|battles=6` |
| `war_archives_20190911_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `war_archives_20230223_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20250424_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `campaign_main/campaign_14_3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=3800|camera=D2,D6,G2,G6|spawnpts=G2|spawn=7|battles=7` |
| `event_20260326_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,F5|spawnpts=E5|spawn=5|battles=5` |
| `event_20211028_cn/sp.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,E6|spawnpts=D3|spawn=8|battles=8` |
| `event_20210624_tw/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20210225_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220414_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=C6,E3,E5,F6|spawnpts=E7|spawn=6|battles=6` |
| `war_archives_20200820_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=490|camera=D3,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20250424_cn/sp.json` | ✅ 匹配 | ✅ | `12,4|13x5|rows=5|tokens=65|weight=3250|camera=F3,H3|spawnpts=F3|spawn=8|battles=8` |
| `event_20221222_cn/d2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=F2|spawn=7|battles=7` |
| `event_20211028_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D5,H5|spawn=7|battles=7` |
| `event_20220728_cn/sp.json` | ✅ 匹配 | ✅ | `18,8|19x9|rows=9|tokens=171|weight=8550|camera=G6,I6,K6,K7|spawnpts=K7|spawn=8|battles=8` |
| `war_archives_20181026_en/b1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20210121_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260226_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E6|spawnpts=E2|spawn=5|battles=5` |
| `campaign_main/campaign_5_2.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1100|camera=D3,E3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20260908_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D2|spawn=5|battles=5` |
| `event_20241024_cn/t6.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=E3,G3|spawnpts=F4|spawn=7|battles=7` |
| `event_20220414_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20230525_cn/t2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20220210_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20230914_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D6,H2,H5,H7|spawnpts=H5|spawn=7|battles=7` |
| `war_archives_20211229_cn/b2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=F5|spawn=6|battles=6` |
| `event_20240229_cn/b2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=6|battles=6` |
| `event_20220526_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D3,D6|spawn=5|battles=5` |
| `campaign_main/campaign_5_3.json` | ✅ 匹配 | ✅ | `6,4|7x5|rows=5|tokens=35|weight=1160|camera=D2,D3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20201229_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20180607_cn/d1.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=E3,E5,F3,F5|spawnpts=|spawn=7|battles=7` |
| `event_20240912_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F2,F6|spawnpts=F6|spawn=5|battles=5` |
| `war_archives_20210624_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20210624_tw/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `campaign_main/campaign_3_1.json` | ✅ 匹配 | ✅ | `6,3|7x4|rows=4|tokens=28|weight=629|camera=D2|spawnpts=D1,D2|spawn=4|battles=4` |
| `war_archives_20191031_en/b2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=5|battles=5` |
| `event_20210527_tw/c3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `event_20230525_cn/t1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,F2,F6|spawnpts=E6,F6|spawn=5|battles=5` |
| `event_20200326_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=0|camera=D2,D5,E2,E5|spawnpts=|spawn=5|battles=5` |
| `event_20230223_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D5,E2,E5|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20210527_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,D6|spawn=6|battles=6` |
| `war_archives_20210422_cn/b2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D5,D7,F3,F7|spawnpts=D2|spawn=6|battles=6` |
| `campaign_sos/campaign_8_5.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2345|camera=D3,D5|spawnpts=D3,D5|spawn=5|battles=5` |
| `event_20210225_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D8,F4,F8|spawnpts=D4,F4|spawn=7|battles=7` |
| `war_archives_20210624_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `event_20220224_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=6|battles=6` |
| `event_20220224_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260908_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20211125_cn/t3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3600|camera=D2,D6,E2,E6|spawnpts=E2,E6|spawn=6|battles=6` |
| `war_archives_20220728_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D6|spawn=5|battles=5` |
| `event_20220407_tw/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20230525_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260226_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20210325_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=E2,E5|spawnpts=E2,E5|spawn=6|battles=6` |
| `event_20220407_tw/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20230803_cn/sp3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D4,D6,F2,F4,F6|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20251218_cn/a3.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=F3,F6|spawnpts=F6|spawn=5|battles=5` |
| `event_20210819_cn/a3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20260625_cn/ht1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20181227_cn/d2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `event_20260813_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `war_archives_20210325_cn/cs1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20220526_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `war_archives_20210819_cn/d2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C2|spawn=7|battles=7` |
| `war_archives_20220728_cn/b2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D3,E3,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20210916_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G2|spawn=6|battles=6` |
| `campaign_main/campaign_10_1.json` | ✅ 匹配 | ✅ | `6,5|7x6|rows=6|tokens=42|weight=1615|camera=D3|spawnpts=D1,D4|spawn=7|battles=7` |
| `event_20211028_tw/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,E2,E6,E7|spawnpts=D2,D7|spawn=6|battles=6` |
| `event_20260326_cn/ht2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F5|spawnpts=F2|spawn=7|battles=7` |
| `event_20230223_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D5,E2,E5|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20210325_cn/ds2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5,F2,F5|spawnpts=E5|spawn=7|battles=7` |
| `event_20210325_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `event_20220224_cn/c1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20201029_cn/sp.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D3,D6|spawnpts=D6|spawn=8|battles=8` |
| `event_20251023_cn/t4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D3|spawn=6|battles=6` |
| `war_archives_20211229_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G6|spawn=6|battles=6` |
| `event_20241024_cn/t3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D5,G2,G5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20191031_en/b4.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20200603_en/sp3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D3,F5|spawnpts=|spawn=6|battles=6` |
| `event_20210325_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F6|spawn=6|battles=6` |
| `event_20240229_cn/d2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=7|battles=7` |
| `event_20250227_cn/d1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D6|spawn=6|battles=6` |
| `event_20230525_cn/t3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `campaign_main/campaign_12_2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `campaign_hard/campaign_12_4.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D6|spawn=8|battles=8` |
| `war_archives_20211028_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D5,H5|spawn=7|battles=7` |
| `event_20220818_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20240425_cn/isp6.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `war_archives_20190221_en/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20211028_tw/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20240725_cn/ht2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=7|battles=7` |
| `event_20210225_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20200903_en/sp2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3820|camera=D3,D5,H3,H5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20251023_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E6,F3,F6|spawnpts=F3,F6|spawn=8|battles=8` |
| `war_archives_20201029_cn/sp3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20220428_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=6|battles=6` |
| `campaign_hard/campaign_14_4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4600|camera=D2,D6,D7,H2,H6,H7|spawnpts=H2|spawn=8|battles=8` |
| `war_archives_20210422_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,D6|spawn=5|battles=5` |
| `event_20241024_cn/t4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D4,E6,G4|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20220414_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=6|battles=6` |
| `event_20260625_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F5|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20220428_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `event_20220526_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E5,G2,G5|spawnpts=E6|spawn=5|battles=5` |
| `campaign_main/campaign_13_3.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=D5,G5|spawn=7|battles=7` |
| `event_20240521_cn/a3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=F2,F5,H2,H5|spawnpts=H2|spawn=5|battles=5` |
| `event_20250912_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D4|spawn=6|battles=6` |
| `event_20230525_cn/ts2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `event_20231026_cn/t4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,G3,G6|spawnpts=G3|spawn=6|battles=6` |
| `war_archives_20190221_en/c1.json` | ✅ 匹配 | ✅ | `6,4|7x5|rows=5|tokens=35|weight=1750|camera=D2,D3|spawnpts=D3|spawn=5|battles=5` |
| `event_20221222_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20180726_cn/d1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20220224_cn/a3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5,E7|spawnpts=E7|spawn=5|battles=5` |
| `event_20251218_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G2|spawn=5|battles=5` |
| `event_20230223_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20221222_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20251218_cn/c2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,F3,F6|spawnpts=E6|spawn=5|battles=5` |
| `event_20220526_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `war_archives_20200917_cn/hts2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=1|battles=1` |
| `event_20210819_cn/c2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `event_20240815_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,D6|spawn=5|battles=5` |
| `event_20200423_cn/d3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=670|camera=D3,D5,F3,F5|spawnpts=|spawn=7|battles=7` |
| `war_archives_20201029_cn/sp1.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=C2,C5|spawnpts=C5|spawn=5|battles=5` |
| `event_20221124_cn/th2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `event_20240725_cn/ht2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=7|battles=7` |
| `event_20210624_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `event_20220428_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D7,F4,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `event_20220210_cn/a1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20230817_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D4,F2,H2|spawnpts=H4|spawn=6|battles=6` |
| `event_20241219_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4110|camera=D2,D5,D7,H2,H5,H7|spawnpts=D6|spawn=7|battles=7` |
| `event_20251218_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,F3,F6|spawnpts=E6|spawn=6|battles=6` |
| `event_20210527_tw/a1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=2100|camera=C2,C5|spawnpts=C2,C5|spawn=5|battles=5` |
| `event_20200917_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20201229_cn/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `event_20260226_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G2|spawn=7|battles=7` |
| `event_20230817_cn/b3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F3,G6,H4|spawnpts=G8|spawn=6|battles=6` |
| `event_20210527_tw/b2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C2|spawn=6|battles=6` |
| `event_20221124_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20220414_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H2,H5|spawn=6|battles=6` |
| `event_20210429_tw/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20180607_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,F3,F6|spawnpts=|spawn=7|battles=7` |
| `event_20250814_cn/ht6.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D6,E5,F6|spawnpts=E5|spawn=7|battles=7` |
| `war_archives_20210225_cn/a3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E3,E6|spawnpts=E6|spawn=5|battles=5` |
| `event_20200611_en/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=0|camera=D2,D4,E2,E4|spawnpts=|spawn=0|battles=0` |
| `war_archives_20211028_cn/a2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20190221_en/d3.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D6|spawn=7|battles=7` |
| `event_20240829_cn/t3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D5,D8,F5|spawnpts=D8|spawn=6|battles=6` |
| `war_archives_20191010_en/sp3.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=D5,G5|spawn=6|battles=6` |
| `event_20200723_cn/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=600|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20210819_cn/a1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=2100|camera=C2,C5|spawnpts=C2,C5|spawn=5|battles=5` |
| `event_20260813_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=5|battles=5` |
| `event_20211028_cn/d1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C5,C7|spawnpts=C7|spawn=6|battles=6` |
| `campaign_main/campaign_4_2.json` | ✅ 匹配 | ✅ | `5,5|6x6|rows=6|tokens=36|weight=1400|camera=D2,D4|spawnpts=D2|spawn=4|battles=4` |
| `war_archives_20181026_en/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20230223_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6|spawnpts=F3|spawn=6|battles=6` |
| `event_20210121_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20200917_cn/t3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D5|spawn=5|battles=5` |
| `event_20220414_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=5|battles=5` |
| `event_20241024_cn/t2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=F2|spawn=5|battles=5` |
| `event_20210325_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `war_archives_20190911_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20210624_tw/d1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C7|spawn=6|battles=6` |
| `event_20231123_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E3,E6|spawnpts=E3|spawn=5|battles=5` |
| `campaign_main/campaign_1_4.json` | ✅ 匹配 | ✅ | `6,2|7x3|rows=3|tokens=21|weight=0|camera=D1|spawnpts=D1|spawn=4|battles=4` |
| `event_20210121_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `event_20240521_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E6,F2,F6|spawnpts=E2|spawn=5|battles=5` |
| `war_archives_20220224_cn/c2.json` | ✅ 匹配 | ✅ | `7,9|8x10|rows=10|tokens=80|weight=4000|camera=D2,E5,E7|spawnpts=D2|spawn=5|battles=5` |
| `event_20201002_en/sp1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H5|spawn=5|battles=5` |
| `event_20200806_cn/sp3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=560|camera=D2,D5,E2,E5|spawnpts=E5|spawn=6|battles=6` |
| `event_20220915_cn/d2.json` | ✅ 匹配 | ✅ | `12,5|13x6|rows=6|tokens=78|weight=3900|camera=D4,E3,G3,G4|spawnpts=H3|spawn=7|battles=7` |
| `event_20231123_cn/t5.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `event_20210225_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D8,F4,F8|spawnpts=D4,F4|spawn=6|battles=6` |
| `event_20211028_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20201229_cn/c3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D4|spawn=6|battles=6` |
| `event_20220728_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,E6|spawnpts=E2|spawn=5|battles=5` |
| `war_archives_20211028_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20210325_cn/c2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20200917_cn/ht6.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=7|battles=7` |
| `war_archives_20210325_cn/as2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `event_20201002_en/sp2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,H2|spawn=5|battles=5` |
| `event_20230223_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6|spawnpts=F3|spawn=7|battles=7` |
| `event_20201229_cn/d3.json` | ✅ 匹配 | ✅ | `9,9|10x10|rows=10|tokens=100|weight=5000|camera=D2,D6,D8,G2,G6,G8|spawnpts=D8,G8|spawn=7|battles=7` |
| `war_archives_20200507_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `event_20211028_cn/a2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `event_20200611_en/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=0|camera=D2,D5,E2,E5|spawnpts=|spawn=6|battles=6` |
| `event_20210624_tw/d3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F2,F6,F8,I2,I6,I8|spawnpts=F8,I8|spawn=7|battles=7` |
| `event_20230817_cn/c3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D6,F4,F6|spawnpts=E8|spawn=6|battles=6` |
| `event_20250814_cn/ht3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20180607_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=|spawn=5|battles=5` |
| `event_20241121_cn/ttl4.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `war_archives_20220818_cn/sp4.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D6,E3,F6|spawnpts=E7|spawn=6|battles=6` |
| `event_20220210_cn/d2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=7|battles=7` |
| `campaign_main/campaign_16_2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=C2,C6,G2,G6|spawnpts=C6|spawn=6|battles=6` |
| `event_20200423_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=0|camera=D2,D5,F2,F5|spawnpts=|spawn=0|battles=0` |
| `war_archives_20211229_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `campaign_main/campaign_16_base_submarine.json` | ⏭️ AttributeError: module 'campaign.campaign_main.campaign_16_base_submarine' has no attribute 'MAP' | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20230223_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20220224_cn/c3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5,E7|spawnpts=E7|spawn=6|battles=6` |
| `war_archives_20210916_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20220818_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D5,E3,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20241121_cn/ttl2.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `event_20240829_cn/t2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D3,D5,G5|spawnpts=G3|spawn=5|battles=5` |
| `war_archives_20201229_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D2|spawn=7|battles=7` |
| `war_archives_20220224_cn/c1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20231221_cn/b2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E6,F2,F6|spawnpts=E2|spawn=6|battles=6` |
| `event_20210225_tw/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20200917_cn/ht1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D5,H5|spawn=5|battles=5` |
| `war_archives_20210325_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `war_archives_20190911_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20210121_cn/cs1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2440|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20260226_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D4,D6,E3|spawnpts=D4|spawn=6|battles=6` |
| `campaign_sos/campaign_5_5.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1850|camera=D2,D4|spawnpts=D4|spawn=5|battles=5` |
| `event_20210325_cn/as1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20230525_cn/ht1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,F2,F6|spawnpts=E6,F6|spawn=5|battles=5` |
| `war_archives_20211014_cn/sp5.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=C4|spawn=7|battles=7` |
| `war_archives_20211014_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=A5|spawn=5|battles=5` |
| `event_20200716_en/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=490|camera=D2,D5|spawnpts=|spawn=5|battles=5` |
| `event_20210624_cn/c3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4,F4|spawn=6|battles=6` |
| `war_archives_20230525_cn/config_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20201229_cn/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20221222_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=6|battles=6` |
| `war_archives_20181227_cn/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20220414_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `war_archives_20220414_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=6|battles=6` |
| `event_20220210_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20210527_tw/d2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C2|spawn=7|battles=7` |
| `war_archives_20180726_cn/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20220428_cn/a1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `event_20231221_cn/d2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E6,F2,F6|spawnpts=E2|spawn=7|battles=7` |
| `war_archives_20200917_cn/hts1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=E4|spawn=4|battles=4` |
| `event_20210225_tw/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20211229_cn/d3.json` | ✅ 匹配 | ✅ | `11,5|12x6|rows=6|tokens=72|weight=3600|camera=E3,G4,I3|spawnpts=D4|spawn=7|battles=7` |
| `war_archives_20180607_cn/b1.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=|spawn=6|battles=6` |
| `event_20210722_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E5|spawn=5|battles=5` |
| `war_archives_20220526_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E5,G2,G5|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20201229_cn/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `event_20211125_cn/t2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=5|battles=5` |
| `event_20240815_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E5|spawnpts=E2|spawn=5|battles=5` |
| `event_20210527_tw/sp.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=8|battles=8` |
| `event_20210624_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `war_archives_20190911_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `war_archives_20200820_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D2,D5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `event_20210527_tw/a3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20181026_en/b3.json` | ✅ 匹配 | ✅ | `11,8|12x9|rows=9|tokens=108|weight=5400|camera=D2,D6,D7,I2,I6,I7|spawnpts=D7|spawn=6|battles=6` |
| `event_20220428_cn/sp.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D5,F3,F5|spawnpts=F3,F5|spawn=8|battles=8` |
| `event_20210722_cn/sp2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D2|spawn=5|battles=5` |
| `event_20220428_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=C2,C6,E2,E6|spawnpts=C2|spawn=5|battles=5` |
| `war_archives_20211028_cn/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20250424_cn/ht1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20210325_cn/as2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20220210_cn/a1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20210527_tw/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `event_20220414_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `war_archives_20210325_cn/as1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20210422_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,D6|spawn=6|battles=6` |
| `event_20211028_tw/c1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20241121_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20210624_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=6|battles=6` |
| `campaign_main/campaign_12_4.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D6|spawn=7|battles=7` |
| `event_20210624_tw/c2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20230803_cn/sp3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D4,D6,F2,F4,F6|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20201029_cn/sp5.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D3,D6,E6|spawnpts=D7|spawn=7|battles=7` |
| `event_20220414_cn/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20200227_cn/d3.json` | ⏭️ ImportError: cannot import name 'D3' from 'module.campaign.assets' (<developer-home>\source\ALAS fork project\source project\AzurLaneAutoScript\module\campaign\assets.py) | — | `7,6|8x7|rows=7|tokens=56|weight=560|camera=D3,D5,E5|spawnpts=|spawn=7|battles=7` |
| `war_archives_20210422_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20230525_cn/ht2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20230525_cn/ht3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `war_archives_20200820_cn/b2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=700|camera=D2,D5,G2,G5|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20210916_cn/c3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4070|camera=D5,E2,E5,E7|spawnpts=F5|spawn=6|battles=6` |
| `war_archives_20210422_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D3|spawn=5|battles=5` |
| `event_20241219_cn/d2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D5,H2,H5|spawnpts=D5|spawn=7|battles=7` |
| `event_20251023_cn/t2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `event_20250912_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=3750|camera=D5,F2,F5,F7|spawnpts=I5|spawn=7|battles=7` |
| `war_archives_20210916_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,F3,F5,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20200611_en/d3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=1560|camera=F3,G6,G8,H4|spawnpts=|spawn=7|battles=7` |
| `event_20231221_cn/c2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20230223_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D5,E2,E5|spawnpts=D6|spawn=5|battles=5` |
| `event_20200723_cn/sp.json` | ✅ 匹配 | ✅ | `12,9|13x10|rows=10|tokens=130|weight=1300|camera=D5,E8,G8,I8,J6|spawnpts=G8|spawn=8|battles=8` |
| `event_20220407_tw/d2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `event_20220526_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E5,G2,G5|spawnpts=E6|spawn=6|battles=6` |
| `event_20200917_cn/ht5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D3,F7|spawn=7|battles=7` |
| `war_archives_20220224_cn/a2.json` | ✅ 匹配 | ✅ | `7,9|8x10|rows=10|tokens=80|weight=4000|camera=D2,E5,E7|spawnpts=D2|spawn=5|battles=5` |
| `event_20240521_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=5430|camera=D3,D6,H3,H6|spawnpts=D7|spawn=7|battles=7` |
| `event_20260226_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D6,D8,F4,F6,F8|spawnpts=D2|spawn=7|battles=7` |
| `war_archives_20191031_en/c2.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=D2,D4,H2,H4|spawnpts=D2,D4|spawn=5|battles=5` |
| `campaign_main/campaign_6_1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1170|camera=D3,E2|spawnpts=D2|spawn=5|battles=5` |
| `event_20220407_tw/b3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=6|battles=6` |
| `event_20200723_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=900|camera=C6,E3,E5,F6|spawnpts=E7|spawn=7|battles=7` |
| `event_20250814_cn/ht4.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E3,E6,H3,H6|spawnpts=H6|spawn=6|battles=6` |
| `event_20240425_cn/isp4.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `war_archives_20220526_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=7|battles=7` |
| `event_20220210_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=7|battles=7` |
| `event_20200603_cn/sp3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2|spawn=6|battles=6` |
| `event_20211028_tw/a3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20240912_cn/sp.json` | ✅ 匹配 | ✅ | `10,4|11x5|rows=5|tokens=55|weight=2750|camera=D3,F3|spawnpts=I2|spawn=8|battles=8` |
| `event_20221222_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20240521_cn/c3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=F2,F5,H2,H5|spawnpts=H2|spawn=6|battles=6` |
| `event_20250912_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20211229_cn/ds1.json` | ✅ 匹配 | ✅ | `11,5|12x6|rows=6|tokens=72|weight=3600|camera=D2,D4,I2,I4|spawnpts=I2,I4|spawn=7|battles=7` |
| `event_20251218_cn/d3.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=F3,F6|spawnpts=F6|spawn=7|battles=7` |
| `war_archives_20180726_cn/a3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20210527_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `war_archives_20190321_en/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=2530|camera=D2,D5,F2,F5|spawnpts=D5|spawn=8|battles=8` |
| `event_20200326_cn/b3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=0|camera=D2,D6,D7|spawnpts=|spawn=6|battles=6` |
| `event_20240815_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D7,H3,H7|spawnpts=D3|spawn=6|battles=6` |
| `campaign_main/campaign_3_4.json` | ✅ 匹配 | ✅ | `7,3|8x4|rows=4|tokens=32|weight=958|camera=E2|spawnpts=D1,D2|spawn=4|battles=4` |
| `event_20230914_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=5|battles=5` |
| `event_20201229_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D2|spawn=7|battles=7` |
| `event_20250724_cn/ts3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,E6|spawnpts=D6|spawn=7|battles=7` |
| `event_20210121_cn/as2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20240815_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E5|spawnpts=E2|spawn=5|battles=5` |
| `event_20220526_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=6|battles=6` |
| `event_20220210_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20251218_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,F3,F6|spawnpts=E6|spawn=7|battles=7` |
| `war_archives_20220915_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,E5|spawnpts=D7|spawn=6|battles=6` |
| `war_archives_20190221_en/d1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2|spawn=6|battles=6` |
| `event_20220414_cn/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20231026_cn/t2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `campaign_main/campaign_15_4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=C2,C5,C7,F2,F5,F7,H2,H5,H7|spawnpts=H2|spawn=9|battles=9` |
| `event_20211125_cn/tss5.json` | ✅ 匹配 | ✅ | `4,6|5x7|rows=7|tokens=35|weight=1750|camera=C3|spawnpts=C3|spawn=1|battles=1` |
| `event_20241219_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20190911_cn/cs1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2440|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200521_cn/d3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=1560|camera=F3,G6,G8,H4|spawnpts=|spawn=7|battles=7` |
| `war_archives_20181026_en/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=7|battles=7` |
| `war_archives_20220210_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=7|battles=7` |
| `event_20220428_cn/d1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20210624_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `campaign_main/campaign_6_2.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1350|camera=D3,D4,E4|spawnpts=D2|spawn=5|battles=5` |
| `event_20210121_cn/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3020|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=8|battles=8` |
| `campaign_hard/campaign_hard.json` | ⏭️ AttributeError: module 'campaign.campaign_hard.campaign_hard' has no attribute 'MAP' | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20250912_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D5,F2,F5,F7|spawnpts=I5|spawn=6|battles=6` |
| `event_20260417_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D6,E3,E6|spawnpts=D3,D6|spawn=8|battles=8` |
| `event_20210225_cn/sp.json` | ✅ 匹配 | ✅ | `6,9|7x10|rows=10|tokens=70|weight=3500|camera=D2,D6,D7|spawnpts=D7|spawn=8|battles=8` |
| `event_20221124_cn/sp.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D3,D6|spawnpts=D6|spawn=8|battles=8` |
| `war_archives_20190911_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20240425_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=C5,F5|spawnpts=E7|spawn=8|battles=8` |
| `event_20220224_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=E2|spawn=7|battles=7` |
| `event_20230525_cn/sp.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=E6|spawn=8|battles=8` |
| `war_archives_20191031_en/b1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20200917_cn/t2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D3,D5,E3,E5|spawnpts=E5|spawn=5|battles=5` |
| `event_20241024_cn/t5.json` | ✅ 匹配 | ✅ | `11,7|12x8|rows=8|tokens=96|weight=4800|camera=E3,E6,H6|spawnpts=D2|spawn=7|battles=7` |
| `event_20250520_cn/c3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=3355|camera=D5,D7,F5,F7|spawnpts=D5,F5|spawn=6|battles=6` |
| `event_20210429_tw/c4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20240229_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=E3,E6,G3,G6|spawnpts=E3,G3|spawn=6|battles=6` |
| `event_20220428_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D7,F4,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `war_archives_20200917_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `campaign_main/campaign_8_4.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=3121|camera=D3,E3,E5|spawnpts=C1|spawn=5|battles=5` |
| `war_archives_20200917_cn/t1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D5,H5|spawn=5|battles=5` |
| `event_20210722_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E2|spawn=6|battles=6` |
| `event_20250814_cn/t4.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E3,E6,H3,H6|spawnpts=H6|spawn=6|battles=6` |
| `event_20210624_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `war_archives_20220818_cn/sp2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6|spawnpts=D3|spawn=5|battles=5` |
| `event_20210819_cn/d2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C2|spawn=7|battles=7` |
| `event_20250912_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,G3,G6|spawnpts=E6|spawn=6|battles=6` |
| `event_20260520_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20220728_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G4|spawnpts=D2|spawn=6|battles=6` |
| `event_20211028_tw/b1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20211125_cn/sp.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=8|battles=8` |
| `war_archives_20181227_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20260520_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20201229_cn/b3.json` | ✅ 匹配 | ✅ | `9,9|10x10|rows=10|tokens=100|weight=4680|camera=D2,D6,D8,G2,G6,G8|spawnpts=D8,G8|spawn=6|battles=6` |
| `war_archives_20190911_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20211125_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20210325_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F6|spawn=6|battles=6` |
| `war_archives_20210422_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=3250|camera=D2,D5,D7,F2,F5,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `campaign_main/campaign_3_2.json` | ✅ 匹配 | ✅ | `7,3|8x4|rows=4|tokens=32|weight=1204|camera=E2|spawnpts=D1,D2|spawn=4|battles=4` |
| `event_20200326_cn/a2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=0|camera=C2,C6|spawnpts=|spawn=5|battles=5` |
| `war_archives_20211028_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20200723_cn/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=600|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `event_20250520_cn/b2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=3660|camera=D4,D6,G4,G6|spawnpts=D4|spawn=6|battles=6` |
| `war_archives_20181026_en/c3.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=D5|spawn=6|battles=6` |
| `campaign_main/campaign_14_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `campaign_main/campaign_6_3.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1030|camera=D3,E2|spawnpts=D3,E2|spawn=5|battles=5` |
| `war_archives_20210819_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20230223_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6|spawnpts=F3|spawn=7|battles=7` |
| `event_20230817_cn/a2.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D6,E8,G5|spawnpts=E8|spawn=5|battles=5` |
| `event_20210225_tw/a1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20230803_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20251023_cn/t5.json` | ✅ 匹配 | ✅ | `7,9|8x10|rows=10|tokens=80|weight=4000|camera=D4,D7,E4,E7|spawnpts=D7|spawn=7|battles=7` |
| `war_archives_20220526_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E5,G2,G5|spawnpts=E6|spawn=5|battles=5` |
| `war_archives_20210527_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `campaign_main/campaign_8_2.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2270|camera=D2,D3,E3|spawnpts=E3|spawn=5|battles=5` |
| `war_archives_20201229_cn/a3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D4|spawn=5|battles=5` |
| `event_20260520_cn/a3.json` | ✅ 匹配 | ✅ | `7,8|8x9|rows=9|tokens=72|weight=3600|camera=E3,E5,E7|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20231026_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E5|spawnpts=E5|spawn=5|battles=5` |
| `war_archives_20210527_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20230525_cn/t6.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `event_20230525_cn/ht5.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=7|battles=7` |
| `event_20230525_cn/ht3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `event_20201229_cn/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `event_20260908_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G2,G6|spawnpts=G6|spawn=7|battles=7` |
| `event_20200326_cn/a3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=530|camera=D2,D5|spawnpts=|spawn=5|battles=5` |
| `event_20210527_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `event_20200521_en/c1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=0|camera=D2,D3,F2,F3|spawnpts=|spawn=5|battles=5` |
| `event_20200820_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `event_20231221_cn/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=8|battles=8` |
| `event_20260813_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=5|battles=5` |
| `campaign_main/campaign_5_1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1100|camera=D2,D4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20181026_en/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20230525_cn/ts1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `event_20210527_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `campaign_main/campaign_7_3.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=940|camera=D2,D4,E2,E4|spawnpts=E4|spawn=6|battles=6` |
| `event_20240425_cn/sp5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=C4,C7,F4,F7|spawnpts=C7|spawn=7|battles=7` |
| `event_20211028_cn/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220224_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `war_archives_20190911_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20210527_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=5|battles=5` |
| `war_archives_20221222_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220414_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H2,H5|spawn=6|battles=6` |
| `event_20200521_cn/b1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=0|camera=C3,C5,C7|spawnpts=|spawn=0|battles=0` |
| `event_20231123_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D6,F4,F6|spawnpts=D6,F6|spawn=8|battles=8` |
| `event_20210722_cn/sp4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3410|camera=D2,D4,D6,F2,F4,F6|spawnpts=D6,F6|spawn=6|battles=6` |
| `war_archives_20211229_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20180726_cn/c1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20200820_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `campaign_main/campaign_support_fleet.json` | ⏭️ AttributeError: module 'campaign.campaign_main.campaign_support_fleet' has no attribute 'MAP' | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20220526_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=7|battles=7` |
| `event_20210325_cn/ds1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `event_20211028_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D5,H5|spawn=6|battles=6` |
| `event_20230525_cn/t5.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20211028_cn/d1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C5,C7|spawnpts=C7|spawn=6|battles=6` |
| `event_20220526_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D3,D6|spawn=5|battles=5` |
| `event_20210819_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `event_20251218_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G2|spawn=6|battles=6` |
| `war_archives_20220915_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=E2|spawn=5|battles=5` |
| `event_20210225_tw/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=8|battles=8` |
| `war_archives_20230525_cn/t4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G2,G6|spawnpts=E6|spawn=6|battles=6` |
| `event_20231123_cn/t4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6,F6|spawn=6|battles=6` |
| `event_20240425_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F5|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20190620_en/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=5|battles=5` |
| `event_20250724_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,E6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20201012_cn/sp3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2,F2|spawn=6|battles=6` |
| `war_archives_20220414_cn/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20220818_cn/sp1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D4,E5,F4|spawnpts=D3|spawn=5|battles=5` |
| `event_20210819_cn/a1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=2100|camera=C2,C5|spawnpts=C2,C5|spawn=5|battles=5` |
| `event_20210121_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `campaign_main/campaign_2_1.json` | ✅ 匹配 | ✅ | `5,3|6x4|rows=4|tokens=24|weight=610|camera=C2|spawnpts=C1|spawn=3|battles=3` |
| `event_20230803_cn/sp2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `event_20250912_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,G3,G6|spawnpts=E6|spawn=6|battles=6` |
| `event_20200716_en/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=490|camera=C3,C5|spawnpts=|spawn=5|battles=5` |
| `war_archives_20231026_cn/t5.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D3,D5,D7,F3,F5,F7|spawnpts=E8|spawn=7|battles=7` |
| `event_20260520_cn/c3.json` | ✅ 匹配 | ✅ | `7,8|8x9|rows=9|tokens=72|weight=3600|camera=E3,E5,E7|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20200917_cn/t6.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `event_20251023_cn/t3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D5,G3,G5|spawnpts=D5,G5|spawn=6|battles=6` |
| `war_archives_20210527_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=5|battles=5` |
| `war_archives_20230525_cn/ht4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G2,G6|spawnpts=E6|spawn=6|battles=6` |
| `event_20231026_cn/t5.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D3,D5,D7,F3,F5,F7|spawnpts=E8|spawn=7|battles=7` |
| `event_20240815_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D7,H3,H7|spawnpts=D3|spawn=7|battles=7` |
| `event_20210722_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20211028_cn/b1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C5,C7|spawnpts=C7|spawn=6|battles=6` |
| `event_20250520_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=2900|camera=D3,D6,E3,E6|spawnpts=D3,E3|spawn=5|battles=5` |
| `event_20210624_tw/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D5,H5|spawn=6|battles=6` |
| `event_20221124_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=5|battles=5` |
| `event_20250912_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20190321_en/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2330|camera=D2,D5,E2,E5|spawnpts=C1|spawn=7|battles=7` |
| `war_archives_20220526_cn/c1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `war_archives_20231026_cn/t4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,G3,G6|spawnpts=G3|spawn=6|battles=6` |
| `event_20200716_en/d1.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=700|camera=E3,E5,F3,F5|spawnpts=|spawn=7|battles=7` |
| `campaign_main/campaign_16_4.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=C2,F2,F5,H2,H5|spawnpts=C6|spawn=9|battles=9` |
| `war_archives_20210325_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=E2,E5|spawnpts=E2,E5|spawn=7|battles=7` |
| `event_20260326_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E3,E6|spawnpts=E6|spawn=6|battles=6` |
| `event_20250424_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20230223_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E4|spawnpts=E6|spawn=8|battles=8` |
| `event_20201002_en/sp3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2|spawn=6|battles=6` |
| `event_20250814_cn/t2.json` | ✅ 匹配 | ✅ | `6,9|7x10|rows=10|tokens=70|weight=3500|camera=C5,C8|spawnpts=C2|spawn=5|battles=5` |
| `event_20231123_cn/tsk5.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=D3|spawnpts=D3|spawn=1|battles=1` |
| `war_archives_20190314_en/sp2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20220915_cn/d1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D5|spawnpts=E6|spawn=6|battles=6` |
| `event_20230817_cn/a3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D6,F4,F6|spawnpts=E8|spawn=5|battles=5` |
| `event_20220728_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20190911_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20250424_cn/ht2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `event_20260908_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20240815_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D3|spawn=6|battles=6` |
| `event_20220224_cn/c2.json` | ✅ 匹配 | ✅ | `7,9|8x10|rows=10|tokens=80|weight=4000|camera=D2,E5,E7|spawnpts=D2|spawn=5|battles=5` |
| `event_20200521_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=0|camera=D2,D4,E2,E4|spawnpts=|spawn=0|battles=0` |
| `event_20230914_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=6|battles=6` |
| `event_20231026_cn/t2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20181026_en/a3.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=D5|spawn=5|battles=5` |
| `event_20210325_cn/a2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20210819_cn/a3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20190321_en/d3.json` | ✅ 匹配 | ✅ | `5,9|6x10|rows=10|tokens=60|weight=2380|camera=C2,C6,C8|spawnpts=C8|spawn=8|battles=8` |
| `war_archives_20211014_cn/sp3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=I4|spawn=6|battles=6` |
| `war_archives_20190620_en/sp1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220407_tw/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `event_20210527_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `campaign_main/campaign_13_4.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4720|camera=D2,D4,D6,H2,H4,H6|spawnpts=D2,D6|spawn=8|battles=8` |
| `war_archives_20181227_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20230525_cn/hts2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=7|battles=7` |
| `event_20241121_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E3,E6,F3,F6|spawnpts=E6|spawn=5|battles=5` |
| `event_20220407_tw/d3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=7|battles=7` |
| `campaign_main/campaign_13_2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `event_20240912_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220728_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G4|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20220915_cn/d2.json` | ✅ 匹配 | ✅ | `12,5|13x6|rows=6|tokens=78|weight=3900|camera=D4,E3,G3,G4|spawnpts=H3|spawn=7|battles=7` |
| `event_20200423_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=0|camera=D2,D5,F2,F5|spawnpts=|spawn=0|battles=0` |
| `event_20210325_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200716_en/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=640|camera=D3,D6|spawnpts=|spawn=6|battles=6` |
| `event_20220915_cn/d1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D5|spawnpts=E6|spawn=6|battles=6` |
| `campaign_sos/campaign_4_5.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2260|camera=D2,D3,E5|spawnpts=E5|spawn=5|battles=5` |
| `event_20241219_cn/a3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D2,D5,D7,H2,H5,H7|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20191031_en/c1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D2,D3|spawn=4|battles=4` |
| `event_20201126_cn/sp4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6,F6|spawn=6|battles=6` |
| `war_archives_20210325_cn/bs1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `campaign_main/campaign_11_3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=5670|camera=D3,F5|spawnpts=D5|spawn=7|battles=7` |
| `event_20250724_cn/ts5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=D5|spawn=7|battles=7` |
| `event_20200716_en/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=800|camera=D2,D6,G2,G6|spawnpts=|spawn=6|battles=6` |
| `event_20260908_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20241219_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20240725_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=5|battles=5` |
| `event_20241219_cn/b1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20210527_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G6|spawn=7|battles=7` |
| `event_20240229_cn/c3.json` | ✅ 匹配 | ✅ | `6,9|7x10|rows=10|tokens=70|weight=3500|camera=D5,D8|spawnpts=D2|spawn=6|battles=6` |
| `event_20250814_cn/t5.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D4,E2,E6,F4|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20220324_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `event_20250227_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D2|spawn=5|battles=5` |
| `event_20260417_cn/sp1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=5|battles=5` |
| `event_20220428_cn/a1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `event_20200521_cn/b3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=0|camera=F3,G6,G8,H4|spawnpts=|spawn=0|battles=0` |
| `event_20200716_en/c4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=E3,E5|spawnpts=|spawn=6|battles=6` |
| `event_20210429_tw/d1.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=E2,E5,G2,G5|spawnpts=G5|spawn=7|battles=7` |
| `event_20200521_cn/c2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D1,D4,F2,F4|spawnpts=|spawn=0|battles=0` |
| `event_20250520_cn/d1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=3730|camera=E3,E6,H3,H6|spawnpts=H6|spawn=6|battles=6` |
| `event_20240425_cn/isp3.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `campaign_main/campaign_5_4.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1085|camera=D3,E3|spawnpts=D2,E3|spawn=5|battles=5` |
| `event_20200820_cn/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=8|battles=8` |
| `event_20260813_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G2,G5|spawnpts=D3|spawn=7|battles=7` |
| `war_archives_20210916_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G2|spawn=7|battles=7` |
| `event_20220224_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=7|battles=7` |
| `event_20230223_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20211125_cn/tss2.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `war_archives_20231026_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20250912_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,E6,F2|spawnpts=D2|spawn=5|battles=5` |
| `event_20211028_cn/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20200521_en/d1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=0|camera=D2,D4,E2,E4|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220915_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,E5|spawnpts=D7|spawn=7|battles=7` |
| `war_archives_20210819_cn/a2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `campaign_main/campaign_11_4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=800|camera=D2,D6,G2,G6|spawnpts=D2,G2|spawn=7|battles=7` |
| `war_archives_20221222_cn/d2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=F2|spawn=7|battles=7` |
| `event_20200423_cn/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D2,D4,F2,F4|spawnpts=|spawn=0|battles=0` |
| `event_20240229_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D5,F3,F5|spawnpts=D2|spawn=6|battles=6` |
| `event_20210624_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20250724_cn/t5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=D5|spawn=7|battles=7` |
| `war_archives_20211028_cn/sp.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,E6|spawnpts=D3|spawn=8|battles=8` |
| `event_20241219_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D2,D5,D7,H2,H5,H7|spawnpts=D6|spawn=6|battles=6` |
| `event_20210624_tw/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20210527_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G6|spawn=6|battles=6` |
| `war_archives_20210527_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `event_20250724_cn/t4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,F2,F5|spawnpts=F5|spawn=6|battles=6` |
| `war_archives_20200312_cn/sp2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220310_tw/sp1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2000|camera=D2,D3,E2,E3|spawnpts=D2,D3|spawn=5|battles=5` |
| `war_archives_20181020_en/sp2.json` | ✅ 匹配 | ✅ | `6,4|7x5|rows=5|tokens=35|weight=1750|camera=D2,D3|spawnpts=D3|spawn=5|battles=5` |
| `event_20240912_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D5,F2,F5|spawnpts=D1|spawn=5|battles=5` |
| `war_archives_20210225_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20191031_en/d4.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20200820_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20220407_tw/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20210819_cn/b3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D6,D7|spawnpts=D6|spawn=6|battles=6` |
| `event_20200521_cn/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=0|camera=D2,D5,E2,E5|spawnpts=|spawn=0|battles=0` |
| `event_20220407_tw/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20220428_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210819_cn/b2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C2|spawn=6|battles=6` |
| `war_archives_20190321_en/c2.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2285|camera=D2,D4,E2,E4|spawnpts=D4|spawn=7|battles=7` |
| `war_archives_20180726_cn/a2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20200820_cn/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D2,D5,F2,F5|spawnpts=D2|spawn=8|battles=8` |
| `event_20220414_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=6|battles=6` |
| `event_20250520_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3265|camera=D3,D6,E3,E6|spawnpts=D3|spawn=5|battles=5` |
| `war_archives_20220428_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=7|battles=7` |
| `event_20200521_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=0|camera=D2,D4,E2,E4|spawnpts=|spawn=0|battles=0` |
| `event_20240425_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20240229_cn/a3.json` | ✅ 匹配 | ✅ | `6,9|7x10|rows=10|tokens=70|weight=3500|camera=D5,D8|spawnpts=D2|spawn=5|battles=5` |
| `event_20241024_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220915_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D4,D6,E3|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20231026_cn/t6.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E5,E8|spawnpts=E8|spawn=7|battles=7` |
| `event_20200806_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D3,D5,F3,F5|spawnpts=D3|spawn=5|battles=5` |
| `event_20240521_cn/sp.json` | ✅ 匹配 | ✅ | `4,9|5x10|rows=10|tokens=50|weight=2620|camera=B6,B8|spawnpts=B8|spawn=8|battles=8` |
| `event_20240912_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E2,E5|spawnpts=E7|spawn=7|battles=7` |
| `event_20220324_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20200917_cn/ts2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=1|battles=1` |
| `event_20210429_tw/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20200423_cn/b2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=0|camera=D2,D5,G2,G5|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220210_cn/b2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=6|battles=6` |
| `war_archives_20220210_cn/a3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `event_20260417_cn/sp2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,E2,E6|spawnpts=D2,E2|spawn=5|battles=5` |
| `event_20200611_en/c2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D1,D4,F2,F4|spawnpts=|spawn=0|battles=0` |
| `event_20210121_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20210225_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20220324_cn/sp4.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6,E3,E6|spawnpts=D6|spawn=6|battles=6` |
| `event_20200917_cn/ht1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D5,H5|spawn=5|battles=5` |
| `event_20230525_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20210527_cn/sp.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6,F6|spawn=8|battles=8` |
| `event_20210624_tw/sp.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D6,E3,E6|spawnpts=D3,D6|spawn=8|battles=8` |
| `event_20201229_cn/c3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D4|spawn=6|battles=6` |
| `campaign_main/campaign_15_1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=C2,C5,E2,E5|spawnpts=C5|spawn=7|battles=7` |
| `event_20221222_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,E3,E6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20220728_cn/a2.json` | ✅ 匹配 | ✅ | `11,6|12x7|rows=7|tokens=84|weight=4200|camera=D3,F5,H5|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20220818_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20210325_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=E2,E5|spawnpts=E2,E5|spawn=7|battles=7` |
| `event_20220818_cn/sp1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D4,E5,F4|spawnpts=D3|spawn=5|battles=5` |
| `war_archives_20201229_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3110|camera=D2,D4,F2,F4|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20210527_tw/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20220526_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D3,D6|spawn=5|battles=5` |
| `campaign_main/campaign_15_2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=C2,C6,F2,F6|spawnpts=F2|spawn=7|battles=7` |
| `event_20221222_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,F3,F5,F7|spawnpts=D7|spawn=6|battles=6` |
| `war_archives_20181227_cn/b2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `war_archives_20211229_cn/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=F2|spawn=5|battles=5` |
| `event_20210819_cn/c1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=2100|camera=C2,C5|spawnpts=C2,C5|spawn=5|battles=5` |
| `war_archives_20191031_en/c3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20210325_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20210527_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20250520_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=2900|camera=D3,D6,E3,E6|spawnpts=D3,E3|spawn=5|battles=5` |
| `war_archives_20231026_cn/t3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D3|spawn=6|battles=6` |
| `war_archives_20210819_cn/c3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `event_20260226_cn/c2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,E4|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20210819_cn/d3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D6,D7|spawnpts=D6|spawn=7|battles=7` |
| `event_20210121_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20201126_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20260326_cn/sp.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D2,D6|spawnpts=D2|spawn=8|battles=8` |
| `war_archives_20220414_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20200820_cn/b3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=619|camera=D2,D5,F3,F5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20241121_cn/t5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5|spawnpts=E7|spawn=7|battles=7` |
| `war_archives_20201029_cn/sp5.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D3,D6,E6|spawnpts=D7|spawn=7|battles=7` |
| `event_20220728_cn/b2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D3,E3,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20191031_en/a3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20181227_cn/d3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=7|battles=7` |
| `event_20231221_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D4,D5,F4,F5|spawnpts=D3|spawn=6|battles=6` |
| `event_20250520_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3265|camera=D3,D6,E4,E6|spawnpts=D3|spawn=5|battles=5` |
| `event_20220526_cn/a1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `event_20211028_tw/d1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20220324_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260813_cn/c3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D4,D7|spawnpts=D7|spawn=6|battles=6` |
| `war_archives_20210422_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=3250|camera=D2,D5,D7,F2,F5,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `event_20260908_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20220728_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=7|battles=7` |
| `event_20200716_en/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=540|camera=E3,E4|spawnpts=|spawn=5|battles=5` |
| `event_20200716_en/b1.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=700|camera=D2,D5,G2,G5|spawnpts=|spawn=6|battles=6` |
| `war_archives_20220728_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20181227_cn/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20230525_cn/t6.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `war_archives_20210624_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `war_archives_20200806_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D3,D5,F3,F5|spawnpts=D3|spawn=5|battles=5` |
| `war_archives_20230525_cn/ts2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `war_archives_20200507_cn/sp2.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=D2,D4,H2,H4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20220428_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=C2,C6,E2,E6|spawnpts=C2|spawn=5|battles=5` |
| `event_20201029_cn/sp3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=D2,D5|spawn=6|battles=6` |
| `campaign_main/campaign_12_3.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=D5,G5|spawn=7|battles=7` |
| `event_20230914_cn/sp.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2000|camera=E3|spawnpts=C3|spawn=8|battles=8` |
| `event_20220526_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `event_20200903_en/sp1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3825|camera=D3,D5,H3,H5|spawnpts=D2|spawn=5|battles=5` |
| `event_20200326_cn/a1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=0|camera=C2,C5|spawnpts=|spawn=4|battles=4` |
| `event_20200521_en/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=0|camera=F2,F4|spawnpts=|spawn=0|battles=0` |
| `event_20200716_en/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=800|camera=E3,E6,F3,F6|spawnpts=|spawn=7|battles=7` |
| `event_20200423_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=0|camera=D2,D5|spawnpts=|spawn=0|battles=0` |
| `event_20210624_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `campaign_main/campaign_7_2.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=870|camera=D2,D3|spawnpts=D2,D3|spawn=6|battles=6` |
| `event_20200820_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `war_archives_20200820_cn/d2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=700|camera=D2,D5,G2,G5|spawnpts=E2|spawn=7|battles=7` |
| `event_20260417_cn/sp3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20211111_cn/sp3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=F6|spawn=6|battles=6` |
| `campaign_main/campaign_7_1.json` | ✅ 匹配 | ✅ | `7,2|8x3|rows=3|tokens=24|weight=779|camera=C1,E1|spawnpts=C1|spawn=6|battles=6` |
| `war_archives_20190221_en/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20220428_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=C2,C6,E2,E6|spawnpts=C2|spawn=5|battles=5` |
| `event_20200917_cn/ht2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2730|camera=D3,D5,E3,E5|spawnpts=E5|spawn=5|battles=5` |
| `event_20220210_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20210527_tw/c1.json` | ✅ 匹配 | ✅ | `5,6|6x7|rows=7|tokens=42|weight=2100|camera=C2,C5|spawnpts=C2,C5|spawn=5|battles=5` |
| `war_archives_20220414_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=C6,E3,E5,F6|spawnpts=E7|spawn=6|battles=6` |
| `war_archives_20221222_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20201229_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20200723_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=770|camera=D2,D5,H2,H5|spawnpts=H2,H5|spawn=7|battles=7` |
| `event_20210325_cn/bs2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5,F2,F5|spawnpts=E5|spawn=6|battles=6` |
| `event_20240521_cn/a1.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D7,F3,F7|spawnpts=|spawn=5|battles=5` |
| `event_20240815_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,D6|spawn=5|battles=5` |
| `event_20220210_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20240229_cn/sp.json` | ✅ 匹配 | ✅ | `19,9|20x10|rows=10|tokens=200|weight=10000|camera=B3,B6|spawnpts=B6|spawn=8|battles=8` |
| `war_archives_20201012_cn/sp2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=5|battles=5` |
| `event_20240425_cn/isp5.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `event_20200521_cn/c3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=0|camera=D2,D5,E2,E5|spawnpts=|spawn=0|battles=0` |
| `event_20250912_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G4|spawnpts=G6|spawn=6|battles=6` |
| `event_20250724_cn/ts1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `event_20200723_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=640|camera=D2,D6,E2,E6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20230525_cn/t2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20210624_cn/c3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4,F4|spawn=6|battles=6` |
| `war_archives_20210422_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,G2|spawn=6|battles=6` |
| `war_archives_20180607_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=C3,C5|spawnpts=|spawn=5|battles=5` |
| `event_20240912_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D5,F2,F5|spawnpts=D1|spawn=5|battles=5` |
| `event_20250814_cn/ht5.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D4,E2,E6,F4|spawnpts=E2|spawn=7|battles=7` |
| `event_20201229_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3110|camera=D2,D4,F2,F4|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20200603_en/sp2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=0|camera=D3,D5|spawnpts=|spawn=5|battles=5` |
| `war_archives_20190221_en/a2.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D3|spawn=5|battles=5` |
| `event_20210121_cn/as1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20220310_tw/sp3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4,F2|spawn=6|battles=6` |
| `event_20220728_cn/c2.json` | ✅ 匹配 | ✅ | `11,6|12x7|rows=7|tokens=84|weight=4200|camera=D3,F5,H5|spawnpts=D2|spawn=5|battles=5` |
| `event_20260520_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20260813_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20221222_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20181026_en/d3.json` | ✅ 匹配 | ✅ | `11,8|12x9|rows=9|tokens=108|weight=5400|camera=D2,D6,D7,I2,I6,I7|spawnpts=D7|spawn=7|battles=7` |
| `war_archives_20180607_cn/c4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E3,E5|spawnpts=|spawn=6|battles=6` |
| `event_20240815_cn/c3.json` | ✅ 匹配 | ✅ | `14,4|15x5|rows=5|tokens=75|weight=3750|camera=F2,F3,J2,J3|spawnpts=J2|spawn=6|battles=6` |
| `event_20221222_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,F3,F5,F7|spawnpts=D7|spawn=7|battles=7` |
| `war_archives_20210527_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20230223_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20200603_cn/sp2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,H2|spawn=5|battles=5` |
| `event_20200917_cn/sp.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=F2,F4,F7|spawnpts=F2|spawn=8|battles=8` |
| `event_20220210_cn/a3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20211229_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20210527_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `event_20221222_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,E3,E6|spawnpts=D6|spawn=5|battles=5` |
| `campaign_main/campaign_10_2.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=480|camera=D2,E3,E4|spawnpts=E4|spawn=7|battles=7` |
| `event_20250227_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5,E7|spawnpts=E7|spawn=8|battles=8` |
| `campaign_main/campaign_12_1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4,E4|spawn=7|battles=7` |
| `event_20200903_en/sp0.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=1|battles=1` |
| `event_20200603_cn/sp2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,H2|spawn=5|battles=5` |
| `event_20210624_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `event_20200820_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D3,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20251218_cn/a1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G2|spawn=5|battles=5` |
| `event_20210527_tw/c2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `event_20240725_cn/ht3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `war_archives_20181227_cn/b3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=6|battles=6` |
| `event_20250227_cn/b1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D6|spawn=6|battles=6` |
| `event_20201126_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20221124_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20210624_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=7|battles=7` |
| `war_archives_20200820_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=490|camera=D3,D5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20230525_cn/t1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,F2,F6|spawnpts=E6,F6|spawn=5|battles=5` |
| `war_archives_20210325_cn/a2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20250814_cn/ht1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `campaign_main/campaign_3_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220728_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G4|spawnpts=D2|spawn=6|battles=6` |
| `event_20211028_cn/d3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F3,G6,G8,H4|spawnpts=G8|spawn=7|battles=7` |
| `event_20260226_cn/a2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,E4|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20201229_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20240815_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D3|spawn=7|battles=7` |
| `event_20231221_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,E6|spawnpts=F2|spawn=6|battles=6` |
| `war_archives_20211229_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G6|spawn=5|battles=5` |
| `event_20210429_tw/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D4|spawn=5|battles=5` |
| `event_20200723_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=900|camera=C6,E3,E5,F6|spawnpts=E7|spawn=6|battles=6` |
| `event_20251023_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260813_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20220526_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20200917_cn/ts1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=E4|spawn=4|battles=4` |
| `campaign_main/campaign_15_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260226_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20200521_en/b1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=0|camera=D2,D4,E2,E4|spawnpts=|spawn=0|battles=0` |
| `event_20221222_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E5|spawnpts=E7|spawn=8|battles=8` |
| `war_archives_20221222_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,E3,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210225_tw/a3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `event_20210624_cn/a3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2780|camera=D2,D4,F2,F4|spawnpts=D4,F4|spawn=6|battles=6` |
| `campaign_main/campaign_15_4_121.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=C2,C5,C7,F2,F5,F7,H2,H5,H7|spawnpts=H2|spawn=9|battles=9` |
| `war_archives_20211028_cn/b1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C5,C7|spawnpts=C7|spawn=6|battles=6` |
| `event_20200917_cn/hts1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=E4|spawn=4|battles=4` |
| `event_20260908_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G2,G6|spawnpts=G6|spawn=6|battles=6` |
| `event_20210225_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20210819_cn/d3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D6,D7|spawnpts=D6|spawn=7|battles=7` |
| `event_20220915_cn/b1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D5|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20190321_en/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=2530|camera=D2,D5,F2,F5|spawnpts=D5|spawn=7|battles=7` |
| `war_archives_20220728_cn/c2.json` | ✅ 匹配 | ✅ | `11,6|12x7|rows=7|tokens=84|weight=4200|camera=D3,F5,H5|spawnpts=D2|spawn=5|battles=5` |
| `campaign_main/campaign_14_4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4600|camera=D2,D5,D7,H2,H5,H7|spawnpts=H2|spawn=8|battles=8` |
| `event_20250424_cn/ht3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E3,F3,F6|spawnpts=F6|spawn=7|battles=7` |
| `event_20220915_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20191031_en/a1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D2,D3|spawn=4|battles=4` |
| `war_archives_20201229_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D3,D5,H3,H5|spawnpts=D2|spawn=6|battles=6` |
| `event_20200611_en/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=0|camera=D2,D5,H2,H5|spawnpts=|spawn=6|battles=6` |
| `event_20240815_cn/b1.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D2,D6,H3,H7|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20211028_cn/b3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F3,G6,G8,H4|spawnpts=G8|spawn=6|battles=6` |
| `war_archives_20221222_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20211125_cn/t4.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=F5|spawn=6|battles=6` |
| `event_20210121_cn/cs2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20231221_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,E6|spawnpts=F2|spawn=5|battles=5` |
| `event_20210819_cn/b3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D6,D7|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20210916_cn/c1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E5|spawn=5|battles=5` |
| `war_archives_20220428_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20200917_cn/ht6.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=7|battles=7` |
| `event_20220728_cn/c1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D6|spawn=5|battles=5` |
| `campaign_main/campaign_10_4.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=684|camera=D2,F4|spawnpts=D4|spawn=7|battles=7` |
| `war_archives_20211028_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D5,H5|spawn=6|battles=6` |
| `event_20220526_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=6|battles=6` |
| `event_20240229_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260226_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G2|spawn=6|battles=6` |
| `event_20220818_cn/sp.json` | ✅ 匹配 | ✅ | `4,9|5x10|rows=10|tokens=50|weight=2500|camera=B5,B6|spawnpts=B8|spawn=8|battles=8` |
| `war_archives_20210527_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=6|battles=6` |
| `event_20220728_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=6|battles=6` |
| `war_archives_20181227_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `event_20200423_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=0|camera=D3,D5|spawnpts=|spawn=5|battles=5` |
| `event_20210121_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `war_archives_20220428_cn/c1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `event_20260908_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `event_20240725_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20210527_tw/d3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D6,D7|spawnpts=D6|spawn=7|battles=7` |
| `war_archives_20230223_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20180607_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=|spawn=5|battles=5` |
| `event_20210225_cn/c3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E3,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20211111_cn/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20220210_cn/c1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20200723_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=720|camera=D2,D6,F2,F6|spawnpts=D2|spawn=6|battles=6` |
| `event_20221124_cn/ts1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2000|camera=C3|spawnpts=C3|spawn=6|battles=6` |
| `war_archives_20210624_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20210527_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=G6|spawn=6|battles=6` |
| `war_archives_20200820_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20210624_cn/a3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2780|camera=D2,D4,F2,F4|spawnpts=D4,F4|spawn=6|battles=6` |
| `war_archives_20210225_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20200603_cn/sp3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2|spawn=6|battles=6` |
| `event_20210429_tw/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G6|spawn=7|battles=7` |
| `war_archives_20190321_en/a2.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2285|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20210325_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200423_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=0|camera=D2,D5,F2,F5|spawnpts=|spawn=0|battles=0` |
| `event_20240815_cn/a3.json` | ✅ 匹配 | ✅ | `14,4|15x5|rows=5|tokens=75|weight=3750|camera=F2,F3,J2,J3|spawnpts=J2|spawn=5|battles=5` |
| `event_20200521_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=0|camera=D3,D5,F3,F5,H3,H5|spawnpts=|spawn=0|battles=0` |
| `event_20230223_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D2|spawn=6|battles=6` |
| `event_20200723_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=720|camera=D2,D6,F2,F6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20230223_cn/b3.json` | ✅ 匹配 | ✅ | `9,8|10x9|rows=9|tokens=90|weight=4500|camera=E3,E7,F3,F7|spawnpts=E7|spawn=6|battles=6` |
| `event_20210819_cn/c3.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20190321_en/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2330|camera=D2,D5,E2,E5|spawnpts=C1|spawn=7|battles=7` |
| `event_20220414_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D2,D6,D7,F2,F6,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `war_archives_20180726_cn/a1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20221124_cn/th3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `event_20231026_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E5|spawnpts=E5|spawn=5|battles=5` |
| `event_20200820_cn/b3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F3,F5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20220915_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210610_tw/sp1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `war_archives_20211111_cn/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20200723_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=770|camera=D2,D5,H2,H5|spawnpts=H2,H5|spawn=6|battles=6` |
| `campaign_main/campaign_11_1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=4320|camera=D3,E4|spawnpts=D4|spawn=7|battles=7` |
| `event_20200820_cn/d2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=E2|spawn=7|battles=7` |
| `war_archives_20210325_cn/c2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200820_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20210225_cn/c3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E3,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20230223_cn/c2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20240425_cn/sp1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=C3,C6,E3,E6|spawnpts=E3|spawn=5|battles=5` |
| `event_20250227_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4590|camera=E3,E6,G3,G6|spawnpts=E6,G6|spawn=6|battles=6` |
| `event_20230914_cn/b3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D6,H2,H5,H7|spawnpts=H5|spawn=6|battles=6` |
| `event_20231026_cn/sp.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D2,D6|spawnpts=D6|spawn=8|battles=8` |
| `event_20201029_cn/sp4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20210325_cn/bs2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5,F2,F5|spawnpts=E5|spawn=6|battles=6` |
| `event_20200611_en/a2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D1,D4,F2,F4|spawnpts=|spawn=0|battles=0` |
| `event_20210415_tw/sp2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20230914_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=6|battles=6` |
| `war_archives_20181020_en/sp1.json` | ✅ 匹配 | ✅ | `6,2|7x3|rows=3|tokens=21|weight=1050|camera=D1|spawnpts=D1|spawn=3|battles=3` |
| `war_archives_20190221_en/a1.json` | ✅ 匹配 | ✅ | `6,4|7x5|rows=5|tokens=35|weight=1750|camera=D2,D3|spawnpts=D3|spawn=5|battles=5` |
| `campaign_main/campaign_8_3.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2360|camera=D3,D4,E3,E4|spawnpts=|spawn=5|battles=5` |
| `event_20250724_cn/ts2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D5,F3,F5|spawnpts=F6|spawn=7|battles=7` |
| `war_archives_20180726_cn/b1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20250814_cn/t6.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D6,E5,F6|spawnpts=E5|spawn=6|battles=6` |
| `war_archives_20200917_cn/t5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D3,F7|spawn=6|battles=6` |
| `war_archives_20181020_en/sp3.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2000|camera=D2,D3,E2,E3|spawnpts=D2,D3|spawn=6|battles=6` |
| `event_20250724_cn/t2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D5,F3,F5|spawnpts=F6|spawn=5|battles=5` |
| `event_20200603_cn/sp1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H5|spawn=5|battles=5` |
| `event_20210225_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20240725_cn/ht1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `war_archives_20190911_cn/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=7|battles=7` |
| `war_archives_20211229_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=6|battles=6` |
| `event_20210415_tw/sp1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20240912_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5|spawnpts=F3|spawn=5|battles=5` |
| `event_20230223_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D2|spawn=6|battles=6` |
| `event_20231221_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20221222_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20210527_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,D6|spawn=6|battles=6` |
| `event_20220728_cn/d2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D3,E3,E6|spawnpts=E6|spawn=7|battles=7` |
| `event_20231123_cn/tsk1.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=D3|spawnpts=D3|spawn=1|battles=1` |
| `event_20210819_cn/a2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C2|spawn=5|battles=5` |
| `event_20260813_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D6,F4,F6|spawnpts=D4|spawn=6|battles=6` |
| `war_archives_20210325_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=7|battles=7` |
| `campaign_main/campaign_2_2.json` | ✅ 匹配 | ✅ | `6,4|7x5|rows=5|tokens=35|weight=910|camera=D3|spawnpts=D3|spawn=4|battles=4` |
| `war_archives_20210624_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=6|battles=6` |
| `event_20220818_cn/sp2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6|spawnpts=D3|spawn=5|battles=5` |
| `event_20200521_cn/d1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=1780|camera=C3,C5,C7|spawnpts=|spawn=6|battles=6` |
| `event_20220526_cn/sp.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D6,E3,F6|spawnpts=D6,F6|spawn=8|battles=8` |
| `war_archives_20181227_cn/d1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20220915_cn/b2.json` | ✅ 匹配 | ✅ | `12,5|13x6|rows=6|tokens=78|weight=3900|camera=D4,E3,G3,G4|spawnpts=H3|spawn=6|battles=6` |
| `event_20251218_cn/a2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E3,E6,F3,F6|spawnpts=E6|spawn=5|battles=5` |
| `war_archives_20210422_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D3|spawn=5|battles=5` |
| `war_archives_20221222_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,F3,F5,F7|spawnpts=D7|spawn=6|battles=6` |
| `event_20240912_cn/d2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E2,E6,H2,H6|spawnpts=E2|spawn=7|battles=7` |
| `event_20240521_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20240725_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20241121_cn/ttl1.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `campaign_main/campaign_4_1.json` | ✅ 匹配 | ✅ | `5,5|6x6|rows=6|tokens=36|weight=940|camera=C2,C4|spawnpts=C2|spawn=4|battles=4` |
| `event_20241121_cn/t4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D4|spawn=6|battles=6` |
| `war_archives_20220526_cn/a1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `event_20250227_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4590|camera=E3,E6,G3,G6|spawnpts=E6,G6|spawn=7|battles=7` |
| `war_archives_20201229_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3110|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20211028_cn/a1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20220407_tw/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20200820_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20250520_cn/b1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=3730|camera=E3,E6,H3,H6|spawnpts=H6|spawn=6|battles=6` |
| `campaign_main/campaign_2_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20210325_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F6|spawn=6|battles=6` |
| `event_20220728_cn/a2.json` | ✅ 匹配 | ✅ | `11,6|12x7|rows=7|tokens=84|weight=4200|camera=D3,F5,H5|spawnpts=D2|spawn=5|battles=5` |
| `event_20231123_cn/tsk3.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=D3|spawnpts=D3|spawn=1|battles=1` |
| `event_20200603_en/sp1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=0|camera=D3,D5|spawnpts=|spawn=5|battles=5` |
| `event_20200611_en/b1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=0|camera=C3,C5,C7|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220224_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20220428_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=C2,C6,E2,E6|spawnpts=C2|spawn=5|battles=5` |
| `war_archives_20201029_cn/sp4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `event_20200917_cn/t5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D3,F7|spawn=6|battles=6` |
| `event_20250814_cn/ht2.json` | ✅ 匹配 | ✅ | `6,9|7x10|rows=10|tokens=70|weight=3500|camera=C5,C8|spawnpts=C2|spawn=5|battles=5` |
| `event_20210225_tw/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20200227_cn/c2.json` | ⏭️ ImportError: cannot import name 'C2' from 'module.campaign.assets' (<developer-home>\source\ALAS fork project\source project\AzurLaneAutoScript\module\campaign\assets.py) | — | `6,6|7x7|rows=7|tokens=49|weight=490|camera=C3,D5|spawnpts=|spawn=5|battles=5` |
| `event_20200723_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=640|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `campaign_main/campaign_4_4.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1900|camera=D2,D4|spawnpts=E2,E4|spawn=5|battles=5` |
| `event_20250724_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=5|battles=5` |
| `event_20211028_cn/b3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F3,G6,G8,H4|spawnpts=G8|spawn=6|battles=6` |
| `event_20260625_cn/sp.json` | ✅ 匹配 | ✅ | `6,9|7x10|rows=10|tokens=70|weight=3500|camera=D3,D7,D8|spawnpts=D8|spawn=8|battles=8` |
| `war_archives_20220414_cn/c2.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20200917_cn/t4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4910|camera=D2,D5,D7,H2,H5,H7|spawnpts=D2,D7|spawn=6|battles=6` |
| `campaign_sos/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20210819_cn/b2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C2|spawn=6|battles=6` |
| `event_20260813_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=5|battles=5` |
| `event_20220210_cn/sp.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=8|battles=8` |
| `event_20210527_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `event_20211125_cn/tss1.json` | ✅ 匹配 | ✅ | `4,6|5x7|rows=7|tokens=35|weight=1750|camera=C3|spawnpts=C3|spawn=1|battles=1` |
| `event_20250724_cn/ts4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,F2,F5|spawnpts=F5|spawn=7|battles=7` |
| `event_20210225_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220915_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,E4,F3|spawnpts=E7|spawn=8|battles=8` |
| `campaign_sos/campaign_3_5.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1680|camera=D3,E3|spawnpts=D3|spawn=4|battles=4` |
| `event_20200521_cn/a2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D1,D4,F2,F4|spawnpts=|spawn=0|battles=0` |
| `war_archives_20221222_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,F3,F5,F7|spawnpts=D7|spawn=7|battles=7` |
| `event_20230914_cn/c2.json` | ✅ 匹配 | ✅ | `10,4|11x5|rows=5|tokens=55|weight=2750|camera=D3,F3,H3|spawnpts=D2|spawn=5|battles=5` |
| `campaign_main/campaign_14_2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3770|camera=D2,D6,F2,F6|spawnpts=F2|spawn=7|battles=7` |
| `event_20260908_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20220414_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=H2,H5|spawn=7|battles=7` |
| `war_archives_20191031_en/d2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=6|battles=6` |
| `event_20230817_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20230803_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20230525_cn/t5.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=6|battles=6` |
| `event_20230525_cn/ht4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G2,G6|spawnpts=E6|spawn=6|battles=6` |
| `event_20210429_tw/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6,E3,E6|spawnpts=D3|spawn=6|battles=6` |
| `event_20230525_cn/ht6.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=D6,G6|spawn=7|battles=7` |
| `event_20250814_cn/t3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2|spawn=5|battles=5` |
| `event_20240912_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F2,F6|spawnpts=F6|spawn=5|battles=5` |
| `event_20240229_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D6,E3,E6|spawnpts=D2|spawn=5|battles=5` |
| `event_20251023_cn/t6.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=E4,H4|spawnpts=D4|spawn=7|battles=7` |
| `war_archives_20200312_cn/sp1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=2000|camera=D2,D3,E2,E3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20200423_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=0|camera=D2,D5,F2,F5|spawnpts=|spawn=0|battles=0` |
| `event_20220224_cn/c3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5,E7|spawnpts=E7|spawn=6|battles=6` |
| `campaign_main/campaign_4_3.json` | ✅ 匹配 | ✅ | `6,3|7x4|rows=4|tokens=28|weight=980|camera=D2|spawnpts=D1|spawn=4|battles=4` |
| `event_20220210_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=5|battles=5` |
| `war_archives_20221222_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=5|battles=5` |
| `event_20221124_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20210527_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2,D6|spawn=6|battles=6` |
| `war_archives_20211028_cn/a3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20211229_cn/bs1.json` | ✅ 匹配 | ✅ | `11,5|12x6|rows=6|tokens=72|weight=3600|camera=D2,D4,I2,I4|spawnpts=I2,I4|spawn=6|battles=6` |
| `event_20250724_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220915_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20210225_tw/d2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=7|battles=7` |
| `event_20220407_tw/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20260908_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `event_20220428_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `event_20221222_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=6|battles=6` |
| `event_20210325_cn/ds2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5,F2,F5|spawnpts=E5|spawn=7|battles=7` |
| `war_archives_20190321_en/b3.json` | ✅ 匹配 | ✅ | `5,9|6x10|rows=10|tokens=60|weight=2380|camera=C2,C6,C8|spawnpts=C8|spawn=8|battles=8` |
| `event_20220224_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=E2|spawn=6|battles=6` |
| `campaign_main/campaign_1_2.json` | ✅ 匹配 | ✅ | `4,2|5x3|rows=3|tokens=15|weight=0|camera=C1|spawnpts=C1|spawn=3|battles=3` |
| `event_20201229_cn/b3.json` | ✅ 匹配 | ✅ | `9,9|10x10|rows=10|tokens=100|weight=4680|camera=D2,D6,D8,G2,G6,G8|spawnpts=D8,G8|spawn=6|battles=6` |
| `war_archives_20220526_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5,F5|spawn=6|battles=6` |
| `war_archives_20210325_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20201229_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20240229_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=E3,E6,G3,G6|spawnpts=E3,G3|spawn=7|battles=7` |
| `event_20210325_cn/cs1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20220728_cn/d2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D3,E3,E6|spawnpts=E6|spawn=7|battles=7` |
| `event_20231123_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D3|spawn=5|battles=5` |
| `war_archives_20220210_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20220210_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20260813_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D6,F4,F6|spawnpts=D4,F4|spawn=8|battles=8` |
| `war_archives_20220224_cn/a1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20231221_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,E6|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20220210_cn/d2.json` | ✅ 匹配 | ✅ | `5,7|6x8|rows=8|tokens=48|weight=2400|camera=C2,C6|spawnpts=C6|spawn=7|battles=7` |
| `event_20250724_cn/sp.json` | ✅ 匹配 | ✅ | `10,9|11x10|rows=10|tokens=110|weight=5500|camera=F4,F6|spawnpts=F4|spawn=8|battles=8` |
| `campaign_main/campaign_7_4.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1096|camera=C2,C4,E2,E4|spawnpts=D2,D4|spawn=6|battles=6` |
| `event_20240425_cn/sp3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=C4,E4,E6|spawnpts=C6|spawn=6|battles=6` |
| `event_20200521_en/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=0|camera=D2,D5,F2,F5|spawnpts=|spawn=6|battles=6` |
| `event_20260625_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20211028_tw/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,E2,E6,E7|spawnpts=D2,D7|spawn=7|battles=7` |
| `war_archives_20210325_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `event_20240229_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D6,E3,E6|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20210916_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4070|camera=D2,D5,F3,F5,F7|spawnpts=D7|spawn=7|battles=7` |
| `campaign_main/campaign_16_1.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=C2,C6,F2,F5|spawnpts=F7|spawn=6|battles=6` |
| `event_20250227_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F2|spawn=5|battles=5` |
| `event_20260326_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20210429_tw/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G6|spawn=6|battles=6` |
| `war_archives_20210916_cn/d1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20180726_cn/c3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20220428_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20240229_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20211028_cn/d3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=7000|camera=F3,G6,G8,H4|spawnpts=G8|spawn=7|battles=7` |
| `war_archives_20211229_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=6|battles=6` |
| `event_20200521_en/a1.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=0|camera=D1,D3,F1,F3|spawnpts=|spawn=0|battles=0` |
| `event_20200507_cn/sp2.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=0|camera=D2,D4,H2,H4|spawnpts=|spawn=5|battles=5` |
| `war_archives_20230803_cn/sp1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=F2,F4|spawnpts=D2,D4|spawn=5|battles=5` |
| `war_archives_20211229_cn/b3.json` | ✅ 匹配 | ✅ | `11,5|12x6|rows=6|tokens=72|weight=3600|camera=E3,G4,I3|spawnpts=D4|spawn=6|battles=6` |
| `event_20200312_cn/sp3.json` | ⏭️ ImportError: cannot import name 'EVENT_20200312CN_SP3' from 'module.campaign.assets' (<developer-home>\source\ALAS fork project\source project\AzurLaneAutoScript\module\campaign\assets.py) | — | `8,5|9x6|rows=6|tokens=54|weight=540|camera=D2,D4,F2,F4|spawnpts=|spawn=6|battles=6` |
| `event_20241219_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20230223_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6|spawnpts=F3|spawn=6|battles=6` |
| `event_20210429_tw/b1.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=E2,E5,G2,G5|spawnpts=G5|spawn=6|battles=6` |
| `event_20200723_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D2,D5,F2,F5|spawnpts=D3|spawn=5|battles=5` |
| `event_20250520_cn/sp.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E6,E8|spawnpts=E8|spawn=8|battles=8` |
| `event_20251023_cn/t1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220428_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=7|battles=7` |
| `war_archives_20191010_en/sp1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=2400|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20200820_cn/b2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,G2,G5|spawnpts=E2|spawn=6|battles=6` |
| `event_20240912_cn/b1.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D3,D6,G2,G6|spawnpts=E2|spawn=6|battles=6` |
| `event_20200611_en/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=0|camera=D2,D4,E2,E4|spawnpts=|spawn=0|battles=0` |
| `event_20231026_cn/t6.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=E5,E8|spawnpts=E8|spawn=7|battles=7` |
| `war_archives_20200806_cn/sp1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=480|camera=D3,D4|spawnpts=D3,D4|spawn=5|battles=5` |
| `war_archives_20180726_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,E2,E6,E7|spawnpts=D2,D7|spawn=6|battles=6` |
| `event_20201029_cn/sp1.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=C2,C5|spawnpts=C5|spawn=5|battles=5` |
| `event_20230525_cn/ts1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `event_20201229_cn/a1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3110|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `war_archives_20210422_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D3,D5,F3,F5|spawnpts=D5|spawn=5|battles=5` |
| `event_20200521_en/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=640|camera=D2,D5,F3,F5|spawnpts=|spawn=7|battles=7` |
| `war_archives_20220915_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D4,D6,E3|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20190911_cn/cs2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20260226_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E6|spawnpts=E2|spawn=5|battles=5` |
| `event_20230817_cn/c2.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D6,E8,G5|spawnpts=E8|spawn=5|battles=5` |
| `event_20220210_cn/c1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20220915_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=E2|spawn=5|battles=5` |
| `event_20250520_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=3830|camera=D3,D7,E3,E7|spawnpts=D3,E3|spawn=7|battles=7` |
| `war_archives_20180726_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20231026_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20220526_cn/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20181026_en/c2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=E2,E4,F2,F4|spawnpts=E4|spawn=5|battles=5` |
| `war_archives_20200903_cn/sp0.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=1|battles=1` |
| `war_archives_20190911_cn/as2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20250912_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20180607_cn/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=E3,E4|spawnpts=|spawn=5|battles=5` |
| `event_20220818_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D5,E3,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20190221_en/b3.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D6,H2,H6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20221222_cn/d1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20220210_cn/c3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20220414_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=5|battles=5` |
| `war_archives_20200820_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=D2,D5,F2,F5|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20181026_en/a2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=E2,E4,F2,F4|spawnpts=E4|spawn=5|battles=5` |
| `event_20220407_tw/b2.json` | ✅ 匹配 | ✅ | `9,6|10x7|rows=7|tokens=70|weight=3500|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=6|battles=6` |
| `event_20220414_cn/sp.json` | ✅ 匹配 | ✅ | `12,9|13x10|rows=10|tokens=130|weight=6500|camera=D5,D8,J5,J8|spawnpts=D8,J8|spawn=8|battles=8` |
| `war_archives_20220224_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=6|battles=6` |
| `event_20240725_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20210916_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=5|battles=5` |
| `war_archives_20210325_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220915_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,E4,E6|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20220428_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D7,F4,F7|spawnpts=D7,F7|spawn=7|battles=7` |
| `war_archives_20240725_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20190620_en/sp3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F5|spawn=6|battles=6` |
| `event_20201126_cn/sp3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `event_20260908_cn/sp.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D2,D6,D8,F2,F6,F8|spawnpts=D2|spawn=8|battles=8` |
| `event_20210722_cn/sp.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D2,D6|spawnpts=D6|spawn=8|battles=8` |
| `war_archives_20210916_cn/a3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4070|camera=D5,E2,E5,E7|spawnpts=F5|spawn=5|battles=5` |
| `event_20231221_cn/b1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D4,D5,F4,F5|spawnpts=D3|spawn=6|battles=6` |
| `event_20241121_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,E2,E6|spawnpts=D2|spawn=6|battles=6` |
| `event_20230914_cn/d2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F2|spawn=7|battles=7` |
| `event_20200716_en/a1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=540|camera=E3,E4|spawnpts=|spawn=5|battles=5` |
| `event_20231123_cn/tsk2.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=D3|spawnpts=D3|spawn=1|battles=1` |
| `war_archives_20220428_cn/d1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20210325_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F6|spawn=6|battles=6` |
| `event_20210325_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20230803_cn/sp2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `event_20210624_tw/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D5,H5|spawn=7|battles=7` |
| `war_archives_20210225_cn/d3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D8,F4,F8|spawnpts=D4,F4|spawn=7|battles=7` |
| `event_20260625_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,F2,F5|spawnpts=F5|spawn=6|battles=6` |
| `event_20240521_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E2,E6,F2,F6|spawnpts=E2|spawn=5|battles=5` |
| `event_20230525_cn/ht1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,F2,F6|spawnpts=E6,F6|spawn=5|battles=5` |
| `war_archives_20180726_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,D7,E2,E6,E7|spawnpts=D2,D7|spawn=7|battles=7` |
| `event_20260908_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `event_20250424_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=E3,F3,F6|spawnpts=F6|spawn=6|battles=6` |
| `campaign_main/campaign_11_2.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=5400|camera=F4,G3,H4|spawnpts=D4|spawn=7|battles=7` |
| `event_20230914_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D6|spawn=5|battles=5` |
| `event_20230817_cn/d1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `event_20231221_cn/d3.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4950|camera=D3,D6,H3,H6|spawnpts=D6,H6|spawn=7|battles=7` |
| `event_20200611_en/d1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=1780|camera=C3,C5,C7|spawnpts=|spawn=6|battles=6` |
| `war_archives_20200903_cn/sp2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3820|camera=D3,D5,H3,H5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20240815_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20210819_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=E2|spawn=6|battles=6` |
| `war_archives_20230223_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,F2,F5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20210325_cn/cs2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `war_archives_20210225_cn/c1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `campaign_sos/campaign_9_5.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2140|camera=D2,D4,F2,F4|spawnpts=D2,F4|spawn=6|battles=6` |
| `event_20221124_cn/t4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20210225_tw/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D2,D5|spawn=5|battles=5` |
| `event_20231123_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20260520_cn/a2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,E2,E5|spawnpts=D2|spawn=5|battles=5` |
| `event_20211028_cn/c2.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20211229_cn/d2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=F5|spawn=7|battles=7` |
| `event_20200820_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20200820_cn/c1.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=480|camera=D2,D4,E2,E4|spawnpts=D4|spawn=5|battles=5` |
| `event_20220224_cn/a2.json` | ✅ 匹配 | ✅ | `7,9|8x10|rows=10|tokens=80|weight=4000|camera=D2,E5,E7|spawnpts=D2|spawn=5|battles=5` |
| `event_20210325_cn/cs2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200423_cn/b3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=0|camera=D2,D5,F2,F5|spawnpts=|spawn=0|battles=0` |
| `event_20211125_cn/tss4.json` | ✅ 匹配 | ✅ | `4,6|5x7|rows=7|tokens=35|weight=1750|camera=C3|spawnpts=C3|spawn=1|battles=1` |
| `war_archives_20181227_cn/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=5|battles=5` |
| `event_20250227_cn/c1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=5|battles=5` |
| `war_archives_20200806_cn/sp3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=560|camera=D2,D5,E2,E5|spawnpts=E5|spawn=6|battles=6` |
| `event_20210225_tw/c1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=5|battles=5` |
| `event_20220310_tw/sp2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20220407_tw/a3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20191031_en/a2.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=D2,D4,H2,H4|spawnpts=D2,D4|spawn=4|battles=4` |
| `event_20211028_tw/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=6|battles=6` |
| `war_archives_20200917_cn/ht5.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D3,F7|spawn=7|battles=7` |
| `event_20240912_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E2,E5|spawnpts=E7|spawn=6|battles=6` |
| `event_20201229_cn/a3.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=2840|camera=D2,D4,G2,G4|spawnpts=D4|spawn=5|battles=5` |
| `event_20210429_tw/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6,E3,E6|spawnpts=D3|spawn=5|battles=5` |
| `event_20241219_cn/b2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D2,D5,H2,H5|spawnpts=D5|spawn=6|battles=6` |
| `event_20250424_cn/t2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,F2|spawn=5|battles=5` |
| `war_archives_20210916_cn/b1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=6|battles=6` |
| `event_20241024_cn/sp.json` | ✅ 匹配 | ✅ | `19,2|20x3|rows=3|tokens=60|weight=3000|camera=I1,P1,Q1|spawnpts=I1|spawn=8|battles=8` |
| `event_20250227_cn/d2.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=E2,E6,G2,G6|spawnpts=E2,G2|spawn=7|battles=7` |
| `event_20230817_cn/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D4,F2,H2|spawnpts=H4|spawn=7|battles=7` |
| `event_20210624_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20221124_cn/th1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `event_20231123_cn/tsk4.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=D3|spawnpts=D3|spawn=1|battles=1` |
| `event_20220224_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20220428_cn/a3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=5|battles=5` |
| `event_20230817_cn/a1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,E5,G3|spawnpts=E5|spawn=5|battles=5` |
| `war_archives_20220414_cn/a3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=5|battles=5` |
| `war_archives_20220224_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `event_20210121_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20201229_cn/d3.json` | ✅ 匹配 | ✅ | `9,9|10x10|rows=10|tokens=100|weight=5000|camera=D2,D6,D8,G2,G6,G8|spawnpts=D8,G8|spawn=7|battles=7` |
| `event_20200326_cn/d3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=0|camera=D3,D5,D7|spawnpts=|spawn=7|battles=7` |
| `event_20260417_cn/sp4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=6|battles=6` |
| `campaign_war_archives/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20230525_cn/config_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `campaign_main/campaign_10_3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=3840|camera=D2,E3,F4|spawnpts=E3|spawn=7|battles=7` |
| `event_20210527_cn/a2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=5|battles=5` |
| `war_archives_20210225_cn/b2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D4,H2,H4|spawnpts=H4|spawn=6|battles=6` |
| `event_20210225_tw/d3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=7|battles=7` |
| `war_archives_20181227_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D2,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20210624_cn/b1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20220728_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,F3,F5|spawnpts=E7|spawn=6|battles=6` |
| `event_20220915_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,E5|spawnpts=D7|spawn=7|battles=7` |
| `war_archives_20210225_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=4500|camera=D4,D8,F4,F8|spawnpts=D4,F4|spawn=6|battles=6` |
| `event_20220526_cn/c1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=5|battles=5` |
| `event_20250520_cn/b3.json` | ✅ 匹配 | ✅ | `8,9|9x10|rows=10|tokens=90|weight=3830|camera=D3,D7,E3,E7|spawnpts=D3,E3|spawn=6|battles=6` |
| `war_archives_20220915_cn/b1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D5|spawnpts=E6|spawn=6|battles=6` |
| `event_20221124_cn/t5.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=7|battles=7` |
| `event_20210325_cn/bs1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `event_20230914_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20240725_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E5,E7|spawnpts=E5|spawn=8|battles=8` |
| `war_archives_20220728_cn/c3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,E6|spawnpts=E2|spawn=6|battles=6` |
| `event_20200917_cn/ht4.json` | ✅ 匹配 | ✅ | `10,8|11x9|rows=9|tokens=99|weight=4910|camera=D2,D5,D7,H2,H5,H7|spawnpts=D2,D7|spawn=6|battles=6` |
| `event_20230817_cn/c1.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,E5,G3|spawnpts=E5|spawn=5|battles=5` |
| `event_20200611_en/d2.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=0|camera=D3,D5,F3,F5,H3,H5|spawnpts=|spawn=0|battles=0` |
| `event_20251218_cn/c1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G2|spawn=5|battles=5` |
| `war_archives_20191031_en/d3.json` | ✅ 匹配 | ✅ | `6,7|7x8|rows=8|tokens=56|weight=2800|camera=D2,D6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20190321_en/a1.json` | ✅ 匹配 | ✅ | `7,4|8x5|rows=5|tokens=40|weight=1600|camera=D2,D3,E2,E3|spawnpts=C1,D3|spawn=6|battles=6` |
| `event_20220324_cn/sp4.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6,E3,E6|spawnpts=D6|spawn=6|battles=6` |
| `campaign_main/campaign_16_3.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=D3,E4,G2,H2|spawnpts=C5|spawn=8|battles=8` |
| `war_archives_20220428_cn/b1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20220224_cn/a3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E3,E5,E7|spawnpts=E7|spawn=5|battles=5` |
| `event_20210225_cn/b1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D5,E2,E5|spawnpts=D2,E2|spawn=6|battles=6` |
| `event_20200326_cn/b2.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=0|camera=C2,C6,C7|spawnpts=|spawn=6|battles=6` |
| `war_archives_20210624_cn/a2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2410|camera=D2,D5|spawnpts=D5|spawn=6|battles=6` |
| `war_archives_20220224_cn/d1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5,E5|spawn=6|battles=6` |
| `event_20220324_cn/sp.json` | ✅ 匹配 | ✅ | `10,7|11x8|rows=8|tokens=88|weight=4400|camera=D4,D6,H4,H6|spawnpts=D6,H6|spawn=8|battles=8` |
| `event_20240829_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `campaign_main/campaign_15_3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=C2,C6,G2,G6|spawnpts=G6|spawn=7|battles=7` |
| `event_20260326_cn/ht1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,F5|spawnpts=E5|spawn=6|battles=6` |
| `war_archives_20180726_cn/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2,D5|spawn=7|battles=7` |
| `war_archives_20191031_en/c4.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3850|camera=D2,D5,H2,H5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20250912_cn/a2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,E6,F2|spawnpts=D2|spawn=5|battles=5` |
| `war_archives_20190221_en/b2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=6|battles=6` |
| `event_20240829_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=E5,E7|spawnpts=E5|spawn=8|battles=8` |
| `event_20240912_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=E2,E5|spawnpts=F3|spawn=6|battles=6` |
| `war_archives_20190221_en/d2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=D2|spawn=7|battles=7` |
| `event_20210610_tw/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20190321_en/d1.json` | ✅ 匹配 | ✅ | `9,4|10x5|rows=5|tokens=50|weight=2500|camera=D2,D3,G2,G3|spawnpts=D2,D3|spawn=7|battles=7` |
| `event_20200820_cn/c2.json` | ✅ 匹配 | ✅ | `6,6|7x7|rows=7|tokens=49|weight=2450|camera=D3,D5|spawnpts=D5|spawn=5|battles=5` |
| `event_20200903_en/sp3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3820|camera=D2,D5,H2,H5|spawnpts=D2|spawn=6|battles=6` |
| `event_20210225_tw/c3.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=D2,D4,F2,F4|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20221222_cn/b2.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D2,D5,D7,F2,F5,F7|spawnpts=F2|spawn=6|battles=6` |
| `event_20241121_cn/sp.json` | ✅ 匹配 | ✅ | `10,5|11x6|rows=6|tokens=66|weight=3300|camera=E2,E4,G2,G4|spawnpts=E4|spawn=8|battles=8` |
| `war_archives_20180607_cn/b2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=|spawn=6|battles=6` |
| `war_archives_20180607_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=|spawn=6|battles=6` |
| `event_20260625_cn/ht2.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F5|spawnpts=F2|spawn=7|battles=7` |
| `event_20220915_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20241121_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20220728_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,E6|spawnpts=E2|spawn=5|battles=5` |
| `event_20250227_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D2|spawn=6|battles=6` |
| `event_20210527_tw/b3.json` | ✅ 匹配 | ✅ | `6,8|7x9|rows=9|tokens=63|weight=3150|camera=D2,D6,D7|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20200903_cn/sp3.json` | ✅ 匹配 | ✅ | `10,6|11x7|rows=7|tokens=77|weight=3820|camera=D2,D5,H2,H5|spawnpts=D2|spawn=6|battles=6` |
| `war_archives_20190221_en/c2.json` | ✅ 匹配 | ✅ | `8,4|9x5|rows=5|tokens=45|weight=2250|camera=D2,D3,F2,F3|spawnpts=D3|spawn=5|battles=5` |
| `event_20210121_cn/c3.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=6|battles=6` |
| `war_archives_20220526_cn/b3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D7,F7|spawn=6|battles=6` |
| `event_20210624_tw/b1.json` | ✅ 匹配 | ✅ | `5,8|6x9|rows=9|tokens=54|weight=2700|camera=C2,C6,C7|spawnpts=C7|spawn=6|battles=6` |
| `event_20220414_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=6|battles=6` |
| `event_20230525_cn/t4.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=E2,E6,G2,G6|spawnpts=E6|spawn=6|battles=6` |
| `war_archives_20200917_cn/ht2.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2730|camera=D3,D5,E3,E5|spawnpts=E5|spawn=5|battles=5` |
| `event_20221124_cn/th4.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D6|spawn=7|battles=7` |
| `event_20250912_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `war_archives_20211014_cn/sp4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=C2,C5,F2,F5|spawnpts=C5|spawn=6|battles=6` |
| `event_20210121_cn/b3.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D2,D5|spawn=6|battles=6` |
| `event_20200611_en/b3.json` | ✅ 匹配 | ✅ | `13,9|14x10|rows=10|tokens=140|weight=0|camera=F3,G6,G8,H4|spawnpts=|spawn=6|battles=6` |
| `war_archives_20181026_en/d1.json` | ✅ 匹配 | ✅ | `9,5|10x6|rows=6|tokens=60|weight=3000|camera=D2,D4,G2,G4|spawnpts=D2,D4|spawn=6|battles=6` |
| `event_20260520_cn/d2.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D3,D6,G3,G6|spawnpts=G3|spawn=7|battles=7` |
| `event_20260520_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D3,D5,D7,F3,F5,F7|spawnpts=D7|spawn=7|battles=7` |
| `event_20230223_cn/a3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,F2,F5|spawnpts=D5|spawn=5|battles=5` |
| `war_archives_20200507_cn/sp3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6|spawn=6|battles=6` |
| `event_20230803_cn/sp1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=2700|camera=F2,F4|spawnpts=D2,D4|spawn=5|battles=5` |
| `event_20220428_cn/c3.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6|spawn=6|battles=6` |
| `war_archives_20221222_cn/a1.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,E3,E6|spawnpts=D6|spawn=5|battles=5` |
| `event_20240815_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,E3,F4|spawnpts=E6|spawn=8|battles=8` |
| `event_20240425_cn/isp2.json` | ✅ 匹配 | ✅ | `4,4|5x5|rows=5|tokens=25|weight=1250|camera=C2|spawnpts=C2|spawn=1|battles=1` |
| `campaign_main/campaign_6_4.json` | ✅ 匹配 | ✅ | `7,5|8x6|rows=6|tokens=48|weight=1560|camera=D2,D4,E2,E4|spawnpts=D2,D4|spawn=6|battles=6` |
| `event_20230525_cn/hts1.json` | ✅ 匹配 | ✅ | `9,7|10x8|rows=8|tokens=80|weight=4000|camera=D2,D6,G2,G6|spawnpts=D6,G6|spawn=5|battles=5` |
| `event_20250227_cn/a1.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=F2|spawn=5|battles=5` |
| `event_20241219_cn/sp.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=2530|camera=D2,D5,D7,F2,F5,F7|spawnpts=F6|spawn=8|battles=8` |
| `campaign_main/campaign_9_4.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=590|camera=D3,F3,F4|spawnpts=D4|spawn=6|battles=6` |
| `event_20200716_en/a4.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=630|camera=E3,E5|spawnpts=|spawn=5|battles=5` |
| `war_archives_20240725_cn/campaign_base.json` | ⏭️ 基类模块（无 MAP 对象），按设计跳过 | — | `0,0|1x1|rows=0|tokens=0|weight=0|camera=|spawnpts=|spawn=0|battles=0` |
| `event_20250227_cn/c2.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D2,D6,F2,F6|spawnpts=D2,F2|spawn=5|battles=5` |
| `event_20200423_cn/c1.json` | ✅ 匹配 | ✅ | `8,5|9x6|rows=6|tokens=54|weight=0|camera=D2,D4,F2,F4|spawnpts=|spawn=0|battles=0` |
| `event_20260908_cn/c3.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D3,D6|spawnpts=D2|spawn=6|battles=6` |
| `event_20260813_cn/d3.json` | ✅ 匹配 | ✅ | `8,8|9x9|rows=9|tokens=81|weight=4050|camera=D4,D6,F4,F6|spawnpts=D4|spawn=7|battles=7` |
| `campaign_main/campaign_2_4.json` | ✅ 匹配 | ✅ | `6,3|7x4|rows=4|tokens=28|weight=513|camera=D2|spawnpts=D2|spawn=4|battles=4` |
| `event_20250814_cn/t1.json` | ✅ 匹配 | ✅ | `8,6|9x7|rows=7|tokens=63|weight=3150|camera=D2,D5,F2,F5|spawnpts=F2|spawn=5|battles=5` |
| `event_20231123_cn/t3.json` | ✅ 匹配 | ✅ | `8,7|9x8|rows=8|tokens=72|weight=3600|camera=D3,D6,F3,F6|spawnpts=D6|spawn=6|battles=6` |
| `event_20220428_cn/c1.json` | ✅ 匹配 | ✅ | `7,6|8x7|rows=7|tokens=56|weight=2800|camera=D2,D5,E2,E5|spawnpts=D5|spawn=5|battles=5` |
| `event_20210527_cn/c2.json` | ✅ 匹配 | ✅ | `7,7|8x8|rows=8|tokens=64|weight=3200|camera=D2,D6,E2,E6|spawnpts=D6,E6|spawn=5|battles=5` |

## 跳过原因

- `war_archives_20201029_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20210916_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20220915_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20200903_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20250814_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20211014_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20201029_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20260417_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_16_base_aircraft.json`：AttributeError: module 'campaign.campaign_main.campaign_16_base_aircraft' has no attribute 'MAP'
- `event_20250424_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20210121_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20220224_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20230525_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20220818_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20200917_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20221124_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_16_base_submarine.json`：AttributeError: module 'campaign.campaign_main.campaign_16_base_submarine' has no attribute 'MAP'
- `war_archives_20230525_cn/config_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20200227_cn/d3.json`：ImportError: cannot import name 'D3' from 'module.campaign.assets' (<developer-home>\source\ALAS fork project\source project\AzurLaneAutoScript\module\campaign\assets.py)
- `campaign_hard/campaign_hard.json`：AttributeError: module 'campaign.campaign_hard.campaign_hard' has no attribute 'MAP'
- `war_archives_20200917_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20190911_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20211125_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_14_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20230803_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20221222_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20211229_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_support_fleet.json`：AttributeError: module 'campaign.campaign_main.campaign_support_fleet' has no attribute 'MAP'
- `event_20210722_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240912_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20260908_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20241219_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20231026_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240425_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20241024_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20230525_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20220818_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20220324_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20221222_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20201126_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_3_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20251023_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_15_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20220428_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240229_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240725_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20231221_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240521_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_main/campaign_2_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20220224_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20200227_cn/c2.json`：ImportError: cannot import name 'C2' from 'module.campaign.assets' (<developer-home>\source\ALAS fork project\source project\AzurLaneAutoScript\module\campaign\assets.py)
- `campaign_sos/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20230817_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20230803_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20250724_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20220428_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20200312_cn/sp3.json`：ImportError: cannot import name 'EVENT_20200312CN_SP3' from 'module.campaign.assets' (<developer-home>\source\ALAS fork project\source project\AzurLaneAutoScript\module\campaign\assets.py)
- `event_20231026_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20210325_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20210325_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240815_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20231123_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `campaign_war_archives/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20230525_cn/config_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20230914_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20240829_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20220915_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20241121_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `event_20250912_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过
- `war_archives_20240725_cn/campaign_base.json`：基类模块（无 MAP 对象），按设计跳过

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
| 基类模块（无 MAP 对象），按设计跳过 | 63 |
| AttributeError | 4 |
| ImportError | 3 |

## 复现

```powershell
alashub map-ir                              # C# 解析全部 IR，导出摘要与网格指纹
python tools/diagnostics/verify_map_ir.py   # 与上游活对象对照（固定随机种子）
$env:SAMPLE = "2000"                        # 跑全量（实测 1437 个文件约 10 秒）
```
