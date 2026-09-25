"""替身关卡：只替换 I/O，不替换上游逻辑。

`verify_s3_outcome.py`（结果判别回归）和 `verify_result_contract.py`（合同对拍）
都需要同一批"假屏幕 + 真上游方法"的替身。放在这里只有一份，
避免两个回归各自维护一套替身、慢慢长成两个不同的世界。

纪律：替身类里挂的必须是**上游自己的方法对象**
（`CampaignBase.execute_a_battle`、`MapOperation.withdraw`、`Combat.combat_status`、
`EnemySearchingHandler.handle_in_stage`），不是手工复刻 —— 否则回归就在验证替身，
而不是验证上游行为。
"""
from __future__ import annotations

from types import SimpleNamespace

from module.campaign.campaign_base import CampaignBase
from module.combat.combat import Combat
from module.handler.enemy_searching import EnemySearchingHandler
from module.map.map_operation import MapOperation


class FakeScreenCampaign:
    execute_a_battle = CampaignBase.execute_a_battle
    withdraw = MapOperation.withdraw
    combat_status = Combat.combat_status
    handle_battle_status = Combat.handle_battle_status
    handle_in_stage = EnemySearchingHandler.handle_in_stage
    FUNCTION_NAME_BASE = 'battle_'

    def __init__(self, rank='S', withdraw=False, unknown=False):
        self.rank = rank
        self.withdrawing = withdraw
        self.unknown = unknown
        self.battle_count = 6
        self.battle_status_click_interval = 0
        # This map had already reached 100% before this sortie.
        self.map_clear_percentage = 1.0
        self.config = SimpleNamespace(Error_HandleError=True)
        self.in_stage_timer = SimpleNamespace(reached=lambda: True)
        self.clicked = []
        self.device = SimpleNamespace(
            click=lambda button: self.clicked.append(button.name),
            sleep=lambda *args: None,
            screenshot_interval_set=lambda *args: None,
            stuck_record_clear=lambda: None,
            click_record_clear=lambda: None,
        )

    def battle_function(self):
        if self.withdrawing:
            return False
        if self.unknown:
            return self.handle_in_stage()
        self.handle_battle_status()
        self.combat_status(expected_end='in_stage')

    def appear(self, button, **kwargs):
        return button.name == 'BATTLE_STATUS_' + str(self.rank)

    def is_combat_executing(self):
        return False

    def is_in_stage(self):
        return True

    def ensure_no_info_bar(self, **kwargs):
        return None

    def loop(self):
        yield 0

    def handle_popup_confirm(self, *args):
        return False

    def appear_then_click(self, *args, **kwargs):
        return False

    def handle_auto_search_exit(self):
        return False


class NativeRunCampaign(FakeScreenCampaign):
    run = CampaignBase.run

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.events = []
        self.ENTRANCE = SimpleNamespace(button=(1, 2, 3, 4), area=())
        self.emotion = SimpleNamespace(check_reduce=lambda count: self.events.append(('emotion', count)))
        self._map_battle = 7
        self.config.Campaign_Mode = 'normal'
        self.map_is_auto_search = False
        self.MAP = object()

    def enter_map(self, entrance, mode):
        self.events.append(('enter_map', entrance.area, mode))

    def handle_map_fleet_lock(self):
        self.events.append(('fleet_lock',))

    def map_init(self, map_data):
        self.events.append(('map_init', map_data is self.MAP))
        self.battle_count = 0


class RecoverableEntryError(Exception):
    pass


class RecoveringNativeRunCampaign(NativeRunCampaign):
    """A native subclass owns recovery; the adapter must only observe it."""
    def map_init(self, map_data):
        if not getattr(self, 'retried', False):
            self.retried = True
            raise RecoverableEntryError('recovered entry failure')
        return super().map_init(map_data)

    def run(self):
        try:
            return super().run()
        except RecoverableEntryError:
            return super().run()
