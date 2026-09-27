"""Offline truth from actual Filter and Map.clear_filter_enemy; no device or game actions."""
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
        from module.map.map import Map, ENEMY_FILTER
        from module.map.map_base import CampaignMap
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        logger.setLevel('CRITICAL')
        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            class Replay(Map):
                def __init__(self):
                    self.config = SimpleNamespace(FLEET_2=2, MAP_HAS_AMBUSH=False,
                        MAP_HAS_SIREN=sample['sirenEnabled'], MAP_HAS_FORTRESS=False,
                        MAP_HAS_MOVABLE_NORMAL_ENEMY=sample['movable'], MAP_CLEAR_ALL_THIS_TIME=sample['clearAll'],
                        EnemyPriority_EnemyScaleBalanceWeight=sample['priority'])
                    self.map = CampaignMap('offline-enemy-filter')
                    self.map.shape, self.map.map_data = 'C3', '-- -- --\n-- -- --\n-- -- --'
                    self.map.load_map_data()
                    self.map.grid_connection_initial()
                    for i, grid in enumerate(self.map):
                        grid.is_enemy = bool(sample['enemies'] & (1 << i))
                        grid.is_boss = bool(sample['bosses'] & (1 << i))
                        grid.is_siren = bool(sample['sirens'] & (1 << i))
                        grid.enemy_scale = sample['scales'][i]
                        grid.enemy_genre = sample['genres'][i]
                        grid.weight = sample['weights'][i]
                    self.fleet_1_location, self.fleet_2_location = loc('A1'), loc('C3')
                    self.fleet_current_index = sample['active']
                    self.find_path_initial()
                    self.selected = None

                def clear_chosen_enemy(self, grid, expected=''): self.selected = node(grid.location)

            replay = Replay()
            ENEMY_FILTER.load(sample['expression'])
            grids = replay.map.select(is_enemy=True, is_accessible=True).sort('weight', 'cost')
            ordered = [node(grid.location) for grid in ENEMY_FILTER.apply(grids.grids)[sample['preserve']:]]
            value = replay.clear_filter_enemy(sample['expression'], sample['preserve'])
            results.append(dict(ordered=ordered, value=value, target=replay.selected,
                costs=[[grid.cost, grid.cost_1, grid.cost_2] for grid in replay.map]))
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in
            ['module/map/map.py', 'module/base/filter.py', 'module/map_detection/grid_info.py']}
    output.write_text(json.dumps(dict(results=results, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
