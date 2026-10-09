import hashlib
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from PrepareReadySendBaseline import prepare


class PrepareReadySendBaselineTests(unittest.TestCase):
    def test_current_production_control_preserves_other_routes_and_records_evidence(self):
        repository = Path(__file__).resolve().parents[2]
        paths = ('src/Respire/RespireClient.cs', 'src/Respire/RespireClient.ReadySend.cs')
        with tempfile.TemporaryDirectory() as directory:
            checkout = Path(directory) / 'baseline'
            checkout.mkdir()
            for path in paths:
                target = checkout / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(repository / path, target)
            def git(*arguments):
                return subprocess.check_output(['git', '-C', str(checkout), *arguments], text=True).strip()
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
            self.assertIsNone(recorded['after_sha256'][paths[1]])
            self.assertEqual(set(git('diff', '--name-only').splitlines()), set(paths))
            self.assertIn('diff --git', evidence.with_suffix('.patch').read_text())
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


if __name__ == '__main__':
    unittest.main()
