"""Offline native calibration/view and first edge gesture for stored chapter geometry."""
import contextlib
import importlib
import itertools
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        from module.config.config_manual import ManualConfig
        from module.handler.assets import IN_MAP
        from module.map.camera import Camera
        from module.map_detection.homography import Homography
        from module.map_detection.view import View
        from module.logger import logger
        logger.setLevel('CRITICAL')

        class FirstGesture(Exception): pass

        result = []
        for chapter, stage in [(9, 1), (10, 2), (11, 2), (11, 3)]:
            module = importlib.import_module(f'campaign.campaign_main.campaign_{chapter}_{stage}')
            class Config(module.Config, ManualConfig): pass
            config = Config()
            config.Scheduler_Command = 'Campaign'
            config.DEVICE_CONTROL_METHOD = 'adb'
            config.MAP_SWIPE_OPTIMIZE = False
            homography = Homography(config)
            homography.find_homography(*config.HOMO_STORAGE)
            width, height = homography.homo_size
            # Draw a regular lattice in calibrated space, then project it back into a screenshot.
            # Gray strokes avoid being mistaken for dark map boundaries by edge detection.
            lattice = np.full((height, width, 3), (180, 180, 180), dtype=np.uint8)
            for x in range(35, width, 140): cv2.line(lattice, (x, 0), (x, height - 1), (70, 70, 70), 3)
            for y in range(45, height, 140): cv2.line(lattice, (0, y), (width - 1, y), (70, 70, 70), 3)
            x, y, right, bottom = config.DETECTING_AREA
            image = np.full((720, 1280, 3), 180, dtype=np.uint8)
            image[y:bottom, x:right] = cv2.warpPerspective(lattice, homography.homo_invt, (right - x, bottom - y))
            x, y, right, bottom = IN_MAP.area
            image[y:bottom, x:right] = IN_MAP.color
            filename = f'chapter-{chapter}-{stage}-map.png'
            assert cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
            view = View(config)
            view.load(image)
            assert not any([view.left_edge, view.right_edge, view.lower_edge, view.upper_edge])
            gestures = []
            for draws in itertools.product([.25, .75], repeat=2):
                camera = object.__new__(Camera)
                camera.config, camera.view = config, view
                camera.camera = (9, 9)
                camera.map = SimpleNamespace(shape=(19, 19))
                camera._prev_view = camera._prev_swipe = None
                def swipe(vector, **kwargs):
                    gestures.append(vector.tolist())
                    raise FirstGesture()
                camera.device = SimpleNamespace(swipe_vector=swipe)
                original = np.random.uniform
                direction = iter(draws)
                np.random.uniform = lambda: next(direction)
                try:
                    Camera.ensure_edge_insight(camera)
                    raise AssertionError('Native edge recovery never swiped')
                except FirstGesture:
                    pass
                finally:
                    np.random.uniform = original
            result.append(dict(id=f'campaign_main/campaign_{chapter}_{stage}', image=filename,
                center=list(view.center_loca), offset=view.center_offset.tolist(), swipe=view.swipe_base.tolist(),
                grids=[dict(cell=grid.location, corners=grid.corner.tolist()) for grid in view], gestures=gestures))
    output.write_text(json.dumps(result), encoding='utf-8')


if __name__ == '__main__': main()
