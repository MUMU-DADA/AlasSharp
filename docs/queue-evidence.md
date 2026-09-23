# 真实队列证据

由 `python tools/diagnostics/audit_queue_evidence.py` 从脱敏工件重建；不手写完成结论。

核对 queue、逐任务工件、断点身份与顺序、会话日志、宿主/设备次数及各任务事实；
归档保留原件和脱敏件 SHA-256。原件存在时还会验证原件校验和及可重复脱敏。
离线检出只有脱敏件时只能验证归档完整性与交叉一致性，不能代替现场重跑。

账号配置、设备标识和本机绝对路径已脱敏，截图与原始控制台日志留在忽略目录。
周期任务的 `native_success=true` 只证明原生调度返回，不证明资源实际到账。
这些记录只覆盖表中实际执行的任务；不能证明未解锁功能、其他周期任务或战役通关。

| 归档 | 会话 | 耗时 |
| --- | --- | --- |
| `20260923T144841` | 只读设备；宿主 1 / 设备配置 1 | 6.4 秒 |
| `20260923T144906` | 动作授权；宿主 1 / 设备配置 1 | 21.4 秒 |
| `20260923T145245` | 动作授权；宿主 1 / 设备配置 1 | 7.7 秒 |
| `20260923T154407` | 只读设备；宿主 1 / 设备配置 1 | 5.5 秒 |
| `20260923T155210` | 动作授权；宿主 1 / 设备配置 1 | 7.9 秒 |
| `20260923T160321` | 动作授权；宿主 1 / 设备配置 1 | 10.6 秒 |
| `20260923T175825` | 动作授权；宿主 1 / 设备配置 1 | 8.7 秒 |

| 归档 / 任务 | 任务域 | 已核对事实 |
| --- | --- | --- |
| `20260923T144841/live-state` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260923T144841/observe` | `observe` | 6 tick；抓帧 6/6；错误 0；3.001 秒 |
| `20260923T144906/to-main` | `navigate` | 目标 page_main；完成 1/1 轮；0 次跳转；最终 page_main, page_main_white |
| `20260923T144906/to-campaign` | `navigate` | 目标 page_campaign；完成 2/2 轮；5 次跳转；最终 page_campaign |
| `20260923T144906/return-main` | `navigate` | 目标 page_main；完成 1/1 轮；1 次跳转；最终 page_main, page_main_white |
| `20260923T144906/observe` | `observe` | 4 tick；抓帧 4/4；错误 0；2 秒 |
| `20260923T145245/reward-plan` | `periodic_plan` | 上游绑定 4 项；请求 reward 已找到 |
| `20260923T145245/reward` | `periodic_run` | 上游 AzurLaneAutoScript.reward；decision=ran；native_success=true |
| `20260923T145245/after-reward` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260923T154407/live-state` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260923T154407/observe-map` | `observe` | 6 tick；抓帧 6/6；错误 0；3.001 秒；地图 main 命中 0/6 |
| `20260923T155210/dorm-plan` | `periodic_plan` | 上游绑定 4 项；请求 dorm 已找到 |
| `20260923T155210/dorm` | `periodic_run` | 上游 AzurLaneAutoScript.dorm；decision=ran；native_success=true |
| `20260923T155210/after-dorm` | `account_state` | 实时抓帧；页面 page_dorm；in_map=False |
| `20260923T160321/to-event` | `navigate` | 目标 page_event；完成 1/1 轮；3 次跳转；最终 page_event |
| `20260923T160321/event-state` | `account_state` | 实时抓帧；页面 page_event；in_map=False |
| `20260923T175825/freebies-plan` | `periodic_plan` | 上游绑定 1 项；请求 freebies 已找到 |
| `20260923T175825/freebies-preflight` | `periodic_preflight` | 请求 freebies 已放行；executes=false；上游绑定一致 |
| `20260923T175825/freebies-merit` | `periodic_run` | 上游 AzurLaneAutoScript.freebies；decision=ran；native_success=true |
| `20260923T175825/after-freebies` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |

来源目录（项目相对路径，原件不入库）：

- `data/mainline-device/20260923T144841-observe/artifacts/20260923T144841`
- `data/mainline-device/20260923T144905-navigate/artifacts/20260923T144906`
- `data/mainline-device/20260923T145244-reward/artifacts/20260923T145245`
- `data/mainline-device/20260923T154407-observe_map/artifacts/20260923T154407`
- `data/mainline-device/20260923T155210-dorm/artifacts/20260923T155210`
- `data/mainline-device/20260923T160321-event_nav/artifacts/20260923T160321`
- `data/mainline-device/current-freebies-merit/20260923T175825`
