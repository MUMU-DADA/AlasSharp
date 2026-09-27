"""Offline actual Map fleet advance/rescue methods; device actions are synthetic terminals."""
import contextlib
import hashlib
import importlib
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
        from module.base.utils import node2location as loc, location2node as node
        from module.config.config_manual import ManualConfig
        from module.logger import logger
        logger.setLevel('CRITICAL')
        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            class Replay(Map):
                def __init__(self):
                    self.config = SimpleNamespace(FLEET_BOSS=sample['boss'], FLEET_2=sample['second'],
                        MAP_HAS_AMBUSH=False, MAP_HAS_FORTRESS=False)
                    self.map = CampaignMap('offline-second-fleet')
                    self.map.shape, self.map.map_data = 'C3', '-- -- --\n-- -- --\n-- -- --'
                    self.map.load_map_data()
                    self.map.grid_connection_initial()
                    for i, cell in enumerate(self.map):
                        cell.is_enemy = bool(sample['enemies'] & (1 << i))
                        cell.is_land = bool(sample['land'] & (1 << i))
                        cell.weight = sample['weights'][i]
                    self.fleet_1_location, self.fleet_2_location = loc('A1'), loc('C3')
                    self.fleet_current_index = sample['active']
                    self.find_path_initial()
                    self.trace, self.selected, self.hypothetical_costs = [], None, False

                def fleet_ensure(self, index):
                    self.trace.append(f'switch:{index}')
                    self.fleet_current_index = index
                    self.find_path_initial()

                def goto(self, grid):
                    self.selected = node(grid.location)
                    self.trace.append('move:' + self.selected)
                    self.fleet_current = grid.location

                def clear_chosen_enemy(self, grid):
                    self.selected = node(grid.location)
                    self.trace.append('fight:' + self.selected)

                def brute_find_roadblocks(self, grid, fleet=None):
                    # Native may leave hypothetical costs (or the query fleet on failure).
                    # Preserve the native chosen set, but restore actual selection costs,
                    # as required by the Engine's existing immutable-search boundary.
                    active = self.fleet_current_index
                    selected = super().brute_find_roadblocks(grid, fleet)
                    before = [(cell.cost, cell.cost_1, cell.cost_2) for cell in self.map]
                    self.fleet_current_index = active
                    self.find_path_initial()
                    self.hypothetical_costs = before != [(cell.cost, cell.cost_1, cell.cost_2) for cell in self.map]
                    return selected

            replay = Replay()
            result = replay.fleet_2_push_forward() if sample['operation'] == 'push' else replay.fleet_2_rescue(replay.map[loc(sample['target'])])
            results.append(dict(result=result, trace=replay.trace, selected=replay.selected,
                active=replay.fleet_current_index, hypotheticalCosts=replay.hypothetical_costs))
        configs = []
        names = ['FLEET_BOSS', 'MAP_MYSTERY_HAS_CARRIER', 'INTERNAL_LINES_HOUGHLINES_THRESHOLD',
            'EDGE_LINES_HOUGHLINES_THRESHOLD', 'HOMO_EDGE_HOUGHLINES_THRESHOLD', 'COINCIDENT_POINT_ENCOURAGE_DISTANCE',
            'INTERNAL_LINES_FIND_PEAKS_PARAMETERS', 'EDGE_LINES_FIND_PEAKS_PARAMETERS', 'HOMO_CANNY_THRESHOLD',
            'HOMO_EDGE_COLOR_RANGE', 'MID_DIFF_RANGE_H', 'MID_DIFF_RANGE_V']
        for i in range(1, 5):
            module = importlib.import_module(f'campaign.campaign_main.campaign_3_{i}')
            class Config(module.Config, ManualConfig): pass
            configs.append({name: getattr(Config(), name) for name in names})
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest()
            for p in ['module/map/map.py', 'module/map/fleet.py']}
    output.write_text(json.dumps(dict(results=results, configs=configs, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
