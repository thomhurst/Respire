"""Require dispatch listings for the code paths present in each benchmark source."""


def has_generic_listing(log: str, method: str, arity: int) -> bool:
    """Count outer generic arguments without counting nested types or arrays."""
    header = f'; Assembly listing for method Respire.RespireClient:{method}['
    for line in log.splitlines():
        if not line.startswith(header):
            continue
        signature = line[len(header):]
        depth = 1
        arguments = 1
        for index, character in enumerate(signature):
            if character == '[':
                depth += 1
            elif character == ']':
                depth -= 1
                if depth == 0:
                    if arguments == arity and signature[index + 1:].startswith('('):
                        return True
                    break
            elif character == ',' and depth == 1:
                arguments += 1
    return False


def require_dispatch_evidence(log: str, *, candidate: bool) -> None:
    # Observation entry points now wrap these cores. Require the cores that
    # actually choose and dispatch the typed reply, not only wrapper listings.
    methods = {'ConvertResponseCoreAsync': 3, 'StringOrNullCoreAsync': 1, 'BytesOrNullCoreAsync': 1}
    if candidate:
        methods['SendOnReadyPrimaryAsync'] = 3
    for method, arity in methods.items():
        if not has_generic_listing(log, method, arity):
            raise ValueError(f'Missing dispatch JIT evidence: {method}')
