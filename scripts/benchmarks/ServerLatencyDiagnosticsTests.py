import copy
from contextlib import chdir
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from ServerLatencyDiagnostics import COMMON, UNCACHED, main, validate_cases, validate_measurements, wait_for_redis


class RedisReadinessTests(unittest.TestCase):
    def setUp(self):
        self.now = 0
        self.run = self.enterContext(patch('ServerLatencyDiagnostics.subprocess.run'))
        self.enterContext(patch('ServerLatencyDiagnostics.time.monotonic', side_effect=lambda: self.now))
        self.enterContext(patch('ServerLatencyDiagnostics.time.sleep', side_effect=self.advance))

    def advance(self, seconds):
        self.now += seconds

    def test_retries_connection_failure_and_requires_pong(self):
        self.run.side_effect = [
            subprocess.CompletedProcess([], 1, '', 'Connection refused'),
            subprocess.CompletedProcess([], 0, 'LOADING Redis is loading', ''),
            subprocess.CompletedProcess([], 0, 'PONG\n', ''),
        ]
        wait_for_redis('owned-container')
        self.assertEqual(3, self.run.call_count)
        self.assertEqual(1, self.now)
        self.assertEqual('owned-container', self.run.call_args.args[0][2])

    def test_unavailable_server_stops_at_deadline(self):
        self.run.return_value = subprocess.CompletedProcess([], 1, '', 'Connection refused')
        with self.assertRaisesRegex(TimeoutError, 'Connection refused'):
            wait_for_redis('owned-container', timeout=2)
        self.assertEqual(2, self.now)

    def test_hung_ping_is_bounded_by_remaining_deadline(self):
        def hung_ping(command, **kwargs):
            self.advance(kwargs['timeout'])
            raise subprocess.TimeoutExpired(command, kwargs['timeout'])

        self.run.side_effect = hung_ping
        with self.assertRaisesRegex(TimeoutError, 'PING timed out'):
            wait_for_redis('owned-container', timeout=7)
        self.assertEqual(7, self.now)
        self.assertEqual([5, 1.5], [call.kwargs['timeout'] for call in self.run.call_args_list])


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

    def test_failed_case_preserves_prior_observations_and_removes_owned_container(self):
        cases = self.cases()
        original_directory = Path.cwd()
        with tempfile.TemporaryDirectory() as temporary, chdir(original_directory):
            root = Path(temporary)
            artifacts = root / 'benchmarks/Respire.ComparisonBenchmarks/BenchmarkDotNet.Artifacts'
            reports = artifacts / 'results'
            reports.mkdir(parents=True)
            (reports / 'test-report-full-compressed.json').write_text(json.dumps({
                'Benchmarks': [{'FullName': 'Respire.ComparisonBenchmarks.' + case['name']}
                               for case in cases]}))

            def run(command, **kwargs):
                if cases[0]['name'] in command:
                    (Path(command[3]) / 'observation.json').write_text('{"completed": true}')
                elif cases[1]['name'] in command:
                    raise subprocess.CalledProcessError(1, command)

            with patch('ServerLatencyDiagnostics.__file__', str(root / 'scripts/benchmarks/script.py')), \
                 patch.dict(os.environ, REDIS_CONTAINER_ID='service-container'), \
                 patch('ServerLatencyDiagnostics.subprocess.check_output', side_effect=[
                     json.dumps(cases), 'image-id', 'owned-container', '[]']), \
                 patch('ServerLatencyDiagnostics.wait_for_redis'), \
                 patch('ServerLatencyDiagnostics.subprocess.run', side_effect=run) as runner:
                with self.assertRaises(subprocess.CalledProcessError):
                    main()
                runner.assert_called_with(['docker', 'rm', '-f', 'owned-container'], check=True)
            self.assertEqual([{'completed': True}], json.loads(
                (artifacts / 'server-latency/observations.json').read_text()))


if __name__ == '__main__':
    unittest.main()
