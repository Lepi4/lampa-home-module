"""Install reviewed control code on Proxmox; does not upgrade any media service.
Source tree is supplied through --source. Previous code/config are saved on host and CT.
"""
import argparse, datetime, json, os, pathlib, shutil, subprocess

def deploy(source):
    source=pathlib.Path(source);root=pathlib.Path('/opt/lmpc-control')
    def run(*args):return subprocess.check_output(args,text=True,timeout=90)
    assert run('pct','exec','116','--','hostname').strip()=='lampac'
    code_files=['release_sources.py','service-worker.py','service_agent.py','service_catalog.py']
    for name in code_files:compile((source/name).read_text(),name,'exec')
    # Refuse a controller restart while another user's job is active.
    probe="import pathlib,json;p=pathlib.Path('/opt/lampac/database/service-control');print(json.dumps([x.name for pat in ('*.running','*.request') for x in p.glob(pat)]))"
    assert not json.loads(run('pct','exec','116','--','python3','-c',probe)),'worker_busy'
    stamp=datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f');saved=root/'backups'/stamp;saved.mkdir(parents=True,mode=0o700)
    config=root/'config-production.json';shutil.copy2(config,saved/config.name)
    for name in code_files:
        if (root/name).exists():shutil.copy2(root/name,saved/name)
    cfg=json.loads(config.read_text());tests=['profiles-http.py','profiles-contract-http.py','history-http.py','profile-settings-http.py','service-policy-http.py']
    for c in cfg['components']:
        if c.get('source'):c.pop('download_proxy',None)
        if c['id'] in ('lampac','homemedia'):
            c.update(isolate_network=True,compatibility_tests=['/opt/lmpc-control/tests/'+n for n in tests],compatibility_fixture_dir='/opt/lmpc-control/tests/fixtures/compatibility')
        if c['id']=='lampac':c['update_note']='Ядро, библиотеки и Online-парсеры обновляются и откатываются одним комплектом. Локальные модули и настройки сохраняются.'
        if c['id']=='parsers':
            c.pop('source',None);c.pop('allow_package',None);c['managed_by']='lampac';c['name']='Парсеры Lampac';c['update_note']='Входят в комплект Lampac. Отдельное обновление отключено.'
        if c['id']=='homemedia':
            c['name']='HomeMedia'
            c['source']={'provider':'gitverse','repo':'Lepi4/lampa-home-module','branch':'master','manifest':'module-release.json','asset':'homemedia.tar.gz','format':'package'}
        if c['id']=='tvheadend':
            c.pop('allow_package',None);c.pop('source',None);c['monitor_only']=True;c['update_note']='Проверка установленной версии. Обновление DVB-сервера через эту панель отключено.'
        if c['id']=='channel-manager':
            c['source']={'provider':'gitverse','repo':'Lepi4/lmpc-channel-manager','branch':'main','manifest':'module-release.json','asset':'channel-manager.tar.gz','format':'package'}
            c['update_note']='GitVerse: Lepi4/lmpc-channel-manager. База каналов и параметры источников сохраняются; установка запускается вручную.'
    for c in cfg['components']:
        if c['id'] in ('homemedia','channel-manager'):
            c['source']['mirrors']=[dict(c['source'],provider='github')]
    cfg['automation_interval_hours']=24
    try:
        for name in code_files:
            temp=root/(name+'.next');shutil.copy2(source/name,temp);temp.chmod(0o644);os.replace(temp,root/name)
            run('pct','exec','116','--','mkdir','-p','/opt/lmpc-control/backups/'+stamp)
            backup_code="import pathlib,shutil;p=pathlib.Path('/opt/lmpc-control')/"+repr(name)+";b=pathlib.Path('/opt/lmpc-control/backups')/"+repr(stamp)+"/"+repr(name)+";shutil.copy2(p,b) if p.exists() else None"
            run('pct','exec','116','--','python3','-c',backup_code)
            run('pct','push','116',str(root/name),'/opt/lmpc-control/'+name)
        for name in tests+['fixtures/compatibility/kp_map.json']:
            target='/opt/lmpc-control/tests/'+name;run('pct','exec','116','--','mkdir','-p',str(pathlib.PurePosixPath(target).parent))
            run('pct','push','116',str(source/'tests'/name),target)
        temp=config.with_suffix('.next');temp.write_text(json.dumps(cfg,ensure_ascii=False,indent=2));temp.chmod(0o600);os.replace(temp,config)
        run('systemctl','restart','lmpc-control');assert run('systemctl','is-active','lmpc-control').strip()=='active'
    except Exception:
        for name in ['config-production.json']+code_files:
            if (saved/name).exists():shutil.copy2(saved/name,root/name)
            else:(root/name).unlink(missing_ok=True)
        for name in code_files:
            if (root/name).exists():run('pct','push','116',str(root/name),'/opt/lmpc-control/'+name)
            else:run('pct','exec','116','--','python3','-c',"import pathlib;(pathlib.Path('/opt/lmpc-control')/"+repr(name)+").unlink(missing_ok=True)")
        run('systemctl','restart','lmpc-control');raise
    receipt={'installed':True,'backup':str(saved),'media_services_restarted':False,'automation_default':'off','automatic_apply':False}
    (saved/'receipt.json').write_text(json.dumps(receipt,indent=2));print(json.dumps(receipt))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--source',required=True);a=p.parse_args();deploy(a.source)
