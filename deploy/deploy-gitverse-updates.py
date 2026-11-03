#!/usr/bin/env python3
"""Run on the Proxmox host. Updates the controller and APK mirror, not media services."""
import argparse
import base64
import datetime
import json
import os
import pathlib
import shutil
import subprocess

FILES = ['release_sources.py', 'apk_sync.py', 'sync-apk.py', 'sync-github-apk.py', 'service_catalog.py']


def deploy(source, ct, expected):
    root = pathlib.Path('/opt/lmpc-control')
    config = root / 'config-production.json'
    def run(*args, data=None):
        return subprocess.check_output(list(args), input=data, text=True, timeout=90)
    def inside(code, data=None):
        return run('pct', 'exec', str(ct), '--', 'python3', '-c', code, data=data)
    assert run('pct', 'exec', str(ct), '--', 'hostname').strip() == expected, 'wrong_container'
    cfg = json.loads(config.read_text())
    for c in cfg['components']:
        if c['id'] in ('homemedia', 'channel-manager'):
            assert c.get('ct') == ct, 'component_container_mismatch'
    for name in FILES:
        compile((source / name).read_text(), name, 'exec')
    busy = inside("import pathlib,json;p=pathlib.Path('/opt/lampac/database/service-control');print(json.dumps([x.name for pattern in ('*.request','*.running') for x in p.glob(pattern)]))")
    assert not json.loads(busy), 'controller_busy'
    stamp = 'gitverse-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    backup = root / 'backups' / stamp
    backup.mkdir(parents=True, mode=0o700, exist_ok=False)
    for name in FILES + ['config-production.json']:
        if (root / name).exists():
            shutil.copy2(root / name, backup / name)
    state = json.loads(inside(r'''
import pathlib,json,subprocess
paths=['/opt/lmpc-control/'+n for n in ['release_sources.py','apk_sync.py','sync-apk.py','sync-github-apk.py','service_catalog.py','apk-source.json']]
paths+=['/etc/systemd/system/'+n for n in ['lmpc-apk-sync.service','lmpc-apk-sync.timer']]
paths+=['/opt/lampac/releases/android/latest.json','/opt/lampac/releases/android/previous-latest.json']
files={p:__import__('base64').b64encode(pathlib.Path(p).read_bytes()).decode() if pathlib.Path(p).exists() else None for p in paths}
timers=[n for n in ['lmpc-apk-sync.timer','lmpc-github-apk-sync.timer'] if subprocess.run(['systemctl','is-enabled','--quiet',n],capture_output=True).returncode==0]
print(json.dumps({'files':files,'enabled':bool(timers),'timers':timers}))
'''))
    (backup / 'container-before.json').write_text(json.dumps(state))
    for c in cfg['components']:
        if c['id'] in ('homemedia', 'channel-manager'):
            repo = 'Lepi4/lampa-home-module' if c['id'] == 'homemedia' else 'Lepi4/lmpc-channel-manager'
            c['source'] = dict(c['source'], provider='gitverse', repo=repo, branch='master' if c['id'] == 'homemedia' else 'main', manifest='module-release.json')
            c['source'].pop('mirrors', None)
            c['source']['mirrors'] = [dict(c['source'], provider='github')]
            c['update_note'] = 'Источник: GitVerse · ' + repo + '. Подготовка с SHA-256 и проверкой совместимости; установка вручную, настройки сохраняются.'
    previous_apk = state['files'].get('/opt/lmpc-control/apk-source.json')
    mirror_enabled = json.loads(base64.b64decode(previous_apk)).get('enabled', state['enabled']) if previous_apk else state['enabled']
    apk_config = {'enabled': mirror_enabled, 'source': {'provider': 'gitverse', 'repo': 'Lepi4/lampa-home-module', 'branch': 'master', 'manifest': 'module-release.json'}}
    apk_config['source']['mirrors'] = [dict(apk_config['source'], provider='github')]
    service = '[Unit]\nDescription=Mirror LMPC APK from configured release source\nAfter=network-online.target lampac.service\nWants=network-online.target\n\n[Service]\nType=oneshot\nUser=root\nExecStart=/usr/bin/python3 /opt/lmpc-control/sync-apk.py\n'
    timer = '[Unit]\nDescription=Periodic LMPC APK mirror\n\n[Timer]\nOnBootSec=5min\nOnUnitActiveSec=30min\nPersistent=true\nUnit=lmpc-apk-sync.service\n\n[Install]\nWantedBy=timers.target\n'
    payload = {'files': {name: (source / name).read_text() for name in FILES}, 'apk_config': apk_config, 'service': service, 'timer': timer, 'stamp': stamp}
    run('systemctl', 'stop', 'lmpc-control.service')
    try:
        inside(r'''
import pathlib,json,sys,os,subprocess,shutil
v=json.load(sys.stdin);root=pathlib.Path('/opt/lmpc-control');b=root/'backups'/v['stamp'];b.mkdir(parents=True,mode=0o700)
for name in v['files']:
 p=root/name
 if p.exists():shutil.copy2(p,b/name)
def atomic(p,text,mode=0o644):
 t=p.with_name(p.name+'.gitverse-next');t.write_text(text);t.chmod(mode);os.replace(t,p)
subprocess.run(['systemctl','disable','--now','lmpc-github-apk-sync.timer'],check=False,capture_output=True)
subprocess.run(['systemctl','stop','lmpc-github-apk-sync.service'],check=False,capture_output=True)
subprocess.run(['systemctl','stop','lmpc-apk-sync.timer'],check=False,capture_output=True)
for name,text in v['files'].items():atomic(root/name,text)
atomic(root/'apk-source.json',json.dumps(v['apk_config'],indent=2),0o600)
atomic(pathlib.Path('/etc/systemd/system/lmpc-apk-sync.service'),v['service'])
atomic(pathlib.Path('/etc/systemd/system/lmpc-apk-sync.timer'),v['timer'])
subprocess.run(['systemctl','daemon-reload'],check=True)
subprocess.run(['systemctl','reset-failed','lmpc-github-apk-sync.service'],check=False,capture_output=True)
''', json.dumps(payload))
        for name in FILES:
            temp = root / (name + '.gitverse-next')
            shutil.copy2(source / name, temp); temp.chmod(0o644); os.replace(temp, root / name)
        temp = config.with_suffix('.gitverse-next')
        temp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2)); temp.chmod(0o600); os.replace(temp, config)
        # A failed source must be detected before changing the mirror's timer.
        inside("import subprocess;subprocess.run(['python3','/opt/lmpc-control/sync-apk.py'],check=True)")
        run('systemctl', 'start', 'lmpc-control.service')
        assert run('systemctl', 'is-active', 'lmpc-control.service').strip() == 'active'
        if state['enabled']:
            inside("import subprocess;subprocess.run(['systemctl','enable','--now','lmpc-apk-sync.timer'],check=True)")
    except Exception:
        inside(r'''
import pathlib,json,sys,base64,subprocess
v=json.load(sys.stdin)
subprocess.run(['systemctl','disable','--now','lmpc-apk-sync.timer'],check=False,capture_output=True)
for name,data in v['files'].items():
 p=pathlib.Path(name)
 if data is None:p.unlink(missing_ok=True)
 else:p.write_bytes(base64.b64decode(data))
subprocess.run(['systemctl','daemon-reload'],check=True)
for timer in v['timers']:subprocess.run(['systemctl','enable','--now',timer],check=True)
''', json.dumps(state))
        for name in FILES + ['config-production.json']:
            if (backup / name).exists():
                shutil.copy2(backup / name, root / name)
            else:
                (root / name).unlink(missing_ok=True)
        run('systemctl', 'start', 'lmpc-control.service')
        raise
    result = {'installed': True, 'host_backup': str(backup), 'container_backup': '/opt/lmpc-control/backups/' + stamp, 'apk_timer_enabled': state['enabled'], 'media_services_restarted': False, 'automatic_media_apply': False}
    (backup / 'receipt.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=pathlib.Path)
    parser.add_argument('--ct', required=True, type=int)
    parser.add_argument('--expected-hostname', required=True)
    args = parser.parse_args()
    deploy(args.source, args.ct, args.expected_hostname)
