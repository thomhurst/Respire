"""Hosted, task-scoped investigation for #1744. Never used for published BDN scores."""

import json
import os
import subprocess
from pathlib import Path


COMMON = {
    'EXISTS', 'GET', 'GET long', 'GET x100 sequential', 'HGET', 'HSET', 'INCR',
    'LPUSH+LPOP', 'PING', 'PING x100 sequential', 'SADD', 'SET 10KB', 'SET 13B',
    'SET 1KB', 'SET x100 sequential', 'SET+DEL', 'SISMEMBER',
    'GET x50 concurrent', 'GET x200 pipelined',
}
UNCACHED = {'EXISTS hot', 'GET hot', 'GET missing hot', 'HGET hot'}


def validate_cases(cases):
    expected = {(suite, category, client)
                for suite, categories in [('CommonOperationsBenchmarks', COMMON),
                                          ('ClientSideCachingBenchmarks', UNCACHED)]
                for category in categories for client in ('Respire', 'StackExchange')}
    actual = set()
    for case in cases:
        suite, method = case['name'].split('.')
        if suite == 'ClientSideCachingBenchmarks' and not method.endswith('_ServerRead'):
            raise ValueError(f'Not an uncached control: {case}')
        category = case['category']
        identity = (suite, category, method.split('_')[0])
        if identity in actual:
            raise ValueError(f'Duplicate diagnostic case: {identity}')
        actual.add(identity)
        operations = (100 if 'x100 sequential' in category else
                      50 if category == 'GET x50 concurrent' else
                      200 if category == 'GET x200 pipelined' else 1)
        if case['operationsPerInvoke'] != operations:
            raise ValueError(f'Wrong operation normalization: {case}')
    if actual != expected:
        raise ValueError(f'Wrong diagnostic coverage: missing={expected - actual}, extra={actual - expected}')


def validate_measurements(cases, reports):
    names = ['.'.join(benchmark['FullName'].split('.')[-2:])
             for report in reports for benchmark in report['Benchmarks']]
    if len(names) != len(cases) or set(names) != {case['name'] for case in cases}:
        raise ValueError('BenchmarkDotNet results must cover exactly the 46 diagnostic methods')


def main():
    root = Path(__file__).resolve().parents[2]
    os.chdir(root)
    output = root / 'benchmarks/Respire.ComparisonBenchmarks/BenchmarkDotNet.Artifacts/server-latency'
    output.mkdir(parents=True, exist_ok=True)
    dll = root / 'tools/Respire.ServerLatencyDiagnostics/bin/Release/net10.0/Respire.ServerLatencyDiagnostics.dll'
    subprocess.run(['dotnet', 'build', 'tools/Respire.ServerLatencyDiagnostics', '-c', 'Release', '-f', 'net10.0'], check=True)
    cases = json.loads(subprocess.check_output(['dotnet', str(dll), '--list'], text=True))
    validate_cases(cases)
    validate_measurements(cases, [json.loads(path.read_text()) for path in
                                 (output.parent / 'results').glob('*-report-full-compressed.json')])
    (output / 'cases.json').write_text(json.dumps(cases, indent=2))
    container_id = os.environ['REDIS_CONTAINER_ID']
    # Reuse the exact service image, not a second resolution of the mutable tag.
    image = subprocess.check_output(['docker', 'inspect', container_id, '--format', '{{.Image}}'], text=True).strip()
    host_id = None
    observations = []
    try:
        host_id = subprocess.check_output([
            'docker', 'run', '--detach', '--network', 'host', image,
            'redis-server', '--port', '6380'], text=True).strip()
        (output / 'containers.json').write_text(subprocess.check_output(
            ['docker', 'inspect', container_id, host_id], text=True))
        # Fail promptly if the host-network server is unavailable.
        subprocess.run(['docker', 'exec', host_id, 'redis-cli', '-p', '6380', 'PING'], check=True)
        # Bracket the host-network control with the original bridge path on the SAME runner.
        # A fresh process per method keeps per-client process CPU attributable to that workload.
        for phase, port, mode in [('bridge-a', '6379', 'plain'), ('host', '6380', 'plain'),
                                  ('bridge-b', '6379', 'plain'), ('bridge-profile', '6379', 'trace')]:
            environment = dict(os.environ, REDIS_HOST='127.0.0.1', REDIS_PORT=port, LATENCY_PHASE=phase)
            for case in cases:
                directory = output / phase / case['name']
                directory.mkdir(parents=True, exist_ok=True)
                with (directory / 'console.log').open('w') as log:
                    subprocess.run(['dotnet', str(dll), case['name'], str(directory), mode],
                                   env=environment, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=90)
                    if mode == 'trace':
                        subprocess.run(['dotnet', str(dll), '--summarize', str(directory / 'cpu.nettrace'),
                                        str(directory / 'stacks.json')], stdout=log, stderr=subprocess.STDOUT,
                                       check=True, timeout=90)
                observations.append(json.loads((directory / 'observation.json').read_text()))
        (output / 'observations.json').write_text(json.dumps(observations, indent=2))
    finally:
        if host_id is not None:
            subprocess.run(['docker', 'rm', '-f', host_id], check=True)


if __name__ == '__main__':
    main()
