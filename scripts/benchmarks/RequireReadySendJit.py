"""Require dispatch listings for the code paths present in each benchmark source."""


def require_dispatch_evidence(log: str, *, candidate: bool) -> None:
    # Observation entry points now wrap these cores. Require the cores that
    # actually choose and dispatch the typed reply, not only wrapper listings.
    methods = ['ConvertResponseCoreAsync', 'StringOrNullCoreAsync', 'BytesOrNullCoreAsync']
    if candidate:
        methods.append('SendOnReadyPrimaryAsync')
    for method in methods:
        header = f'Assembly listing for method Respire.RespireClient:{method}['
        if header not in log:
            raise ValueError(f'Missing dispatch JIT evidence: {method}')
