"""Compile actual YouTube sources in an offline Lampac clone and run relay contracts.

Requires root SSH to the Proxmox host and the existing service_catalog runner in CT.
Does not install sources, change the live DB, restart services or contact YouTube.
"""
import argparse, base64, io, json, pathlib, shlex, subprocess, tarfile

ROOT=pathlib.Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser()
p.add_argument('--host',default='proxmox')
p.add_argument('--ct',default=116,type=int)
a=p.parse_args()
bundle=io.BytesIO()
with tarfile.open(fileobj=bundle,mode='w:gz') as archive:
    files={f'payload/{n}':(ROOT/'server-mod/HomeMedia'/n).read_bytes() for n in ['Youtube.cs','YoutubeReliability.cs','manifest.json']}
    manifest=json.loads(files['payload/manifest.json'])
    manifest['tree'].append('YoutubeReliabilityFixture.cs')
    files['payload/manifest.json']=json.dumps(manifest).encode()
    files['payload/YoutubeReliabilityFixture.cs']=(ROOT/'server-mod/tests/YoutubeReliabilityFixture.cs').read_bytes()
    files['youtube-reliability-http.py']=(ROOT/'server-mod/tests/youtube-reliability-http.py').read_bytes()
    for name,data in files.items():
        entry=tarfile.TarInfo(name);entry.size=len(data)
        archive.addfile(entry,io.BytesIO(data))
code='''import pathlib,tempfile,tarfile,io,base64,sys
sys.path.insert(0,'/opt/lmpc-control');import service_catalog
with tempfile.TemporaryDirectory(prefix='lmpc-youtube-contract-') as name:
 p=pathlib.Path(name)
 tarfile.open(fileobj=io.BytesIO(base64.b64decode(sys.stdin.buffer.read())),mode='r:gz').extractall(p,filter='data')
 c={'id':'homemedia','catalog_dir':str(p/'catalog'),'compatibility_tests':[str(p/'youtube-reliability-http.py')]}
 try:service_catalog.stage_core(c,p/'payload')
 finally:
  log=p/'catalog/homemedia/compatibility-tests.log'
  if log.exists():print(log.read_text())
print('Candidate compilation and YouTube contracts PASS')
'''
r=subprocess.run(['ssh','-o','BatchMode=yes',a.host,f'pct exec {a.ct} -- python3 -c '+shlex.quote(code)],input=base64.b64encode(bundle.getvalue()),capture_output=True,timeout=310)
print(r.stdout.decode(errors='replace'))
if r.returncode:print(r.stderr.decode(errors='replace'))
raise SystemExit(r.returncode)
