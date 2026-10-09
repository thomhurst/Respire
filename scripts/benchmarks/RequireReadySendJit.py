"""Require dispatch listings for the code paths present in each benchmark source."""

import re


def has_generic_listing(log: str, method: str, arity: int, *, typed_sender: bool = False) -> bool:
    """Match outer generic arguments and optionally require a typed sender specialization."""
    header = f'; Assembly listing for method Respire.RespireClient:{method}['
    for line in log.splitlines():
        if not line.startswith(header):
            continue
        signature = line[len(header):]
        depth = 1
        arguments = []
        argument_start = 0
        for index, character in enumerate(signature):
            if character == '[':
                depth += 1
            elif character == ']':
                depth -= 1
                if depth == 0:
                    arguments.append(signature[argument_start:index].strip())
                    sender_matches = not typed_sender or re.fullmatch(
                        r'Respire\.RespireClient\+(?:StringReadySend|BytesReadySend|ConvertedReadySend`2\[.+\])',
                        arguments[-1]) is not None
                    if len(arguments) == arity and signature[index + 1:].startswith('(') and sender_matches:
                        return True
                    break
            elif character == ',' and depth == 1:
                arguments.append(signature[argument_start:index].strip())
                argument_start = index + 1
    return False


def require_dispatch_evidence(log: str, *, candidate: bool) -> None:
    # Observation entry points now wrap these cores. Require the cores that
    # actually choose and dispatch the typed reply, not only wrapper listings.
    methods = {'ConvertResponseCoreAsync': 3, 'StringOrNullCoreAsync': 1, 'BytesOrNullCoreAsync': 1}
    if candidate:
        methods['SendOnReadyPrimaryAsync'] = 3
    for method, arity in methods.items():
        # Raw PING also reaches the three-argument helper. Its RawReadySend
        # specialization does not establish typed strategy code generation.
        if not has_generic_listing(log, method, arity, typed_sender=method == 'SendOnReadyPrimaryAsync'):
            raise ValueError(f'Missing dispatch JIT evidence: {method}')
