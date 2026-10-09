import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from PrepareReadySendBaseline import git_environment, prepare


class PrepareReadySendBaselineTests(unittest.TestCase):
    def test_control_ignores_caller_git_repository_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.dict(os.environ, {
                'GIT_DIR': str(Path(directory) / 'other.git'),
                'GIT_WORK_TREE': directory,
                'GIT_INDEX_FILE': str(Path(directory) / 'other.index'),
            }):
                self.assert_current_production_control()
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_current_production_control_preserves_other_routes_and_records_evidence(self):
        self.assert_current_production_control()

    def assert_current_production_control(self):
        repository = Path(__file__).resolve().parents[2]
        paths = ('src/Respire/RespireClient.cs', 'src/Respire/RespireClient.ReadySend.cs')
        with tempfile.TemporaryDirectory() as directory:
            checkout = Path(directory) / 'baseline'
            checkout.mkdir()
            for path in paths:
                target = checkout / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(repository / path, target)
            environment = git_environment()

            def git(*arguments):
                return subprocess.check_output(['git', '-C', str(checkout), *arguments],
                                               text=True, env=environment).strip()
            git('init', '--quiet')
            git('add', '.')
            git('-c', 'user.name=Control test', '-c', 'user.email=control@example.invalid',
                'commit', '--quiet', '-m', 'Current production snapshot')
            before = {path: hashlib.sha256((checkout / path).read_bytes()).hexdigest() for path in paths}
            evidence = Path(directory) / 'control.json'
            prepare(checkout, evidence)
            recorded = json.loads(evidence.read_text())
            self.assertEqual(recorded['baseline_commit'], git('rev-parse', 'HEAD'))
            self.assertEqual(recorded['before_sha256'], before)
            self.assertEqual(recorded['patch_sha256'], hashlib.sha256(
                (repository / 'scripts/benchmarks/ReadySendStrategy.patch').read_bytes()).hexdigest())
            self.assertEqual(recorded['after_sha256'][paths[0]], hashlib.sha256(
                (checkout / paths[0]).read_bytes()).hexdigest())
            self.assertIsNone(recorded['after_sha256'][paths[1]])
            self.assertEqual(set(git('diff', '--name-only').splitlines()), set(paths))
            self.assertEqual(evidence.with_suffix('.patch').read_text(), git('diff', '--binary') + '\n')
            control = (checkout / paths[0]).read_text()
            ready = (repository / paths[1]).read_text()
            # Compare complete relocated blocks, so ownership behavior cannot drift
            # while the names and interfaces still happen to compile.
            mutation_helpers = ready[ready.index('    // A typed source reports'):
                                     ready.index('    private readonly struct RawReadySend')]
            typed_helpers = ready[ready.index('    private readonly struct StringReadySend'):
                                  ready.rindex('\n}')]
            self.assertIn(mutation_helpers, control)
            self.assertIn(typed_helpers, control)
            # Later replica, cluster, circuit and mutation ownership contracts remain available.
            for marker in ('TryDispatchReplica', 'SendOnReadyClusterAsync', 'SendCircuitReadyAsync',
                           'CompleteObservedMutationAsync', 'IReadySend<TResult>'):
                self.assertIn(marker, control)
            self.assertNotIn('SendOnReadyPrimaryAsync<TCommand, TResult, TSend>', control)
            for method, result, sender, send in (
                ('ConvertResponseCoreAsync', 'TResult',
                 'ConvertedReadySend<TState, TResult>',
                 'connection.SendConvertedAsync(in command, state, converter, transferOwnership, ct, operation,\n'
                 '                        durationStarted: durationStarted, observation: observation)'),
                ('StringOrNullCoreAsync', 'string?', 'StringReadySend',
                 'connection.SendStringAsync(in command, ct, operation, durationStarted: durationStarted, observation: observation)'),
                ('BytesOrNullCoreAsync', 'byte[]?', 'BytesReadySend',
                 'connection.SendBytesAsync(in command, ct, operation, durationStarted: durationStarted, observation: observation)'),
            ):
                with self.subTest(method=method):
                    start = control.index(f' {method}<')
                    body = control[start:control.index('\n    private ', start)]
                    sender_value = ('new ConvertedReadySend<TState, TResult>(state, converter, transferOwnership)'
                                    if method == 'ConvertResponseCoreAsync' else 'default')
                    expected = f'''                var durationStarted = RespireTelemetry.CaptureOperationStart(operation);
                RespireConnection connection;
                try {{ connection = GetCircuitAwareConnection(readyMultiplexer, ct); }}
                catch (Exception error)
                {{
                    if (core.Sentinel is not null)
                        return CaptureReadySendFailure<{result}>(error, cache, mutationFence);
                    if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
                    throw;
                }}
                // Preserve later circuit and mutation ownership paths; only ordinary
                // ready-primary dispatch returns to its pre-strategy direct send.
                if (core.Circuits is not null && !_snapshotPrefixedBinaryKeys)
                    return SendCircuitReadyAsync<TCommand, {result}, {sender}>(
                        operation, connection, command, ct, {sender_value}, durationStarted, cache, mutationFence, observation);
                if (mutationFence.IsRequired)
                    return SendReadyMutationAsync<TCommand, {result}, {sender}>(
                        connection, operation, in command, ct, {sender_value}, cache!, mutationFence, durationStarted, observation);
                try
                {{
                    return {send};
                }}
                catch (Exception error) when (core.Sentinel is not null || durationStarted.MetricEnabled)
                {{
                    return CaptureReadySendFailure<{result}>(error);
                }}
'''
                    primary_start = body.index('                var durationStarted =')
                    primary_end = body.index('            }\n            else if', primary_start)
                    self.assertEqual(body[primary_start:primary_end], expected)


if __name__ == '__main__':
    unittest.main()
