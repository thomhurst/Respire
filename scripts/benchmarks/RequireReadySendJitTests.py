import unittest

from RequireReadySendJit import require_dispatch_evidence


def listing(method, types=None):
    if types is None:
        types = ('Respire.Commands.Cmd1,System.__Canon,long'
                 if method == 'ConvertResponseCoreAsync' else 'Respire.Commands.Cmd1')
    return f'; Assembly listing for method Respire.RespireClient:{method}[{types}]() (Tier0)\n'


class RequireReadySendJitTests(unittest.TestCase):
    def setUp(self):
        self.baseline = ''.join(listing(method) for method in (
            'ConvertResponseCoreAsync', 'StringOrNullCoreAsync', 'BytesOrNullCoreAsync'))
        self.candidate = self.baseline + listing(
            'SendOnReadyPrimaryAsync',
            'Respire.Commands.Cmd1,System.__Canon,Respire.RespireClient+StringReadySend')

    def test_raw_overload_does_not_satisfy_strategy_evidence(self):
        with self.assertRaisesRegex(ValueError, 'SendOnReadyPrimaryAsync'):
            require_dispatch_evidence(self.baseline + listing('SendOnReadyPrimaryAsync'), candidate=True)

    def test_nested_generics_and_array_arguments_preserve_strategy_arity(self):
        for types in (
            'Respire.Commands.Cmd1,ubyte[],Respire.RespireClient+BytesReadySend',
            'Respire.Commands.Cmd1,bool,Respire.RespireClient+ConvertedReadySend`2[System.__Canon,bool]',
        ):
            with self.subTest(types=types):
                require_dispatch_evidence(self.baseline + listing('SendOnReadyPrimaryAsync', types), candidate=True)

    def test_nested_commas_do_not_turn_raw_overload_into_strategy_evidence(self):
        raw = listing('SendOnReadyPrimaryAsync', 'Other.Command`3[System.__Canon,bool,int]')
        with self.assertRaisesRegex(ValueError, 'SendOnReadyPrimaryAsync'):
            require_dispatch_evidence(self.baseline + raw, candidate=True)

    def test_pre_strategy_dispatch_does_not_require_the_unused_strategy_helper(self):
        require_dispatch_evidence(self.baseline, candidate=False)

    def test_candidate_requires_both_typed_entries_and_strategy_helper(self):
        require_dispatch_evidence(self.candidate, candidate=True)
        with self.assertRaisesRegex(ValueError, 'SendOnReadyPrimaryAsync'):
            require_dispatch_evidence(self.baseline, candidate=True)

    def test_missing_typed_dispatch_is_rejected_for_both_sources(self):
        for candidate, log in ((False, self.baseline), (True, self.candidate)):
            for method in ('ConvertResponseCoreAsync', 'StringOrNullCoreAsync', 'BytesOrNullCoreAsync'):
                with self.subTest(candidate=candidate, method=method):
                    with self.assertRaisesRegex(ValueError, method):
                        require_dispatch_evidence(log.replace(listing(method), ''), candidate=candidate)

    def test_unrelated_listing_or_plain_method_reference_is_not_evidence(self):
        for log in ('', listing('ConnectAsync'), 'ConvertResponseCoreAsync StringOrNullCoreAsync BytesOrNullCoreAsync',
                    ''.join(listing(method) for method in
                            ('ConvertResponseAsync', 'StringOrNullAsync', 'BytesOrNullAsync')),
                    self.baseline.replace('Respire.RespireClient:', 'Other.Client:')):
            with self.subTest(log=log):
                with self.assertRaises(ValueError):
                    require_dispatch_evidence(log, candidate=False)


if __name__ == '__main__':
    unittest.main()
