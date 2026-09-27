"""Compare game-page recognition with native UI rules, without a device."""
from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import alas_vision as av
import numpy as np
from module.base.button import Button
from module.ui.page import Page
from module.ui.ui import UI


class NativePageRulesTests(unittest.TestCase):
    def setUp(self):
        self.server = av.server_module.server
        self.image = av._state['image']
        av._state['image'] = np.zeros((720, 1280, 3), dtype=np.uint8)

    def tearDown(self):
        av._state['image'] = self.image
        av.op_set_server({'server': self.server})

    def test_every_page_uses_native_predicate_with_original_arguments(self):
        for server in av.server_module.VALID_SERVER:
            av.op_set_server({'server': server})
            for name, page in Page.all_pages.items():
                if page.check_button is None:
                    continue
                for offset in ((30, 30), 0, True, 12):
                    with self.subTest(server=server, page=name, offset=offset):
                        with patch.object(UI, 'ui_page_appear', return_value=True) as native:
                            result = av.op_page_appear({'page': name, 'offset': offset})
                        self.assertTrue(result['appear'])
                        native.assert_called_once()
                        receiver, actual = native.call_args.args
                        self.assertIs(actual, page)
                        self.assertIs(receiver.device.image, av._state['image'])
                        self.assertEqual(receiver.config.SERVER, server)
                        self.assertEqual(native.call_args.kwargs['offset'], offset)

    def test_native_branch_order_and_short_circuit_for_every_page(self):
        for server in av.server_module.VALID_SERVER:
            av.op_set_server({'server': server})
            for name, page in Page.all_pages.items():
                if page.check_button is None:
                    continue
                for hit_at in (None, 0, 1):
                    def observe(calls):
                        def appear(button, **kwargs):
                            calls.append((id(button), kwargs))
                            return len(calls) - 1 == hit_at
                        return appear
                    expected_calls, actual_calls = [], []
                    expected = UI.ui_page_appear(SimpleNamespace(
                        config=SimpleNamespace(SERVER=server),
                        appear=observe(expected_calls)), page)
                    shim = av._make_main_shim(av._state['image'])
                    shim.appear = observe(actual_calls)
                    with patch.object(av, '_make_main_shim', return_value=shim):
                        actual = av.op_page_appear({'page': name})
                    self.assertEqual(actual['appear'], bool(expected), (server, name))
                    self.assertEqual(actual_calls, expected_calls, (server, name))

    def test_resource_failure_is_not_reported_as_a_normal_miss(self):
        page = next(p for p in Page.all_pages.values() if p.check_button is not None)
        with patch.object(Button, 'match', side_effect=FileNotFoundError('fixture missing template')):
            with self.assertRaisesRegex(FileNotFoundError, 'fixture missing template'):
                av.op_page_appear({'page': page.name})
            current = av.op_page_current({})
        self.assertTrue(current['errors'])
        self.assertFalse(current['hit'])

    def test_server_change_releases_assets_imported_by_native_pages(self):
        # The page module imports assets without going through av._resolve().
        button = next(p.check_button for p in Page.all_pages.values()
                      if p.check_button is not None and isinstance(p.check_button.raw_area, dict)
                      and p.check_button.raw_area['cn'] != p.check_button.raw_area['en'])
        saved = dict(av._state['assets'])
        av._state['assets'].clear()
        try:
            av.op_set_server({'server': 'cn'})
            self.assertEqual(button.area, button.raw_area['cn'])
            av.op_set_server({'server': 'en'})
            self.assertEqual(button.area, button.raw_area['en'])
        finally:
            av._state['assets'].update(saved)

    def test_graph_preserves_only_declared_link_objects(self):
        graph = av.op_ui_page_graph({})
        self.assertFalse(graph['unmapped'])
        self.assertFalse(graph['roundtrip_bad'])
        for node in graph['nodes']:
            upstream = Page.all_pages[node['name']]
            self.assertEqual(len(node['links']), len(upstream.links))
            for link in node['links']:
                self.assertIs(av._resolve(link['button']), upstream.links[Page.all_pages[link['to']]])
                self.assertEqual(link['variants'], [link['button']])

    def test_nested_exported_asset_ids_resolve_original_module_objects(self):
        # The exporter represents module directories with slashes, whereas
        # Python imports use dots. Both ids must resolve the same native object.
        button = Button(area=(0, 0, 2, 2), color=(1, 2, 3), button=(0, 0, 2, 2),
                        file='fixture.png', name='NESTED')
        module_name = 'module.fixture.inner.assets'
        module = SimpleNamespace(NESTED=button)
        with patch.dict(sys.modules, {module_name: module}), \
                patch.dict(av._state, assets={}, imported=set()), \
                patch.object(av.importlib, 'import_module', return_value=module) as loader:
            self.assertIs(av._resolve('fixture/inner/NESTED'), button)
            self.assertIs(av._resolve('fixture.inner/NESTED'), button)
            self.assertEqual(av._asset_id_map()[id(button)], 'fixture/inner/NESTED')
            loader.assert_called_once_with(module_name)


if __name__ == '__main__':
    unittest.main()
