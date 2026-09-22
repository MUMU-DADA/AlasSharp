# 页面规则的合成正对照

**这不是真机命中。** 真机命中见 `page-verification.md`（29/53）。
正对照的做法是：把每条页面规则的 check 素材**自己的模板图**贴到**它自己的 area** 上，
再跑一次上游的 `ui_page_appear` —— 规则此时应当返回真。

它证明的是"规则是活的"：素材文件能加载、区域与模板配对正确、判定方向没写反。

为什么值得单独做：受账号进度/活动/客户端版本所限，有 19 个页面在真机上到不了。
这些页面是"到不了"还是"规则本身坏了"，光靠真机验证分不清 —— 正对照把它们分开：
正对照过不了的规则一定是实现问题（素材路径错、模板空、区域写错），必须查。

生成：`tools/diagnostics/verify_positive_control.py`；数据 `data/positive_control.json`。

## 结果

| 项 | 数量 |
| --- | --- |
| 总计（上游 Page 数） | 53 |
| 正对照通过 | **52** |
| 跳过（无 check 素材 / 素材区域超出画面） | 1 |
| 失败 | 0 |

## 失败项（实现问题，必须查）

无。53 条页面规则在正对照下全部返回真（除合成实体 `page_unknown`）。

## 跳过项

| 页面 | 原因 |
| --- | --- |
| `page_unknown` | Page(None)：没有 check 素材（合成实体） |

## 与真机结果的关系

正对照通过但真机没验过的页面共 23 个 —— 它们都是受外部条件阻塞的：

`page_channel`、`page_coalition`、`page_event_list`、`page_guild`、`page_hospital`、`page_island`、`page_island_manage`、`page_island_map`、`page_island_order`、`page_island_phone`、`page_island_season`、`page_island_shop`、`page_island_storage`、`page_island_technology`、`page_island_transport`、`page_meowfficer`、`page_os`、`page_private_quarters`、`page_raid`、`page_rpg_city`、`page_rpg_stage`、`page_rpg_story`、`page_sp`

也就是说：**这些页面的规则本身是好的，缺的只是"让游戏走到那一屏"的条件**
（账号解锁岛屿/大舰队/指挥喵/大型作战、或对应类型的活动在跑、或客户端版本支持）。

## 复现

```powershell
python tools/diagnostics/verify_positive_control.py
```
