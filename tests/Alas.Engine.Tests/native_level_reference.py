"""Actual native Level/LevelOcr oracle using synthetic images and no device actions."""
import contextlib
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, inputs, output, worker_path = [Path(arg).resolve() for arg in sys.argv[1:]]
    sys.path.insert(0, str(root))
    os.chdir(root)
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.config.server as servers
        import module.combat.level as levels
        from module.logger import logger
        logger.setLevel('CRITICAL')
        spec = importlib.util.spec_from_file_location('pixel_worker', worker_path)
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)

        class Replay(levels.Level):
            def __init__(self, limit=0, flag32=False, server='cn'):
                self.config = SimpleNamespace(SERVER=server, StopCondition_ReachLevel=limit,
                    STOP_IF_REACH_LV32=flag32, LV_TRIGGERED=False, LV32_TRIGGERED=False)
                self.device = SimpleNamespace(image=None)
                self.lv_reset()

        cases, pixel_count = [], 0
        rng = np.random.default_rng(33719)
        for source in json.loads(inputs.read_text(encoding='utf-8')):
            servers.server = source['server']
            replay = Replay(server=source['server'])
            areas = [b.area for b in replay._lv_grid().buttons]
            for actual, expected in zip(source['areas'], areas):
                assert actual == [expected[0], expected[1], expected[2]-expected[0], expected[3]-expected[1]]
            jp = source['server'] == 'jp'
            actor = levels.LevelOcr(replay._lv_grid().buttons, name='LevelOracle')
            for mode in range(9):
                image = np.zeros((720, 1280, 3), dtype=np.uint8)
                for slot, area in enumerate(areas):
                    x1, y1, x2, y2 = area
                    patch = np.full((19, x2-x1, 3), (33, 65, 115), dtype=np.uint8)
                    left = (45 if jp else 25) if mode == 5 else 1
                    if mode != 2:
                        patch[5:16, left:left+2] = 255
                        patch[14:16, left:left+7] = 255
                    text = ['31', '0', '119', '23', '99', '1'][slot]
                    cv2.putText(patch, text, (left+(23 if jp else 17), 16), cv2.FONT_HERSHEY_SIMPLEX,
                        .42, (255, 255, 255), 1, cv2.LINE_AA)
                    if mode == 1: patch = cv2.addWeighted(patch, (107+105+107)/3/255, patch, 0, 0)
                    if mode == 3: patch[8:19, :9] = (255, 40, 20)  # Repair icon below brightness test.
                    if mode == 4: patch[:5] = (107, 138, 189)
                    if mode == 6: patch = rng.integers(0, 256, patch.shape, dtype=np.uint8)
                    if mode == 7: patch[:] = 0
                    if mode == 8: patch[:] = (107, 105, 107)
                    expected = actor.pre_process(patch)
                    actual = worker.ocr_preprocess(patch, [255, 255, 255], 128, 'prefixcrop', source['prefix'])
                    assert np.array_equal(expected, actual), (source['server'], mode, slot, expected.shape, actual.shape)
                    pixel_count += 1
                    image[y1:y2, x1:x2] = patch
                name = f"level-{source['server']}-{mode}.png"
                cv2.imwrite(str(output.parent/name), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                cases.append(dict(server=source['server'], file=name, levels=actor.ocr(image)))

        servers.server = 'cn'
        original = levels.LevelOcr.ocr
        states = []
        pairs = [(0,32), (31,32), (31,34), (31,35), (33,35), (34,35), (35,36),
                 (118,120), (119,120), (120,120), (120,119), (5,32), (5,34), (5,35)]
        try:
            for limit in [0, 32, 35, 120]:
                for flag32 in [False, True]:
                    for slot in range(6):
                        for before, after in pairs:
                            replay = Replay(limit, flag32)
                            trace = []
                            for index, value in enumerate([before, after, max(0, after-1)]):
                                numbers = [0]*6
                                numbers[slot] = value
                                levels.LevelOcr.ocr = lambda self, image, values=numbers: values.copy()
                                returned = replay.lv_get(after_battle=index>0)
                                trace.append(dict(input=numbers, afterBattle=index>0, returned=returned,
                                    levels=replay.lv, before=replay._lv_before_battle,
                                    triggered=replay.config.LV_TRIGGERED, triggered32=replay.config.LV32_TRIGGERED))
                            replay.lv_reset()
                            states.append(dict(limit=limit, flag32=flag32, trace=trace,
                                resetLevels=replay.lv, resetBefore=replay._lv_before_battle,
                                resetTriggered=replay.config.LV_TRIGGERED, reset32=replay.config.LV32_TRIGGERED))
        finally:
            levels.LevelOcr.ocr = original
        sources = {p: hashlib.sha256((root/p).read_bytes()).hexdigest() for p in ['module/combat/level.py']}
    output.write_text(json.dumps(dict(cases=cases, states=states, pixels=pixel_count, sources=sources)), encoding='utf-8')


if __name__ == '__main__': main()
