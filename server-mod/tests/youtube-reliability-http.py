"""Runs only inside the offline compatibility candidate with its test-only fixture."""
import json, os, urllib.request
base=os.environ['PROFILE_TEST_URL']
with urllib.request.urlopen(base+'/home/profiles',timeout=10) as response:
    assert json.load(response)['test_instance'] is True
request=urllib.request.Request(base+'/home/session/login',data=json.dumps({'profile_id':'u_default','device':'youtube-fixture','boot':'isolated'}).encode(),headers={'Content-Type':'application/json'})
with urllib.request.urlopen(request,timeout=10) as response:
    token=json.load(response)['token']
request=urllib.request.Request(base+'/yt/reliability-fixture',headers={'X-Home-Session':token})
with urllib.request.urlopen(request,timeout=40) as response:
    data=json.load(response)
assert data.get('ok') is True and len(data['passed'])>=20,data
print(json.dumps(data))
