"""Run upstream submarine selection and option semantics with synthetic I/O; never accesses a device."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, source, output = [Path(value).resolve() for value in sys.argv[1:4]]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        from module.map.fleet import Fleet
        from module.map.map_base import CampaignMap, location2node
        from module.map.utils import location_ensure
        from module.handler.auto_search import AutoSearchHandler, AUTO_SEARCH_SETTINGS
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class Replay(Fleet):
            def __init__(self, sample):
                self.config = SimpleNamespace(SUBMARINE=sample['fleet'], Submarine_Mode=sample['mode'],
                    Submarine_DistanceToBoss=sample['distance'])
                self.map = CampaignMap()
                self.map.shape = sample['shape']
                self.map.map_data = sample['tiles']
                self.map.grid_connection_initial()
                self.fleet_submarine_location = location_ensure(sample['origin'])
                self.target = None
                self.restored = 0
            def find_path_initial(self):
                self.restored += 1
            def submarine_goto(self, location):
                self.target = location2node(location)
                return True

        results = []
        for sample in json.loads(source.read_text(encoding='utf-8')):
            replay = Replay(sample)
            moved = replay.submarine_move_near_boss(sample['boss'])
            results.append(dict(target=replay.target, restored=replay.restored,
                ordinary=replay._submarine_mode('combat'), boss=replay._submarine_mode('combat_boss'),
                moved=bool(moved)))

        class Settings(AutoSearchHandler):
            def __init__(self, mask):
                self.mask, self.clicks = mask, []
                self.device = SimpleNamespace(click=lambda button: self.clicks.append(button.name))
            def image_color_count(self, area, **kwargs):
                assert kwargs == dict(color=(156, 255, 82), threshold=30, count=20)
                return bool(self.mask & (1 << next(i for i, button in enumerate(AUTO_SEARCH_SETTINGS) if button.button == area)))
        settings = []
        for mask in range(64):
            replay = Settings(mask)
            ready = replay._auto_search_set_click('sub_standby')
            settings.append(dict(mask=mask, ready=ready, clicks=replay.clicks))
        sources = {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in
            ['module/map/fleet.py', 'module/handler/auto_search.py', 'module/handler/strategy.py']}
    output.write_text(json.dumps(dict(results=results, settings=settings, sources=sources)), encoding='utf-8')


if __name__ == '__main__':
    main()
