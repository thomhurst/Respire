"""Restore pre-strategy dispatch in an isolated benchmark checkout, failing on drift.

ReadySendStrategy.patch contains only the production dispatch change from
445869b2f3284ee82c7fcf40537eb4f498b104e2 and its later readonly-core comment.
Apply it in reverse to keep the baseline's runtime, dependencies, transport,
test infrastructure, and other code identical to the merge's first parent.
"""

import hashlib
import json
import subprocess
import sys
from pathlib import Path


def prepare(checkout: Path, evidence: Path) -> None:
    checkout = checkout.resolve()
    patch = Path(__file__).with_name('ReadySendStrategy.patch').resolve()

    def git(*arguments: str) -> str:
        return subprocess.check_output(['git', '-C', str(checkout), *arguments], text=True).strip()

    if Path(git('rev-parse', '--show-toplevel')).resolve() != checkout:
        raise RuntimeError('The baseline must be a separate checkout root.')
    if git('status', '--porcelain'):
        raise RuntimeError('The baseline checkout must be clean before applying the control.')

    paths = {'src/Respire/RespireClient.cs', 'src/Respire/RespireClient.ReadySend.cs'}

    def hashes() -> dict[str, str | None]:
        return {path: hashlib.sha256((checkout / path).read_bytes()).hexdigest()
                if (checkout / path).exists() else None for path in sorted(paths)}

    before = hashes()
    try:
        git('apply', '--reverse', '--check', str(patch))
    except subprocess.CalledProcessError as error:
        raise RuntimeError(
            'ReadySendStrategy.patch no longer matches the baseline. In a separate clean checkout '
            'of the new baseline, manually restore only the pre-strategy dispatch from '
            '445869b2f3284ee82c7fcf40537eb4f498b104e2^ and remove the strategy file, preserving '
            'unrelated changes. Save git diff --binary -R for the two production paths as the '
            'replacement patch. Build and validate both frameworks and inspect the recorded '
            'source hashes before accepting that regenerated control; do not copy an entire historical client file.'
        ) from error
    git('apply', '--reverse', str(patch))
    if set(git('diff', '--name-only').splitlines()) != paths:
        raise RuntimeError('The control changed an unexpected set of production files.')
    if (checkout / 'src/Respire/RespireClient.ReadySend.cs').exists():
        raise RuntimeError('The strategy file was not removed by the control.')
    git('diff', '--check')
    evidence.write_text(json.dumps({
        'baseline_commit': git('rev-parse', 'HEAD'),
        'control': 'pre-strategy dispatch; reversed production-only patch',
        'patch_sha256': hashlib.sha256(patch.read_bytes()).hexdigest(),
        'before_sha256': before,
        'after_sha256': hashes(),
    }, indent=2) + '\n')
    evidence.with_suffix('.patch').write_text(git('diff', '--binary') + '\n')


if __name__ == '__main__':
    prepare(Path(sys.argv[1]), Path(sys.argv[2]))
