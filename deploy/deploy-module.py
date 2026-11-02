#!/usr/bin/env python3
"""Upload and apply a HomeMedia package through the Lampac panel worker."""
import argparse, json, pathlib, time, requests

def main():
    ap=argparse.ArgumentParser(description='Deploy HomeMedia through the safe panel updater')
    ap.add_argument('--url', required=True, help='Lampac base URL, for example http://192.168.1.80:9118')
    ap.add_argument('--profile-id', default='u_default'); ap.add_argument('--pin', default='')
    ap.add_argument('--package', default=str(pathlib.Path(__file__).resolve().parents[1]/'release/homemedia.tar.gz'))
    ap.add_argument('--no-apply', action='store_true'); a=ap.parse_args()
    base=a.url.rstrip('/'); s=requests.Session()
    login=s.post(base+'/home/admin/login',json={'profile_id':a.profile_id,'pin':a.pin,'device':'web_deploy','boot':'deploy-'+str(int(time.time()))},timeout=20);login.raise_for_status();token=login.json().get('token')
    if not token: raise SystemExit('admin session was not issued')
    headers={'X-Home-Session':token}; package=pathlib.Path(a.package)
    upload=s.post(base+'/home/admin/services/package?component=homemedia',headers={**headers,'Content-Type':'application/octet-stream'},data=package.read_bytes(),timeout=90);upload.raise_for_status();job=upload.json()['id']
    def state():
        r=s.get(base+'/home/admin/services',headers=headers,timeout=20);r.raise_for_status();return r.json()
    for _ in range(90):
        done=next((x for x in state().get('jobs',[]) if x.get('id')==job),None)
        if done:
            if not done.get('ok'): raise SystemExit(json.dumps(done,ensure_ascii=False))
            break
        time.sleep(2)
    else: raise SystemExit('package verification timed out')
    if a.no_apply: print(json.dumps({'package_job':done},ensure_ascii=False)); return
    apply=s.post(base+'/home/admin/services/job',headers={**headers,'Content-Type':'application/json'},json={'component':'homemedia','action':'update'},timeout=20);apply.raise_for_status();uid=apply.json()['id']
    for _ in range(120):
        done=next((x for x in state().get('jobs',[]) if x.get('id')==uid),None)
        if done and done.get('finished_at'):
            print(json.dumps({'package_job':job,'update_job':done},ensure_ascii=False));
            if not done.get('ok'): raise SystemExit(1)
            return
        time.sleep(2)
    raise SystemExit('update timed out; inspect panel before retrying')
if __name__=='__main__': main()
