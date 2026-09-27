"""Execute upstream _goto bookkeeping and supply selection with synthetic I/O only."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        from module.exception import CampaignEnd
        from module.map.map import Map
        from module.map.map_base import CampaignMap
        from module.base.utils import location2node
        import module.base.timer as timers
        from module.logger import logger
        logger.setLevel("CRITICAL")

        class SupplyRequired(Exception):
            pass

        class Replay(Map):
            def __init__(self, index):
                self.config = SimpleNamespace(Campaign_UseFleetLock=False, Submarine_Mode="do_not_use",
                    MAP_HAS_LAND_BASED=False, MAP_HAS_MOVABLE_ENEMY=False, MAP_HAS_MAZE=False,
                    MAP_HAS_DECOY_ENEMY=False, MAP_HAS_BOUNCING_ENEMY=False, MAP_FOCUS_ENEMY_AFTER_BATTLE=False)
                self.map = CampaignMap("ammo-reference")
                self.map.shape, self.map.map_data = "D1", "SP ME ME MA"
                self.map.load_map_data()
                self.map.grid_connection_initial()
                self.fleet_current_index = self.fleet_show_index = index
                self.fleet_1_location = self.fleet_2_location = (0, 0)
                self.map[(0, 0)].is_fleet = True
                self.battle_count = self.siren_count = 0
                self.combat_exit = None
                self.device = SimpleNamespace(image=None, screenshot=lambda: None, click=lambda grid: None)
                self.view = SimpleNamespace(update=lambda **kw: None)

            def hp_retreat_triggered(self): return False
            def fleet_ensure(self, index): return False
            def in_sight(self, *args, **kw): pass
            def focus_to_grid_center(self): pass
            def convert_global_to_local(self, location):
                grid = self.map[location]
                grid.predict_fleet = grid.predict_current_fleet = lambda: True
                return grid
            def ambush_color_initial(self): pass
            def enemy_searching_color_initial(self): pass
            def combat_appear(self): return True
            def combat(self, **kw):
                if self.combat_exit:
                    raise self.combat_exit
            def _expected_end(self, expected): return None
            def _submarine_mode(self, expected): return None
            def hp_get(self): pass
            def lv_get(self, **kw): pass
            def catch_camera_repositioning(self, grid): return False
            def handle_ambush(self): return False
            def handle_mystery(self, **kw): return False
            def handle_map_cat_attack(self): return False
            def handle_guild_popup_cancel(self): return False
            def handle_walk_out_of_step(self): return False
            def is_in_map(self): return True
            def predict(self): pass
            def find_path_initial(self): pass
            def goto(self, grid, **kw): raise SupplyRequired()

        def snapshot(driver):
            return dict(battle=driver.battle_count, siren=driver.siren_count,
                        stock=driver.ammo_count, fleet=driver.fleet_ammo)

        results = []
        for index in [1, 2]:
            for siren in [False, True]:
                driver = Replay(index)
                snapshots = [snapshot(driver)]
                for battle in range(8):
                    destination = (1 + battle % 2, 0)
                    driver.map[destination].is_enemy = not siren
                    driver.map[destination].is_siren = siren
                    # Execute the entire native move; only vision/device and
                    # combat outcome are supplied by the synthetic harness.
                    driver._goto(destination, expected="combat_siren" if siren else "combat")
                    snapshots.append(snapshot(driver))
                for error in [RuntimeError("synthetic combat failure"), CampaignEnd()]:
                    driver.combat_exit = error
                    try:
                        driver._goto((1, 0), expected="combat")
                    except type(error):
                        pass
                    snapshots.append(snapshot(driver))
                supply = driver.map[(3, 0)]
                supply.cost = 1
                assert not supply.is_ammo and supply.may_ammo
                required = False
                try:
                    driver.pick_up_ammo()
                except SupplyRequired:
                    required = True
                results.append(dict(index=index, siren=siren, snapshots=snapshots, supply_required=required))
        class SupplyReplay(Replay):
            def __init__(self, battles, notification, current, visible):
                super().__init__(1)
                self.config.MAP_HAS_FLEET_STEP = self.config.MAP_HAS_PORTAL = False
                self.config.MAP_HAS_AMBUSH = self.config.MAP_HAS_FORTRESS = False
                self.config.MAP_WALK_USE_CURRENT_FLEET = False
                self.now, self.sequence = 100., 0
                self.trace = []
                self.notification = notification
                self.battle_count, self.fleet_ammo = battles, 5 - battles
                self.mystery_count = 0
                self.fleet_1_location, self.fleet_2_location = ((3, 0) if current else (0, 0)), ()
                self.map[(0, 0)].is_fleet = not current
                self.map[(3, 0)].is_fleet = current
                self.map[(3, 0)].is_ammo = visible
                self._get_ammo_log_timer = timers.Timer(3)
                self.device = SimpleNamespace(image=None, screenshot=self.screenshot, click=self.click)
                self.find_path_initial()
            def screenshot(self):
                self.sequence += 1
                self.now += .25
                if self.sequence > 100: raise AssertionError("Supply fixture did not terminate")
            def click(self, grid): self.trace.append("tap:" + location2node(grid.location))
            def combat_appear(self): return False
            def handle_mystery(self, **kw):
                return "get_ammo" if self.handle_mystery_ammo() else False
            def info_bar_count(self): return int(self.notification and self.sequence in [1, 2])
            def appear(self, asset, **kw): return asset.name == "GET_AMMO" and bool(self.info_bar_count())
            def goto(self, grid, **kw): return Map.goto(self, grid, **kw)
            def find_path_initial(self): return Map.find_path_initial(self)
            def ensure_no_info_bar(self, **kw):
                self.trace.append("wait:start")
                result = super().ensure_no_info_bar(**kw)
                self.trace.append("wait:end")
                return result

        pickups = []
        for battles in [0, 1, 2, 3, 5, 8]:
            for notification in [False, True]:
                for current in [False, True]:
                    for visible in [False, True]:
                        driver = SupplyReplay(battles, notification, current, visible)
                        timers.time = lambda: driver.now
                        states = []
                        for visit in range(2):
                            result = driver.pick_up_ammo()
                            states.append(dict(**snapshot(driver), mystery=driver.mystery_count,
                                location=location2node(driver.fleet_current), returned=bool(result), trace=list(driver.trace)))
                        pickups.append(dict(battles=battles, notification=notification, current=current, visible=visible, states=states))
        # Preserve native accounting when a partly consumed supply is revisited
        # after more battles; the source does not clamp recovery to stock.
        driver = SupplyReplay(2, False, False, True)
        timers.time = lambda: driver.now
        driver.pick_up_ammo()
        driver.battle_count += 3
        driver.fleet_ammo -= 3
        driver.pick_up_ammo()
        partial = snapshot(driver)

        import cv2
        import numpy as np
        from module.handler.assets import GET_AMMO, INFO_BAR_AREA
        from module.handler.mystery import MysteryHandler
        images = []
        for bar, ammo in [(False, False), (True, False), (False, True), (True, True)]:
            pixels = np.zeros((720, 1280, 3), dtype=np.uint8)
            if bar:
                x1, y1, x2, _ = INFO_BAR_AREA.area
                pixels[y1+10:y1+12, x1:x2] = (107, 158, 255)
            if ammo:
                x1, y1, x2, y2 = GET_AMMO.area
                pixels[y1:y2, x1:x2] = GET_AMMO.color
            visual = object.__new__(MysteryHandler)
            visual.device = SimpleNamespace(image=pixels, stuck_record_add=lambda button: None)
            visual._get_ammo_log_timer = timers.Timer(3)
            present = bool(visual.handle_mystery_ammo())
            assert present == (bar and ammo)
            name = f"ammo-{int(bar)}-{int(ammo)}.png"
            cv2.imwrite(str(output.parent / name), cv2.cvtColor(pixels, cv2.COLOR_RGB2BGR))
            images.append(dict(file=name, present=present, bars=visual.info_bar_count()))
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                   for name in ["module/map/fleet.py", "module/map/map.py", "module/handler/mystery.py", "module/handler/info_handler.py"]}
    output.write_text(json.dumps(dict(results=results, pickups=pickups, partial=partial, images=images, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
