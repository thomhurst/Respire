"""Select independent Sentinel controls from the triggering event, without precedence."""

import json
import os
from pathlib import Path

LABEL_MODES = {
    'run-sentinel-benchmarks': 'main',
    'run-ready-strategy-benchmarks': 'pre-strategy',
}


def select_modes(event: dict) -> list[str]:
    if event.get('action') == 'labeled':
        mode = LABEL_MODES.get(event.get('label', {}).get('name'))
        return [mode] if mode else []
    return []


if __name__ == '__main__':
    modes = select_modes(json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text()))
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write(f'enabled={str(bool(modes)).lower()}\n')
        output.write('modes=' + json.dumps(modes) + '\n')
