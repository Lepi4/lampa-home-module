#!/usr/bin/env python3
"""Root-owned executor, invoked only with root-owned configuration by service-worker.
No request may supply commands, target paths, units, download URLs or CT numbers.
"""
from contextlib import closing
import fnmatch, hashlib, json, os, pathlib, re, shutil, sqlite3, subprocess, tarfile, tempfile, time, urllib.request

def catalog(c):
    import sys,importlib
    folder=str(pathlib.Path(globals().get('__file__','/opt/lmpc-control/service_agent.py')).parent)
    if folder not in sys.path:sys.path.insert(0,folder)
    return importlib.import_module('service_catalog')

def protected(c):
    common=['*.db','*.db-wal','*.db-shm','*.sql','config.json','settings.json','viewed.json','venv']
    if c['id']=='lampac':common+=['database','cache','.cache','data','mods','plugins','yt','releases','logs','.aspnet','.local','.config','.playwright','init.conf*','current.conf','passwd*','appsettings.Production.json']
    return common+list(c.get('preserve',[]))

def is_protected(c,relative):
    path=pathlib.PurePosixPath(relative).as_posix()
    return any(fnmatch.fnmatchcase(path,p) or path.startswith(p.rstrip('/')+'/') or ('/' not in p and any(fnmatch.fnmatchcase(part,p) for part in pathlib.PurePosixPath(path).parts)) for p in protected(c))

def command(*args):
    return subprocess.run(args,check=True,capture_output=True,text=True,timeout=90).stdout.strip()

def atomic(path, value):
    p=pathlib.Path(path);p.parent.mkdir(parents=True,exist_ok=True)
    tmp=p.with_suffix(p.suffix+'.tmp');tmp.write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8');os.replace(tmp,p)

def health(c):
    if c.get('verify'):
        try:
            p=subprocess.run(c['verify'],capture_output=True,text=True,timeout=15)
            if p.returncode!=0:return False
            if c.get('version_expect') and c['version_expect'] not in p.stdout:return False
        except Exception:return False
    if c.get('health'):
        try:
            with urllib.request.urlopen(c['health'],timeout=3) as r:
                data=r.read(65536)
                if r.status!=200:return False
                if c.get('health_json'):return json.loads(data).get('module')=='HomeMedia' and json.loads(data).get('ok') is True
                return True
        except Exception:
            if c.get('legacy_health'):
                try:
                    with urllib.request.urlopen(c['legacy_health'],timeout=3) as r:
                        return r.status==200 and isinstance(json.load(r).get('profiles'),list)
                except Exception:pass
            return False
    if c.get('unit'):return subprocess.run(['systemctl','is-active','--quiet',c['unit']],timeout=10).returncode==0
    return pathlib.Path(c['root']).exists()

def wait_health(c):
    for _ in range(c.get('health_attempts',25)):
        if health(c):return
        time.sleep(2)
    raise RuntimeError('health_check_failed')

def idle(c):
    # The conservative guard deliberately also postpones some unrelated traffic.
    if not c.get('unit'):
        if c['id']=='yt-dlp':
            for p in pathlib.Path('/proc').glob('[0-9]*/cmdline'):
                try:
                    if c['root'].encode() in p.read_bytes().split(b'\0'):raise RuntimeError('busy: yt-dlp extraction is active; retry later')
                except (OSError,PermissionError):pass
        return
    pid=command('systemctl','show',c['unit'],'-p','MainPID','--value')
    if not pid or pid=='0':return
    def tx():
        result={};key=None
        for line in command('ss','-tinpH').splitlines():
            if re.match(r'^\S+\s+\d+\s+\d+\s+',line):
                key=line.split('users:')[0].strip() if re.search(r'pid='+re.escape(pid)+r'[,)]',line) else None
            if key:
                m=re.search(r'bytes_sent:(\d+)',line)
                if m:result[key]=int(m[1])
        return result
    # Other components now share the LXC: their streams must not block this one.
    before=tx();time.sleep(2);after=tx()
    if sum(max(0,v-before.get(k,0)) for k,v in after.items())/2>100*1024:raise RuntimeError('busy: retry after playback stops')

def snapshot(c, dest):
    root=pathlib.Path(c['root']).resolve();dest.mkdir(parents=True,exist_ok=False)
    if not root.exists():raise RuntimeError('component_not_installed')
    if root.is_dir():
        # Runtime caches are retained in place by restore(), not duplicated into
        # every program backup. With all services on one disk this matters.
        omitted=set(c.get('snapshot_exclude',[]))
        if c['id']=='lampac':omitted.update(['cache','.cache','logs','releases','.playwright','yt'])
        shutil.copytree(root,dest/'payload',symlinks=True,ignore=lambda p,n:[x for x in n if pathlib.Path(p)==root and x in omitted])
    else:shutil.copy2(root,dest/'payload')
    db=c.get('database')
    if db and pathlib.Path(db).exists():
        with closing(sqlite3.connect(db)) as src,closing(sqlite3.connect(dest/'home.db')) as dst:src.backup(dst)
        with closing(sqlite3.connect(dest/'home.db')) as conn:
            if conn.execute('PRAGMA quick_check').fetchone()[0]!='ok':raise RuntimeError('backup_database_corrupt')
    for i,name in enumerate(c.get('state_paths',[])):
        p=pathlib.Path(name)
        if p.is_dir():shutil.copytree(p,dest/('state-'+str(i)),symlinks=True)
        elif p.is_file():shutil.copy2(p,dest/('state-'+str(i)))
    version=catalog(c).read(c).get('installed') if c.get('source') or c.get('allow_package') else c.get('version')
    atomic(dest/'receipt.json',{'component':c['id'],'at':int(time.time()*1000),'root':str(root),'version':version})

def restore(c, saved, preserve_state=True):
    root=pathlib.Path(c['root']).resolve()
    if not (saved/'receipt.json').exists():raise RuntimeError('backup_incomplete')
    # Hold current state outside the replaced program directory, on the same FS.
    # Manual code rollback must not rewind views/settings written after an update.
    held=pathlib.Path(tempfile.mkdtemp(prefix='.lmpc-state-',dir=root.parent))
    preserved=[]
    try:
        if preserve_state and root.is_dir():
            for p in sorted(root.rglob('*'),key=lambda p:len(p.parts)):
                rel=p.relative_to(root).as_posix()
                if is_protected(c,rel) and not any(rel.startswith(name+'/') for name in preserved):
                    (held/rel).parent.mkdir(parents=True,exist_ok=True)
                    shutil.move(str(p),str(held/rel));preserved.append(rel)
        if root.is_dir():shutil.rmtree(root)
        elif root.exists():root.unlink()
        if (saved/'payload').is_dir():shutil.copytree(saved/'payload',root,symlinks=True)
        else:shutil.copy2(saved/'payload',root)
    finally:
        for name in preserved:
            p=root/name
            if p.is_dir():shutil.rmtree(p)
            elif p.exists() or p.is_symlink():p.unlink()
            p.parent.mkdir(parents=True,exist_ok=True);shutil.move(str(held/name),str(p))
        shutil.rmtree(held)
    if not preserve_state and c.get('database') and (saved/'home.db').exists():
        for suffix in ('-wal','-shm'):pathlib.Path(c['database']+suffix).unlink(missing_ok=True)
        shutil.copy2(saved/'home.db',c['database'])
    for i,name in enumerate([] if preserve_state else c.get('state_paths',[])):
        p=pathlib.Path(name);extra=saved/('state-'+str(i))
        if p.is_dir():shutil.rmtree(p)
        elif p.exists():p.unlink()
        if extra.is_dir():shutil.copytree(extra,p,symlinks=True)
        elif extra.exists():shutil.copy2(extra,p)
    ownership(c)

def ownership(c):
    if c.get('owner'):
        command('chown','-R',c['owner'],c['root'])
        if c.get('database'):command('chown',c['owner'],c['database'])

def unpack(c, dest):
    package=pathlib.Path(c['package']);expected=c['sha256']
    if len(expected)!=64 or hashlib.sha256(package.read_bytes()).hexdigest()!=expected:raise RuntimeError('package_checksum_mismatch')
    dest.mkdir(parents=True,exist_ok=False)
    with tarfile.open(package,'r:*') as tf:
        members=tf.getmembers()
        if len(members)>10000 or sum(m.size for m in members)>2*1024**3:raise RuntimeError('package_too_large')
        for m in members:
            rel=pathlib.PurePosixPath(m.name)
            if rel.is_absolute() or '..' in rel.parts or not (m.isfile() or m.isdir()):raise RuntimeError('unsafe_package_member')
            if not rel.parts or rel.parts[0] not in ('payload','manifest.json'):raise RuntimeError('invalid_package_layout')
            if len(rel.parts)>1 and is_protected(c,pathlib.PurePosixPath(*rel.parts[1:])):raise RuntimeError('package_contains_state')
        tf.extractall(dest, members=members)
    manifest=json.loads((dest/'manifest.json').read_text())
    if manifest.get('component')!=c['id'] or not manifest.get('version'):raise RuntimeError('wrong_component')
    if not (dest/'payload').exists():raise RuntimeError('empty_package')
    # Core versions are only approved after compiling HomeMedia against that core.
    if c['id'] in ('lampac','homemedia') and manifest.get('compatibility_verified') is not True:raise RuntimeError('compatibility_not_verified')
    return manifest

def recover(c, base):
    """Restore an interrupted transaction before the worker accepts new jobs."""
    folder=pathlib.Path(base).resolve()/c['id'];journal=folder/'transaction.json'
    if not journal.exists():return {'ok':True,'recovered':False}
    job=json.loads(journal.read_text());saved=pathlib.Path(job['backup']).resolve()
    if saved.parent!=folder:raise RuntimeError('invalid_recovery_backup')
    if job.get('applying'):
        if c.get('unit'):command('systemctl','stop',c['unit'])
        restore(c,saved)
        if c.get('source') or c.get('allow_package'):
            v=json.loads((saved/'receipt.json').read_text()).get('version');catalog(c).installed(c,v)
            if c['id']=='lampac':catalog(c).installed(dict(c,id='parsers'),v)
    if c.get('unit') and job.get('running'):command('systemctl','start',c['unit']);wait_health(c)
    journal.unlink();return {'ok':True,'recovered':True,'backup':str(saved)}

def operate(c, action, base):
    if action not in ('check','prepare','package','start','stop','update','rollback'):raise RuntimeError('unknown_action')
    if (c.get('monitor_only') or c.get('managed_by')) and action!='check':raise RuntimeError('component_read_only')
    if action=='package':return catalog(c).accept_package(c,c['candidate_upload'])
    if action=='prepare':return catalog(c).prepare(c)
    if action=='check':
        healthy=health(c)
        info={}
        if c.get('source'):
            try:info={'release':catalog(c).discover(c,force=True)}
            except Exception as e:info={'update_error':str(e)}
        return {'ok':healthy,'healthy':healthy,**info,**({} if healthy else {'error':'health_check_failed'})}
    if action in ('start','stop'):
        if action=='stop' and c['id'] in ('lampac','homemedia','parsers'):raise RuntimeError('web_panel_service_cannot_be_stopped')
        if not c.get('unit'):raise RuntimeError('not_a_service')
        if action=='stop':idle(c)
        command('systemctl',action,c['unit'])
        if action=='start':wait_health(c)
        return {'ok':True,'action':action}
    base=pathlib.Path(base).resolve()/c['id'];base.mkdir(parents=True,exist_ok=True)
    stamp=time.strftime('%Y%m%d-%H%M%S')+'-'+str(time.time_ns()%1000000000)
    saved=base/stamp;stage=base/(stamp+'-stage')
    previous=None
    if action=='update' and (c.get('source') or c.get('allow_package')):
        ready=catalog(c).read(c).get('prepared')
        if not ready:raise RuntimeError('prepare_release_first')
        c=dict(c,package=ready['package'],sha256=ready['sha256'])
    if action=='update':manifest=unpack(c,stage)
    else:
        candidates=sorted(p for p in base.iterdir() if (p/'receipt.json').exists() and not p.name.endswith('-stage'))
        if not candidates:raise RuntimeError('no_backup')
        previous=candidates[-1]
    try:
        if c['id'] in ('lampac','parsers','homemedia'):
            catalog(c).stage_core(c,stage/'payload' if action=='update' else previous/'payload')
    except Exception:
        if stage.exists():shutil.rmtree(stage)
        raise
    try:idle(c)
    except Exception:
        if stage.exists():shutil.rmtree(stage)
        raise
    running=c.get('unit') and subprocess.run(['systemctl','is-active','--quiet',c['unit']],timeout=10).returncode==0
    journal=base/'transaction.json'
    if journal.exists():raise RuntimeError('unfinished_transaction: recovery required')
    transaction={'backup':str(saved),'running':bool(running),'applying':False}
    atomic(journal,transaction)
    copied=False
    try:
        if c.get('unit'):command('systemctl','stop',c['unit'])
        snapshot(c,saved)
        transaction['applying']=True;atomic(journal,transaction)
        if action=='rollback':
            copied=True;restore(c,previous)
        else:
            root=pathlib.Path(c['root']);payload=stage/'payload';copied=True
            if root.is_dir():
                # Overlay preserves local configuration/data outside packaged files.
                shutil.copytree(payload,root,dirs_exist_ok=True)
            else:
                # Readers of the old inode remain valid during a binary replacement.
                next_file=root.with_name(root.name+'.lmpc-next')
                shutil.copy2(payload,next_file);os.replace(next_file,root)
            ownership(c)
        if c.get('unit') and running:command('systemctl','start',c['unit']);wait_health(c)
        elif not c.get('unit'):wait_health(c)
        if c.get('source') or c.get('allow_package'):
            v=manifest['version'] if action=='update' else json.loads((previous/'receipt.json').read_text()).get('version')
            catalog(c).installed(c,v)
            if c['id']=='lampac':catalog(c).installed(dict(c,id='parsers',root=str(pathlib.Path(c['root'])/'module')),v)
        journal.unlink()
        return {'ok':True,'action':action,'backup':str(saved),'restored':str(previous) if previous else None,'version':manifest['version'] if action=='update' else None}
    except Exception as error:
        if copied:
            if c.get('unit'):command('systemctl','stop',c['unit'])
            restore(c,saved)
        if c.get('unit') and running:command('systemctl','start',c['unit']);wait_health(c)
        journal.unlink()
        return {'ok':False,'error':str(error),'rolled_back':copied,'backup':str(saved)}
    finally:
        if stage.exists():shutil.rmtree(stage)

if __name__=='__main__':
    import sys
    args=json.loads(sys.stdin.read());handler=recover if args['action']=='recover' else None
    print(json.dumps(handler(args['component'],args['backup_root']) if handler else operate(args['component'],args['action'],args['backup_root']),ensure_ascii=False))
