#!/usr/bin/env python3
"""Check that static host encoding never confuses super dispatch with instance dispatch."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src/Alas.Server/bin/Release/net10.0/Alas.Server.exe'


class SuperDispatch(unittest.TestCase):
    def audit(self, op, *, kind='call', args=None):
        runtime = ROOT / '.runtime/verification'
        runtime.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='super-dispatch-', dir=runtime) as folder:
            data = Path(folder)
            campaign = data / 'campaign/fixture'
            campaign.mkdir(parents=True)
            (campaign / 'stage.json').write_text(json.dumps({'campaign': {'battles': [
                {'method': 'clear_boss', 'plan_complete': True, 'steps': [
                    {'kind': kind, 'op': op, 'args': args or {}}]}]}}), encoding='utf-8')
            result = subprocess.run([str(SERVER), 'r5-calls', '--data', str(data), '--json'],
                cwd=ROOT, capture_output=True, encoding='utf-8', errors='replace', timeout=30)
            self.assertTrue(result.stdout.lstrip().startswith('{'), 'call audit did not return JSON')
            return result.returncode, json.loads(result.stdout)

    def test_python_reference_distinguishes_lexical_super_from_instance(self):
        class Root:
            def clear_boss(self):
                return 'root'

        class Intermediate(Root):
            def clear_boss(self):
                return 'intermediate'

        class Leaf(Intermediate):
            def clear_boss(self):
                return 'leaf'

            def delegate(self):
                return super().clear_boss()

        instance = Leaf()
        self.assertEqual(instance.delegate(), 'intermediate')
        self.assertEqual(instance.clear_boss(), 'leaf')
        self.assertEqual(Root.clear_boss(instance), 'root')

    def test_super_is_explicitly_unsupported_for_every_call_shape(self):
        for kind in ('call', 'terminal', 'super_delegate'):
            with self.subTest(kind=kind):
                code, result = self.audit('super().clear_boss', kind=kind)
                self.assertEqual(code, 1)
                self.assertEqual(result['translated'], 0)
                self.assertEqual(result['sample'], [])
                self.assertEqual(len(result['unsupported']), 1)
                self.assertEqual(result['unsupported'][0]['op'], 'super().clear_boss')
                self.assertIn('MRO', result['unsupported'][0]['reason'])
                self.assertFalse(result['complete'])
                self.assertFalse(result['dynamic_callability_verified'])

    def test_super_with_runtime_parameter_is_not_mistaken_for_resolved_binding(self):
        code, result = self.audit('super().handle_boss_appear_refocus', kind='super_delegate',
            args={'positional': [{'__param__': 'preset'}]})
        self.assertEqual(code, 1)
        self.assertEqual(result['runtime_resolved'], 0)
        self.assertEqual(result['unsupported'][0]['op'], 'super().handle_boss_appear_refocus')

    def test_normal_instance_and_fleet_paths_remain_encodable(self):
        for op, prefix in (('clear_boss', None), ('fleet_2.clear_boss', 'fleet_2')):
            with self.subTest(op=op):
                code, result = self.audit(op)
                self.assertEqual(code, 0)
                self.assertEqual(result['unsupported'], [])
                self.assertEqual(result['sample'][0]['method'], op)
                self.assertEqual(result['sample'][0]['fleet_prefix'], prefix)
                self.assertFalse(result['dynamic_callability_verified'])


if __name__ == '__main__':
    unittest.main()
