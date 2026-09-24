"""Fresh native task paths must initialize existing campaign compatibility.

Real loader/config and CampaignBase.run; synthetic fleet pixels, camera frames
and inert battle endpoints. No emulator, account reads or completion claims.
"""
from __future__ import annotations

import copy
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'tools'), str(ROOT / 'tools/diagnostics')]
sys.stdout.reconfigure(encoding='utf-8', errors='replace')


def exercise():
    import numpy as np
    from PIL import Image
    import alas_vision as av
    from module.base.base import ModuleBase
    from module.base.button import Button
    from module.campaign.campaign_base import CampaignBase
    from module.campaign.run import CampaignRun
    from module.config.config import AzurLaneConfig
    from module.map.map_fleet_preparation import FleetOperator
    import module.map.camera as camera_module
    from verify_s3_camera_compat import CameraHarness
    from s3_stub_campaign import NativeRunCampaign

    original_load, original_run = CampaignRun.load_campaign, CampaignBase.run
    original_color = Button.appear_on
    original_fleet = FleetOperator.bar_opened
    calls = []
    frame = np.zeros((246, 2, 3), dtype=np.uint8)
    frame[:70, -1, :] = 255
    fleet = SimpleNamespace(_bar=SimpleNamespace(button=(0, 0, 2, 246)),
                            main=SimpleNamespace(image_crop=lambda *a, **k: frame))
    assert not original_fleet(fleet)
    button = av._resolve('combat/BATTLE_STATUS_S')
    crop = np.asarray(Image.open(ROOT / 'tools/diagnostics/fixtures/combat_status_s_near_color.png').convert('RGB'))
    result_frame = np.zeros((720, 1280, 3), dtype=np.uint8)
    x1, y1, x2, y2 = button.area
    result_frame[y1:y2, x1:x2] = crop
    assert not button.appear_on(result_frame)
    config = AzurLaneConfig('template')
    device = SimpleNamespace(image=result_frame)
    checks = []

    def check(label, condition):
        checks.append((label, bool(condition)))

    # A task that never constructs/runs a campaign must not install its shims.
    with av.native_task_runtime():
        check('non-campaign task keeps fleet predicate', FleetOperator.bar_opened is original_fleet)
        check('non-campaign task keeps color predicate', Button.appear_on is original_color)
    check('idle task restores loader and run',
          CampaignRun.load_campaign is original_load and CampaignBase.run is original_run)

    with patch.object(ModuleBase, 'EARLY_OCR_IMPORT', True):
        expected_loader = CampaignRun(copy.deepcopy(config), device)
        expected_loader.load_campaign('campaign_2_2')
        with av.native_task_runtime():
            loader = CampaignRun(copy.deepcopy(config), device)
            loader.load_campaign('campaign_2_2')
            actual, expected = loader.campaign, expected_loader.campaign
            check('native loader preserves class and MAP identity', type(actual) is type(expected) and actual.MAP is expected.MAP)
            check('native loader preserves inherited Config', actual.config.INTERNAL_LINES_FIND_PEAKS_PARAMETERS ==
                  expected.config.INTERNAL_LINES_FIND_PEAKS_PARAMETERS)
            check('native fleet predicate sees short expanded bar', FleetOperator.bar_opened(fleet))
            frame[:] = 0
            check('closed bar remains closed', not FleetOperator.bar_opened(fleet))
            check('loader does not expand combat color scope', Button.appear_on is original_color)
            camera = CameraHarness([(0.4, 0.1), (0.5, 0.5)])
            try:
                with patch.object(camera_module, 'Timer', side_effect=camera.timer):
                    camera_module.Camera.update(camera, wait_swipe=True)
            except TypeError:
                check('first camera wait handles absent prior view', False)
            else:
                check('first camera wait handles absent prior view', camera.frames == 2 and camera.final_updates == 1)

            # Keep the native run method; substitute only emotion/map/device I/O.
            class CampaignFixture(CampaignBase):
                pass
            instance = object.__new__(CampaignFixture)
            fake = NativeRunCampaign()
            instance.__dict__.update(vars(fake))
            # Cached property descriptors are supplied via instance state.
            instance.__dict__['_map_battle'] = fake._map_battle
            for name in ('enter_map', 'handle_map_fleet_lock', 'map_init'):
                setattr(instance, name, getattr(fake, name))
            def battle():
                calls.append('battle')
                check('color compatibility only inside native campaign run', button.appear_on(result_frame))
                from module.exception import CampaignEnd
                raise CampaignEnd('fixture boundary')
            instance.execute_a_battle = battle
            check('original native run returns its own result', instance.run() is True)
            check('color restored after native run', Button.appear_on is original_color)
            def fail(*args, **kwargs):
                raise RuntimeError('fixture entry failure')
            instance.enter_map = fail
            try:
                instance.run()
            except RuntimeError:
                pass
            else:
                raise AssertionError('native failure swallowed')
            check('color restored after native failure', Button.appear_on is original_color)
        check('task restores original native method identities',
              CampaignRun.load_campaign is original_load and CampaignBase.run is original_run)
        try:
            with av.native_task_runtime():
                with av.native_task_runtime():
                    raise RuntimeError('fixture nested task failure')
        except RuntimeError:
            pass
        check('nested failure restores native methods and colors',
              CampaignRun.load_campaign is original_load and CampaignBase.run is original_run
              and Button.appear_on is original_color)
    check('exactly one synthetic battle endpoint', calls == ['battle'])
    for label, passed in checks:
        print(f'{"PASS" if passed else "FAIL"}: {label}')
    return int(not all(passed for _, passed in checks))


if __name__ == '__main__':
    raise SystemExit(exercise())
