"""Actual Map selectors and RoadGrids.combine; synthetic mystery arrivals only, no device."""
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
                def __init__(self):
                    self.config = SimpleNamespace(FLEET_2=2, MAP_HAS_AMBUSH=False, MAP_HAS_FORTRESS=False,
                        MAP_CLEAR_ALL_THIS_TIME=sample['clearAll'], EnemyPriority_EnemyScaleBalanceWeight=sample['priority'])
                    self.map = CampaignMap('offline-selection')
                    self.map.shape, self.map.map_data = 'C3', '-- -- --\n-- -- --\n-- -- --'
                    self.map.load_map_data()
                    self.map.grid_connection_initial()
                    for i, grid in enumerate(self.map):
                        grid.is_enemy = bool(sample['enemies'] & (1 << i))
                        grid.is_mystery = bool(sample['mysteries'] & (1 << i))
                        grid.is_cleared = bool(sample['cleared'] & (1 << i))
                        grid.enemy_scale = sample['scales'][i]
                        grid.weight = sample['weights'][i]
                    self.fleet_1_location, self.fleet_2_location = loc('A1'), loc('C3')
                    self.fleet_current_index = sample['active']
                    self.find_path_initial()
                    self.selected, self.mysteries = None, []

                def clear_chosen_enemy(self, grid): self.selected = node(grid.location)
                def clear_chosen_mystery(self, grid):
                    self.mysteries.append(node(grid.location))
                    self.map[self.fleet_current].is_fleet = False
                    grid.wipe_out()
                    grid.is_fleet = True
                    self.fleet_current = grid.location
                    self.find_path_initial()

            replay = Replay()
            roads = [RoadGrids([[replay.map[loc(cell)] for cell in group] for group in road]) for road in sample['roads']]
            combined = roads[0].combine(roads[1])
            groups = [sorted(node(grid.location) for grid in group) for group in combined.grids]
            options = dict(scale=tuple(sample['filter']), strongest=sample['strongest'], weakest=sample['weakest'])
            choices = []
            for call in [lambda: replay.clear_enemy(**options), lambda: replay.clear_roadblocks([combined], **options),
                         lambda: replay.clear_potential_roadblocks([combined], **options)]:
                replay.selected = None
                value = call()
                choices.append(dict(value=value, target=replay.selected))
            ignore = SelectedGrids([replay.map[loc(cell)] for cell in sample['ignore']])
            value = replay.clear_all_mystery(ignore=ignore, nearby=sample['nearby'])
            results.append(dict(groups=groups, choices=choices, mysteries=replay.mysteries,
                fleet=node(replay.fleet_current), value=value))
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in
            ['module/map/map.py', 'module/map/map_grids.py']}
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
