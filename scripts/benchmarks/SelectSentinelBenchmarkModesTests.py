import unittest

from SelectSentinelBenchmarkModes import select_modes


class SelectSentinelBenchmarkModesTests(unittest.TestCase):
    def test_label_event_selects_only_the_requested_comparison_even_with_both_labels(self):
        for label, expected in (
            ('run-sentinel-benchmarks', ['main']),
            ('run-ready-strategy-benchmarks', ['pre-strategy']),
            ('documentation', []),
        ):
            with self.subTest(label=label):
                self.assertEqual(select_modes({
                    'action': 'labeled', 'label': {'name': label},
                    'pull_request': {'labels': [{'name': 'run-sentinel-benchmarks'},
                                                {'name': 'run-ready-strategy-benchmarks'}]},
                }), expected)

    def test_unrequested_events_do_not_start_measurements(self):
        self.assertEqual(select_modes({}), [])
        self.assertEqual(select_modes({'action': 'opened'}), [])
        self.assertEqual(select_modes({
            'action': 'synchronize',
            'pull_request': {'labels': [{'name': 'run-sentinel-benchmarks'}]},
        }), [])


if __name__ == '__main__':
    unittest.main()
