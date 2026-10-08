"""Fail-closed parity evidence for generated mappers versus bracketed handwritten controls."""

import argparse
import hashlib
import json
import math
import re
import subprocess
import sys
from pathlib import Path


METHODS = {
    'HashWrite', 'HashRead', 'HashKey', 'JsonWrite', 'JsonRead', 'JsonKey',
    'VectorHashWrite', 'VectorHashRead', 'HashDefinition', 'JsonDefinition', 'HashValidate', 'JsonValidate',
}
PARAMETERS = {'Populated=False', 'Populated=True'}
PHASES = ('handwritten-validation', 'generated-validation', 'handwritten-a', 'generated', 'handwritten-b')
LATENCY_MARGIN = 1.10
MAX_DRIFT = 0.05
MAX_CV = 0.10


def finite(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f'Invalid numeric evidence: {value!r}')
    return value


def index_cases(cases, phase):
    expected_type = ('Generated' if phase.startswith('generated') else 'Handwritten') + 'MapperBenchmarks'
    indexed = {}
    for case in cases:
        identity = (case['Method'], case.get('Parameters', ''))
        if identity in indexed or identity[0] not in METHODS or identity[1] not in PARAMETERS:
            raise ValueError(f'{phase}: duplicate or unexpected row {identity}')
        if case['Type'] != expected_type:
            raise ValueError(f'{phase}: wrong implementation {case["Type"]}')
        stats = case['Statistics']
        mean, median, deviation = (finite(stats[name]) for name in ('Mean', 'Median', 'StandardDeviation'))
        samples = finite(stats['N'])
        allocated = finite(case['Memory']['BytesAllocatedPerOperation'])
        if mean <= 0 or median <= 0 or deviation < 0 or allocated < 0:
            raise ValueError(f'{phase}: invalid statistics {identity}')
        # A one-sample Dry run has undefined confidence bounds in BDN's JSON.
        # It proves execution only and cannot enter the measurement comparison.
        if not phase.endswith('validation'):
            lower, upper = (finite(stats['ConfidenceInterval'][name]) for name in ('Lower', 'Upper'))
            if not lower <= mean <= upper:
                raise ValueError(f'{phase}: invalid confidence interval {identity}')
        minimum = 1 if phase.endswith('validation') else 15
        if samples < minimum or int(samples) != samples:
            raise ValueError(f'{phase}: insufficient samples {identity}: {samples}')
        indexed[identity] = case
    if indexed.keys() != {(method, parameter) for method in METHODS for parameter in PARAMETERS}:
        raise ValueError(f'{phase}: incomplete evidence; require all 24 rows')
    return indexed


def verdict(a, generated, b):
    controls = (a, b)
    stats = generated['Statistics']
    allocation = generated['Memory']['BytesAllocatedPerOperation']
    if any(allocation > control['Memory']['BytesAllocatedPerOperation'] for control in controls):
        return 'FAIL: extra allocation'
    if all(stats['ConfidenceInterval']['Lower'] > LATENCY_MARGIN * control['Statistics']['ConfidenceInterval']['Upper'] for control in controls):
        return 'FAIL: latency exceeds 10% margin'
    drift = abs(b['Statistics']['Mean'] / a['Statistics']['Mean'] - 1)
    if drift > MAX_DRIFT:
        return 'INCONCLUSIVE: control drift exceeds 5%'
    if any(case['Statistics']['StandardDeviation'] / case['Statistics']['Mean'] > MAX_CV for case in (a, generated, b)):
        return 'INCONCLUSIVE: dispersion exceeds 10%'
    if all(control['Statistics']['ConfidenceInterval']['Lower'] > 0 and
           stats['ConfidenceInterval']['Upper'] <= LATENCY_MARGIN * control['Statistics']['ConfidenceInterval']['Lower'] for control in controls):
        return 'PASS'
    return 'INCONCLUSIVE: confidence bounds do not establish parity'


def summarize(phases, manifest):
    indexed = {phase: index_cases(phases[phase], phase) for phase in PHASES}
    runner = manifest.get('runner', {})
    lines = ['## Generated mapper parity', '',
             f"Source/PR head: `{manifest['head_sha']}`; event base: `{manifest['event_base_sha']}`.",
             f"Runner: `{runner.get('RUNNER_NAME', '')}` / `{runner.get('RUNNER_OS', '')}` / `{runner.get('RUNNER_ARCH', '')}`; "
             f"image `{runner.get('ImageOS', '')}` / `{runner.get('ImageVersion', '')}`; "
             f"run `{runner.get('GITHUB_RUN_ID', '')}`, attempt `{runner.get('GITHUB_RUN_ATTEMPT', '')}`.",
             'All phases use the same revision and runner. Order: handwritten A, generated, handwritten B.',
             'Parity requires generated upper 99.9% latency bound <= 1.10 times each control lower bound, '
             'no extra allocated bytes versus either control, <=5% control mean drift, and <=10% coefficient of variation in all phases.',
             'Failure or inconclusive evidence blocks acceptance. Dry results validate execution only. Inspect every row and raw evidence; never rerun merely for green.', '',
             '| Operation / input | A: mean / median / SD ns [99.9% CI], N | Generated: mean / median / SD ns [99.9% CI], N | B: mean / median / SD ns [99.9% CI], N | Generated/A | Generated/B | B/A drift | Bytes A/generated/B | Result |',
             '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |']
    accepted = True
    for identity in sorted(indexed['generated']):
        cases = [indexed[phase][identity] for phase in ('handwritten-a', 'generated', 'handwritten-b')]
        a, generated, b = (case['Statistics']['Mean'] for case in cases)
        times = []
        for case in cases:
            stats = case['Statistics']
            interval = stats['ConfidenceInterval']
            times.append(f"{stats['Mean']:.3f} / {stats['Median']:.3f} / {stats['StandardDeviation']:.3f} [{interval['Lower']:.3f}, {interval['Upper']:.3f}], {stats['N']}")
        allocated = '/'.join(str(case['Memory']['BytesAllocatedPerOperation']) for case in cases)
        result = verdict(*cases)
        accepted &= result == 'PASS'
        lines.append(f"| {' / '.join(identity)} | {' | '.join(times)} | {generated/a:.4f} | {generated/b:.4f} | {100*(b/a-1):+.2f}% | {allocated} | {result} |")
    lines.extend(['', 'Limitations: warmed, in-process conversion and metadata APIs on .NET 10; '
                  'three model types, two fixed input shapes, 16-dimensional FLOAT32 vectors. No Redis network, end-to-end query, Native AOT, '
                  'cold-start, retained-memory, other runtimes or hardware claims. Zero bytes in a BDN row is measurement evidence, not a zero-allocation unit assertion.', '',
                  'Runner and source hashes: `mapper-provenance.json`. Raw BDN JSON, measurements, runtime/CPU metadata and phase logs accompany this report.'])
    return '\n'.join(lines) + '\n', accepted


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('results', type=Path)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--summary', type=Path)
    args = parser.parse_args()
    accepted = False
    try:
        manifest = json.loads(args.manifest.read_text())
        head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
        if not re.fullmatch(r'[0-9a-f]{40}', manifest['head_sha']) or head != manifest['head_sha']:
            raise ValueError('Current source does not match the pinned PR head')
        if manifest['phases'] != list(PHASES) or not manifest['source_sha256'] or not manifest['runner']:
            raise ValueError('Missing source, runner or phase provenance')
        for path, expected in manifest['source_sha256'].items():
            if hashlib.sha256(Path(path).read_bytes()).hexdigest() != expected:
                raise ValueError(f'Source changed after pinning: {path}')
        phases = {}
        for phase in PHASES:
            if Path(f'{phase}.log').read_text().splitlines()[0] != manifest['head_sha']:
                raise ValueError(f'Wrong source in phase {phase}')
            reports = list((args.results / phase / 'results').glob('*-report-full-compressed.json'))
            if len(reports) != 1:
                raise ValueError(f'{phase}: require exactly one full JSON report')
            phases[phase] = json.loads(reports[0].read_text())['Benchmarks']
        report, accepted = summarize(phases, manifest)
    except (ValueError, KeyError, TypeError, OSError, IndexError) as error:
        report = f'## Generated mapper parity\n\nEvidence unavailable or invalid: {error}\n\nParity is not established. Inspect uploaded logs; do not rerun merely for green.\n'
    Path('mapper-parity.md').write_text(report)
    if args.summary:
        with args.summary.open('a') as stream:
            stream.write(report)
    print(report)
    return 0 if accepted else 1


if __name__ == '__main__':
    sys.exit(main())
