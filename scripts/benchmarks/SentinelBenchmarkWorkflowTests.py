import itertools
import re
import unittest
from pathlib import Path


class SentinelBenchmarkWorkflowTests(unittest.TestCase):
    def test_out_of_order_heads_and_independent_controls_cannot_cancel_each_other(self):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-sentinel-routing.yml'
        group = re.search(r'^\s+group: (.+)$', workflow.read_text(), re.MULTILINE).group(1)
        keys = []
        for head, framework, baseline in itertools.product(
                ('older-head', 'current-head'), ('net8.0', 'net10.0'), ('main', 'pre-strategy')):
            context = {
                'github.workflow': 'Sentinel ready routing benchmarks',
                'github.event.pull_request.number': '1074',
                'github.event.pull_request.head.sha': head,
                'matrix.framework': framework,
                'matrix.baseline': baseline,
            }
            keys.append(re.sub(r'\$\{\{\s*(.*?)\s*\}\}',
                               lambda match: context[match.group(1)], group))
        # Even if the older selector completes last, none of its jobs share a
        # cancellation group with the current head or another requested control.
        self.assertEqual(len(set(keys)), 8)


if __name__ == '__main__':
    unittest.main()
