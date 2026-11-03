#!/usr/bin/env python3
"""Run on Proxmox after placing the three reviewed source files in /tmp/lmpc-code."""
import pathlib,subprocess,json,os,time,shutil
ROOT=pathlib.Path('/opt/lmpc-control');BASE=pathlib.Path('/mnt/disk2/lmpc-integration-20261101')
def run(*a):return subprocess.run(a,check=True,capture_output=True,text=True,timeout=90).stdout
save=BASE/('control-code-'+time.strftime('%H%M%S'));save.mkdir(mode=0o700)
for name in ['release_sources.py','service-worker.py','service_agent.py','service_catalog.py']:
 if (ROOT/name).exists():shutil.copy2(ROOT/name,save/name)
 source=pathlib.Path('/tmp/lmpc-code')/name
 compile(source.read_text(),name,'exec')
 temp=ROOT/(name+'.next');shutil.copy2(source,temp);temp.chmod(0o644);os.replace(temp,ROOT/name)
 run('pct','push','116',str(ROOT/name),'/opt/lmpc-control/'+name)
p=ROOT/'config-production.json';d=json.loads(p.read_text());shutil.copy2(p,save/p.name)
tvh=next(x for x in d['components'] if x['id']=='tvheadend')
tvh['verify']=['/usr/bin/env','LD_LIBRARY_PATH=/opt/tvheadend/lib','/opt/tvheadend/tvheadend','--version']
temp=p.with_suffix('.next');temp.write_text(json.dumps(d,ensure_ascii=False,indent=2));temp.chmod(0o600);os.replace(temp,p)
run('systemctl','restart','lmpc-control');print(json.dumps({'deployed':True,'backup':str(save)}))
