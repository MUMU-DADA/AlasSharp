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


def detect(view_class, mode='main'):
    config = Config(DETECTION_BACKEND='perspective',
                    INTERNAL_LINES_HOUGHLINES_THRESHOLD=1)
    with patch.object(view_mod, 'View', view_class), \
         patch.object(vision, '_require_image', return_value=object()), \
         patch.object(vision, '_map_config', return_value=config), \
         patch.object(vision, 'apply_numpy2_compat'), \
         patch.object(vision, 'apply_points_empty_compat'):
        return vision.op_map_detect({'mode': mode})


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

    mask_states = []
    with patch.object(vision, '_make_main_shim', return_value=object()), \
         patch.object(vision, 'apply_os_mask_compat'), \
         patch.object(vision, 'set_os_mask_mode', side_effect=mask_states.append), \
         patch.object(EnemySearchingHandler, 'is_in_map', return_value=True):
        os_constructed = detect(ConstructError, mode='os')
    assert os_constructed['construct_error'].startswith('TypeError:')
    assert mask_states == [True, False], mask_states

    print('map_detect 故障语义通过：正常负样本、三阶段故障、OS 遮罩复位（5 例）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
