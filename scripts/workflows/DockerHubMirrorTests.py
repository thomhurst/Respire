import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import yaml


ROOT = Path(__file__).resolve().parents[2]
HOSTED_LINUX = "runner.os == 'Linux' && runner.environment == 'github-hosted'"


class DockerHubMirrorWorkflowTests(unittest.TestCase):
    def test_benchmarks_load_one_hosted_action_from_workflow_revision(self):
        for path in (ROOT / '.github/workflows').glob('benchmark-*.yml'):
            workflow = yaml.load(path.read_text(encoding='utf-8'), Loader=yaml.BaseLoader)
            for job in workflow.get('jobs', {}).values():
                steps = job.get('steps', [])
                mirror = next((step for step in steps if step.get('name') == 'Use Docker Hub mirror'), None)
                if mirror is None:
                    continue
                with self.subTest(workflow=path.name):
                    checkout = next(step for step in steps if step.get('id') == 'docker-mirror-checkout')
                    self.assertEqual(checkout['if'], HOSTED_LINUX)
                    self.assertEqual(checkout['with']['ref'], '${{ github.workflow_sha }}')
                    self.assertEqual(checkout['with']['sparse-checkout'], '.github/actions/docker-hub-mirror')
                    self.assertEqual(mirror['uses'], './docker-mirror/.github/actions/docker-hub-mirror')
                    self.assertIn(HOSTED_LINUX, mirror['if'])
                    self.assertLess(steps.index(checkout), steps.index(mirror))
                    self.assertNotIn('sudo cat /etc/docker/daemon.json', path.read_text(encoding='utf-8'))

    def test_shared_action_skips_self_hosted_runners(self):
        action = yaml.safe_load((ROOT / '.github/actions/docker-hub-mirror/action.yml').read_text(encoding='utf-8'))
        self.assertEqual(action['runs']['steps'][0]['if'], HOSTED_LINUX)


@unittest.skipUnless(shutil.which('bash') and shutil.which('jq'), 'bash and jq are required')
class DockerHubMirrorShellTests(unittest.TestCase):
    def run_action(self, config=None, **variables):
        action = yaml.safe_load((ROOT / '.github/actions/docker-hub-mirror/action.yml').read_text(encoding='utf-8'))
        script = action['runs']['steps'][0]['run']
        with TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / 'daemon.json'
            if config is not None:
                target.write_text(config, encoding='utf-8')
            script = script.replace('config=/etc/docker/daemon.json', 'config="$TEST_ROOT/daemon.json"')
            # Exercise the actual action under GitHub's explicit bash shell flags without sudo or Docker access.
            stubs = r'''
sudo() {
  case "$1" in
    cat) [ "${READ_FAIL:-0}" = 1 ] && return 1 ;;
    install) [ "${INSTALL_FAIL:-0}" = 1 ] && return 1 ;;
    kill)
      printf '%s\n' "$@" > "$TEST_ROOT/signal"
      [ "${SIGNAL_FAIL:-0}" = 1 ] && return 1
      return 0 ;;
  esac
  command "$@"
}
pidof() { [ -n "$DOCKER_PIDS" ] || return 1; echo "$DOCKER_PIDS"; }
docker() { [ "${INFO_FAIL:-0}" = 1 ] && return 1; echo "$DOCKER_MIRRORS"; }
mktemp() { command mktemp "$TEST_ROOT/updated.XXXXXX"; }
sleep() { :; }
'''
            environment = dict(os.environ, TEST_ROOT=root.as_posix(), DOCKER_PIDS='42',
                               DOCKER_MIRRORS='["https://mirror.gcr.io/"]')
            environment.update(variables)
            result = subprocess.run([shutil.which('bash'), '-e', '-o', 'pipefail', '-c', stubs + script],
                                    env=environment, capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(list(root.glob('updated.*')), [], 'Temporary config leaked')
            signal = root / 'signal'
            return result.stdout, target.read_text(encoding='utf-8') if target.exists() else None, signal.read_text(encoding='utf-8') if signal.exists() else None

    def test_absent_config_is_created_under_pipefail(self):
        output, config, signal = self.run_action()
        self.assertEqual(json.loads(config), {'registry-mirrors': ['https://mirror.gcr.io']})
        self.assertIn('Docker Hub pulls use mirror.gcr.io', output)
        self.assertEqual(signal.splitlines(), ['kill', '-HUP', '42'])

    def test_existing_config_and_mirrors_are_preserved_and_deduplicated(self):
        output, config, _ = self.run_action('{"debug":true,"registry-mirrors":["https://other.example","https://mirror.gcr.io"]}')
        self.assertEqual(json.loads(config), {'debug': True, 'registry-mirrors': ['https://mirror.gcr.io', 'https://other.example']})
        self.assertNotIn('::warning::', output)

    def test_invalid_config_warns_without_overwriting(self):
        output, config, signal = self.run_action('{invalid')
        self.assertIn('::warning::Could not read or parse', output)
        self.assertEqual(config, '{invalid')
        self.assertIsNone(signal)

    def test_read_failure_warns_without_overwriting(self):
        output, config, signal = self.run_action('{"debug":true}', READ_FAIL='1')
        self.assertIn('::warning::Could not read or parse', output)
        self.assertEqual(config, '{"debug":true}')
        self.assertIsNone(signal)

    def test_install_failure_warns_without_signaling(self):
        output, config, signal = self.run_action(INSTALL_FAIL='1')
        self.assertIn('::warning::Could not install', output)
        self.assertIsNone(config)
        self.assertIsNone(signal)

    def test_missing_dockerd_warns(self):
        output, _, signal = self.run_action(DOCKER_PIDS='')
        self.assertIn('::warning::dockerd is not running', output)
        self.assertIsNone(signal)

    def test_multiple_dockerd_pids_are_separate_arguments(self):
        output, _, signal = self.run_action(DOCKER_PIDS='42 43')
        self.assertEqual(signal.splitlines(), ['kill', '-HUP', '42', '43'])
        self.assertNotIn('::warning::', output)

    def test_reload_failure_warns(self):
        output, _, _ = self.run_action(SIGNAL_FAIL='1')
        self.assertIn('::warning::Could not reload dockerd', output)

    def test_unapplied_mirror_warns(self):
        output, _, _ = self.run_action(DOCKER_MIRRORS='[]')
        self.assertIn('::warning::Docker did not report mirror.gcr.io', output)

    def test_docker_info_failure_warns(self):
        output, _, _ = self.run_action(INFO_FAIL='1')
        self.assertIn('::warning::Docker did not report mirror.gcr.io', output)


if __name__ == '__main__':
    unittest.main()

