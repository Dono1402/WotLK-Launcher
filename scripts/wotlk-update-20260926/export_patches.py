"""Export reviewed local integration diffs as reproducible preparation inputs."""
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parents[2]
PORT = WORKSPACE / '.codex-stage/update-prep-20260926'
OUTPUT = HERE / 'patches'
OUTPUT.mkdir(exist_ok=True)
for name, base in [('playerbots', '7bae1b5c58c76a0aa20381155edc08096d1485b2'),
                   ('hermes', '5ea8767f0edbd1496511053d7a30136f7963b0a5')]:
    repo = PORT / (name + '-port')
    subprocess.run(['git', '-C', str(repo), 'diff', base, '--check'], check=True)
    patch = subprocess.check_output(['git', '-C', str(repo), 'diff', '--binary', base])
    (OUTPUT / (name + '-atlas.patch')).write_bytes(patch)
    print(name, len(patch))
