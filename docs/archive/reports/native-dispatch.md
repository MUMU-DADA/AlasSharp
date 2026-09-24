# 原生任务全量分派验证

由 `tools/diagnostics/verify_native_dispatch_catalog.py` 生成；不连接设备，不读取账号配置。

原生 `alas.py` SHA-256：`c84ad690117c125eb5c9396a4a21f1e34866c286238a0b65e82bea884a9a9020`。
原生任务参数 SHA-256：`edb5dc1ec58d0f664c09efe9d1d216eb40295db800c0dfaff10a4cb203ac12ee`。

发现 57 个周期任务和 7 个独立工具；64/64 个入口通过，共 256 个通过场景。

每个入口运行正常返回、普通 False 返回、TaskEnd 和 GameNotRunningError 四种场景。
使用真实 ConfigUpdater、AzurLaneConfig、原生 run() 与 alas.py 方法；只替换最终领域类/函数、设备、通知传输及配置文件位置。
对照实际领域签名和入口 AST 核对参数，并验证任务绑定、共享设备恢复、截图次数以及原生 Restart 重试写入。
配置由上游参数默认值生成，所有配置读写限制在独立临时目录；普通 False 领域返回不能被误判为原生调度失败。
新入口自动纳入；未审阅的方法体、参数表达式、重复入口或空目录令检查失败。

这不验证领域构造器和内部业务循环，不证明领取、购买、战斗或工具实际效果；工具构造器内的任务绑定也不在此范围。
错误详情仅留在本地忽略目录；此表只保留上游符号、源码哈希及判据结果。

| 类型 | 命令 | 原生领域入口 | 通过场景 |
| --- | --- | --- | --- |
| periodic | `Restart` | `module.handler.login.LoginHandler.app_restart` | 4/4 通过 |
| periodic | `Main` | `module.campaign.run.CampaignRun.run` | 4/4 通过 |
| periodic | `Main2` | `module.campaign.run.CampaignRun.run` | 4/4 通过 |
| periodic | `Main3` | `module.campaign.run.CampaignRun.run` | 4/4 通过 |
| periodic | `GemsFarming` | `module.campaign.gems_farming.GemsFarming.run` | 4/4 通过 |
| periodic | `Event` | `module.campaign.run.CampaignRun.run` | 4/4 通过 |
| periodic | `Event2` | `module.campaign.run.CampaignRun.run` | 4/4 通过 |
| periodic | `Raid` | `module.raid.run.RaidRun.run` | 4/4 通过 |
| periodic | `Hospital` | `module.event_hospital.hospital.Hospital.run` | 4/4 通过 |
| periodic | `Coalition` | `module.coalition.coalition.Coalition.run` | 4/4 通过 |
| periodic | `EventShop` | `module.shop_event.shop_event.EventShop.run` | 4/4 通过 |
| periodic | `WarArchives` | `module.war_archives.war_archives.CampaignWarArchives.run` | 4/4 通过 |
| periodic | `EventA` | `module.event.campaign_abcd.CampaignABCD.run` | 4/4 通过 |
| periodic | `EventB` | `module.event.campaign_abcd.CampaignABCD.run` | 4/4 通过 |
| periodic | `EventC` | `module.event.campaign_abcd.CampaignABCD.run` | 4/4 通过 |
| periodic | `EventD` | `module.event.campaign_abcd.CampaignABCD.run` | 4/4 通过 |
| periodic | `EventSp` | `module.event.campaign_sp.CampaignSP.run` | 4/4 通过 |
| periodic | `RaidDaily` | `module.raid.daily.RaidDaily.run` | 4/4 通过 |
| periodic | `CoalitionSp` | `module.coalition.coalition_sp.CoalitionSP.run` | 4/4 通过 |
| periodic | `Commission` | `module.commission.commission.RewardCommission.run` | 4/4 通过 |
| periodic | `Tactical` | `module.tactical.tactical_class.RewardTacticalClass.run` | 4/4 通过 |
| periodic | `Research` | `module.research.research.RewardResearch.run` | 4/4 通过 |
| periodic | `Dorm` | `module.dorm.dorm.RewardDorm.run` | 4/4 通过 |
| periodic | `Meowfficer` | `module.meowfficer.meowfficer.RewardMeowfficer.run` | 4/4 通过 |
| periodic | `Guild` | `module.guild.guild_reward.RewardGuild.run` | 4/4 通过 |
| periodic | `Reward` | `module.reward.reward.Reward.run` | 4/4 通过 |
| periodic | `Awaken` | `module.awaken.awaken.Awaken.run` | 4/4 通过 |
| periodic | `Daily` | `module.daily.daily.Daily.run` | 4/4 通过 |
| periodic | `Hard` | `module.hard.hard.CampaignHard.run` | 4/4 通过 |
| periodic | `Exercise` | `module.exercise.exercise.Exercise.run` | 4/4 通过 |
| periodic | `ShopFrequent` | `module.shop.shop_reward.RewardShop.run_frequent` | 4/4 通过 |
| periodic | `ShopOnce` | `module.shop.shop_reward.RewardShop.run_once` | 4/4 通过 |
| periodic | `Shipyard` | `module.shipyard.shipyard_reward.RewardShipyard.run` | 4/4 通过 |
| periodic | `Gacha` | `module.gacha.gacha_reward.RewardGacha.run` | 4/4 通过 |
| periodic | `Freebies` | `module.freebies.freebies.Freebies.run` | 4/4 通过 |
| periodic | `Minigame` | `module.minigame.minigame.Minigame.run` | 4/4 通过 |
| periodic | `PrivateQuarters` | `module.private_quarters.private_quarters.PrivateQuarters.run` | 4/4 通过 |
| periodic | `OpsiAshBeacon` | `module.os_ash.meta.OpsiAshBeacon.run` | 4/4 通过 |
| periodic | `OpsiAshAssist` | `module.os_ash.meta.AshBeaconAssist.run` | 4/4 通过 |
| periodic | `OpsiExplore` | `module.campaign.os_run.OSCampaignRun.opsi_explore` | 4/4 通过 |
| periodic | `OpsiShop` | `module.campaign.os_run.OSCampaignRun.opsi_shop` | 4/4 通过 |
| periodic | `OpsiVoucher` | `module.campaign.os_run.OSCampaignRun.opsi_voucher` | 4/4 通过 |
| periodic | `OpsiDaily` | `module.campaign.os_run.OSCampaignRun.opsi_daily` | 4/4 通过 |
| periodic | `OpsiObscure` | `module.campaign.os_run.OSCampaignRun.opsi_obscure` | 4/4 通过 |
| periodic | `OpsiAbyssal` | `module.campaign.os_run.OSCampaignRun.opsi_abyssal` | 4/4 通过 |
| periodic | `OpsiArchive` | `module.campaign.os_run.OSCampaignRun.opsi_archive` | 4/4 通过 |
| periodic | `OpsiStronghold` | `module.campaign.os_run.OSCampaignRun.opsi_stronghold` | 4/4 通过 |
| periodic | `OpsiMonthBoss` | `module.campaign.os_run.OSCampaignRun.opsi_month_boss` | 4/4 通过 |
| periodic | `OpsiMeowfficerFarming` | `module.campaign.os_run.OSCampaignRun.opsi_meowfficer_farming` | 4/4 通过 |
| periodic | `OpsiHazard1Leveling` | `module.campaign.os_run.OSCampaignRun.opsi_hazard1_leveling` | 4/4 通过 |
| periodic | `OpsiCrossMonth` | `module.campaign.os_run.OSCampaignRun.opsi_cross_month` | 4/4 通过 |
| periodic | `IslandProduction` | `module.island.production.IslandProduction.run` | 4/4 通过 |
| periodic | `IslandOrder` | `module.island.order.IslandOrder.run` | 4/4 通过 |
| periodic | `IslandFreebie` | `module.island.freebie.IslandFreebie.run` | 4/4 通过 |
| periodic | `IslandCollect` | `module.island.collect.IslandCollect.run` | 4/4 通过 |
| periodic | `IslandSeasonTask` | `module.island.season_task.IslandSeasonTask.run` | 4/4 通过 |
| periodic | `IslandBusiness` | `module.island.business.IslandBusiness.run` | 4/4 通过 |
| tool | `Daemon` | `module.daemon.daemon.AzurLaneDaemon.run` | 4/4 通过 |
| tool | `OpsiDaemon` | `module.daemon.os_daemon.AzurLaneDaemon.run` | 4/4 通过 |
| tool | `EventStory` | `module.eventstory.eventstory.EventStory.run` | 4/4 通过 |
| tool | `IslandProductionPlanner` | `module.island_handler.production_planner.IslandProductionPlanner.run` | 4/4 通过 |
| tool | `AzurLaneUncensored` | `module.daemon.uncensored.AzurLaneUncensored.run` | 4/4 通过 |
| tool | `Benchmark` | `module.daemon.benchmark.run_benchmark` | 4/4 通过 |
| tool | `GameManager` | `module.daemon.game_manager.GameManager.run` | 4/4 通过 |
