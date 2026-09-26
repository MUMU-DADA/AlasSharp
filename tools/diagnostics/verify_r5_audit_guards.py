#!/usr/bin/env python3
"""Counterexamples for recursive call/coverage audits; all files and calls are offline fixtures."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import verify_r5_calls as call_audit
import verify_r5_coverage as coverage

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src/Alas.Server/bin/Release/net10.0/Alas.Server.exe'


def call(op='clear_enemy', **extra):
    return dict(kind='call', op=op, **extra)


class AuditGuards(unittest.TestCase):
    def setUp(self):
        local = ROOT / '.runtime/verification'
        local.mkdir(parents=True, exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix='r5-audit-', dir=local)
        self.addCleanup(self.folder.cleanup)
        self.data = Path(self.folder.name)
        (self.data / 'campaign/fixture').mkdir(parents=True)

    def plan(self, steps, *, complete=True, extra=()):
        path = self.data / 'campaign/fixture/stage.json'
        path.write_text(json.dumps({'campaign': {'battles': [
            {'method': 'battle_0', 'plan_complete': complete, 'steps': steps}, *extra]}}), encoding='utf-8')
        return path

    def audit(self):
        result = subprocess.run([str(SERVER), 'r5-calls', '--data', str(self.data), '--json'],
                                cwd=ROOT, capture_output=True, encoding='utf-8', errors='replace', timeout=30)
        payload = json.loads(result.stdout) if result.stdout.lstrip().startswith('{') else None
        return result.returncode, payload

    def test_recursive_body_else_nested_conditions_and_runtime_arguments(self):
        self.plan([{'kind': 'branch', 'test': {'all': [
            {'call': {'op': 'clear_siren'}}, {'nested': {'call': {'op': 'clear_boss'}}}]},
            'body': [call(args={'positional': [{'__local__': 'target'}]})],
            'orelse': [{'kind': 'branch', 'test': {'call': {'op': 'clear_enemy'}},
                        'body': [call('battle_default')], 'orelse': [{'kind': 'return', 'value': False}]}]}])
        code, result = self.audit()
        self.assertEqual(code, 0)
        self.assertEqual(result['steps'], 5)
        self.assertEqual(result['condition_calls'], 3)
        self.assertEqual(result['calls'], 5)
        self.assertEqual(result['static_encoded'], 4)
        self.assertEqual(result['runtime_resolved'], 1)
        self.assertEqual(call_audit.validate(result, call_audit.inventory(self.data), code), [])
        self.assertFalse(result['dynamic_callability_verified'])

    def test_nested_bad_arguments_fail_instead_of_skipping_branch(self):
        self.plan([{'kind': 'branch', 'test': {'local': 'target'}, 'body': [],
                    'orelse': [call(args={'positional': [{'opaque': 7}]})]}])
        code, result = self.audit()
        self.assertEqual(code, 1)
        self.assertEqual(len(result['unsupported']), 1)
        self.assertIn('.orelse[0]', result['unsupported'][0]['source'])

    def test_condition_bad_arguments_are_audited(self):
        self.plan([{'kind': 'branch', 'test': {'call': {'op': 'clear_enemy',
                    'args': {'keyword': {'value': {'opaque': 7}}}}}, 'body': [], 'orelse': []}])
        code, result = self.audit()
        self.assertEqual(code, 1)
        self.assertEqual(len(result['unsupported']), 1)
        self.assertTrue(result['unsupported'][0]['source'].endswith('.test.call'))

    def test_unknown_inherited_method_is_unverified_not_declared_callable(self):
        self.plan([call('inherited_helper')])
        code, result = self.audit()
        self.assertEqual(code, 1)
        self.assertEqual(result['unsupported'], [])
        self.assertEqual(result['sample'][0]['binding'], 'native_resolution_required')
        self.assertIsNone(result['sample'][0]['dynamic_callable'])

    def test_exported_hook_binding_is_distinguished(self):
        self.plan([call('declared_helper')], extra=[{'method': 'declared_helper',
                  'plan_complete': True, 'steps': [{'kind': 'return', 'value': True}]}])
        code, result = self.audit()
        self.assertEqual(code, 0)
        self.assertEqual(result['sample'][0]['binding'], 'declared_hook')
        self.assertIsNone(result['sample'][0]['dynamic_callable'])

    def test_fleet_receiver_keeps_declared_override_ahead_of_registry(self):
        for prefix in ('', 'fleet_1.', 'fleet_2.', 'fleet_boss.', 'fleet_submarine.'):
            with self.subTest(prefix=prefix):
                self.plan([call(prefix + 'clear_boss')], extra=[{'method': 'clear_boss',
                    'plan_complete': True, 'steps': [{'kind': 'return', 'value': False}]}])
                code, result = self.audit()
                self.assertEqual(code, 0)
                self.assertEqual(result['sample'][0]['binding'], 'declared_hook')

    def test_unknown_receiver_is_not_assumed_to_be_current_instance(self):
        self.plan([call('unknown.declared_helper')], extra=[{'method': 'declared_helper',
            'plan_complete': True, 'steps': [{'kind': 'return', 'value': True}]}])
        code, result = self.audit()
        self.assertEqual(code, 1)
        self.assertEqual(result['sample'][0]['binding'], 'native_resolution_required')

    def test_incomplete_plan_prevents_full_coverage_claim(self):
        self.plan([], complete=False)
        code, result = self.audit()
        self.assertEqual(code, 1)
        self.assertEqual(len(result['incomplete_hooks']), 1)
        self.assertTrue(call_audit.validate(result, call_audit.inventory(self.data), code))

    def test_missing_empty_and_malformed_exports_fail(self):
        code, _ = self.audit()
        self.assertEqual(code, 1)
        self.plan([call()])
        (self.data / 'campaign/fixture/broken.json').write_text('{', encoding='utf-8')
        code, _ = self.audit()
        self.assertEqual(code, 1)
        with self.assertRaises(json.JSONDecodeError):
            call_audit.inventory(self.data)

    def test_samples_do_not_truncate_after_400_calls(self):
        self.plan([call() for _ in range(405)])
        code, result = self.audit()
        self.assertEqual(code, 0)
        self.assertEqual(len(result['sample']), 405)
        self.assertEqual(call_audit.validate(result, call_audit.inventory(self.data), code), [])
        result['sample'].pop()
        self.assertTrue(call_audit.validate(result, call_audit.inventory(self.data), code))

    def fixture(self):
        fixture = self.data / 'fixture.json'
        fixture.write_text('{"cases":[{"name":"one"}]}', encoding='utf-8')
        return fixture

    def test_nonzero_process_cannot_supply_coverage(self):
        result = subprocess.CompletedProcess(['fixture'], 7, '{"cases":[]}', 'failed')
        with patch.object(coverage.subprocess, 'run', return_value=result):
            with self.assertRaisesRegex(RuntimeError, '7'):
                coverage.run('r5-exec', self.fixture())

    def test_steps_actions_and_logs_do_not_count_as_invocations(self):
        fixture = self.fixture()
        payload = {'cases': [{'name': 'one', 'steps': ['clear_enemy'], 'actions': ['clear_enemy()'],
                              'logs': ['No battle executed.'], 'invoked_ops': []}]}
        self.assertEqual(coverage.observed(payload, fixture), set())
        payload['cases'][0].pop('invoked_ops')
        with self.assertRaisesRegex(ValueError, 'invoked_ops'):
            coverage.observed(payload, fixture)

    def test_missing_cases_and_missing_plan_inputs_fail(self):
        fixture = self.fixture()
        with self.assertRaises(ValueError):
            coverage.observed({'cases': []}, fixture)
        with self.assertRaises(ValueError):
            coverage.observed({'cases': [{'name': 'other', 'invoked_ops': ['clear_enemy']}]}, fixture)
        with self.assertRaises(ValueError):
            coverage.plan_gaps(self.data, {'clear_enemy'})

    def test_plan_gaps_include_nested_unknowns_and_incomplete_hooks(self):
        self.plan([{'kind': 'branch', 'test': {'call': {'op': 'unknown_condition'}},
                    'body': [call('unknown_body')], 'orelse': []}],
                  extra=[{'method': 'incomplete', 'plan_complete': False, 'steps': []}])
        incomplete, unresolved = coverage.plan_gaps(self.data, {'clear_enemy'})
        self.assertEqual(len(incomplete), 1)
        self.assertEqual(len(unresolved), 2)


if __name__ == '__main__':
    unittest.main()
