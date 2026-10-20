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

    def test_predecessor_control_preserves_standalone_backlog_helpers(self):
        self.assert_current_production_control(standalone=True)

    def test_pre_optout_control_preserves_primary_backlog_helpers(self):
        self.assert_current_production_control(before_optout=True)

    def test_pre_redirect_control_preserves_primary_backlog_helpers(self):
        self.assert_current_production_control(before_redirect=True)

    def test_unknown_strategy_drift_fails_without_changing_production(self):
        self.assert_current_production_control(drift=True)

    def assert_current_production_control(self, standalone=False, drift=False, before_optout=False, before_redirect=False):
        repository = Path(__file__).resolve().parents[2]
        paths = ('src/Respire/RespireClient.cs', 'src/Respire/RespireClient.ReadySend.cs')
        with tempfile.TemporaryDirectory() as directory:
            checkout = Path(directory) / 'baseline'
            checkout.mkdir()
            for path in paths:
                target = checkout / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(repository / path, target)
            strategy = checkout / paths[1]
            if standalone:
                # The predecessor patch contains the complete prior strategy file.
                # Reconstruct that exact source rather than guessing old helper names.
                predecessor = (repository / 'scripts/benchmarks/ReadySendStrategy.Standalone.patch').read_text()
                added_file = predecessor.split('diff --git ', 2)[1]
                strategy.write_text('\n'.join(line[1:] for line in added_file.splitlines()
                                              if line.startswith('+') and not line.startswith('+++')) + '\n')
            if drift:
                strategy.write_text(strategy.read_text().replace('UsesPrimaryBacklog', 'UsesUnknownBacklog'))
            if before_optout:
                strategy.write_text(strategy.read_text().replace('            && !ShouldSkipTracking(operation, in command)\n', ''))
            if before_optout or before_redirect:
                strategy.write_text(strategy.read_text().replace(
                    '(!_core.Options.EnableReplicaRedirects || _core.Options.UseCluster)\n            && ', ''))
            ready = strategy.read_text()
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
            if drift:
                with self.assertRaisesRegex(RuntimeError, 'no longer matches'):
                    prepare(checkout, evidence)
                self.assertEqual(git('status', '--porcelain'), '')
                self.assertFalse(evidence.exists())
                return
            prepare(checkout, evidence)
            recorded = json.loads(evidence.read_text())
            self.assertEqual(recorded['baseline_commit'], git('rev-parse', 'HEAD'))
            self.assertEqual(recorded['before_sha256'], before)
            patch_name = ('ReadySendStrategy.Standalone.patch' if standalone else
                          'ReadySendStrategy.BeforeReplicaRedirects.patch' if before_redirect else
                          'ReadySendStrategy.BeforeOptOut.patch' if before_optout else 'ReadySendStrategy.patch')
            self.assertEqual(recorded['patch_file'], patch_name)
            self.assertEqual(recorded['patch_sha256'], hashlib.sha256(
                (repository / 'scripts/benchmarks' / patch_name).read_bytes()).hexdigest())
            self.assertEqual(recorded['after_sha256'][paths[0]], hashlib.sha256(
                (checkout / paths[0]).read_bytes()).hexdigest())
            self.assertIsNone(recorded['after_sha256'][paths[1]])
            self.assertEqual(set(git('diff', '--name-only').splitlines()), set(paths))
            self.assertEqual(evidence.with_suffix('.patch').read_text(), git('diff', '--binary') + '\n')
            control = (checkout / paths[0]).read_text()
            if not standalone and not before_optout:
                self.assertIn('&& !ShouldSkipTracking(operation, in command)', control)
            if not standalone and not before_optout and not before_redirect:
                self.assertIn('(!_core.Options.EnableReplicaRedirects || _core.Options.UseCluster)', control)
            # Compare complete relocated blocks, so ownership behavior cannot drift
            # while the names and interfaces still happen to compile.
            mutation_helpers = ready[ready.index('    // A typed source reports'):
                                     ready.index('    private readonly struct RawReadySend')]
            typed_helpers = ready[ready.index('    private readonly struct StringReadySend'):
                                  ready.rindex('\n}')]
            backlog_helpers = ready[ready.index('    private async ValueTask<TResult> SendBackloggedReadyAsync'):
                                    ready.index('    // A typed source reports')]
            raw_sender = ready[ready.index('    private readonly struct RawReadySend'):
                               ready.index('    private readonly struct StringReadySend')]
            self.assertIn(mutation_helpers, control)
            self.assertIn(typed_helpers, control)
            self.assertIn(backlog_helpers, control)
            self.assertIn(raw_sender, control)
            raw_start = control.index('    private ValueTask<RespValue> SendOnReadyPrimaryAsync')
            raw_body = control[raw_start:control.index('    private async ValueTask<TResult> SendBackloggedReadyAsync', raw_start)]
            expected_selection = '''            connection = UsesPrimaryBacklog && !CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)
                && !CursorCommandMetadata.IsServerCursorContinuation(in command)
                ? SelectPrimaryBacklogConnection(cancellationToken)
                : GetCircuitAwareConnection(multiplexer, cancellationToken);'''
            if standalone:
                expected_selection = expected_selection.replace('UsesPrimaryBacklog', 'UsesStandaloneBacklog').replace(
                    'SelectPrimaryBacklogConnection', 'SelectStandaloneConnection')
            self.assertIn(expected_selection, raw_body)
            self.assertIn('''        if (connection is null)
            return SendBackloggedReadyAsync<TCommand, RespValue, RawReadySend>(operation, command, cancellationToken,
                new RawReadySend(this, observation), default, null, default, false, observation);''', raw_body)
            self.assertIn('return SendOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation);', raw_body)
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
                    expected = f'''                var durationStarted = RespireTelemetry.CaptureCommandDurationStart(operation);
                RespireConnection? connection;
                try
                {{
                    connection = UsesPrimaryBacklog && !CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)
                        && !CursorCommandMetadata.IsServerCursorContinuation(in command)
                        ? SelectPrimaryBacklogConnection(ct)
                        : GetCircuitAwareConnection(readyMultiplexer, ct);
                }}
                catch (Exception error)
                {{
                    // The captured generation names the attempted data node, with or without Sentinel.
                    var endpoint = ct.IsCancellationRequested ? (RespireEndpoint?)null : readyMultiplexer.ActiveConnectionEndpoint;
                    if (core.Sentinel is not null)
                        return CaptureReadySendFailure<{result}>(error, cache, mutationFence, false, endpoint);
                    if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
                    throw;
                }}
                if (connection is null)
                    return SendBackloggedReadyAsync<TCommand, {result}, {sender}>(operation, command, ct, {sender_value},
                        durationStarted, cache, mutationFence, false, observation);
                if (core.Circuits is not null && !_snapshotPrefixedBinaryKeys)
                    return SendCircuitReadyAsync<TCommand, {result}, {sender}>(operation, connection, command, ct, {sender_value},
                        durationStarted, cache, mutationFence, observation);
                if (mutationFence.IsRequired)
                    return SendReadyMutationAsync<TCommand, {result}, {sender}>(connection, operation, in command, ct, {sender_value},
                        cache!, mutationFence, durationStarted, observation);
                try
                {{
                    return {send};
                }}
                catch (Exception error) when (core.Sentinel is not null || durationStarted.MetricEnabled)
                {{
                    return CaptureReadySendFailure<{result}>(error);
                }}
'''
                    if standalone:
                        expected = expected.replace('UsesPrimaryBacklog', 'UsesStandaloneBacklog').replace(
                            'SelectPrimaryBacklogConnection', 'SelectStandaloneConnection')
                    primary_start = body.index('                var durationStarted =')
                    primary_end = body.index('            }\n            else if', primary_start)
                    self.assertEqual(body[primary_start:primary_end], expected)


if __name__ == '__main__':
    unittest.main()
