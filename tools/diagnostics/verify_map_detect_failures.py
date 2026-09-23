#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""离线注入 View 故障，核对 map_detect 的负样本与执行错误边界。"""
from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import alas_vision as vision  # noqa: E402
from module.map_detection import view as view_mod  # noqa: E402
from module.map_detection.view import MapDetectionError  # noqa: E402
from module.os_handler.enemy_searching import EnemySearchingHandler  # noqa: E402


class Config(SimpleNamespace):
    def merge(self, other):
        return self


class View:
    def __init__(self, config, **kwargs):
        self.grids = {(0, 0): object()}

    def load(self, image):
        pass

    def predict(self):
        pass


class ExplodingItems(dict):
    def items(self):
        raise RuntimeError('injected grid flag extraction failure')


def detect(view_class, mode='main', **options):
    config = Config(DETECTION_BACKEND='perspective',
                    INTERNAL_LINES_HOUGHLINES_THRESHOLD=1)
    with patch.object(view_mod, 'View', view_class), \
         patch.object(vision, '_require_image', return_value=object()), \
         patch.object(vision, '_map_config', return_value=config), \
         patch.object(vision, 'apply_numpy2_compat'), \
         patch.object(vision, 'apply_points_empty_compat'):
        return vision.op_map_detect({'mode': mode, **options})


def main():
    class NoGrid(View):
        def load(self, image):
            raise MapDetectionError('No map grids found')

    negative = detect(NoGrid)
    assert negative['load'] == 'negative' and negative['detected'] is False
    assert negative['reason'].startswith('MapDetectionError:')

    class ConstructError(View):
        def __init__(self, config, **kwargs):
            raise TypeError('injected constructor failure')

    constructed = detect(ConstructError)
    assert constructed['construct_error'].startswith('TypeError:')
    assert constructed.get('load') != 'negative'

    class LoadError(View):
        def load(self, image):
            raise TypeError('injected load failure')

    loaded = detect(LoadError)
    assert loaded['load'] == 'error' and loaded['detected'] is False
    assert loaded['reason'].startswith('TypeError:')

    class PredictError(View):
        def predict(self):
            raise RuntimeError('injected predict failure')

    predicted = detect(PredictError)
    assert predicted['load'] == 'ok' and predicted['predict'].startswith('RuntimeError:')
    assert predicted['detected'] is False and predicted['grid_count'] == 1

    class GridFlagsError(View):
        def __init__(self, config, **kwargs):
            self.grids = ExplodingItems({(0, 0): object()})

    flags = detect(GridFlagsError)
    assert flags['load'] == 'ok' and flags['predict'] == 'ok'
    assert flags['detected_raw'] is True and flags['detected'] is True
    assert flags['grid_flags_error'].startswith('RuntimeError:')
    assert flags['ships'] is None and flags['ship_tiles'] is None
    assert 'reason' not in flags

    no_ship_gate = detect(View)
    assert no_ship_gate['detected_raw'] is True and no_ship_gate['detected'] is False
    assert no_ship_gate['ships'] == 0 and '没有任何船标志' in no_ship_gate['reason']
    no_ship_gate_disabled = detect(View, require_ships=False)
    assert no_ship_gate_disabled['detected_raw'] is True
    assert no_ship_gate_disabled['detected'] is True and no_ship_gate_disabled['ships'] == 0

    mask_states = []
    with patch.object(vision, '_make_main_shim', return_value=object()), \
         patch.object(vision, 'apply_os_mask_compat'), \
         patch.object(vision, 'set_os_mask_mode', side_effect=mask_states.append), \
         patch.object(EnemySearchingHandler, 'is_in_map', return_value=True):
        os_constructed = detect(ConstructError, mode='os')
    assert os_constructed['construct_error'].startswith('TypeError:')
    assert mask_states == [True, False], mask_states

    print('map_detect 故障语义通过：正常负样本、四阶段故障、船标志门控、OS 遮罩复位（8 例）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
