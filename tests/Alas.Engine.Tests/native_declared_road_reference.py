"""Actual RoadGrids/Map selection and chapter Config oracle with synthetic observations only."""
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
        from module.map.map_grids import RoadGrids
        from module.base.utils import node2location as loc, location2node as node
        from module.config.config_manual import ManualConfig
        from module.config.config import AzurLaneConfig
        from module.map.fleet import Fleet
        from module.logger import logger
        logger.setLevel('CRITICAL')
        results = []
        for sample in json.loads(inputs.read_text(encoding='utf-8')):
            class Replay(Map):
                def __init__(self):
                    self.config = SimpleNamespace(EnemyPriority_EnemyScaleBalanceWeight=sample['priority'],
                        MAP_CLEAR_ALL_THIS_TIME=sample['clearAll'], MAP_HAS_AMBUSH=False, MAP_HAS_FORTRESS=False,
                        FLEET_BOSS=sample['bossFleet'], FLEET_2=2)
                    self.map = CampaignMap('offline-roads')
                    self.map.shape, self.map.map_data = 'C3', '-- -- --\n-- -- --\n-- -- --'
                    self.map.load_map_data()
                    self.map.grid_connection_initial()
                    for index, cell in enumerate(self.map):
                        cell.is_enemy = bool(sample['enemy'] & (1 << index))
                        cell.is_cleared = bool(sample['cleared'] & (1 << index))
                        cell.enemy_scale = sample['scales'][index]
                        cell.weight = sample['weights'][index]
                    self.fleet_1_location, self.fleet_2_location = loc('A1'), loc('C3')
                    self.fleet_current_index = sample['active']
                    self.find_path_initial()
                    self.selected = None
                def clear_chosen_enemy(self, grid): self.selected = node(grid.location)
            replay = Replay()
            roads = [RoadGrids([[replay.map[loc(cell)] for cell in group] for group in road]) for road in sample['roads']]
            selected = sorted({node(cell.location) for road in roads for cell in
                (road.potential_roadblocks() if sample['potential'] else road.roadblocks())})
            access = [[replay.check_accessibility(cell, fleet=fleet) for cell in replay.map] for fleet in [1, 2]]
            success = replay.clear_potential_roadblocks(roads) if sample['potential'] else replay.clear_roadblocks(roads)
            results.append(dict(selected=selected, access=access, target=replay.selected, success=success))
        configs = []
        names = ['FLEET_BOSS', 'INTERNAL_LINES_HOUGHLINES_THRESHOLD', 'EDGE_LINES_HOUGHLINES_THRESHOLD',
            'HOMO_EDGE_HOUGHLINES_THRESHOLD', 'COINCIDENT_POINT_ENCOURAGE_DISTANCE', 'INTERNAL_LINES_FIND_PEAKS_PARAMETERS',
            'EDGE_LINES_FIND_PEAKS_PARAMETERS', 'HOMO_CANNY_THRESHOLD', 'HOMO_EDGE_COLOR_RANGE', 'MID_DIFF_RANGE_H', 'MID_DIFF_RANGE_V']
        for index in range(1, 5):
            module = importlib.import_module(f'campaign.campaign_main.campaign_2_{index}')
            class Config(module.Config, ManualConfig): pass
            configs.append({name: getattr(Config(), name) for name in names})
        boss_roles = []
        for order in ['fleet1_mob_fleet2_boss', 'fleet1_boss_fleet2_mob', 'fleet1_all_fleet2_standby', 'fleet1_standby_fleet2_all']:
            for second in [0, 2]:
                for override in [None, 1, 2]:
                    config = SimpleNamespace(Fleet_FleetOrder=order, Fleet_Fleet2=second, _fleet_boss=0)
                    if override is not None:
                        AzurLaneConfig.FLEET_BOSS.fset(config, override)
                    config.FLEET_BOSS = AzurLaneConfig.FLEET_BOSS.fget(config)
                    config.FLEET_2 = second
                    boss_roles.append(dict(order=order, second=second, override=override,
                        expected=Fleet.fleet_boss_index.fget(SimpleNamespace(config=config))))
        paths = ['module/map/map.py', 'module/map/map_grids.py', 'module/map/fleet.py', 'module/config/config.py']
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in paths}
    output.write_text(json.dumps(dict(results=results, configs=configs, bossRoles=boss_roles, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
