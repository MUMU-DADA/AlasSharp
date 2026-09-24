"""Failure classification and verification entry points; no devices."""
from contextlib import redirect_stderr, redirect_stdout
import io
import os
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import sync_all
import sync_upstream_assets
import fixture_runtime
import verify_map_ir
import verify_all
import verify_controls
import verify_pages


class VerificationContracts(unittest.TestCase):
    def test_quiet_sync_still_exposes_failure_reason(self):
        output = io.StringIO()
        with patch.object(sync_all.subprocess, 'run', return_value=SimpleNamespace(
                returncode=1, stdout=b'fixture failed dependency', stderr=b'')), redirect_stdout(output):
            self.assertEqual(sync_all.run(['fixture'], 'check', quiet=True), 1)
        self.assertIn('failed dependency', output.getvalue())

    def test_invalid_suite_filter_cannot_succeed_without_running_checks(self):
        for arguments in (['--only', 'absent.py'], ['--only', ''], ['--only'],
                          ['--docs-only', '--device-only'], ['--doc-only']):
            with patch.object(sys, 'argv', ['verify_all.py', *arguments]), \
                    redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()), \
                    patch.object(verify_all, 'run_step') as run:
                self.assertEqual(verify_all.main(), 2)
                run.assert_not_called()

    def test_historical_controls_preserve_verdict_and_restoration_gap(self):
        rows = [dict(page='fixture', rule='SWITCH#drive', verdict='hit', detail='on -> off; restored=off')]
        report = verify_controls.render(rows, 'fixture-digest')
        self.assertIn('on -> off; restored=off', report)
        self.assertIn('不从动作 hit 推断已经恢复', report)
        self.assertIn('hit=1', report)
        with self.assertRaises(ValueError):
            verify_controls.render([dict(rows[0], verdict='completed')], 'fixture-digest')
        # Retired page entry must exit with a migration notice without importing
        # a device driver or depending on STUB_ADB/SEGMENTS environment settings.
        with redirect_stdout(io.StringIO()):
            self.assertEqual(verify_pages.main(), 2)

    def test_verify_runs_without_update_and_propagates_failure(self):
        with patch.object(sync_all, 'check', return_value={'ir': 0, 'assets': 0}), \
                patch.object(sync_all, 'verify', return_value=1) as verify, redirect_stdout(io.StringIO()):
            self.assertEqual(sync_all.main(['--verify']), 1)
            verify.assert_called_once()

    def test_missing_required_check_is_failure(self):
        with patch.object(sync_all, 'VERIFY_STEPS', [('absent_fixture_check.py', [])]), \
                patch.object(sync_all.os.path, 'isfile', return_value=False), redirect_stdout(io.StringIO()):
            self.assertEqual(sync_all.verify(), 1)

    def test_fixture_paths_remain_relative_to_invocation(self):
        with patch.object(fixture_runtime, 'INVOCATION_DIRECTORY', ROOT):
            self.assertEqual(fixture_runtime.invocation_path('data/fixtures/example.json'),
                             str(ROOT / 'data/fixtures/example.json'))

    def test_snapshot_does_not_inherit_parent_git_identity(self):
        with patch.object(sync_upstream_assets.subprocess, 'run',
                          return_value=SimpleNamespace(returncode=0, stdout=str(ROOT))):
            info = sync_upstream_assets.git_info(str(ROOT / '.runtime/engine'))
        self.assertIsNone(info['commit'])
        self.assertIsNone(info['branch'])
        self.assertIsNone(info['worktree_clean'])

    def test_map_support_import_and_grid_failures_remain_distinct(self):
        previous = Path.cwd()
        try:
            os.chdir(verify_map_ir.FORK)
            sys.path.insert(0, verify_map_ir.FORK)
            from module.map.map_base import CampaignMap
            module = SimpleNamespace(Campaign=SimpleNamespace(MAP=CampaignMap('fixture')))
            with patch('importlib.import_module', side_effect=ImportError('missing native symbol')):
                self.assertEqual(verify_map_ir.compare('fixture/map.json', 'x', 'x')['status'], 'upstream_error')
            with patch('importlib.import_module', return_value=SimpleNamespace()):
                self.assertEqual(verify_map_ir.compare('fixture/regular_name.json', 'x', 'x')['status'], 'support_module')
            with patch('importlib.import_module', return_value=module), \
                    patch.object(verify_map_ir, 'upstream_digest', return_value='digest'), \
                    patch.object(verify_map_ir, 'upstream_grid_fingerprint', return_value='grid'):
                self.assertEqual(verify_map_ir.compare('fixture/runnable_base.json', 'digest', 'grid')['status'], 'passed')
                result = verify_map_ir.compare('fixture/map.json', 'digest', 'different')
                self.assertTrue(result['match'])
                self.assertEqual(result['status'], 'failed')
                self.assertEqual(verify_map_ir.compare('fixture/map.json', 'digest', None)['status'], 'failed')
        finally:
            os.chdir(previous)


if __name__ == '__main__':
    unittest.main()
