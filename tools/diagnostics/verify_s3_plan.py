# -*- coding: utf-8 -*-
"""Offline regression for campaign identity, IR diagnostics and S3 dry-run.

Run: .runtime/venv314/Scripts/python.exe tools/diagnostics/verify_s3_plan.py
No campaign initialization, device connection, screenshot or game action.
"""
import json
from contextlib import contextmanager, ExitStack
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from campaign_rules import CAMPAIGN_DATA, CampaignRuleError, load_campaign_rules


class CampaignRuleTests(unittest.TestCase):
    def write_rule(self, root, package, source=None, battles=None, complete=True):
        path = Path(root) / package / 'a1.json'
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({
            'source': source or f'campaign/{package}/a1.py',
            'campaign': {'plan_complete': complete, 'battles': battles or []},
        }), encoding='utf-8')
        return path

    def test_same_basename_keeps_activity_identity(self):
        with tempfile.TemporaryDirectory() as root:
            self.write_rule(root, 'event_a', battles=[
                {'method': 'battle_1', 'calls': ['clear_boss'], 'plan_complete': True}])
            expected = self.write_rule(root, 'event_b', battles=[
                {'method': 'battle_10', 'calls': ['clear_boss'], 'plan_complete': True},
                {'method': 'battle_2', 'calls': ['clear_siren'], 'plan_complete': True},
                {'method': 'map_data_init', 'calls': [], 'plan_complete': False}])
            result = load_campaign_rules('campaign.event_b.a1', root)
            self.assertEqual(Path(result['ir_path']), expected)
            self.assertEqual(result['ir_source'], 'campaign/event_b/a1.py')
            self.assertEqual(result['plan_steps'], ['battle_2', 'battle_10'])
            self.assertEqual(result['semantic_trace'], ['clear_siren', 'clear_boss'])

    def test_missing_activity_never_falls_back_to_same_basename(self):
        with tempfile.TemporaryDirectory() as root:
            self.write_rule(root, 'event_a')
            with self.assertRaises(CampaignRuleError) as caught:
                load_campaign_rules('campaign.event_missing.a1', root)
            self.assertEqual(caught.exception.code, 'ir_not_found')

    def test_wrong_source_rejected_even_at_expected_path(self):
        with tempfile.TemporaryDirectory() as root:
            self.write_rule(root, 'event_b', source='campaign/event_a/a1.py')
            with self.assertRaises(CampaignRuleError) as caught:
                load_campaign_rules('campaign.event_b.a1', root)
            self.assertEqual(caught.exception.code, 'ir_source_mismatch')

    def test_unqualified_and_path_chapters_rejected(self):
        for chapter in ('a1', 'event_a.a1', 'campaign..a1',
                        '../campaign/a1', 'campaign.event_a.a1.json'):
            with self.subTest(chapter=chapter), self.assertRaises(CampaignRuleError):
                load_campaign_rules(chapter)

    def test_incomplete_plan_retains_native_execution(self):
        result = load_campaign_rules('campaign.campaign_main.campaign_2_1')
        self.assertEqual(result['plan_steps'], ['battle_0', 'battle_2'])
        self.assertEqual(result['incomplete_methods'], ['battle_2'])
        self.assertEqual(result['ir_plan_status'], 'incomplete')
        self.assertFalse(result['ir_plan_complete'])
        self.assertEqual(result['runtime_dispatch'], 'execute_a_battle')
        self.assertEqual(result['runtime_entrypoint'], 'Campaign.run')
        self.assertEqual(result['execution_mode'], 'upstream_campaign')
        self.assertFalse(result['json_plan_replayed'])

    def test_no_local_battles_does_not_claim_complete(self):
        with tempfile.TemporaryDirectory() as root:
            self.write_rule(root, 'event_a', complete=True)
            result = load_campaign_rules('campaign.event_a.a1', root)
            self.assertEqual(result['ir_plan_status'], 'no_exported_battle_methods')
            self.assertFalse(result['ir_plan_complete'])
            self.assertEqual(result['plan_steps'], [])

    def test_config_metadata_preserves_origins_without_runtime_imports(self):
        with tempfile.TemporaryDirectory() as root:
            path = self.write_rule(root, 'event_a')
            ir = json.loads(path.read_text(encoding='utf-8'))
            origin = {'module': 'campaign.event_a.base', 'class': 'Config',
                      'line': 3, 'expression': '255 - 49'}
            ir.update(config={'THRESHOLD': 206}, config_meta={
                'present': True, 'complete': True, 'origins': {'THRESHOLD': origin}})
            path.write_text(json.dumps(ir), encoding='utf-8')
            result = load_campaign_rules('campaign.event_a.a1', root)
            self.assertTrue(result['config_present'])
            self.assertTrue(result['config_complete'])
            self.assertEqual(result['config_count'], 1)
            self.assertEqual(result['config_origins'], {'THRESHOLD': origin})
            self.assertEqual(result['config_sources'], ['campaign.event_a.base.Config'])
            self.assertEqual(result['runtime_config_source'], 'campaign.event_a.a1.Config')

    def test_legacy_config_metadata_is_unknown(self):
        with tempfile.TemporaryDirectory() as root:
            self.write_rule(root, 'event_a')
            result = load_campaign_rules('campaign.event_a.a1', root)
            self.assertIsNone(result['config_present'])
            self.assertIsNone(result['config_complete'])
            self.assertEqual(result['config_count'], 0)

    def test_every_exported_source_resolves_to_its_own_file(self):
        paths = list(CAMPAIGN_DATA.rglob('*.json'))
        self.assertGreater(len(paths), 1000)
        for path in paths:
            ir = json.loads(path.read_text(encoding='utf-8'))
            chapter = ir['source'][:-3].replace('/', '.')
            with self.subTest(chapter=chapter):
                result = load_campaign_rules(chapter)
                self.assertEqual(Path(result['ir_path']), path)


class S3DryRunTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Importing the host loads its protocol table, but must not initialize a
        # campaign. Patching its action entry points catches hidden dry-run IO.
        import alas_vision
        cls.av = alas_vision

    def invoke(self, **args):
        response = json.loads(self.av.handle_line(json.dumps({
            'id': 1, 'op': 's3_run_plan', 'args': args})))
        self.assertTrue(response['ok'], response)
        return response['result']

    def test_protocol_dry_run_never_initializes_campaign_or_device(self):
        with patch.object(self.av, 'op_s3_campaign_init',
                          side_effect=AssertionError('unexpected campaign init')), \
             patch.object(self.av, '_device_engine',
                          side_effect=AssertionError('unexpected device access')):
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1')
        self.assertNotIn('error', result)
        self.assertTrue(result['dry_run'])
        self.assertEqual(result['stage'], '2-1')
        self.assertEqual(result['plan_steps'], ['battle_0', 'battle_2'])
        self.assertEqual(result['ir_plan_status'], 'incomplete')

    def test_missing_rules_fail_before_any_live_initialization(self):
        with patch.object(self.av, 'op_s3_campaign_init',
                          side_effect=AssertionError('unexpected campaign init')):
            result = self.invoke(chapter='campaign.event_missing.a1',
                                 dry_run=False, allow_actions=True)
        self.assertEqual(result['stage'], 'rules')
        self.assertEqual(result['error_code'], 'ir_not_found')

    def test_live_action_lock_still_required(self):
        with patch.object(self.av, 'op_s3_campaign_init',
                          side_effect=AssertionError('unexpected campaign init')):
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1', dry_run=False)
        self.assertTrue(result['refused'])

    @contextmanager
    def native_protocol(self, navigation_error=None, native_error=None, own_enter=False,
                        prior_map=False, withdraw_error=None, cached_image=True,
                        navigation_withdraw=False):
        """Use real protocol dispatch with fake Campaign methods and no device."""
        events = []

        class FakeCampaign:
            config = SimpleNamespace(Campaign_Mode='normal')
            # 导航期就撤退时没人设过 ENTRANCE；给它一个空值，别让替身自己抛 AttributeError。
            ENTRANCE = None
            device = SimpleNamespace(
                has_cached_image=cached_image,
                stuck_record_clear=lambda: events.append(('clear_stuck',)),
                click_record_clear=lambda: events.append(('clear_click',)),
                screenshot=lambda: events.append(('screenshot',)),
            )

            def is_in_map(self):
                return prior_map

            def withdraw(self):
                from module.exception import CampaignEnd
                events.append(('navigation_withdraw',) if navigation_withdraw
                              else ('withdraw_previous',))
                if withdraw_error:
                    raise RuntimeError(withdraw_error)
                raise CampaignEnd('In stage.')

            def ensure_campaign_ui(self, name, mode):
                events.append(('navigate', name, mode))
                if navigation_error:
                    raise RuntimeError(navigation_error)
                if navigation_withdraw:
                    # 客户端状态残留时上游导航自己会撤退一次（实测 2026-09-23 02:10:37）。
                    return self.withdraw()
                self.config.Campaign_Mode = 'hard'
                self.ENTRANCE = 'resolved entrance'
                return True

            def enter_map(self, *args, **kwargs):
                raise AssertionError('entry belongs to the native runner')

        inst = FakeCampaign()
        if own_enter:
            inst.enter_map = lambda *args: 'instance-owned entry'
        original_enter = inst.enter_map

        def execute(campaign, **options):
            events.append(('native', campaign.ENTRANCE, campaign.config.Campaign_Mode))
            if native_error:
                raise RuntimeError(native_error)
            return {'execution': 'upstream_run', 'steps': [{'step': 'run'}],
                    'cleared': False, 'outcome': 'incomplete', 'stop_reason': 'round_limit'}

        with ExitStack() as stack:
            stack.enter_context(patch.object(self.av, '_CAMPAIGN', {
                'obj': inst, 'loader': SimpleNamespace(stage='upstream-stage')}))
            initialize = stack.enter_context(patch.object(
                self.av, 'op_s3_campaign_init', return_value={'instantiated': True}))
            abort = stack.enter_context(patch.object(
                self.av, 'op_s3_abort_unfinished', return_value={'unfinished_dialog': False}))
            device = stack.enter_context(patch.object(
                self.av, '_device_engine', side_effect=AssertionError('unexpected device access')))
            watcher = stack.enter_context(patch.object(
                self.av, '_proactive_abort_worker', side_effect=AssertionError('unexpected watcher')))
            native = stack.enter_context(patch(
                's3_campaign_execution.run_native_campaign', side_effect=execute))
            yield SimpleNamespace(inst=inst, events=events, native=native, initialize=initialize,
                                  original_enter=original_enter, abort=abort)
            device.assert_not_called()
            watcher.assert_not_called()

    def test_protocol_navigates_then_forwards_limits_to_native_run(self):
        with self.native_protocol() as state:
            result = self.invoke(chapter='campaign.event_20220210_cn.a1', dry_run=False,
                                 allow_actions=True, max_rounds=7, max_seconds=321,
                                 stop_after='map_init', battle_count=4,
                                 fleet1=3, fleet2=2, submarine_fleet=1,
                                 clear_all=True, emotion_mode='calculate_ignore')
            self.assertEqual(state.events, [('clear_stuck',), ('clear_click',),
                                            ('navigate', 'upstream-stage', 'normal'),
                                            ('clear_stuck',), ('clear_click',),
                                            ('native', 'resolved entrance', 'hard')])
            state.native.assert_called_once_with(state.inst, max_rounds=7, max_seconds=321.0,
                                                 stop_after='map_init', withdraw_file=None,
                                                 battle_count=4)
            options = state.initialize.call_args.args[0]
            self.assertEqual((options['fleet1'], options['fleet2'], options['submarine_fleet']),
                             (3, 2, 1))
            self.assertTrue(options['clear_all'])
            self.assertEqual(result['stage'], 'upstream-stage')
            self.assertEqual(result['execution'], 'upstream_run')
            self.assertEqual([step['step'] for step in result['steps']],
                             ['prepare_campaign_navigation', 'abort_unfinished',
                              'ensure_campaign_ui', 'prepare_campaign_run', 'run'])
            self.assertFalse(result['cleared'])
            self.assertNotIn('enter_map', vars(state.inst))
            self.assertEqual(state.inst.enter_map, state.original_enter)

    def test_protocol_navigation_failure_stops_before_native_run(self):
        with self.native_protocol(navigation_error='chapter navigation failed') as state:
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1',
                                 dry_run=False, allow_actions=True)
            state.native.assert_not_called()
            self.assertEqual(state.events, [('clear_stuck',), ('clear_click',),
                                            ('navigate', 'upstream-stage', 'normal')])
            self.assertFalse(result['cleared'])
            self.assertEqual(result['outcome'], 'error')
            self.assertIn('chapter navigation failed', result['error'])
            self.assertNotIn('enter_map', vars(state.inst))

    def test_protocol_init_failure_stops_before_navigation_or_dialog(self):
        with self.native_protocol() as state:
            state.initialize.return_value = {'error': 'configuration failed'}
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1',
                                 dry_run=False, allow_actions=True)
            state.abort.assert_not_called()
            state.native.assert_not_called()
            self.assertEqual(state.events, [])
            self.assertEqual(result['stage'], 'init')
            self.assertEqual(result['error'], 'configuration failed')

    def test_protocol_single_round_preserves_instance_owned_entry(self):
        with self.native_protocol(own_enter=True) as state:
            self.invoke(chapter='campaign.campaign_main.campaign_2_1', dry_run=False,
                        allow_actions=True, repeat_until_cleared=False, max_rounds=9)
            self.assertEqual(state.native.call_args.kwargs['max_rounds'], 1)
            self.assertIs(state.inst.enter_map, state.original_enter)

    def test_protocol_restores_entry_when_native_runner_raises(self):
        with self.native_protocol(native_error='runner failed', own_enter=True) as state:
            response = json.loads(self.av.handle_line(json.dumps({
                'id': 1, 'op': 's3_run_plan', 'args': {
                    'chapter': 'campaign.campaign_main.campaign_2_1',
                    'dry_run': False, 'allow_actions': True}})))
            self.assertFalse(response['ok'])
            self.assertIn('runner failed', str(response['error']))
            self.assertIs(state.inst.enter_map, state.original_enter)

    def test_previous_map_withdrawal_precedes_navigation_and_is_not_a_clear(self):
        with self.native_protocol(prior_map=True, cached_image=False) as state:
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1',
                                 dry_run=False, allow_actions=True)
            self.assertEqual(state.events, [
                ('clear_stuck',), ('clear_click',), ('screenshot',), ('withdraw_previous',),
                ('navigate', 'upstream-stage', 'normal'), ('clear_stuck',), ('clear_click',),
                ('native', 'resolved entrance', 'hard')])
            self.assertTrue(result['steps'][0]['withdrew_previous_sortie'])
            self.assertFalse(result['cleared'])
            self.assertEqual(result['outcome'], 'incomplete')

    def test_previous_map_withdrawal_failure_prevents_navigation(self):
        with self.native_protocol(prior_map=True, withdraw_error='cannot withdraw') as state:
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1',
                                 dry_run=False, allow_actions=True)
            state.native.assert_not_called()
            state.abort.assert_not_called()
            self.assertEqual(state.events, [('clear_stuck',), ('clear_click',),
                                            ('withdraw_previous',)])
            self.assertEqual(result['outcome'], 'error')
            self.assertIn('cannot withdraw', result['error'])
            self.assertFalse(result['cleared'])

    def test_navigation_time_withdrawal_is_recorded_and_never_a_clear(self):
        """导航期间上游自己撤退（客户端状态残留）不能静默消失，更不能变成通关。

        真机证据：`data/s3_native_small_and_clearall.log` 02:10:37
        `handle_campaign_ui_additional -> self.withdraw()`，当时这一步在 `ensure_campaign_ui`
        的返回里被吞掉，事后只能翻原始日志（见 `docs/result-evidence.md`）。
        """
        with self.native_protocol(navigation_withdraw=True) as state:
            result = self.invoke(chapter='campaign.campaign_main.campaign_2_1',
                                 dry_run=False, allow_actions=True)
            ui = next(step for step in result['steps'] if step['step'] == 'ensure_campaign_ui')
            self.assertEqual(ui['navigation_end'], 'withdrawn')
            self.assertTrue(ui['navigation_withdrawn'])
            self.assertFalse(result['cleared'])
            self.assertNotEqual(result['outcome'], 'cleared')
            state.native.assert_called_once()
            self.assertIn(('navigation_withdraw',), state.events)


if __name__ == '__main__':
    unittest.main(verbosity=2)
