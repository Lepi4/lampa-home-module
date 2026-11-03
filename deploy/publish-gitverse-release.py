#!/usr/bin/env python3
"""Publish immutable GitVerse files and a public checksum manifest (no API token)."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import tarfile


def publish(repo, component, version, package, apk=None, sdk=None, push=False, mirror_github=True):
    def git(*args):
        return subprocess.check_output(['git', '-C', str(repo), *args], text=True, encoding='utf-8').strip()
    assert re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.-]{0,99}', version), 'invalid_version'
    assert not git('status', '--porcelain'), 'commit_reviewed_source_changes_first'
    assert re.fullmatch(r'(git@gitverse\.ru:|https://gitverse\.ru/)[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git', git('remote', 'get-url', 'origin')), 'origin_must_be_gitverse'
    if mirror_github and push:
        name = re.split(r'[:/]', git('remote', 'get-url', 'origin'))[-2:]
        expected = '/'.join(name)
        assert git('remote', 'get-url', 'github') in ('https://github.com/' + expected, 'git@github.com:' + expected), 'wrong_github_mirror'
    branch = git('branch', '--show-current'); assert branch
    tag = 'gitverse-' + version
    assert not git('tag', '--list', tag), 'version_tag_already_exists'
    with tarfile.open(package, 'r:gz') as archive:
        meta = json.load(archive.extractfile('manifest.json'))
    assert meta.get('component') == component and meta.get('version') == version, 'package_manifest_mismatch'
    files = [(pathlib.Path(package), 'homemedia.tar.gz' if component == 'homemedia' else 'channel-manager.tar.gz')]
    apk_meta = None
    if apk:
        assert component == 'homemedia' and sdk, 'APK_requires_HomeMedia_and_Android_SDK'
        tools = sdk / 'build-tools/34.0.0'
        badging = subprocess.check_output([str(tools / ('aapt.exe' if os.name == 'nt' else 'aapt')), 'dump', 'badging', str(apk)], text=True, encoding='utf-8')
        m = re.search(r"package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'", badging)
        assert m and m[1] == 'app.lampac.home.mc', 'wrong_APK_package'
        certs = subprocess.check_output([str(tools / ('apksigner.bat' if os.name == 'nt' else 'apksigner')), 'verify', '--print-certs', str(apk)], text=True, encoding='utf-8')
        assert '71ea04563313864b3f8e78995bfb5592eefa9d68c7822550f4cd960eaad31813' in certs, 'wrong_APK_signer'
        name = 'LMPC-' + re.sub('-mc$', '', m[3]) + '-' + m[2] + '.apk'
        files.append((pathlib.Path(apk), name))
        apk_meta = {'versionCode': int(m[2]), 'versionName': m[3], 'package': m[1]}
    directory = repo / 'release' / version
    directory.mkdir(parents=True, exist_ok=True)
    entries = []
    for source, name in files:
        data = source.read_bytes(); digest = hashlib.sha256(data).hexdigest(); target = directory / name
        if target.exists():
            assert hashlib.sha256(target.read_bytes()).hexdigest() == digest, 'immutable_release_conflict'
        else:
            shutil.copy2(source, target)
        rel = target.relative_to(repo).as_posix()
        entries.append({'file': rel, 'sha256': digest, 'bytes': len(data)})
        git('add', '-f', '--', rel)
    if git('diff', '--cached', '--name-only'):
        git('commit', '-m', 'Publish immutable ' + component + ' artifacts ' + version)
    artifact_ref = git('rev-parse', 'HEAD')
    manifest_path = repo / 'module-release.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8')) if manifest_path.exists() else {}
    manifest.update(schema=1, artifact_ref=artifact_ref, published_utc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    manifest.setdefault('components', {})[component] = dict(entries[0], version=version)
    if component == 'homemedia':
        manifest.update(homemedia=version, package=entries[0]['file'])
    else:
        manifest['version'] = version
    if apk_meta:
        manifest['apk'] = dict(entries[1], **apk_meta)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    git('add', '--', 'module-release.json')
    git('commit', '-m', 'Advance GitVerse release manifest to ' + version)
    git('tag', '-a', tag, '-m', component + ' ' + version)
    if push:
        git('push', 'origin', branch, tag)
        if mirror_github:
            # Fast-forward only. Never overwrite independent mirror changes.
            try:
                git('push', 'github', branch, tag)
            except subprocess.CalledProcessError as error:
                raise RuntimeError('GitVerse published successfully; GitHub mirror push failed. Retry git push github ' + branch + ' ' + tag) from error
    print(json.dumps({'repo': str(repo), 'version': version, 'artifact_ref': artifact_ref, 'manifest_commit': git('rev-parse', 'HEAD'), 'pushed': push}))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--repo', type=pathlib.Path, required=True)
    p.add_argument('--component', choices=['homemedia', 'channel-manager'], required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--package', type=pathlib.Path, required=True)
    p.add_argument('--apk', type=pathlib.Path)
    p.add_argument('--sdk', type=pathlib.Path, default=os.environ.get('ANDROID_HOME'))
    p.add_argument('--push', action='store_true')
    p.add_argument('--no-github-mirror', dest='mirror_github', action='store_false', help='Skip the default push to the github mirror')
    a = p.parse_args()
    publish(a.repo.resolve(), a.component, a.version, a.package, a.apk, a.sdk, a.push, a.mirror_github)
