#!/usr/bin/env python3
"""Register Lift in Lampac's real bootstrap; preserve service access to init.conf."""
import argparse
import datetime
import json
import os
import pathlib
import pwd
import shutil
import subprocess
import time
import urllib.request


def configure(path, service_user):
    data = json.loads(path.read_text(encoding='utf8'))
    items = data.setdefault('LampaWeb', {}).setdefault('customPlugins', [])
    if not isinstance(items, list):
        raise ValueError('customPlugins must be an array')
    hits = [x for x in items if isinstance(x, dict) and '/lift.js' in str(x.get('url', ''))]
    if hits:
        for item in hits:
            item['status'] = 1
    else:
        items.append({'url': '{localhost}/lift.js', 'status': 1, 'name': 'Lift VP9', 'author': 'home'})
    # Remove only the unused entry created by the previous Lift instructions.
    unused = data.get('plugins')
    if isinstance(unused, dict) and isinstance(unused.get('items'), list):
        unused['items'] = [x for x in unused['items'] if not isinstance(x, dict) or '/lift.js' not in str(x.get('url', ''))]
        if not unused['items'] and set(unused) == {'items'}:
            data.pop('plugins')
    original = path.stat()
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    backup = path.with_name(path.name + '.before-lift-' + stamp)
    shutil.copy2(path, backup)
    temp = path.with_name(path.name + '.lift.part')
    temp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    json.loads(temp.read_text(encoding='utf8'))
    # copy2/copystat alone do not preserve uid/gid on a root-created replacement.
    os.chown(temp, original.st_uid, pwd.getpwnam(service_user).pw_gid)
    os.chmod(temp, 0o640)
    os.replace(temp, path)
    return backup


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--expected-hostname', required=True)
    p.add_argument('--config', type=pathlib.Path, default=pathlib.Path('/opt/lampac/init.conf'))
    p.add_argument('--restart', action='store_true')
    a = p.parse_args()
    if pathlib.Path('/etc/hostname').read_text().strip() != a.expected_hostname:
        raise SystemExit('wrong target hostname')
    service_user = subprocess.check_output(['systemctl', 'show', 'lampac', '-p', 'User', '--value'], text=True).strip() or 'root'
    backup = configure(a.config, service_user)
    if a.restart:
        subprocess.run(['systemctl', 'restart', 'lampac'], check=True, timeout=120)
        ready = False
        for _ in range(30):
            try:
                with urllib.request.urlopen('http://127.0.0.1:9118/lampainit.js', timeout=2) as r:
                    ready = '/lift.js' in r.read().decode()
            except Exception:
                pass
            if ready:
                break
            time.sleep(1)
        if not ready:
            raise SystemExit('Lift missing from actual bootstrap; backup: ' + str(backup))
    print(json.dumps({'configured': True, 'bootstrap_verified': a.restart, 'backup': str(backup)}))


if __name__ == '__main__':
    main()
