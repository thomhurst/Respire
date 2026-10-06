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

    def test_updates_run_every_requested_comparison_independently(self):
        for labels, expected in (
            ([], []),
            (['documentation'], []),
            (['run-sentinel-benchmarks'], ['main']),
            (['run-ready-strategy-benchmarks'], ['pre-strategy']),
            (['run-ready-strategy-benchmarks', 'documentation', 'run-sentinel-benchmarks'], ['main', 'pre-strategy']),
        ):
            with self.subTest(labels=labels):
                self.assertEqual(select_modes({
                    'action': 'synchronize',
                    'pull_request': {'labels': [{'name': label} for label in labels]},
                }), expected)

    def test_unrequested_events_do_not_start_measurements(self):
        self.assertEqual(select_modes({}), [])
        self.assertEqual(select_modes({'action': 'opened'}), [])


if __name__ == '__main__':
    unittest.main()
