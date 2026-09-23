# 真实队列证据

由 `python tools/diagnostics/audit_queue_evidence.py` 从脱敏工件重建；不手写完成结论。

核对 queue、逐任务工件、断点身份与顺序、会话日志、宿主/设备次数及各任务事实；
归档保留原件和脱敏件 SHA-256。原件存在时还会验证原件校验和及可重复脱敏。
离线检出只有脱敏件时只能验证归档完整性与交叉一致性，不能代替现场重跑。

账号配置、设备标识和本机绝对路径已脱敏，截图与原始控制台日志留在忽略目录。
这些记录只覆盖表中实际执行的任务；不能证明未解锁功能、其他页面或战役通关。

| 归档 | 会话 | 耗时 |
| --- | --- | --- |
| `20260923T144841` | 只读设备；宿主 1 / 设备配置 1 | 6.4 秒 |
| `20260923T144906` | 动作授权；宿主 1 / 设备配置 1 | 21.4 秒 |

| 归档 / 任务 | 任务域 | 已核对事实 |
| --- | --- | --- |
| `20260923T144841/live-state` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260923T144841/observe` | `observe` | 6 tick；抓帧 6/6；错误 0；3.001 秒 |
| `20260923T144906/to-main` | `navigate` | 目标 page_main；完成 1/1 轮；0 次跳转；最终 page_main, page_main_white |
| `20260923T144906/to-campaign` | `navigate` | 目标 page_campaign；完成 2/2 轮；5 次跳转；最终 page_campaign |
| `20260923T144906/return-main` | `navigate` | 目标 page_main；完成 1/1 轮；1 次跳转；最终 page_main, page_main_white |
| `20260923T144906/observe` | `observe` | 4 tick；抓帧 4/4；错误 0；2 秒 |

来源目录（项目相对路径，原件不入库）：

- `data/mainline-device/20260923T144841-observe/artifacts/20260923T144841`
- `data/mainline-device/20260923T144905-navigate/artifacts/20260923T144906`
