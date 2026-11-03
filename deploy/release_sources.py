"""Trusted release sources for the root-owned updater (GitHub and GitVerse).

GitVerse's public raw manifest avoids distributing API tokens. Artifacts are
pinned to a full Git commit and always require an independent SHA-256 and size.
"""
import hashlib
import json
import pathlib
import re
import urllib.parse
import urllib.request

APK_NAME = re.compile(r'^LMPC-([A-Za-z0-9.-]+)-(\d+)\.apk$')


def source_key(source):
    return hashlib.sha256(json.dumps(source, sort_keys=True).encode()).hexdigest()


def repo_name(source):
    repo = source.get('repo', '')
    if not re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*', repo):
        raise RuntimeError('invalid_release_repository')
    return repo


def artifact_path(value):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9_./-]+', value):
        raise RuntimeError('invalid_artifact_path')
    if not value.startswith('release/') or any(p in ('', '.', '..') for p in value.split('/')):
        raise RuntimeError('invalid_artifact_path')
    return value


def raw_url(source, path, commit=None):
    repo = repo_name(source)
    if commit is not None:
        if not isinstance(commit, str) or not re.fullmatch(r'[0-9a-f]{40}', commit):
            raise RuntimeError('artifact_commit_required')
        selector = 'commit/' + commit
    else:
        branch = source.get('branch', 'master')
        if not re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]*', branch):
            raise RuntimeError('invalid_release_branch')
        selector = 'branch/' + branch
    return 'https://gitverse.ru/api/repos/' + repo + '/raw/' + selector + '/' + path


def get_json(source, fetch):
    provider = source.get('provider', 'github')
    if provider == 'github':
        url = 'https://api.github.com/repos/' + repo_name(source) + '/releases/latest'
        accept = 'application/vnd.github+json'
    elif provider == 'gitverse':
        path = source.get('manifest', 'module-release.json')
        if not re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]*\.json', path):
            raise RuntimeError('invalid_release_manifest')
        url = raw_url(source, path)
        accept = 'application/json,text/plain'
    else:
        raise RuntimeError('unsupported_release_provider')
    request = urllib.request.Request(url, headers={'User-Agent': 'LMPC-Updater', 'Accept': accept})
    with fetch(request) as response:
        data = response.read(1_048_577)
    if len(data) > 1_048_576:
        raise RuntimeError('release_manifest_too_large')
    result = json.loads(data)
    if not isinstance(result, dict):
        raise RuntimeError('invalid_release_manifest')
    return result


def normalize(source, manifest, asset, version):
    provider = source.get('provider', 'github')
    if provider == 'gitverse':
        path = artifact_path(asset.get('file'))
        commit = manifest.get('artifact_ref')
        if not isinstance(commit, str) or not re.fullmatch(r'[0-9a-f]{40}', commit):
            raise RuntimeError('artifact_commit_required')
        url = raw_url(source, path, commit)
        name = pathlib.PurePosixPath(path).name
        digest = asset.get('sha256', '')
        size = asset.get('bytes')
    else:
        name = asset.get('name', '')
        url = asset.get('browser_download_url', '')
        parsed = urllib.parse.urlsplit(url)
        expected = '/' + repo_name(source) + '/releases/download/'
        if parsed.scheme != 'https' or parsed.netloc != 'github.com' or not parsed.path.startswith(expected) or parsed.query or parsed.fragment:
            raise RuntimeError('unexpected_release_url')
        digest = asset.get('digest', '')
        digest = digest[7:] if isinstance(digest, str) and digest.startswith('sha256:') else ''
        size = asset.get('size')
    if not isinstance(digest, str) or not re.fullmatch(r'[0-9a-f]{64}', digest):
        raise RuntimeError('upstream_checksum_missing')
    if type(size) is not int or not 0 < size <= 2_000_000_000:
        raise RuntimeError('upstream_size_invalid')
    if not isinstance(version, str) or not version or len(version) > 100:
        raise RuntimeError('release_version_missing')
    return {'version': version, 'name': name, 'url': url, 'sha256': digest, 'bytes': size,
            'provider': provider, 'published_utc': manifest.get('published_utc') or manifest.get('published_at')}


def discover_component(source, component, fetch):
    manifest = get_json(source, fetch)
    if source.get('provider', 'github') == 'gitverse':
        asset = manifest.get('components', {}).get(component)
        if not isinstance(asset, dict):
            raise RuntimeError('release_asset_missing')
        if pathlib.PurePosixPath(artifact_path(asset.get('file'))).name != source.get('asset'):
            raise RuntimeError('release_asset_missing')
        return normalize(source, manifest, asset, asset.get('version'))
    asset = next((a for a in manifest.get('assets', []) if a.get('name') == source.get('asset')), None)
    if not asset:
        raise RuntimeError('release_asset_missing')
    return normalize(source, manifest, asset, manifest.get('tag_name'))


def discover_apk(source, fetch):
    manifest = get_json(source, fetch)
    if source.get('provider', 'github') == 'gitverse':
        asset = manifest.get('apk')
        if not isinstance(asset, dict):
            raise RuntimeError('release_apk_missing')
        result = normalize(source, manifest, asset, manifest.get('homemedia') or manifest.get('version'))
    else:
        choices = [a for a in manifest.get('assets', []) if APK_NAME.fullmatch(a.get('name', ''))]
        if not choices:
            raise RuntimeError('release_apk_missing')
        asset = max(choices, key=lambda a: int(APK_NAME.fullmatch(a['name'])[2]))
        result = normalize(source, manifest, asset, manifest.get('tag_name'))
    match = APK_NAME.fullmatch(result['name'])
    if not match or int(match[2]) <= 0:
        raise RuntimeError('invalid_apk_name')
    result.update(version_code=int(match[2]), version_name=match[1] + '-mc')
    if result['bytes'] > 150_000_000:
        raise RuntimeError('apk_size_invalid')
    if source.get('provider', 'github') == 'gitverse':
        if type(asset.get('versionCode')) is not int or asset['versionCode'] != result['version_code'] or asset.get('versionName') != result['version_name']:
            raise RuntimeError('apk_version_mismatch')
        if asset.get('package') != 'app.lampac.home.mc':
            raise RuntimeError('apk_package_mismatch')
    return result
