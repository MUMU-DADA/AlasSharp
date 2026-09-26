"""Offline oracle: execute actual upstream rules with synthetic terminal actions.

This file is test-only, never imported or shipped by Alas.Engine. No AST plan,
exported rule, old C# interpreter, device, account config or real outcome is used.
"""
import contextlib
import copy
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    upstream, input_file, output_file = (Path(value).resolve() for value in sys.argv[1:])
    os.chdir(output_file.parent)
    sys.path.insert(0, str(upstream))
    with open(os.devnull, "w", encoding="utf-8") as quiet, contextlib.redirect_stdout(quiet):
        from module.exception import CampaignEnd, MapEnemyMoved, ScriptError
        from module.map.fleet import Fleet
        from module.logger import logger

        logger.setLevel("CRITICAL")
        results = []
        modules = {}
        for scenario in json.loads(input_file.read_text(encoding="utf-8")):
            module_name = "campaign." + scenario["rule"].replace("/", ".")
            if module_name not in modules:
                modules[module_name] = importlib.import_module(module_name)
            source = modules[module_name]

            class Probe(source.Campaign):
                def record(self, operation):
                    self.calls.append(operation)
                    if operation == scenario["signalOperation"]:
                        self.signals += 1
                        if self.signals <= scenario["signalCount"]:
                            signal = scenario["signal"]
                            if signal == "moved":
                                raise MapEnemyMoved()
                            if signal == "moved_after_battle":
                                self.battle_count += 1
                                raise MapEnemyMoved()
                            if signal == "ended":
                                raise CampaignEnd()
                            if signal == "error":
                                raise IOError("synthetic device failure")
                    combat = operation in ("clear_enemy", "clear_boss", "brute_clear_boss")
                    if combat and scenario["advance"]:
                        self.battle_count += 1
                    return operation == scenario["trueOperation"] or (combat and scenario["combatReturn"])

                def clear_enemy(self): return self.record("clear_enemy")
                def clear_boss(self): return self.record("clear_boss")
                def brute_clear_boss(self): return self.record("brute_clear_boss")
                def fleet_2_break_siren_caught(self): return self.record("fleet_2_break_siren_caught")
                def clear_all_mystery(self): return self.record("clear_all_mystery")
                def pick_up_ammo(self): return self.record("pick_up_ammo")
                def clear_siren(self): return self.record("clear_siren")
                def clear_any_enemy(self, sort):
                    assert sort == ("cost_2",), sort
                    return self.record("clear_any_enemy:cost_2")
                def clear_bouncing_enemy(self): return self.record("clear_bouncing_enemy")
                def clear_mechanism(self): return self.record("clear_mechanism")
                def enter_map(self, entrance, mode):
                    assert entrance is self.ENTRANCE
                    self.map_is_auto_search = scenario["autoSearch"]
                    self.record("enter_map:" + mode)
                def handle_map_fleet_lock(self): self.record("handle_map_fleet_lock")
                def map_init(self, definition):
                    assert definition is self.MAP
                    self.record("map_init")
                def lv_reset(self): self.record("lv_reset")
                def lv_get(self): self.record("lv_get")
                def auto_search_moving(self): self.record("auto_search_moving")
                def auto_search_combat(self, fleet_index): self.record(f"auto_search_combat:{fleet_index}")
                def withdraw(self): self.record("withdraw")

            probe = object.__new__(Probe)
            probe.calls, probe.signals = [], 0
            probe.config = SimpleNamespace(POOR_MAP_DATA=scenario["poor"],
                MAP_CLEAR_ALL_THIS_TIME=scenario["clearAll"], MAP_HAS_MOVABLE_NORMAL_ENEMY=scenario["movable"],
                Error_HandleError=scenario["handleError"], Campaign_Mode="normal")
            probe.battle_count = scenario["battleCount"]
            probe.map = copy.deepcopy(source.MAP)
            grid = next(iter(probe.map))
            grid.is_boss = scenario["cells"] in ("boss", "boss_enemy")
            grid.is_enemy = scenario["cells"] in ("enemy", "boss_enemy")
            grid.is_siren = scenario["cells"] == "siren"
            grid.is_fortress = scenario["cells"] == "fortress"
            probe.ENTRANCE = SimpleNamespace(area=None, button=(0, 0, 0, 0))
            probe.emotion = SimpleNamespace(check_reduce=lambda amount: probe.record(f"check_emotion:{amount}"))
            probe.fleet_show_index = 1
            probe.map_is_auto_search = False
            value, error = None, None
            original_refocus = Fleet.handle_boss_appear_refocus

            def refocus(instance, preset=None):
                instance.record("refocus:null" if preset is None else f"refocus:{preset[0]},{preset[1]}")

            try:
                Fleet.handle_boss_appear_refocus = refocus
                operation = scenario["operation"]
                if operation == "dispatch": value = probe.battle_function()
                elif operation == "execute": value = probe.execute_a_battle()
                elif operation == "run": value = probe.run()
                elif operation == "refocus": value = probe.handle_boss_appear_refocus()
                else: raise ValueError(operation)
            except CampaignEnd:
                error = "CampaignEnd"
            except ScriptError:
                error = "ScriptError"
            except IOError:
                error = "IOError"
            finally:
                Fleet.handle_boss_appear_refocus = original_refocus
            results.append(dict(calls=probe.calls, value=value, exception=error, battleCount=probe.battle_count))
    output_file.write_text(json.dumps(results, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
