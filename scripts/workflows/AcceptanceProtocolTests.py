import itertools
import os
import shutil
import subprocess
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import yaml


class AcceptanceProtocolTests(unittest.TestCase):
    def test_both_protocols_report_before_the_step_returns_failure(self):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml'
        job = yaml.safe_load(workflow.read_text(encoding='utf-8'))['jobs']['stackexchange-interop']
        bash = shutil.which('bash')
        if os.name == 'nt':
            # Use Git Bash rather than the Windows Subsystem for Linux launcher.
            bash = str(Path(os.environ['ProgramFiles']) / 'Git/bin/bash.exe')
        self.assertTrue(bash, 'Bash is required to exercise the workflow scripts')

        suites = (
            ('Respire.StackExchangeCompat.Tests', 'stackexchange', 219),
            ('Respire.Hangfire.Acceptance.Tests', 'hangfire', 95),
            ('Respire.Orleans.Acceptance.Tests', 'orleans', 28),
        )
        for (project, results, minimum), framework, failures in itertools.product(
                suites, job['strategy']['matrix']['framework'], ('', '2', '3', '2 3')):
            with self.subTest(project=project, framework=framework, failures=failures):
                step, = [step for step in job['steps']
                         if f'--project tests/{project} ' in step.get('run', '')
                         and '--no-build' in step['run']]
                self.assertEqual(step['shell'], 'bash')
                self.assertNotIn('continue-on-error', step)
                upload, = [step for step in job['steps']
                           if step.get('with', {}).get('path') == f'TestResults/{results}']
                self.assertEqual(upload['if'], 'always()')
                self.assertTrue(upload['uses'].startswith('actions/upload-artifact@'))

                # A controlled executable substitutes for dotnet, writes a report
                # even on failure, and checks the real loop's test/report options.
                stub = r'''
export GITHUB_WORKSPACE="$PWD"
dotnet() {
  printf '%s\n' "$RESPIRE_TEST_PROTOCOL" >> invocations
  local directory='' minimum='' report=0
  while (( $# )); do
    case "$1" in
      --results-directory) directory="$2"; shift ;;
      --minimum-expected-tests) minimum="$2"; shift ;;
      --report-trx) report=1 ;;
    esac
    shift
  done
  [[ -n "$directory" && "$minimum" == "$EXPECTED_MINIMUM" && "$report" == 1 ]] || return 99
  mkdir -p "$directory"
  printf '<TestRun />\n' > "$directory/result.trx"
  case " $FAIL_PROTOCOLS " in
    *" $RESPIRE_TEST_PROTOCOL "*)
      if [[ "$RESPIRE_TEST_PROTOCOL" == 2 ]]; then return 9; fi
      return 17 ;;
  esac
  return 0
}
'''
                script = stub + step['run'].replace('${{ matrix.framework }}', framework)
                with TemporaryDirectory() as directory:
                    completed = subprocess.run(
                        [bash, '--noprofile', '--norc', '-e', '-o', 'pipefail', '-c', script],
                        cwd=directory, capture_output=True, text=True, timeout=15,
                        env={**os.environ, 'FAIL_PROTOCOLS': failures, 'EXPECTED_MINIMUM': str(minimum)},
                    )
                    expected_status = 17 if '3' in failures else 9 if failures else 0
                    self.assertEqual(completed.returncode, expected_status, completed.stderr)
                    self.assertEqual((Path(directory) / 'invocations').read_text().splitlines(), ['2', '3'])
                    for protocol in failures.split():
                        self.assertIn(f'RESP{protocol} failed with exit code', completed.stdout)
                    reports = Path(directory) / 'TestResults' / results / framework
                    self.assertEqual(sorted(str(path.relative_to(reports)).replace('\\', '/')
                                            for path in reports.rglob('*.trx')),
                                     ['resp2/result.trx', 'resp3/result.trx'])


if __name__ == '__main__':
    unittest.main()
