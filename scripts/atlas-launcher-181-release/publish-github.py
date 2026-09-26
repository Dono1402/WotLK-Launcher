import hashlib, json, os, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path

repository = 'Dono1402/WotLK-Launcher'
tag = 'v1.8.1'
stage = Path(__file__).resolve().parent
config = json.loads((stage / 'github-inputs.json').read_text(encoding='utf-8'))
commit = config['commit']
def gh(*args): return subprocess.check_output(['gh', *args], text=True)
def api(path): return json.loads(gh('api', 'repos/' + repository + '/' + path))
def digest(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()

assert api('commits/' + tag)['sha'] == commit, 'Release tag resolves to another commit.'
assets = [stage / item['name'] for item in config['assets']]
expected = {item['name']: item for item in config['assets']}
assert set(expected) == {'AtlasLauncherSetup.exe', 'armory-runtime.zip', 'launcher-update.json', 'PATCH-NOTES.md', 'PATCH-NOTES.en.md', 'SHA256SUMS.txt'}
for path in assets:
    item = expected[path.name]
    assert path.stat().st_size == item['bytes'] and digest(path) == item['sha256'], 'Frozen GitHub file differs: ' + path.name
for line in (stage / 'SHA256SUMS.txt').read_text(encoding='utf-8').splitlines():
    sha, name = line.split('  ', 1)
    assert expected[name]['sha256'] == sha
notes = (stage / 'GITHUB-RELEASE.md').read_text(encoding='utf-8')
assert '# Atlas Launcher 1.8.1' in notes
mode = sys.argv[1] if len(sys.argv) == 2 else ''
assert mode in {'--prepare', '--publish', '--verify'}

def find_release():
    matches = [r for r in api('releases?per_page=100') if r['tag_name'] == tag]
    assert len(matches) <= 1
    return api('releases/' + str(matches[0]['id'])) if matches else None

release = find_release()
if mode == '--prepare':
    if release is None:
        url = gh('release', 'create', tag, '--repo', repository, '--target', commit,
            '--verify-tag', '--draft', '--title', 'Atlas Launcher 1.8.1',
            '--notes-file', str(stage / 'GITHUB-RELEASE.md'), *map(str, assets)).strip()
        print(json.dumps({'draftCreated': True, 'url': url, 'assetsUploaded': 6}), flush=True)
        release = find_release()
    assert release['draft'] is True, 'Preparation cannot change a published release.'

def verify_release(published):
    release = find_release()
    assert release is not None and release['draft'] is (not published) and release['prerelease'] is False
    assert release['name'] == 'Atlas Launcher 1.8.1', 'Unexpected GitHub release title.'
    assert release['body'].replace('\r\n', '\n').strip() == notes.replace('\r\n', '\n').strip(), 'GitHub notes differ beyond newline normalization.'
    assert release['author']['login'] == 'Dono1402'
    assert {asset['name'] for asset in release['assets']} == set(expected)
    for asset in release['assets']:
        item = expected[asset['name']]
        assert asset['size'] == item['bytes'] and asset['state'] == 'uploaded'
        assert asset['digest'] == 'sha256:' + item['sha256'], 'GitHub digest differs: ' + asset['name']
    return release

if mode == '--prepare':
    release = verify_release(False)
    print(json.dumps({'draftVerified': True, 'releaseId': release['id'], 'assets': 6, 'digestMatches': True}), flush=True)
    sys.exit(0)
if mode == '--publish':
    verify_release(False)
    gh('release', 'edit', tag, '--repo', repository, '--draft=false', '--latest')
release = verify_release(True)
assert api('releases/latest')['tag_name'] == tag
report = dict(published=True, tag=tag, commit=commit, url=release['html_url'], author=release['author']['login'],
    publishedAt=release['published_at'], verifiedAt=datetime.now(timezone.utc).isoformat(),
    draft=False, prerelease=False, isLatest=True, notesMatch=True, assetDigestsMatch=True,
    assets=[dict(name=a['name'], bytes=a['size'], sha256=expected[a['name']]['sha256'], url=a['browser_download_url']) for a in release['assets']])
(stage / 'github-publication.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
print(json.dumps(report), flush=True)
