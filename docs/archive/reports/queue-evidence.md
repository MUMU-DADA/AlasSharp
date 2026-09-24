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
| `20260923T200327` | 动作授权；宿主 1 / 设备配置 1 | 22.9 秒 |
| `20260923T203555` | 动作授权；宿主 1 / 设备配置 1 | 13.9 秒 |
| `20260923T204223` | 动作授权；宿主 1 / 设备配置 1 | 6.6 秒 |
| `20260924T005953` | 动作授权；宿主 1 / 设备配置 1 | 19.3 秒 |
| `20260924T011313` | 动作授权；宿主 1 / 设备配置 1 | 10.9 秒 |
| `20260924T011513` | 动作授权；宿主 1 / 设备配置 1 | 16.7 秒 |
| `20260924T011711` | 动作授权；宿主 1 / 设备配置 1 | 8.9 秒 |
| `20260924T035857` | 只读设备；宿主 1 / 设备配置 1 | 5.5 秒 |
| `20260925T025717` | 动作授权；宿主 1 / 设备配置 1 | 16.5 秒 |
| `20260925T025946` | 动作授权；宿主 1 / 设备配置 1 | 111.7 秒 |
| `20260925T030928` | 动作授权；宿主 1 / 设备配置 1 | 48.6 秒 |
| `20260925T040837` | 只读设备；宿主 1 / 设备配置 1 | 6.1 秒 |
| `20260925T040915` | 动作授权；宿主 1 / 设备配置 1 | 4.1 秒 |
| `20260925T043232` | 只读设备；宿主 1 / 设备配置 1 | 2.9 秒 |
| `20260925T045455` | 动作授权；宿主 1 / 设备配置 1 | 17.8 秒 |

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
| `20260923T200327/before-reward-mission` | `account_state` | 实时抓帧；页面 page_event；in_map=False |
| `20260923T200327/reward-mission-plan` | `periodic_plan` | 上游绑定 1 项；请求 reward 已找到 |
| `20260923T200327/reward-mission-preflight` | `periodic_preflight` | 请求 reward 已放行；executes=false；上游绑定一致 |
| `20260923T200327/reward-mission-run` | `periodic_run` | 上游 AzurLaneAutoScript.reward；decision=ran；native_success=true |
| `20260923T200327/after-reward-mission` | `account_state` | 实时抓帧；页面 page_mission；in_map=False |
| `20260923T203555/prepare-main` | `navigate` | 目标 page_main；完成 1/1 轮；1 次跳转；最终 page_main, page_main_white |
| `20260923T203555/to-tactical` | `navigate` | 目标 page_tactical；完成 1/1 轮；2 次跳转；最终 page_tactical |
| `20260923T203555/at-tactical` | `account_state` | 实时抓帧；页面 page_tactical；in_map=False |
| `20260923T203555/back-main` | `navigate` | 目标 page_main；完成 1/1 轮；1 次跳转；最终 page_main, page_main_white |
| `20260923T203555/at-main` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260923T204223/before-tactical` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260923T204223/tactical-plan` | `periodic_plan` | 上游绑定 1 项；请求 tactical 已找到 |
| `20260923T204223/tactical-preflight` | `periodic_preflight` | 请求 tactical 已放行；executes=false；上游绑定一致 |
| `20260923T204223/tactical-run` | `periodic_run` | 上游 AzurLaneAutoScript.tactical；decision=ran；native_success=true |
| `20260923T204223/after-tactical` | `account_state` | 实时抓帧；页面 page_reward；in_map=False |
| `20260924T005953/meowfficer-plan` | `periodic_plan` | 上游绑定 1 项；请求 meowfficer 已找到 |
| `20260924T005953/meowfficer-preflight` | `periodic_preflight` | 请求 meowfficer 已放行；executes=false；上游绑定一致 |
| `20260924T005953/meowfficer-fort` | `periodic_run` | 上游 AzurLaneAutoScript.meowfficer；decision=ran；native_success=true |
| `20260924T005953/after-meowfficer` | `account_state` | 实时抓帧；页面 page_meowfficer；in_map=False |
| `20260924T011313/enter-os` | `navigate` | 上游原生导航到 page_os；完成 1/1 轮；最终 page_os |
| `20260924T011313/os-map` | `os_state` | 实时海域抓帧；上游在图；47 格；perspective |
| `20260924T011313/os-page` | `account_state` | 实时抓帧；页面 page_os；in_map=False |
| `20260924T011513/daily-plan` | `periodic_plan` | 上游绑定 1 项；请求 OpsiDaily 已找到 |
| `20260924T011513/daily-preflight` | `periodic_preflight` | 请求 OpsiDaily 已放行；executes=false；上游绑定一致 |
| `20260924T011513/daily-action` | `os_action` | 上游 AzurLaneAutoScript.opsi_daily；decision=ran；native_success=true；仅证明原生调度返回 |
| `20260924T011513/daily-after` | `os_state` | 实时海域抓帧；上游在图；47 格；perspective |
| `20260924T011711/obscure-plan` | `periodic_plan` | 上游绑定 1 项；请求 OpsiObscure 已找到 |
| `20260924T011711/obscure-preflight` | `periodic_preflight` | 请求 OpsiObscure 已放行；executes=false；上游绑定一致 |
| `20260924T011711/obscure-action` | `os_action` | 上游 AzurLaneAutoScript.opsi_obscure；decision=ran；native_success=true；仅证明原生调度返回 |
| `20260924T011711/obscure-after` | `os_state` | 实时海域抓帧；上游在图；47 格；perspective |
| `20260924T035857/observe-os` | `observe` | 4 tick；抓帧 4/4；错误 0；3.05 秒；地图 os 命中 4/4 |
| `20260925T025717/daily-plan` | `periodic_plan` | 上游绑定 1 项；请求 daily 已找到 |
| `20260925T025717/daily-preflight` | `periodic_preflight` | 请求 daily 已放行；executes=false；上游绑定一致 |
| `20260925T025717/daily-native` | `periodic_run` | 上游 AzurLaneAutoScript.daily；decision=ran；native_success=true |
| `20260925T025717/daily-state` | `account_state` | 实时抓帧；页面 page_daily；in_map=False |
| `20260925T025946/daily-plan` | `periodic_plan` | 上游绑定 1 项；请求 daily 已找到 |
| `20260925T025946/daily-preflight` | `periodic_preflight` | 请求 daily 已放行；executes=false；上游绑定一致 |
| `20260925T025946/daily-native` | `periodic_run` | 上游 AzurLaneAutoScript.daily；decision=ran；native_success=true |
| `20260925T025946/daily-state` | `account_state` | 实时抓帧；页面 page_daily；in_map=False |
| `20260925T030928/to-page_fleet` | `navigate` | 上游原生导航到 page_fleet；完成 2/2 轮；最终 page_fleet |
| `20260925T030928/state-page_fleet` | `account_state` | 实时抓帧；页面 page_fleet；in_map=False |
| `20260925T030928/to-page_dock` | `navigate` | 上游原生导航到 page_dock；完成 2/2 轮；最终 page_dock |
| `20260925T030928/state-page_dock` | `account_state` | 实时抓帧；页面 page_dock；in_map=False |
| `20260925T030928/to-page_commission` | `navigate` | 上游原生导航到 page_commission；完成 2/2 轮；最终 page_commission |
| `20260925T030928/state-page_commission` | `account_state` | 实时抓帧；页面 page_commission；in_map=False |
| `20260925T030928/to-page_exercise` | `navigate` | 上游原生导航到 page_exercise；完成 2/2 轮；最终 page_exercise |
| `20260925T030928/state-page_exercise` | `account_state` | 实时抓帧；页面 page_exercise；in_map=False |
| `20260925T030928/return-main` | `navigate` | 上游原生导航到 page_main；完成 1/1 轮；最终 page_main |
| `20260925T040837/chapter-map` | `observe` | 4 tick；抓帧 4/4；错误 0；4 秒；地图 main 命中 4/4；原生章节配置 `campaign.campaign_main.campaign_1_1` |
| `20260925T040915/exit-observation` | `navigate` | 上游原生导航到 page_main；完成 1/1 轮；最终 page_main |
| `20260925T040915/after-cleanup` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260925T043232/scheduler-after` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |
| `20260925T045455/shop` | `navigate` | 上游原生导航到 page_shop；完成 1/1 轮；最终 page_shop |
| `20260925T045455/shop-state` | `account_state` | 实时抓帧；页面 page_munitions, page_shop, page_supply_pack；in_map=False |
| `20260925T045455/shop-home` | `navigate` | 上游原生导航到 page_main；完成 1/1 轮；最终 page_main |
| `20260925T045455/build` | `navigate` | 上游原生导航到 page_build；完成 1/1 轮；最终 page_build |
| `20260925T045455/build-state` | `account_state` | 实时抓帧；页面 page_build；in_map=False |
| `20260925T045455/build-home` | `navigate` | 上游原生导航到 page_main；完成 1/1 轮；最终 page_main |
| `20260925T045455/mail` | `navigate` | 上游原生导航到 page_mail；完成 1/1 轮；最终 page_mail |
| `20260925T045455/mail-state` | `account_state` | 实时抓帧；页面 page_mail；in_map=False |
| `20260925T045455/mail-home` | `navigate` | 上游原生导航到 page_main；完成 1/1 轮；最终 page_main |
| `20260925T045455/research` | `navigate` | 上游原生导航到 page_research；完成 1/1 轮；最终 page_research |
| `20260925T045455/research-state` | `account_state` | 实时抓帧；页面 page_research；in_map=False |
| `20260925T045455/research-home` | `navigate` | 上游原生导航到 page_main；完成 1/1 轮；最终 page_main |
| `20260925T045455/final-state` | `account_state` | 实时抓帧；页面 page_main, page_main_white；in_map=False |

来源目录（项目相对路径，原件不入库）：

- `data/mainline-device/20260923T144841-observe/artifacts/20260923T144841`
- `data/mainline-device/20260923T144905-navigate/artifacts/20260923T144906`
- `data/mainline-device/20260923T145244-reward/artifacts/20260923T145245`
- `data/mainline-device/20260923T154407-observe_map/artifacts/20260923T154407`
- `data/mainline-device/20260923T155210-dorm/artifacts/20260923T155210`
- `data/mainline-device/20260923T160321-event_nav/artifacts/20260923T160321`
- `data/mainline-device/current-freebies-merit/20260923T175825`
- `data/mainline-device/current-reward-mission-artifacts/20260923T200327`
- `data/mainline-device/current-tactical-access-retry-artifacts/20260923T203555`
- `data/mainline-device/current-tactical-native-artifacts/20260923T204223`
- `data/mainline-device/20260924-meowfficer-probe/artifacts/20260924T005953`
- `data/mainline-device/20260924-os-cleared-account/entry-artifacts/20260924T011313`
- `data/mainline-device/20260924-os-cleared-account/daily-artifacts/20260924T011513`
- `data/mainline-device/20260924-os-cleared-account/obscure-artifacts/20260924T011711`
- `data/mainline-device/20260924-observe-os/artifacts/20260924T035857`
- `data/mainline-device/live-20260925-daily/artifacts/20260925T025717`
- `data/mainline-device/live-20260925-daily-advance/artifacts/20260925T025946`
- `data/mainline-device/live-20260925-pages/artifacts/20260925T030928`
- `data/mainline-device/live-20260925-observe-chapter/artifacts/20260925T040837`
- `data/mainline-device/live-20260925-observe-cleanup/artifacts/20260925T040915`
- `data/mainline-device/live-20260925-scheduler-after/artifacts/20260925T043232`
- `data/mainline-device/live-20260925-navigation-more/artifacts/20260925T045455`
