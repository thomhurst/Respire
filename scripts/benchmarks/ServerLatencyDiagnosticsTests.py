import copy
import unittest

from ServerLatencyDiagnostics import COMMON, UNCACHED, validate_cases, validate_measurements


class ServerLatencyDiagnosticsTests(unittest.TestCase):
    def cases(self):
        return [dict(name=f'{suite}.{client}_{index}' + ('_ServerRead' if suite == 'ClientSideCachingBenchmarks' else ''), category=category,
                     operationsPerInvoke=(100 if 'x100 sequential' in category else
                                          50 if category == 'GET x50 concurrent' else
                                          200 if category == 'GET x200 pipelined' else 1))
                for suite, categories in [('CommonOperationsBenchmarks', COMMON),
                                          ('ClientSideCachingBenchmarks', UNCACHED)]
                for index, category in enumerate(sorted(categories))
                for client in ('Respire', 'StackExchange')]

    def test_all_21_ties_and_two_controls_have_both_clients(self):
        cases = self.cases()
        self.assertEqual(46, len(cases))
        validate_cases(cases)

    def test_missing_or_duplicate_baseline_fails(self):
        cases = self.cases()
        for altered in (cases[1:], cases + [cases[0]]):
            with self.assertRaises(ValueError):
                validate_cases(altered)

    def test_cache_hit_cannot_replace_server_read(self):
        cases = copy.deepcopy(self.cases())
        cases[-1]['name'] = cases[-1]['name'].replace('_ServerRead', '_ClientCacheHit')
        with self.assertRaises(ValueError):
            validate_cases(cases)

    def test_batch_normalization_cannot_be_lost(self):
        cases = self.cases()
        for case in cases:
            if case['operationsPerInvoke'] != 1:
                case['operationsPerInvoke'] = 1
                break
        with self.assertRaises(ValueError):
            validate_cases(cases)

    def test_bdn_results_must_cover_all_diagnostic_methods(self):
        cases = self.cases()
        benchmarks = [{'FullName': 'Respire.ComparisonBenchmarks.' + case['name']} for case in cases]
        validate_measurements(cases, [{'Benchmarks': benchmarks}])
        for altered in ([], benchmarks[1:], benchmarks[1:] + [benchmarks[1]]):
            with self.assertRaises(ValueError):
                validate_measurements(cases, [{'Benchmarks': altered}])


if __name__ == '__main__':
    unittest.main()
