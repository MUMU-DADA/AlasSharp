# 原生关卡入口边界复核

由 `tools/diagnostics/verify_native_campaign_entry.py` 生成。无设备连接，点击仅记录为内存意图。
上游 `CampaignRun.run()` 先清设备记录并处理上一局在图状态，再执行 `ensure_campaign_ui()` 与 `Campaign.run()`。
`CampaignUI.handle_campaign_ui_additional()` 使用原生 WITHDRAW 素材、`withdraw()` 和 `CampaignEnd` 清理；
`MapOperation.enter_map()` 自带准备、舰队、退役、道具、心情与剧情处理，不并发运行另一套点击流程。

已删除宿主固定红色区域/坐标撤退线程及入口覆写。该线程绕开共享设备后端、吞掉异常，
且只等待退出 5 秒，不能保证其截图子进程结束后不会再次点击。冻结结果词表中的旧步骤名保留以读取历史工件。

原生 additional-handler 的未命中、成功、CampaignEnd 和其他异常传播共 6 种输入通过。

## 存盘帧原生判据对照

| 本地证据标签 | SHA-256 | WITHDRAW | 原生确认弹窗 |
| --- | --- | --- | --- |
| `_shot_dialog.png` | `2cd38a95b39095613a504b78ec7561790dafbb90a76bf4c6d032c658866c1815` | False | True |
| `_after_abort.png` | `d5cf67da00140bc2c4f028468844162ccb3c6966adfdccab8b1e928d479f8c97` | False | False |
| `_live_enter_map_stall.png` | `7cf14c29fed6287415913af8ed4e4343118f691cb61850da97c002f30b122bf6` | True | False |
| `_o49_05.png` | `ebf0044c561b338fbfa3ab3e7a6bf8bd5d785279d07e3f2cf924725957bc5fb8` | False | False |

## 尚未验证的行为

上述模板判据不证明整条弹窗操作完成；未提供的存盘帧不产生测量结论。
本次提供的历史 `_o49_05.png` 残留出击弹窗在原生 WITHDRAW/确认判据中均未命中。
删除旁路不意味着该客户端状态已被修好。后续必须复核原生撤退和导航后的现场状态及上游支持情况，
不能恢复单页颜色阈值/固定坐标，也不能从其他关卡成功推断它可用。
本次未新增真机通关；原有成功结算与撤退记录不变。

脱敏范围：仅导出本地文件标签、内容哈希和布尔判据；不发布图像、账号、绝对路径或原始运行日志。
