"""Offline control discovery and recognition error contracts."""
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from ui_rule_catalog import discover_controls


class ControlCatalogTests(unittest.TestCase):
    def test_aliases_inheritance_annotations_and_lazy_factories(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sources = {
                'module/ui/switch.py': 'class Switch: pass',
                'module/fixture/base.py': 'from module.ui.switch import Switch as Native\nclass Toggle(Native): pass',
                'module/fixture/rules.py': '''
from .base import Toggle as Mode
import module.ui.switch as switches
from module.ui.scroll import Scroll
ONE = Mode()
TWO: object = switches.Switch()
if True:
    THREE = Scroll()
class UI:
    @cached_property
    def dynamic(self):
        return Mode()
    def prepare(self, main):
        rule = Mode()
        rule.set(main)
''',
            }
            for name, source in sources.items():
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(source, encoding='utf-8')
            inventory = discover_controls(root)
            self.assertFalse(inventory['errors'])
            records = inventory['declarations']
            self.assertEqual({r['name'] for r in records if r['scope'] == 'module'},
                             {'ONE', 'TWO', 'THREE'})
            self.assertEqual([(r['owner'], r['attr'], r['kind']) for r in records
                              if r['scope'] == 'property'], [('UI', 'dynamic', 'Switch')])
            self.assertEqual([r['attr'] for r in records if r['scope'] == 'factory'], ['prepare'])

    def test_native_inventory_resolves_every_discovered_module_control(self):
        import alas_vision as av
        inventory = av.op_ui_rule_list({})
        self.assertFalse(inventory['errors'], inventory['errors'])
        declared = {(r['module'], r['attr']) for r in inventory['declarations'] if r['scope'] == 'module'}
        self.assertEqual(declared, {(r['module'], r['attr']) for r in inventory['rules']})
        self.assertTrue(any(r['class'] != r['native_class'] for r in inventory['rules']))
        self.assertTrue(any(r['scope'] == 'factory' for r in inventory['declarations']))

    def test_recognition_error_has_a_separate_structured_field(self):
        import alas_vision as av
        import module.ui.switch as module
        import numpy as np
        rule = module.Switch('fixture')
        with patch.object(rule, 'get', side_effect=RuntimeError('fixture recognition failure')), \
                patch.object(module, 'fixture', rule, create=True), \
                patch.dict(av._state, image=np.zeros((720, 1280, 3), dtype=np.uint8)):
            result = av.op_ui_rule_check({'module': module.__name__, 'name': 'fixture'})
        self.assertNotIn('get', result['results'])
        self.assertEqual({r['method'] for r in result['errors']}, {'get', 'appear'})

    def test_partial_switch_positive_control_cannot_pass(self):
        import alas_vision as av
        from module.ui.switch import Switch
        import module.ui.switch as module
        import numpy as np
        button = SimpleNamespace(ensure_template=lambda: None,
                                 image=np.zeros((10, 10, 3), dtype=np.uint8), area=(0, 0, 10, 10))
        rule = Switch('fixture')
        rule.add_state('present', button)
        rule.add_state('broken', None)
        inventory = {'rules': [{'module': module.__name__, 'name': 'fixture'}], 'errors': []}
        with patch.object(av, 'op_ui_rule_list', return_value=inventory), \
                patch.object(module, 'fixture', rule, create=True), \
                patch.object(av, '_make_main_shim', return_value=SimpleNamespace(appear=lambda *a, **k: True)):
            result = av.op_rule_positive_control({})
        self.assertEqual(result['passed'], 0)
        self.assertEqual(result['failed'], 1)


if __name__ == '__main__':
    unittest.main()
