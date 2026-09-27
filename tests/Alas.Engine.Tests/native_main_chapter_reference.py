"""Offline compiled-chapter truth: actual inherited Config, Campaign attributes and overlay methods."""
import contextlib
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
    sys.path.insert(0, str(root))
    os.chdir(root)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.config.server as server
        server.server = server_name
        from module.config.config_manual import ManualConfig
        import module.base.timer as timers
        from module.handler.assets import MAP_AIR_RAID, MAP_AMBUSH
        from module.base.utils import get_color
        from module.logger import logger
        logger.setLevel('CRITICAL')
        names = ['FLEET_BOSS', 'MAP_MYSTERY_HAS_CARRIER', 'INTERNAL_LINES_HOUGHLINES_THRESHOLD',
            'EDGE_LINES_HOUGHLINES_THRESHOLD', 'HOMO_EDGE_HOUGHLINES_THRESHOLD', 'COINCIDENT_POINT_ENCOURAGE_DISTANCE',
            'INTERNAL_LINES_FIND_PEAKS_PARAMETERS', 'EDGE_LINES_FIND_PEAKS_PARAMETERS', 'HOMO_CANNY_THRESHOLD',
            'HOMO_EDGE_COLOR_RANGE', 'MID_DIFF_RANGE_H', 'MID_DIFF_RANGE_V']
        attribute_names = ['MAP_AMBUSH_OVERLAY_TRANSPARENCY_THRESHOLD', 'MAP_AIR_RAID_OVERLAY_TRANSPARENCY_THRESHOLD',
            'MAP_ENEMY_SEARCHING_OVERLAY_TRANSPARENCY_THRESHOLD']
        baseline = np.full((720, 1280, 3), 99, np.uint8)
        def save(name, image):
            filename = f'{server_name}-{name}.png'
            assert cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
            return filename
        initial = save('baseline', baseline)
        fixtures = []
        for kind, asset in [('air', MAP_AIR_RAID), ('ambush', MAP_AMBUSH)]:
            for red in [99, 135, 136, 137, 143, 144, 150, 151, 158, 159, 200]:
                image = baseline.copy()
                x, y, right, bottom = asset.area
                image[y:bottom, x:right, 0] = red
                fixtures.append((image, dict(image=save(f'{kind}-{red}', image),
                    airRed=float(get_color(image, MAP_AIR_RAID.area)[0]),
                    ambushRed=float(get_color(image, MAP_AMBUSH.area)[0]))))
        chapters = []
        for chapter in ([int(value) for value in sys.argv[4:]] or [4, 5]):
            for stage in range(1, 5):
                module = importlib.import_module(f'campaign.campaign_main.campaign_{chapter}_{stage}')
                class Config(module.Config, ManualConfig): pass
                probe = object.__new__(module.Campaign)
                probe.device = SimpleNamespace(image=baseline)
                probe.ambush_color_initial()
                cases = []
                for image, fixture in fixtures:
                    probe.device.image = image
                    air, ambush = bool(probe._air_raid_appear()), bool(probe._ambush_appear())
                    now, frames = 100., 0
                    timers.time = lambda: now
                    def screenshot():
                        nonlocal now, frames
                        frames += 1
                        now += .25
                        assert frames <= 16
                        probe.device.image = image if frames <= 2 else baseline
                    probe.device.screenshot = screenshot
                    probe._handle_air_raid()
                    cases.append(dict(**fixture, air=air, ambush=ambush, waitFrames=frames,
                        encounter='AirRaid' if air else 'Ambush' if ambush else 'None'))
                chapter_names = names + (['SUBMARINE'] if chapter >= 7 else [])
                if chapter >= 8:
                    chapter_names += ['MAP_SWIPE_MULTIPLY', 'MAP_SWIPE_MULTIPLY_MINITOUCH', 'MAP_SWIPE_MULTIPLY_MAATOUCH']
                if chapter >= 9:
                    chapter_names += ['HOMO_STORAGE', 'MAP_ENSURE_EDGE_INSIGHT_CORNER', 'MAP_HAS_MYSTERY']
                if chapter >= 10:
                    chapter_names += ['DETECTION_BACKEND']
                entry = dict(id=f'campaign_main/campaign_{chapter}_{stage}', config={name: getattr(Config(), name, None) for name in chapter_names},
                    attributes={name: getattr(module.Campaign, name) for name in attribute_names}, cases=cases)
                if chapter == 8 and stage == 1:
                    from module.base.utils import node2location as loc, location2node as node
                    class BossRoad(module.Campaign):
                        def clear_chosen_enemy(self, grid):
                            entry['bossRoad'] = dict(target=node(grid.location), fleet=self.fleet_current_index)
                    road = object.__new__(BossRoad)
                    road.config = Config()
                    road.config.FLEET_2, road.config.FLEET_BOSS, road.config.MAP_HAS_AMBUSH = 2, 2, False
                    road.map = module.MAP
                    road.map.reset()
                    road.map.load_map_data()
                    road.map.grid_connection_initial()
                    road.fleet_1_location, road.fleet_2_location, road.fleet_current_index = loc('F2'), loc('A2'), 1
                    for cell in ['G1', 'G2', 'G3']: road.map[loc(cell)].is_enemy = True
                    road.map[loc('H1')].is_boss = True
                    road.find_path_initial()
                    assert road.battle_4()
                chapters.append(entry)
        sources = {p: hashlib.sha256((root / p).read_bytes()).hexdigest()
            for p in ['module/handler/ambush.py', 'module/handler/enemy_searching.py', 'module/config/config_manual.py']}
    output.write_text(json.dumps(dict(baseline=initial, chapters=chapters, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
