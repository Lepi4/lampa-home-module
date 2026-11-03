#!/usr/bin/env python3
"""Proxmox (pct) or one-LXC worker. Run from root-owned systemd service/config.
Configuration is installation-owned; web requests supply only component + action.
"""
import argparse, fcntl, json, os, pathlib, re, subprocess, threading, time, uuid
from service_agent import atomic, health, operate, recover

def run(args, data=None):
    p=subprocess.run(args,input=data,text=True,capture_output=True,timeout=900)
    if p.returncode:raise RuntimeError(p.stderr[-1500:] or 'command_failed')
    return p.stdout

def inside(ct, script, data=None):
    return run(['pct','exec',str(ct),'--','python3','-c',script],data)

class Worker:
    def __init__(self,cfg):self.cfg=cfg;self.spool=cfg['spool'];self.web=cfg.get('web_ct');self.components={c['id']:c for c in cfg['components']};self.last_state={}
    def io(self,op,**kw):
        v={'dir':self.spool,'op':op,**kw}
        code='''import json,sys,pathlib,os,re,time
v=json.load(sys.stdin);d=pathlib.Path(v['dir']);d.mkdir(parents=True,exist_ok=True)
if v['op']=='publish':
 p=d/'state.json.tmp';p.write_text(json.dumps(v['value'],ensure_ascii=False));os.replace(p,d/'state.json');print('{}')
elif v['op']=='claim':
 # Never replay a possibly completed disruptive command after worker crash.
 for p in d.glob('*.running'):
  dst=p.with_suffix('.result');dst.write_text(json.dumps({'id':p.stem,'ok':False,'error':'worker_interrupted: inspect service before retry','at':int(time.time()*1000)}));p.unlink()
 files=sorted(d.glob('*.request'))
 if not files:print('null')
 else:
  p=files[0];q=p.with_suffix('.running');p.rename(q)
  try:print(json.dumps(json.loads(q.read_text())))
  except: q.with_suffix('.result').write_text('{"ok":false,"error":"invalid_request"}');q.unlink();print('null')
elif v['op']=='automation':
 try:print((d/'automation.json').read_text())
 except FileNotFoundError:print('{}')
elif v['op']=='finish':
 ident=v['id'];assert re.fullmatch('[a-f0-9]{32}',ident)
 p=d/(ident+'.result.tmp');p.write_text(json.dumps(v['value'],ensure_ascii=False));os.replace(p,d/(ident+'.result'));(d/(ident+'.running')).unlink(missing_ok=True)
 results=sorted(d.glob('*.result'),key=lambda p:p.stat().st_mtime,reverse=True)
 for p in results[100:]:p.unlink()
 print('{}')
'''
        if self.web:return json.loads(inside(self.web,code,json.dumps(v)))
        return json.loads(run(['python3','-c',code],json.dumps(v)))
    def automation(self):
        try:settings=self.io('automation')
        except Exception:settings={}
        if not isinstance(settings,dict):settings={}
        return {key:settings.get(key,'off') if settings.get(key) in ('check','prepare') and c.get('source') and not c.get('monitor_only') and not c.get('managed_by') else 'off' for key,c in self.components.items()}
    def recover(self):
        for c in self.components.values():
            if c.get('ct'):
                agent=pathlib.Path(__file__).with_name('service_agent.py').read_text()
                result=json.loads(inside(c['ct'],agent,json.dumps({'component':c,'action':'recover','backup_root':self.cfg['backup_root']})))
            else:result=recover(c,self.cfg['backup_root'])
            if result.get('recovered'):print(json.dumps(dict(component=c['id'],**result)),flush=True)
    def scheduled(self):
        modes=self.automation();path=pathlib.Path(self.cfg.get('automation_state','/opt/lmpc-control/automation-state.json'))
        try:history=json.loads(path.read_text())
        except FileNotFoundError:history={}
        for key,mode in modes.items():
            if mode=='off' or time.time()-history.get(key,0)<self.cfg.get('automation_interval_hours',24)*3600:continue
            history[key]=time.time();atomic(path,history)
            return {'id':uuid.uuid4().hex,'component':key,'action':'check','automation_mode':mode,'at':int(time.time()*1000),'actor':'scheduler'}
    def state(self):
        modes=self.automation()
        rows=[]
        for c in self.components.values():
            code="import pathlib,json,subprocess; c="+repr(c)+"; print(json.dumps({'installed':pathlib.Path(c['root']).exists(),'active':subprocess.run(['systemctl','is-active','--quiet',c['unit']],timeout=10).returncode==0 if c.get('unit') else pathlib.Path(c['root']).exists()}))"
            try:row=json.loads(inside(c['ct'],code) if c.get('ct') else run(['python3','-c',code]))
            except Exception as e:row={'installed':False,'active':False,'error':str(e)[:150]}
            actions=['check']
            release={}
            if row['installed'] and not c.get('monitor_only') and not c.get('managed_by'):
                if c.get('unit'):
                    if not row['active']:actions+=['start']
                    elif c['id'] not in ('lampac','homemedia','parsers'):actions+=['stop']
                if c.get('source') or c.get('allow_package'):
                    actions+=['prepare'] if c.get('source') else ['package']
                    meta=str(pathlib.Path(c.get('catalog_dir','/opt/lmpc-update-cache'))/c['id']/'status.json')
                    probe="import pathlib,json;p=pathlib.Path("+repr(meta)+");print(p.read_text() if p.exists() else '{}')"
                    try:release=json.loads(inside(c['ct'],probe) if c.get('ct') else run(['python3','-c',probe]))
                    except Exception:release={}
                    if release.get('prepared'):actions+=['update']
                elif c.get('package') and c.get('sha256'):actions+=['update']
                backup_check="import pathlib; p=pathlib.Path("+repr(self.cfg['backup_root'])+")/"+repr(c['id'])+";print(int(p.exists() and any(p.glob('*/receipt.json'))))"
                try:
                    available=(inside(c['ct'],backup_check) if c.get('ct') else run(['python3','-c',backup_check])).strip()=='1'
                    if available:actions+=['rollback']
                except Exception:pass
            if c.get('managed_by'):
                parent=c['managed_by'];meta=str(pathlib.Path(c.get('catalog_dir','/opt/lmpc-update-cache'))/parent/'status.json')
                probe="import pathlib;p=pathlib.Path("+repr(meta)+");print(p.read_text() if p.exists() else '{}')"
                try:release=json.loads(inside(c['ct'],probe) if c.get('ct') else run(['python3','-c',probe]))
                except Exception:pass
            rows.append({'id':c['id'],'name':c.get('name',c['id']),**row,'actions':actions,'monitor_only':bool(c.get('monitor_only')),'managed_by':c.get('managed_by'),'automation_available':bool(c.get('source')) and not c.get('monitor_only') and not c.get('managed_by'),'automation_mode':modes[c['id']],'error':release.get('error'),'update_note':c.get('update_note','Обновление доступно после подготовки совместимого пакета с SHA-256'),'version':release.get('installed') or c.get('version'),'latest':release.get('latest'),'prepared':release.get('prepared',{}).get('version')})
        self.last_state={'at':int(time.time()*1000),'services':rows}
        self.io('publish',value=self.last_state)
    def tick(self):
        job=self.io('claim') or self.scheduled()
        if job:
            ident=job.get('id','');comp=job.get('component');action=job.get('action')
            if not re.fullmatch('[a-f0-9]{32}',ident):raise RuntimeError('invalid_job_id')
            stop=threading.Event()
            def pulse():
                while not stop.wait(12):
                    try:self.io('publish',value={**self.last_state,'at':int(time.time()*1000),'working':{'component':comp,'action':action}})
                    except Exception:pass
            heartbeat=threading.Thread(target=pulse,daemon=True);heartbeat.start()
            try:
                if comp not in self.components or action not in ('check','prepare','package','start','stop','update','rollback'):raise RuntimeError('unknown_job')
                c=self.components[comp]
                if action=='package':
                    if c.get('ct')!=self.web or not c.get('allow_package'):raise RuntimeError('package_upload_unavailable')
                    c=dict(c,candidate_upload=str(pathlib.Path(self.spool)/(ident+'.upload')))
                if c.get('ct'):
                    agent=pathlib.Path(__file__).with_name('service_agent.py').read_text()
                    value={'component':c,'action':action,'backup_root':self.cfg['backup_root']}
                    result=json.loads(inside(c['ct'],agent,json.dumps(value)))
                else:result=operate(c,action,self.cfg['backup_root'])
                if job.get('automation_mode')=='prepare' and result.get('ok'):
                    from service_catalog import version
                    release=result.get('release',{})
                    if version(release.get('latest'))>version(release.get('installed')) and release.get('prepared',{}).get('version')!=release.get('latest'):
                        value={'component':c,'action':'prepare','backup_root':self.cfg['backup_root']}
                        result=json.loads(inside(c['ct'],agent,json.dumps(value))) if c.get('ct') else operate(c,'prepare',self.cfg['backup_root'])
            except Exception as e:result={'ok':False,'error':str(e)[:1500]}
            finally:stop.set();heartbeat.join(timeout=15)
            if action=='package':
                cleanup="import pathlib;pathlib.Path("+repr(str(pathlib.Path(self.spool)/(ident+'.upload')))+").unlink(missing_ok=True)"
                if self.web:inside(self.web,cleanup)
            self.io('finish',id=ident,value={**job,**result,'finished_at':int(time.time()*1000)})
        self.state()

if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('--config',required=True);ap.add_argument('--once',action='store_true');a=ap.parse_args()
    config_path=pathlib.Path(a.config)
    if config_path.stat().st_uid!=0 or config_path.stat().st_mode&0o022:raise SystemExit('config must be owned by root and not writable by group/others')
    cfg=json.loads(config_path.read_text());lock=open('/run/lmpc-service-worker.lock','w');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    worker=Worker(cfg)
    worker.recover()
    worker.state()
    while True:
        try:worker.tick()
        except Exception as e:print(str(e),flush=True)
        if a.once:break
        time.sleep(10)
