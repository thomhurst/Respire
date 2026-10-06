"""Require dispatch listings for the code paths present in each benchmark source."""


def require_dispatch_evidence(log: str, *, candidate: bool) -> None:
    # Pre-strategy typed dispatch lives in these entry points. The strategy
    # candidate retains them and adds its constrained generic send helper.
    methods = ['ConvertResponseAsync', 'StringOrNullAsync', 'BytesOrNullAsync']
    if candidate:
        methods.append('SendOnReadyPrimaryAsync')
    for method in methods:
        header = f'Assembly listing for method Respire.RespireClient:{method}['
        if header not in log:
            raise ValueError(f'Missing dispatch JIT evidence: {method}')
