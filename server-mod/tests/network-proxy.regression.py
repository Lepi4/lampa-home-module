"""Compile candidate module and test routes in a disposable, offline Lampac clone."""
import argparse,base64,io,json,pathlib,shlex,subprocess,tarfile
ROOT=pathlib.Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('--host',default='proxmox');p.add_argument('--ct',type=int,default=116);a=p.parse_args()
bundle=io.BytesIO()
with tarfile.open(fileobj=bundle,mode='w:gz') as archive:
    files={f'payload/{n}':(ROOT/'server-mod/HomeMedia'/n).read_bytes() for n in ['NetworkProxy.cs','ServiceUi.cs','Youtube.cs','YoutubeReliability.cs','KinoPubController.cs','KpSearch.cs','manifest.json']}
    manifest=json.loads(files['payload/manifest.json']);manifest['tree'].append('ProxyFixture.cs');files['payload/manifest.json']=json.dumps(manifest).encode()
    files['payload/ProxyFixture.cs']=(ROOT/'server-mod/tests/ProxyFixture.cs').read_bytes()
    files['network-proxy-http.py']=(ROOT/'server-mod/tests/network-proxy-http.py').read_bytes()
    for name,data in files.items():
        member=tarfile.TarInfo(name);member.size=len(data);archive.addfile(member,io.BytesIO(data))
code='''import pathlib,tempfile,tarfile,io,base64,sys
sys.path.insert(0,'/opt/lmpc-control');import service_catalog
launch=service_catalog.subprocess.Popen
def start(args,*pargs,**kwargs):
 if any(str(x).endswith('/Core.dll') for x in args):
  p=pathlib.Path(kwargs['cwd'])/'init.conf'
  import json
  cfg=json.loads(p.read_text());cfg['tmdb']={'sentinel':'preserve-me'};p.write_text(json.dumps(cfg))
 return launch(args,*pargs,**kwargs)
service_catalog.subprocess.Popen=start
with tempfile.TemporaryDirectory(prefix='lmpc-proxy-contract-') as name:
 p=pathlib.Path(name);tarfile.open(fileobj=io.BytesIO(base64.b64decode(sys.stdin.buffer.read())),mode='r:gz').extractall(p,filter='data')
 c={'id':'homemedia','catalog_dir':str(p/'catalog'),'compatibility_tests':[str(p/'network-proxy-http.py')]}
 try:service_catalog.stage_core(dict(c,isolate_network=False,_network_isolated=True),p/'payload')
 finally:
  for filename in ['compatibility-tests.log','compatibility.log']:
   log=p/'catalog/homemedia'/filename
   if log.exists():print(log.read_text() if filename.endswith('tests.log') else '\\n'.join(x for x in log.read_text().splitlines() if 'error CS' in x or 'Exception' in x))
'''
# Candidate and test HTTP run within one private network namespace.
wrapper="import subprocess,sys;subprocess.run(['ip','link','set','lo','up'],check=True);exec(sys.argv[1])"
cmd=f'pct exec {a.ct} -- unshare --net python3 -c '+shlex.quote(wrapper)+' '+shlex.quote(code)
r=subprocess.run(['ssh','-o','BatchMode=yes',a.host,cmd],input=base64.b64encode(bundle.getvalue()),capture_output=True,timeout=310)
print(r.stdout.decode(errors='replace'))
if r.returncode:print(r.stderr.decode(errors='replace'))
raise SystemExit(r.returncode)
