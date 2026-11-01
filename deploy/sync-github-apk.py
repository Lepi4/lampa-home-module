#!/usr/bin/env python3
"""Periodically mirror the newest signed LMPC APK from GitHub to Lampac.

The Android client continues to download only from the local Lampac endpoint.
GitHub is used by this root-owned server job, never by the client installer.
"""
import fcntl, hashlib, json, os, pathlib, re, shutil, sys, tempfile, time
from urllib.request import Request, urlopen

REPO = os.environ.get('LMPC_APK_REPO', 'Lepi4/lampa-home-module')
ROOT = pathlib.Path(os.environ.get('LMPC_APK_DIR', '/opt/lampac/releases/android'))
LOCK = pathlib.Path(os.environ.get('LMPC_APK_LOCK', '/run/lmpc-github-apk-sync.lock'))
API = f'https://api.github.com/repos/{REPO}/releases/latest'
NAME_RX = re.compile(r'^LMPC-([A-Za-z0-9.-]+)-(\d+)\.apk$')

def get(url):
    req = Request(url, headers={'User-Agent':'LMPC-GitHub-APK-Sync','Accept':'application/vnd.github+json'})
    with urlopen(req, timeout=30) as r: return r.read()

def main():
    ROOT.mkdir(parents=True, exist_ok=True)
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    with LOCK.open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        release = json.loads(get(API).decode('utf-8'))
        assets = release.get('assets') or []
        asset = next((a for a in assets if NAME_RX.fullmatch(a.get('name',''))), None)
        if not asset: raise RuntimeError('github_apk_asset_missing')
        digest = str(asset.get('digest',''))
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest): raise RuntimeError('github_apk_digest_missing')
        url = asset.get('browser_download_url','')
        if not url.startswith(f'https://github.com/{REPO}/releases/download/'): raise RuntimeError('github_apk_url_invalid')
        m = NAME_RX.fullmatch(asset['name']); version_code = int(m.group(2)); version_name = m.group(1) + '-mc'
        latest_path = ROOT / 'latest.json'
        current = json.loads(latest_path.read_text()) if latest_path.exists() else {}
        current_code = int(current.get('version_code', 0))
        if version_code <= current_code:
            print(json.dumps({'updated':False,'reason':'already_current','installed':current_code,'latest':version_code}))
            return
        expected_size = int(asset.get('size') or 0)
        if expected_size <= 0 or expected_size > 150_000_000: raise RuntimeError('github_apk_size_invalid')
        fd, temp_name = tempfile.mkstemp(prefix=asset['name']+'.', suffix='.part', dir=ROOT); os.close(fd)
        temp = pathlib.Path(temp_name); h = hashlib.sha256(); total = 0
        try:
            req = Request(url, headers={'User-Agent':'LMPC-GitHub-APK-Sync'})
            with urlopen(req, timeout=60) as src, temp.open('wb') as dst:
                while True:
                    chunk = src.read(1024*1024)
                    if not chunk: break
                    total += len(chunk)
                    if total > expected_size: raise RuntimeError('github_apk_size_overrun')
                    dst.write(chunk); h.update(chunk)
            sha = h.hexdigest()
            if total != expected_size or sha != digest[7:]: raise RuntimeError('github_apk_checksum_mismatch')
            target = ROOT / asset['name']
            if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() != sha: raise RuntimeError('immutable_release_conflict')
            if not target.exists(): os.replace(temp, target)
            else: temp.unlink()
            manifest = {'version_code':version_code,'version_name':version_name,'package':'app.lampac.home.mc','file':asset['name'],'sha256':sha,'bytes':total,'published_utc':release.get('published_at'),'source':'github','release':release.get('tag_name'),'download_url':url}
            next_manifest = ROOT / 'latest.json.part'; next_manifest.write_text(json.dumps(manifest, indent=2)); os.replace(next_manifest, latest_path)
            try:
                shutil.chown(target, user='lampac', group='lampac'); shutil.chown(latest_path, user='lampac', group='lampac')
            except PermissionError: pass
            for old in ROOT.glob('*.apk'):
                if old.name != target.name and time.time() - old.stat().st_mtime > 14*86400:
                    try: old.unlink()
                    except OSError: pass
            print(json.dumps({'updated':True,'version_code':version_code,'file':asset['name'],'sha256':sha,'source':'github'}))
        finally:
            temp.unlink(missing_ok=True)

if __name__ == '__main__':
    try: main()
    except BlockingIOError: print(json.dumps({'updated':False,'reason':'already_running'}))
