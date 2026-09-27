"""Actual fleet_2_step_on and road/mystery selection; synthetic goto commits, no device."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output = [Path(p).resolve() for p in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from module.map.map import Map
        from module.map.map_base import CampaignMap
        from module.map.map_grids import RoadGrids, SelectedGrids
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        logger.setLevel('CRITICAL')
        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            class Replay(Map):
                def __init__(self, tie_choice=0):
                    self.tie_choice, self.road_ties, self.in_road_selection = tie_choice, 1, False
                    self.config = SimpleNamespace(FLEET_2=sample['second'], FLEET_BOSS=1, MAP_HAS_AMBUSH=False,
                        MAP_HAS_FORTRESS=False, MAP_CLEAR_ALL_THIS_TIME=False, EnemyPriority_EnemyScaleBalanceWeight=sample['priority'])
                    self.map = CampaignMap('offline-position')
                    self.map.shape, self.map.map_data = 'C3', '-- -- --\n-- -- --\n-- -- --'
                    self.map.load_map_data()
                    self.map.grid_connection_initial()
                    for i, grid in enumerate(self.map):
                        grid.is_enemy = bool(sample['enemies'] & (1 << i))
                        grid.is_mystery = bool(sample['mysteries'] & (1 << i))
                        grid.is_cleared = bool(sample['cleared'] & (1 << i))
                        grid.enemy_scale = i % 3 + 1
                        grid.weight = sample['weights'][i]
                    self.fleet_1_location, self.fleet_2_location = loc('A1'), loc('C3')
                    self.fleet_current_index = sample['active']
                    self.find_path_initial()
                    self.trace, self.battles, self.mysteries = [], 0, 0

                def fleet_ensure(self, index):
                    self.trace.append(f'switch:{index}')
                    self.fleet_current_index = index
                    self.find_path_initial()

                def goto(self, grid, **kwargs):
                    self.trace.append(f'goto:{self.fleet_current_index}:{node(grid.location)}')
                    self.battles += int(grid.is_enemy)
                    self.mysteries += int(grid.is_mystery)
                    self.map[self.fleet_current].is_fleet = False
                    grid.wipe_out()
                    grid.is_fleet = True
                    self.fleet_current = grid.location
                    self.find_path_initial()

                def clear_chosen_enemy(self, grid): self.goto(grid)
                def clear_chosen_mystery(self, grid): self.goto(grid)

                def clear_roadblocks(self, roads, **kwargs):
                    self.in_road_selection = True
                    try:
                        return super().clear_roadblocks(roads, **kwargs)
                    finally:
                        self.in_road_selection = False

                def select_grids(self, grids, **kwargs):
                    selected = super().select_grids(grids, **kwargs)
                    if self.in_road_selection and selected:
                        # Native road union uses a set. Its equal weight/cost order is not
                        # a semantic priority. Enumerate all tied winners, then run the
                        # actual downstream mystery selection for each resulting state.
                        best = selected[0]
                        tied = [grid for grid in selected if (grid.weight, grid.cost) == (best.weight, best.cost)]
                        self.road_ties = len(tied)
                        winner = tied[self.tie_choice]
                        return SelectedGrids([winner] + [grid for grid in selected if grid != winner])
                    return selected

            alternatives, tie_choice = [], 0
            while True:
                replay = Replay(tie_choice)
                cells = SelectedGrids([replay.map[loc(cell)] for cell in sample['cells']])
                roads = [RoadGrids([[replay.map[loc(cell)] for cell in group] for group in road]) for road in sample['roads']]
                value = replay.fleet_2_step_on(cells, roads)
                alternatives.append(dict(value=value, trace=replay.trace, active=replay.fleet_current_index,
                    fleet1=node(replay.fleet_1_location), fleet2=node(replay.fleet_2_location), battles=replay.battles, mysteries=replay.mysteries))
                tie_choice += 1
                if tie_choice >= replay.road_ties:
                    break
            results.append(alternatives)
    output.write_text(json.dumps(dict(results=results, source=hashlib.sha256((root / 'module/map/map.py').read_bytes()).hexdigest())), encoding='utf-8')


if __name__ == '__main__': main()
