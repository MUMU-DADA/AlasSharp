"""Offline oracle for actual chapter-14 pickup methods and map-data overrides. No device."""
import contextlib
import copy
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys


def main():
    root, output = (Path(value).resolve() for value in sys.argv[1:])
    sys.path.insert(0, str(root))
    os.chdir(output.parent)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from campaign.campaign_main.campaign_14_base import CampaignBase
        from module.base.utils import node2location as loc, location2node as node
        from module.config.config_manual import ManualConfig
        from module.exception import MapEnemyMoved
        from module.map.map_base import CampaignMap
        from module.logger import logger
        logger.setLevel('CRITICAL')
        pickups = []
        for kind in ['flare', 'light_house']:
            for accessible in [False, True]:
                for picked in [False, True]:
                    for failure in ['', 'goto', 'moved', 'wait']:
                        class Probe(CampaignBase):
                            def goto(self, grid):
                                self.trace.append('goto:' + node(grid.location))
                                if failure == 'goto': raise IOError('synthetic')
                                if failure == 'moved': raise MapEnemyMoved()
                            def ensure_no_info_bar(self):
                                self.trace.append('wait')
                                if failure == 'wait': raise IOError('synthetic')
                        probe = object.__new__(Probe)
                        probe.map = CampaignMap('offline-pickups')
                        probe.map.shape, probe.map.map_data = 'B1', 'SP MM'
                        probe.map.load_map_data()
                        grid = probe.map[loc('B1')]
                        grid.cost = 0 if accessible else 9999
                        probe.picked_flare = [grid] if picked and kind == 'flare' else []
                        probe.picked_light_house = [grid] if picked and kind == 'light_house' else []
                        probe.trace = []
                        error, value = None, None
                        try: value = getattr(probe, 'pick_up_' + kind)(grid)
                        except IOError: error = 'IOError'
                        except MapEnemyMoved: error = 'MapEnemyMoved'
                        pickups.append(dict(kind=kind, accessible=accessible, picked=picked, failure=failure,
                            value=value, error=error, trace=probe.trace, flare=grid.is_flare,
                            flares=[node(g.location) for g in probe.picked_flare],
                            lights=[node(g.location) for g in probe.picked_light_house]))
        maps = []
        for stage in range(1, 5):
            module = importlib.import_module(f'campaign.campaign_main.campaign_14_{stage}')
            for clear in [False, True]:
                class Config(module.Config, ManualConfig): pass
                class Probe(module.Campaign):
                    def handle_clear_mode_config_cover(self): pass
                probe = object.__new__(Probe)
                probe.config, probe.map_is_clear_mode = Config(), clear
                probe.picked_flare, probe.picked_light_house = [True], [True]
                probe.map_data_init(copy.deepcopy(module.MAP))
                maps.append(dict(id=f'campaign_main/campaign_14_{stage}', clear=clear,
                    cells=[dict(cell=node(g.location), enemy=g.may_enemy, ambush=g.may_ambush) for g in probe.map],
                    flares=len(probe.picked_flare), lights=len(probe.picked_light_house)))
        source = 'campaign/campaign_main/campaign_14_base.py'
        sources = {source: hashlib.sha256((root / source).read_bytes()).hexdigest()}
    output.write_text(json.dumps(dict(pickups=pickups, maps=maps, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
