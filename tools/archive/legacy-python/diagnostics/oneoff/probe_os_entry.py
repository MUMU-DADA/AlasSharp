#!/usr/bin/env python3
"""Inspect upstream OS page and map criteria on one saved frame, without device actions."""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
CALLER_CWD = Path.cwd()
sys.path.insert(0, str(ROOT / 'tools'))

import alas_vision as vision  # noqa: E402
from module.os.globe_operation import GlobeOperation  # noqa: E402
from module.os_handler.enemy_searching import EnemySearchingHandler  # noqa: E402


def inspect(frame: Path, server: str) -> dict:
    vision.op_set_server({'server': server})
    vision.op_screenshot_load({'path': str(frame)})
    image = vision._require_image()
    fingerprint = hashlib.sha256(image.tobytes()).hexdigest()
    shim = vision._make_main_shim(image)

    page = vision.op_page_appear({'page': 'page_os'})
    zone_check = vision.op_button_match({
        'asset': 'ui/OS_CHECK', 'offset': (20, 20), 'probe_score': True,
    })
    wait_check = vision.op_appear_on({'asset': 'ui/OS_CHECK'})
    in_map = bool(EnemySearchingHandler.is_in_map(shim))
    in_globe = bool(GlobeOperation.is_in_globe(shim))
    auto_search = {
        name: vision.op_button_match({
            'asset': f'os_handler/{name}', 'offset': (5, 120),
        })['match']
        for name in ('AUTO_SEARCH_OS_MAP_OPTION_OFF',
                     'AUTO_SEARCH_OS_MAP_OPTION_OFF_DISABLED',
                     'AUTO_SEARCH_OS_MAP_OPTION_ON')
    }
    map_result = vision.op_map_detect({'mode': 'os'})
    if image is not vision._state['image'] or fingerprint != hashlib.sha256(image.tobytes()).hexdigest():
        raise RuntimeError('frame changed during inspection')

    return {
        'frame_sha256': fingerprint,
        'shape': list(image.shape),
        'page_os': bool(page['appear']),
        'os_check_zone': zone_check['match'],
        'os_check_score': zone_check['score'],
        'os_check_threshold': zone_check['similarity'],
        'os_check_wait': wait_check['appear'],
        'os_check_color_tolerance': wait_check['tolerance'],
        'os_check_color_threshold': wait_check['threshold'],
        'os_in_map': in_map,
        'os_in_globe': in_globe,
        'auto_search_controls': auto_search,
        'map_detected': map_result.get('detected'),
        'map_load': map_result.get('load'),
        'map_reason': map_result.get('reason'),
        'map_construct_error': map_result.get('construct_error'),
        'map_predict': map_result.get('predict'),
        'grid_count': map_result.get('grid_count'),
        'backend': map_result.get('backend'),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('frame', type=Path, help='saved screenshot to inspect')
    parser.add_argument('--server', choices=('cn', 'en', 'jp', 'tw'), default='cn')
    args = parser.parse_args()
    frame = (CALLER_CWD / args.frame).resolve()
    if not frame.is_file():
        parser.error('saved screenshot does not exist')
    print('OS_ENTRY_JSON=' + json.dumps(inspect(frame, args.server), ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
