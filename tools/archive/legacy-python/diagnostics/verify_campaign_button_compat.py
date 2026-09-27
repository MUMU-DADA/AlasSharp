#!/usr/bin/env python3
"""Check native-sortie button recognition against a redacted live pixel crop."""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))

import alas_vision as av  # noqa: E402
from module.base.button import Button  # noqa: E402
from module.base.utils import color_similarity, get_color  # noqa: E402


def main() -> int:
    button = av._resolve('combat/BATTLE_STATUS_S')
    crop = np.asarray(Image.open(ROOT / 'tools/diagnostics/fixtures/combat_status_s_near_color.png')
                      .convert('RGB'))
    area = button.area
    image = np.zeros((720, 1280, 3), dtype=np.uint8)
    image[area[1]:area[3], area[0]:area[2]] = crop
    original = Button.appear_on
    tolerance = float(color_similarity(get_color(image, area), button.color))
    checks = {
        'fixture is a de-identified fixed-area crop': crop.shape ==
            (area[3] - area[1], area[2] - area[0], 3),
        'native color misses while native template matches':
            10 <= tolerance < 20 and not button.appear_on(image)
            and button.match(image, offset=(0, 0), similarity=0.85),
    }
    with av.campaign_button_color_compat():
        checks['near-color template corroborates native sortie button'] = button.appear_on(image)
        checks['explicit custom threshold keeps upstream behavior'] = not button.appear_on(
            image, threshold=9)
        for name in ('menu_01.png', 'map_event.png', 'inmap_7-1.png'):
            negative = np.asarray(Image.open(ROOT / 'data/fixtures' / name).convert('RGB'))
            checks[f'no result on {name}'] = not button.appear_on(negative)
    checks['native method restored after sortie'] = Button.appear_on is original
    try:
        with av.campaign_button_color_compat():
            raise RuntimeError('fixture failure')
    except RuntimeError:
        pass
    checks['native method restored on error'] = Button.appear_on is original
    for label, passed in checks.items():
        print(f'{"PASS" if passed else "FAIL"}: {label}')
    return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    sys.exit(main())
