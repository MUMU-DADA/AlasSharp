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

                def selection_record(self, kwargs):
                    if kwargs:
                        self.record(f"enemy_selection:{','.join(str(v) for v in kwargs.get('scale', ()))}:{int(kwargs.get('strongest', False))}:{int(kwargs.get('weakest', False))}")
                def clear_enemy(self, **kwargs):
                    self.selection_record(kwargs)
                    return self.record("clear_enemy")
                def clear_chosen_enemy(self, grid, expected=None):
                    from module.base.utils import location2node
                    self.calls.append(f"clear_chosen_enemy:{location2node(grid.location)}:{expected or 'enemy'}")
                    return True
                def mob_move(self, location, target):
                    from module.base.utils import location2node
                    return self.record(f"mob_move:{location2node(location.location if hasattr(location, 'location') else location)}>{location2node(target.location if hasattr(target, 'location') else target)}")
                def clear_filter_enemy(self, string, preserve=0):
                    self.record(f'enemy_filter:{string}:{preserve}')
                    return self.record('clear_filter_enemy')
                def fleet_2_push_forward(self): return self.record("fleet_2_push_forward")
                def fleet_2_step_on(self, grids, roadblocks):
                    from module.base.utils import location2node
                    self.record('step_on:' + ','.join(location2node(grid.location) for grid in grids))
                    self.roads_record(roadblocks)
                    return self.record('fleet_2_step_on')
                def fleet_2_rescue(self, grid):
                    from module.base.utils import location2node
                    return self.record('fleet_2_rescue:' + location2node(grid.location))
                def check_accessibility(self, grid, fleet=None):
                    from module.base.utils import location2node
                    if fleet == 'boss': fleet = self.fleet_boss_index
                    self.record(f'check_access:{location2node(grid.location)}:{fleet}')
                    return scenario['accessible']
                @property
                def fleet_boss(self):
                    self.fleet_current_index = self.fleet_boss_index
                    self.record(f'fleet_boss:{self.fleet_boss_index}')
                    return self
                @property
                def fleet_2(self):
                    if self.config.FLEET_2:
                        self.fleet_current_index = 2
                        self.record('fleet_boss:2')
                    return self
                def roads_record(self, roads):
                    from module.base.utils import location2node
                    self.record('roads:' + '|'.join('/'.join(','.join(sorted({location2node(g.location) for g in group}))
                        for group in road.grids) for road in roads))
                def clear_roadblocks(self, roads, **kwargs):
                    self.roads_record(roads)
                    self.selection_record(kwargs)
                    return self.record('clear_roadblocks')
                def clear_potential_roadblocks(self, roads, **kwargs):
                    self.roads_record(roads)
                    self.selection_record(kwargs)
                    return self.record('clear_potential_roadblocks')
                def clear_boss(self): return self.record("clear_boss")
                def clear_first_roadblocks(self, roads, **kwargs):
                    self.roads_record(roads)
                    self.selection_record(kwargs)
                    return self.record('clear_first_roadblocks')
                def brute_clear_boss(self): return self.record("brute_clear_boss")
                def fleet_2_break_siren_caught(self): return self.record("fleet_2_break_siren_caught")
                def clear_all_mystery(self, **kwargs):
                    if kwargs:
                        from module.base.utils import location2node
                        ignore = kwargs.get('ignore')
                        ignored = 'null' if ignore is None else ','.join(location2node(g.location) for g in ignore)
                        self.record(f"mystery_selection:{int(kwargs.get('nearby', False))}:{ignored}")
                    result = self.record("clear_all_mystery")
                    self.mystery_count += scenario['collectedMysteries']
                    return result
                def clear_chosen_mystery(self, grid):
                    from module.base.utils import location2node
                    self.record('chosen_mystery:' + location2node(grid.location))
                    self.fleet_current = grid.location
                    grid.is_mystery = False
                def pick_up_ammo(self): return self.record("pick_up_ammo")
                def pick_up_flare(self, grid):
                    from module.base.utils import location2node
                    return self.record('pick_up_flare:' + location2node(grid.location))
                def pick_up_light_house(self, grid):
                    from module.base.utils import location2node
                    return self.record('pick_up_light_house:' + location2node(grid.location))
                def goto(self, grid):
                    from module.base.utils import location2node
                    self.record('goto:' + location2node(grid.location))
                @property
                def fleet_1(self):
                    self.fleet_current_index = 1
                    self.record('fleet_boss:1')
                    return self
                def switch_to(self): pass
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
                Error_HandleError=scenario["handleError"], Campaign_Mode="normal",
                FLEET_2=getattr(source.Config, 'FLEET_2', scenario.get('fleet2', 0)),
                FLEET_BOSS=getattr(source.Config, 'FLEET_BOSS', scenario.get('bossFleet') or (2 if scenario.get('fleet2') else 1)))
            probe.config.EnemyPriority_EnemyScaleBalanceWeight = 0
            probe.battle_count = scenario["battleCount"]
            probe.mystery_count = scenario['mysteryCount']
            from module.base.utils import node2location
            probe.fleet_1_location = node2location(scenario['firstFleet']) if scenario.get('firstFleet') else ()
            probe.fleet_2_location = node2location(scenario['secondFleet']) if scenario.get('secondFleet') else ()
            probe.fleet_current_index = 1
            probe.picked_flare = [True] if scenario.get('flarePicked', False) else []
            probe.picked_light_house = []
            # Chapter hooks reference module-level GridInfo objects, as real map_init does.
            source.MAP.reset()
            probe.map = copy.deepcopy(source.MAP)
            grid = next(iter(probe.map))
            grid.is_boss = scenario["cells"] in ("boss", "boss_enemy")
            grid.is_enemy = scenario["cells"] in ("enemy", "boss_enemy")
            grid.is_siren = scenario["cells"] == "siren"
            grid.is_fortress = scenario["cells"] == "fortress"
            for selected_map in (source.MAP, probe.map):
                if scenario['rule'].startswith('campaign_main/campaign_14_'):
                    for selected_grid in selected_map:
                        selected_grid.cost = 0 if scenario['accessible'] else 9999
                next(iter(selected_map)).enemy_scale = scenario.get('firstScale', 0)
                for cell in (scenario.get('mysteries') or '').split(','):
                    if cell: selected_map[node2location(cell)].is_mystery = True
            if scenario.get('bossCells'):
                from module.base.utils import node2location
                for cell in scenario['bossCells'].split(','):
                    probe.map[node2location(cell)].is_boss = True
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
            results.append(dict(calls=probe.calls, value=value, exception=error, battleCount=probe.battle_count,
                weights=[grid.weight for grid in probe.map] if scenario['rule'] == 'campaign_main/campaign_9_2' else None))
    output_file.write_text(json.dumps(results, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
