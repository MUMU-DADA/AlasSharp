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
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest()
                   for name in ["module/map/fleet.py", "module/map/map.py"]}
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding="utf-8")


if __name__ == "__main__":
    main()
