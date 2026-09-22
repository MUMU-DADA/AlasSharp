"""Offline regression checks for effective Config export semantics.

Synthetic modules are imported by Python as the independent semantic oracle;
the exporter itself never executes them. No device or account is instantiated.
"""
from __future__ import annotations

import importlib
import json
from pathlib import Path
import shutil
import sys
import unittest
import uuid

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
from export_upstream_data import export_campaign
from upstream_config_export import ConfigResolver


class ConfigExportTests(unittest.TestCase):
    def setUp(self):
        self.scratch = Path(__file__).resolve().parents[2] / 'data'
        self.root = self.scratch / ('__config_export_test_' + uuid.uuid4().hex)
        self.root.mkdir(parents=True)

    def tearDown(self):
        for name in list(sys.modules):
            if name == 'config_fixture' or name.startswith('config_fixture.'):
                del sys.modules[name]
        if self.root.resolve().parent != self.scratch.resolve():
            raise RuntimeError('test directory escaped the workspace scratch directory')
        shutil.rmtree(self.root)

    def source(self, module, text):
        path = self.root.joinpath(*module.split('.')).with_suffix('.py')
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def native_values(self, module):
        sys.path.insert(0, str(self.root))
        try:
            importlib.invalidate_caches()
            cls = importlib.import_module(module).Config
            values = {name: getattr(cls, name) for name in dir(cls)
                      if not name.startswith('_') and not callable(getattr(cls, name))}
            return json.loads(json.dumps(values))
        finally:
            sys.path.remove(str(self.root))

    def test_import_aliases_and_c3_inheritance_match_python(self):
        self.source('config_fixture.base', '''
LIMIT = 255 - 49
class Root:
    FLAG = "root"
    PEAKS = {"height": (120, LIMIT), "width": (1.5, 10)}
class Left(Root):
    pass
class Right(Root):
    FLAG = "right"
class Config(Left, Right):
    SCALE = (1 / 2, 2 * 3)
''')
        self.source('config_fixture.chapter', 'from .base import Config as Base\nConfig = Base\n')
        result = ConfigResolver(self.root).export('config_fixture.chapter')
        self.assertTrue(result['complete'])
        self.assertEqual(result['values'], self.native_values('config_fixture.chapter'))
        self.assertEqual(result['values']['FLAG'], 'right')
        self.assertEqual(result['origins']['FLAG']['class'], 'Right')
        self.assertEqual(result['typed_values']['SCALE']['type'], 'tuple')
        self.assertEqual(result['source_files'],
                         ['config_fixture/base.py', 'config_fixture/chapter.py'])

    def test_class_and_module_binding_order_match_python(self):
        self.source('config_fixture.order', '''
VALUE = 10
class Config:
    A = VALUE
    A += 2
    B: tuple = (A, VALUE)
    X, *Y, Z = (1, 2, 3, 4)
VALUE = 99
''')
        result = ConfigResolver(self.root).export('config_fixture.order')
        self.assertTrue(result['complete'])
        self.assertEqual(result['values'], self.native_values('config_fixture.order'))

    def test_unknown_override_cannot_fall_back_to_inherited_value(self):
        self.source('config_fixture.unknown', '''
class Base:
    REQUIRED = 1
class Config(Base):
    REQUIRED = build_value()
''')
        result = ConfigResolver(self.root).export('config_fixture.unknown')
        self.assertFalse(result['complete'])
        self.assertNotIn('REQUIRED', result['values'])
        self.assertEqual(result['unresolved'][0]['field'], 'REQUIRED')
        self.assertEqual(result['origins']['REQUIRED']['class'], 'Config')

    def test_missing_import_is_an_explicit_incomplete_export(self):
        self.source('config_fixture.missing', 'from .absent import Config\n')
        result = ConfigResolver(self.root).export('config_fixture.missing')
        self.assertFalse(result['complete'])
        self.assertTrue(result['unresolved'])

    def test_export_keeps_config_and_campaign_attributes_separate(self):
        self.source('campaign.fixture.base', '''
class Config:
    PEAKS = {"height": (120, 255 - 49)}
    FLEET = 0
''')
        self.source('campaign.fixture.chapter', '''
from .base import Config
class Campaign:
    ENEMY_FILTER = "1L > 1M"
    FLEET = 2
    def battle_0(self):
        return self.battle_default()
''')
        self.source('campaign.fixture.incomplete', '''
class Config:
    PEAKS = unsupported()
class Campaign:
    def battle_0(self):
        return self.battle_default()
''')
        manifest = {'errors': []}
        out = self.root / 'out'
        export_campaign(str(self.root), str(out), manifest)
        ir = json.loads((out / 'campaign/fixture/chapter.json').read_text(encoding='utf-8'))
        self.assertEqual(ir['config'], {'FLEET': 0, 'PEAKS': {'height': [120, 206]}})
        self.assertEqual(ir['campaign']['attributes'], {'ENEMY_FILTER': '1L > 1M', 'FLEET': 2})
        self.assertTrue(ir['config_meta']['complete'])
        self.assertEqual(ir['config_meta']['origins']['PEAKS']['module'], 'campaign.fixture.base')
        broken = json.loads((out / 'campaign/fixture/incomplete.json').read_text(encoding='utf-8'))
        self.assertFalse(broken['config_meta']['complete'])
        self.assertTrue(any('Config.PEAKS' in item for item in broken['unresolved']))


if __name__ == '__main__':
    unittest.main()
