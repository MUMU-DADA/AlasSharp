#!/usr/bin/env python3
"""Check that encoding, coverage and execution never confuse super and instance dispatch."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import verify_r5_coverage as coverage

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src/Alas.Server/bin/Release/net10.0/Alas.Server.exe'


class SuperDispatch(unittest.TestCase):
    def setUp(self):
        runtime = ROOT / '.runtime/verification'
        runtime.mkdir(parents=True, exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix='super-execution-', dir=runtime)
        self.addCleanup(self.folder.cleanup)
        self.data = Path(self.folder.name)
        (self.data / 'campaign/fixture').mkdir(parents=True)

    def plan(self, steps, parameters=None):
        (self.data / 'campaign/fixture/stage.json').write_text(json.dumps({'campaign': {
            'class': 'Campaign', 'bases': ['Intermediate'], 'battles': [
                {'method': 'battle_0', 'plan_complete': True, 'steps': steps,
                 'parameters': parameters or {}},
                # Neither a same-name declared hook nor a registry entry proves the base target.
                {'method': 'clear_boss', 'plan_complete': True,
                 'steps': [{'kind': 'return', 'value': True}]}]}}), encoding='utf-8')

    def command(self, command, *arguments):
        result = subprocess.run([str(SERVER), command, '--data', str(self.data), *arguments],
            cwd=ROOT, capture_output=True, encoding='utf-8', errors='replace', timeout=30)
        self.assertEqual(result.returncode, 0, f'{command} exited {result.returncode}')
        return result.stdout

    def execute(self, steps, parameters=None):
        self.plan(steps, parameters)
        fixture = self.data / 'fixture.json'
        fixture.write_text(json.dumps({'cases': [{'name': 'super', 'chapter': 'fixture',
            'level': 'stage', 'hook': 'battle_0', 'grids': [{'location': 'A1', 'is_boss': True}]}]}),
            encoding='utf-8')
        return json.loads(self.command('r5-exec', '--fixture', str(fixture)))['cases'][0]

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

    def test_execution_rejects_super_without_any_primitive_or_device_action(self):
        for kind in ('call', 'terminal', 'conditional', 'assign', 'super_delegate'):
            for nested in (False, True):
                with self.subTest(kind=kind, nested=nested):
                    step = {'kind': kind, 'op': 'super().clear_boss', 'target': 'result'}
                    steps = [step] if not nested else [{'kind': 'branch',
                        'test': {'expr': {'literal': True}}, 'body': [step]}]
                    result = self.execute(steps)
                    self.assertFalse(result['completed'])
                    self.assertIn('MRO', result['blocked'])
                    self.assertEqual(result['actions'], [])
                    self.assertEqual(result['invoked_ops'], [])

    def test_default_parameter_does_not_prove_super_target(self):
        result = self.execute([{'kind': 'super_delegate', 'op': 'super().handle_boss_appear_refocus',
            'args': {'positional': [{'__param__': 'preset'}]}}], {'preset': [-3, 0]})
        self.assertFalse(result['completed'])
        self.assertIn('MRO', result['blocked'])
        self.assertEqual(result['actions'], [])
        self.assertEqual(result['invoked_ops'], [])

    def test_static_surface_and_coverage_do_not_credit_same_name_primitive(self):
        self.plan([{'kind': 'super_delegate', 'op': 'super().clear_boss'}])
        report = self.command('r5-plan', 'fixture')
        self.assertIn('0/2', report)  # super plus the structural return in clear_boss
        incomplete, unresolved = coverage.plan_gaps(self.data, {'clear_boss'})
        self.assertEqual(incomplete, [])
        self.assertEqual(unresolved, ['fixture/stage.json:battle_0:super().clear_boss'])


if __name__ == '__main__':
    unittest.main()
