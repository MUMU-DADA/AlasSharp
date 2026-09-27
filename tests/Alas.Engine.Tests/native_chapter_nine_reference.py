"""Actual chapter-nine dynamic weight changes and target selection; no device or business bridge."""
import contextlib
import json
import os
from pathlib import Path
import sys


def main():
    root, output = (Path(value).resolve() for value in sys.argv[1:])
    sys.path.insert(0, str(root))
    os.chdir(root)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from campaign.campaign_main import campaign_9_2 as source
        from module.config.config_manual import ManualConfig
        from module.base.utils import node2location as loc, location2node as node
        from module.logger import logger
        logger.setLevel('CRITICAL')
        class Config(source.Config, ManualConfig):
            FLEET_2 = 2
            FLEET_BOSS = 2
            MAP_HAS_AMBUSH = False
            MAP_CLEAR_ALL_THIS_TIME = False
            EnemyPriority_EnemyScaleBalanceWeight = 'default'
        class Probe(source.Campaign):
            def clear_chosen_enemy(self, grid): self.target = node(grid.location)
        probe = object.__new__(Probe)
        probe.config = Config()
        probe.map = source.MAP
        probe.map.load_map_data()
        probe.map.grid_connection_initial()
        cases = []
        for second in ['D5', 'G5', 'F4', 'D5', 'F5', 'G5', None]:
            probe.map.reset()
            probe.fleet_1_location = loc('C1')
            probe.fleet_2_location = loc(second) if second else ()
            probe.fleet_current_index = 1
            probe.map[loc('C2')].is_enemy = probe.map[loc('F1')].is_enemy = True
            probe.find_path_initial()
            probe.target = None
            value = probe.battle_0()
            cases.append(dict(second=second, value=value, target=probe.target,
                weights=[grid.weight for grid in probe.map]))
    output.write_text(json.dumps(cases), encoding='utf-8')


if __name__ == '__main__': main()
