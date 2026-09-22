"""Offline regression using upstream combat, withdrawal and stage-exit methods.

The fake screen/device replaces I/O only: CampaignBase.execute_a_battle,
MapOperation.withdraw, Combat.combat_status and handle_in_stage are upstream.
No emulator is contacted or controlled.

替身本身在 `s3_stub_campaign.py`（合同对拍 `verify_result_contract.py` 共用同一份）。
"""
import os
import sys
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from tempfile import TemporaryDirectory
from pathlib import Path

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))

import alas_vision as av
from s3_campaign_outcome import finalize_sortie_result, observe_battle_result
from s3_campaign_execution import run_native_campaign
from s3_stub_campaign import FakeScreenCampaign, NativeRunCampaign


class OutcomeTests(unittest.TestCase):
    def run_sortie(self, **kwargs):
        inst = FakeScreenCampaign(**kwargs)
        av._CAMPAIGN['obj'] = inst
        click = inst.device.click
        result = av.op_s3_campaign_call({'name': 'execute_a_battle', 'allow_actions': True})
        self.assertIs(inst.device.click, click, 'observation must restore the device')
        return inst, result

    def test_upstream_withdraw_raises_in_stage_but_is_not_a_clear(self):
        inst, result = self.run_sortie(withdraw=True)
        self.assertEqual(result['reason'], 'In stage.')
        self.assertTrue(result['campaign_end'])
        self.assertFalse(result['completed'])
        self.assertEqual(result['outcome'], 'withdrawn')
        final = finalize_sortie_result({'map_clear_pct_final': 100}, [], inst._s3_last_end)
        self.assertFalse(final['cleared'])

    def test_upstream_boss_result_and_stage_exit_prove_clear(self):
        for rank in ('S', 'A', 'B'):
            with self.subTest(rank=rank):
                _, result = self.run_sortie(rank=rank)
                self.assertTrue(result['completed'])
                self.assertEqual(result['outcome'], 'cleared')
                self.assertEqual(result['end_evidence']['battle_rank'], rank)

    def test_losing_result_is_not_a_clear(self):
        for rank in ('C', 'D'):
            with self.subTest(rank=rank):
                _, result = self.run_sortie(rank=rank)
                self.assertEqual(result['outcome'], 'defeated')
                self.assertFalse(result['cleared'])

    def test_stage_exit_without_battle_evidence_is_unknown(self):
        _, result = self.run_sortie(unknown=True)
        self.assertEqual(result['outcome'], 'ended_unknown')
        self.assertFalse(result['completed'])

    def test_missing_result_rank_does_not_claim_clear(self):
        _, result = self.run_sortie(rank=None)
        self.assertEqual(result['outcome'], 'ended_unknown')

    def test_later_skipped_step_preserves_error(self):
        final = finalize_sortie_result({}, [
            {'step': 'map_init', 'error': 'MapDetectionError'},
            {'step': 'execute_a_battle', 'skipped': 'previous error'},
        ])
        self.assertEqual(final['outcome'], 'error')
        self.assertEqual(final['error'], 'MapDetectionError')
        self.assertTrue(final['stopped_early'])

    def test_round_limit_on_previously_cleared_map_is_incomplete(self):
        final = finalize_sortie_result({'map_clear_pct_final': 100}, [])
        self.assertEqual(final['outcome'], 'incomplete')
        self.assertFalse(final['cleared'])

    def test_end_state_does_not_leak_between_campaign_instances(self):
        self.run_sortie(rank='S')
        _, result = self.run_sortie(withdraw=True)
        self.assertEqual(result['outcome'], 'withdrawn')

    def test_experience_page_cannot_replace_an_observed_loss(self):
        inst = FakeScreenCampaign(rank='D')
        with observe_battle_result(inst) as evidence:
            inst.handle_battle_status()
            inst.device.click(SimpleNamespace(name='EXP_INFO_S'))
        self.assertEqual(evidence['battle_rank'], 'D')


class NativeRunTests(unittest.TestCase):
    def test_native_run_owns_emotion_entry_and_battle_dispatch(self):
        inst = NativeRunCampaign()
        result = run_native_campaign(inst)
        self.assertEqual(result['outcome'], 'cleared')
        self.assertEqual(inst.events, [('emotion', 7), ('enter_map', (1, 2, 3, 4), 'normal'),
                                       ('fleet_lock',), ('map_init', True)])
        self.assertEqual([step['step'] for step in result['steps']],
                         ['enter_map', 'handle_map_fleet_lock', 'map_init', 'execute_a_battle'])
        self.assertNotIn('execute_a_battle', vars(inst), 'restore inherited methods')

    def test_native_run_catches_withdraw_end_but_adapter_reports_failure(self):
        result = run_native_campaign(NativeRunCampaign(withdraw=True))
        self.assertTrue(result['upstream_returned'])
        self.assertEqual(result['outcome'], 'withdrawn')
        self.assertFalse(result['cleared'])

    def test_campaign_run_override_is_preserved(self):
        class CustomCampaign(NativeRunCampaign):
            def run(self):
                self.events.append(('custom_run',))
                return super().run()
        inst = CustomCampaign()
        result = run_native_campaign(inst)
        self.assertEqual(inst.events[0], ('custom_run',))
        self.assertTrue(result['cleared'])

    def test_battle_limit_returns_incomplete_and_restores_instance_override(self):
        inst = NativeRunCampaign()
        def battle():
            inst.battle_count += 1
            return True
        inst.execute_a_battle = battle
        result = run_native_campaign(inst, max_rounds=3)
        self.assertEqual(inst.battle_count, 3)
        self.assertEqual(result['stop_reason'], 'round_limit')
        self.assertEqual(result['outcome'], 'incomplete')
        self.assertIs(inst.execute_a_battle, battle)

    def test_stop_after_map_init_never_executes_battle(self):
        inst = NativeRunCampaign()
        result = run_native_campaign(inst, stop_after='map_init', battle_count=6)
        self.assertEqual(result['stopped_after'], 'map_init')
        self.assertEqual(inst.battle_count, 6)
        self.assertFalse(any(step['step'] == 'execute_a_battle' for step in result['steps']))

    def test_auto_search_branch_is_owned_by_native_run(self):
        inst = NativeRunCampaign()
        inst.map_is_auto_search = True
        inst.lv_reset = lambda: inst.events.append(('lv_reset',))
        inst.lv_get = lambda: inst.events.append(('lv_get',))
        inst.auto_search_execute_a_battle = inst.execute_a_battle
        result = run_native_campaign(inst)
        self.assertTrue(result['cleared'])
        self.assertNotIn(('map_init', True), inst.events)
        self.assertEqual(result['steps'][-1]['step'], 'auto_search_execute_a_battle')

    def test_elapsed_limit_stops_before_next_upstream_operation(self):
        inst = NativeRunCampaign()
        elapsed = [0.0]
        def enter_map(*args, **kwargs):
            elapsed[0] = 2.0
        inst.enter_map = enter_map
        with patch('s3_campaign_execution.time.monotonic', side_effect=lambda: elapsed[0]):
            result = run_native_campaign(inst, max_seconds=1)
        self.assertEqual(result['stop_reason'], 'time_limit')
        self.assertEqual([step['step'] for step in result['steps']], ['enter_map'])
        self.assertIs(inst.enter_map, enter_map)

    def test_map_error_stops_native_run_and_preserves_trace(self):
        inst = NativeRunCampaign()
        def map_init(*args):
            raise RuntimeError('fixture map failure')
        inst.map_init = map_init
        result = run_native_campaign(inst)
        self.assertEqual(result['outcome'], 'error')
        self.assertIn('fixture map failure', result['error'])
        self.assertTrue(result['steps'][-1]['traceback_tail'])
        self.assertIs(inst.map_init, map_init)

    def test_failure_saves_existing_rgb_frame_without_screenshot(self):
        import numpy as np
        from PIL import Image
        inst = NativeRunCampaign()
        pixels = np.array([[[255, 0, 0], [0, 0, 255]],
                           [[17, 31, 53], [255, 255, 255]]], dtype=np.uint8)
        inst.device.image = pixels.copy()
        def forbidden_capture():
            raise AssertionError('Failure evidence must not capture a new frame')
        inst.device.screenshot = forbidden_capture
        def map_init(*args):
            raise RuntimeError('fixture map failure')
        inst.map_init = map_init
        with TemporaryDirectory() as directory:
            result = run_native_campaign(inst, artifact_dir=directory)
            path = Path(result['failure_frame'])
            self.assertEqual(path.parent, Path(directory).resolve())
            self.assertTrue(path.name.endswith('_map_init.png'))
            self.assertEqual(result['steps'][-1]['failure_frame'], str(path))
            self.assertEqual(result['failure_frames'], [str(path)])
            # Mutating the device frame afterwards must not alter saved evidence.
            inst.device.image[:] = 0
            with Image.open(path) as saved:
                np.testing.assert_array_equal(np.asarray(saved), pixels)

    def test_failure_frame_save_error_preserves_original_failure(self):
        import numpy as np
        inst = NativeRunCampaign()
        inst.device.image = np.zeros((2, 2, 3), dtype=np.uint8)
        def map_init(*args):
            raise RuntimeError('original map failure')
        inst.map_init = map_init
        with TemporaryDirectory() as directory:
            unusable = Path(directory) / 'a-file'
            unusable.write_text('cannot create a directory here')
            result = run_native_campaign(inst, artifact_dir=unusable)
        self.assertEqual(result['error'], 'RuntimeError: original map failure')
        self.assertIn('failure_frame_error', result['steps'][-1])
        self.assertNotIn('failure_frame', result)

    def test_nested_error_keeps_one_original_frame(self):
        import numpy as np
        inst = NativeRunCampaign(withdraw=True)
        inst.device.image = np.zeros((2, 2, 3), dtype=np.uint8)
        def withdraw(*args):
            raise RuntimeError('withdraw failed')
        inst.withdraw = withdraw
        with TemporaryDirectory() as directory:
            result = run_native_campaign(inst, artifact_dir=directory)
            paths = list(Path(directory).glob('*.png'))
            self.assertEqual(len(paths), 1)
            self.assertTrue(paths[0].name.endswith('_withdraw.png'))
            failures = [step for step in result['steps'] if step.get('error')]
            self.assertEqual(len(failures), 2)
            self.assertEqual({step['failure_frame'] for step in failures}, {str(paths[0])})

    def test_run_override_error_also_saves_the_failure_frame(self):
        import numpy as np
        inst = NativeRunCampaign()
        inst.device.image = np.zeros((2, 2, 3), dtype=np.uint8)
        def run():
            raise RuntimeError('custom run failed')
        inst.run = run
        with TemporaryDirectory() as directory:
            result = run_native_campaign(inst, artifact_dir=directory)
            self.assertTrue(Path(result['failure_frame']).is_file())
            self.assertTrue(result['failure_frame'].endswith('_run.png'))


class CliArgumentTests(unittest.TestCase):
    def test_final_run_flag_is_enforced_before_runtime_starts(self):
        import subprocess
        exe = os.path.join(ROOT, 'src', 'Alas.DataTool', 'bin', 'Release', 'net8.0', 'alashub.exe')
        if not os.path.exists(exe):
            self.skipTest('build Release first to verify the CLI')
        result = subprocess.run([exe, 'campaign', 'campaign.campaign_main.campaign_2_1', '--run'],
                                capture_output=True, text=True, encoding='utf-8', timeout=30)
        self.assertEqual(result.returncode, 2)
        self.assertIn('--allow-actions', result.stdout)
        self.assertNotIn('[批量', result.stdout)


if __name__ == '__main__':
    unittest.main(verbosity=2)
