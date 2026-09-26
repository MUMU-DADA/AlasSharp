"""Regression counterexamples for the offline Python-to-plan translation."""
import ast
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from export_upstream_data import _UNRESOLVED, campaign_method_plans, derive_plan


def plan(source):
    return derive_plan(ast.parse(source).body, 'fixture', lambda _: _UNRESOLVED)


class PlanExportTests(unittest.TestCase):
    def test_if_without_else_preserves_fallthrough(self):
        steps, complete, _, dead = plan('if self.flag:\n    return False\nreturn self.battle_default()')
        self.assertTrue(complete)
        self.assertEqual([s['kind'] for s in steps], ['branch', 'terminal'])
        self.assertEqual(dead, [])

    def test_only_two_terminating_branches_remove_following_code(self):
        steps, complete, _, dead = plan('if self.flag:\n    return False\nelse:\n    return True\nself.unreachable()')
        self.assertTrue(complete)
        self.assertEqual(len(steps), 1)
        self.assertEqual(dead, ['Expr'])

    def test_bare_return_is_terminal_none(self):
        steps, complete, _, dead = plan('return\nself.unreachable()')
        self.assertTrue(complete)
        self.assertEqual(steps, [{'kind': 'return', 'value': None}])
        self.assertEqual(dead, ['Expr'])

    def test_membership_polarity_matches_python(self):
        for expression, negate in [('self.battle_count in (1, 2)', False),
                                   ('self.battle_count not in (1, 2)', True),
                                   ('not (self.battle_count in (1, 2))', True),
                                   ('not (self.battle_count not in (1, 2))', False)]:
            with self.subTest(expression=expression):
                steps, complete, _, _ = plan(f'if {expression}:\n    return False')
                self.assertTrue(complete)
                self.assertEqual(steps[0]['test']['negate'], negate)

    def test_unknown_or_duplicate_arguments_do_not_claim_complete(self):
        for call in ['self.go(**options)', 'self.go(*options)', 'self.go(value=unknown)',
                     'self.go(value=1, **{"value": 2})', 'self.go(**{1: 2})']:
            with self.subTest(call=call):
                steps, complete, issues, _ = plan('return ' + call)
                self.assertFalse(complete)
                self.assertEqual(steps, [])
                self.assertTrue(issues)

    def test_literal_argument_unpacking_retains_all_values(self):
        steps, complete, issues, _ = plan('return self.go(*(1, 2), **{"fleet": 2})')
        self.assertTrue(complete, issues)
        self.assertEqual(steps[0]['args'], {'positional': [1, 2], 'keyword': {'fleet': 2}})

    def test_tuple_arguments_retain_type_through_nested_and_unpacked_values(self):
        steps, complete, issues, _ = plan('return self.go([1, 2], (1, 2), *((3, 4),), **{"scale": (1, 2)})')
        self.assertTrue(complete, issues)
        self.assertEqual(steps[0]['args'], {
            'positional': [[1, 2], {'__tuple__': [1, 2]}, {'__tuple__': [3, 4]}],
            'keyword': {'scale': {'__tuple__': [1, 2]}}})

    def test_indexed_local_condition_preserves_index(self):
        steps, complete, issues, _ = plan('boss = self.map.select(is_boss=True)\nif boss[0]:\n    return False')
        self.assertTrue(complete, issues)
        self.assertEqual(steps[1]['test']['expr'], {'local_index': {'name': 'boss', 'index': 0}})

    def test_index_assignment_does_not_label_single_grid_a_collection(self):
        steps, complete, issues, _ = plan('boss = self.map.select(is_boss=True)\nboss = boss[0]\nself.go(boss)')
        self.assertTrue(complete, issues)
        self.assertEqual(steps[-1]['args']['positional'], [{'__local__': 'boss'}])

    def test_nested_expressions_resolve_local_values_and_arguments(self):
        steps, complete, issues, _ = plan('boss = self.map.select(is_boss=True)\nif boss and self.check_accessibility(boss[0]):\n    return False')
        self.assertTrue(complete, issues)
        values = steps[-1]['test']['expr']['and']
        self.assertEqual(values[0], {'local': 'boss'})
        self.assertEqual(values[1]['call']['args']['positional'], [{'__local__': 'boss', '__index__': 0}])

    def test_local_argument_type_is_chosen_by_the_taken_branch(self):
        steps, complete, issues, _ = plan('if self.flag:\n    target = SelectedGrids([])\nelse:\n    target = None\nself.go(target)')
        self.assertTrue(complete, issues)
        self.assertEqual(steps[-1]['args']['positional'], [{'__local__': 'target'}])

    def test_method_transformations_and_unknown_defaults_stay_incomplete(self):
        for method in ['@decorate\n def battle_0(self): return True',
                       'async def battle_0(self): return True',
                       'def battle_0(self, value=factory()): return True',
                       'def battle_0(self, value={1, 2}): return True',
                       'def battle_0(self, **options): return True']:
            with self.subTest(method=method):
                source = 'class Campaign:\n ' + method
                methods = campaign_method_plans(ast.parse(source), 'fixture.chapter', str(ROOT))
                self.assertFalse(methods[0]['plan_complete'])
                self.assertEqual(methods[0]['steps'], [])
                self.assertTrue(methods[0]['unparsed'])

    def test_parameter_default_keeps_tuple_type(self):
        methods = campaign_method_plans(ast.parse('class Campaign:\n def refocus(self, preset=(0, -2)):\n  return super().refocus(preset)'),
                                         'fixture.chapter', str(ROOT))
        self.assertEqual(methods[0]['parameters']['preset'], {'__tuple__': [0, -2]})

    def test_signature_keeps_order_required_none_and_argument_kinds(self):
        methods = campaign_method_plans(ast.parse('class Campaign:\n def helper(self, z, /, a=None, *, required, optional=None):\n  return True'),
                                         'fixture.chapter', str(ROOT))
        method = methods[0]
        self.assertTrue(method['plan_complete'])
        self.assertEqual(method['parameter_order'], ['z', 'a'])
        self.assertEqual(method['required_parameters'], ['z', 'required'])
        self.assertEqual(method['keyword_only_parameters'], ['required', 'optional'])
        self.assertEqual(method['positional_only_parameters'], ['z'])
        self.assertEqual(method['parameters'], {'z': None, 'a': None, 'required': None, 'optional': None})

    def test_log_and_signal_arguments_cannot_hide_calls(self):
        for source in ['logger.info(self.clear_boss())', 'print(self.clear_boss())',
                       'raise CampaignEnd(self.clear_boss())',
                       'raise CampaignEnd() from self.clear_boss()',
                       'return super(self.clear_boss()).battle_0()']:
            with self.subTest(source=source):
                steps, complete, issues, _ = plan(source)
                self.assertFalse(complete)
                self.assertEqual(steps, [])
                self.assertTrue(issues)


if __name__ == '__main__':
    unittest.main()
