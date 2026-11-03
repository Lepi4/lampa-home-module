"""Mirror a checked APK from a trusted release source; clients download from LAN."""
import hashlib
import json
import os
import pathlib
import shutil
import tempfile
import time
import urllib.request
import release_sources


def config():
    path = pathlib.Path(os.environ.get('LMPC_APK_SOURCE_CONFIG', '/opt/lmpc-control/apk-source.json'))
    if path.exists():
        value = json.loads(path.read_text())
        if type(value.get('enabled', True)) is not bool:
            raise RuntimeError('invalid_sync_enabled')
        return value
    return {'enabled': True, 'source': {'provider': os.environ.get('LMPC_APK_PROVIDER', 'github'),
            'repo': os.environ.get('LMPC_APK_REPO', 'Lepi4/lampa-home-module')}}


def fetch(request):
    return urllib.request.urlopen(request, timeout=60)


def write_manifest(root, value, previous=False):
    target = root / 'latest.json'
    if previous and target.exists():
        temp_previous = root / 'previous-latest.json.part'
        shutil.copy2(target, temp_previous)
        os.replace(temp_previous, root / 'previous-latest.json')
    temp = root / 'latest.json.part'
    temp.write_text(json.dumps(value, indent=2))
    os.replace(temp, target)
    try:
        shutil.chown(target, user='lampac', group='lampac')
    except (PermissionError, LookupError):
        pass


def sync(root, source, opener=fetch):
    root = pathlib.Path(root)
    release = release_sources.discover_apk(source, opener)
    root.mkdir(parents=True, exist_ok=True)
    current_path = root / 'latest.json'
    current = json.loads(current_path.read_text()) if current_path.exists() else {}
    current_code = int(current.get('version_code', 0))
    code = release['version_code']
    manifest = {'version_code': code, 'version_name': release['version_name'], 'package': 'app.lampac.home.mc',
                'file': release['name'], 'sha256': release['sha256'], 'bytes': release['bytes'],
                'published_utc': release['published_utc'], 'source': release['provider'],
                'release': release['version'], 'download_url': release['url']}
    if code < current_code:
        return {'updated': False, 'reason': 'newer_installed', 'installed': current_code, 'latest': code, 'source': release['provider']}
    target = root / release['name']
    if code == current_code:
        if current.get('sha256') != release['sha256'] or current.get('bytes') != release['bytes']:
            raise RuntimeError('immutable_version_conflict')
        if current.get('file') != release['name'] or not target.is_file() or hashlib.sha256(target.read_bytes()).hexdigest() != release['sha256']:
            raise RuntimeError('installed_apk_mismatch')
        if current != manifest:
            write_manifest(root, manifest)
        return {'updated': False, 'reason': 'already_current', 'installed': current_code, 'latest': code, 'source': release['provider']}
    fd, name = tempfile.mkstemp(prefix=release['name'] + '.', suffix='.part', dir=root)
    os.close(fd)
    temp = pathlib.Path(name)
    try:
        manifest['download_url'] = release_sources.download(release, temp, opener, max_bytes=150_000_000)
        if target.exists():
            if hashlib.sha256(target.read_bytes()).hexdigest() != release['sha256']:
                raise RuntimeError('immutable_release_conflict')
        else:
            os.replace(temp, target)
        try:
            shutil.chown(target, user='lampac', group='lampac')
        except (PermissionError, LookupError):
            pass
        write_manifest(root, manifest, previous=True)
        for old in root.glob('*.apk'):
            if old.name != target.name and time.time() - old.stat().st_mtime > 14 * 86400:
                try:
                    old.unlink()
                except OSError:
                    pass
        return {'updated': True, 'version_code': code, 'file': target.name, 'sha256': release['sha256'], 'source': release['provider']}
    finally:
        temp.unlink(missing_ok=True)


def main():
    cfg = config()
    if not cfg.get('enabled', True):
        print(json.dumps({'updated': False, 'reason': 'disabled'}))
        return
    lock_path = pathlib.Path(os.environ.get('LMPC_APK_LOCK', '/run/lmpc-apk-sync.lock'))
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open('w') as lock:
        try:
            if os.name == 'posix':
                import fcntl
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            else:
                import msvcrt
                lock.write('0'); lock.flush(); lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            print(json.dumps(sync(os.environ.get('LMPC_APK_DIR', '/opt/lampac/releases/android'), cfg['source'])))
        except BlockingIOError:
            print(json.dumps({'updated': False, 'reason': 'already_running'}))
