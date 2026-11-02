"""Admin-only proxy configuration and rollback contracts; offline test instance only."""
import json,os,time,urllib.request,urllib.error,threading,http.server,base64,http.cookiejar
base=os.environ['PROFILE_TEST_URL'].rstrip('/')
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
def req(path,token=None,body=None,headers=None):
    h=dict(headers or {})
    if token:h['X-Home-Session']=token
    if body is not None:h['Content-Type']='application/json'
    r=urllib.request.Request(base+path,data=json.dumps(body).encode() if body is not None else None,headers=h)
    try:response=opener.open(r,timeout=25)
    except urllib.error.HTTPError as e:response=e
    with response:
        raw=response.read().decode();return response.status,json.loads(raw) if raw else None
assert req('/home/profiles')[1]['test_instance'] is True
admin=req('/home/admin/login',body={'profile_id':'u_default','device':'proxy_contract','boot':'isolated'})[1]['token']
passed=[]
def check(name,value):
    assert value,name
    passed.append(name)
jar=http.cookiejar.CookieJar()
browser=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(jar))
login=urllib.request.Request(base+'/home/admin/login',data=json.dumps({'profile_id':'u_default','device':'proxy_cookie_contract','boot':'isolated'}).encode(),headers={'Content-Type':'application/json'})
with browser.open(login,timeout=10) as r:
    json.load(r)
check('admin login emits functional session cookie',any(c.name=='home_session' for c in jar))
with browser.open(base+'/home/admin/proxy',timeout=10) as r:check('new browser request authenticates by cookie only',r.status==200)
check('anonymous cannot read proxy settings',req('/home/admin/proxy')[0]==401)
for ident,child in [('u_proxy_child',True),('u_proxy_adult',False)]:
    assert req('/home/admin/profiles',admin,{'id':ident,'name':ident,'is_child':child,'max_age':12 if child else 18})[0]==200
    token=req('/home/session/login',body={'profile_id':ident,'device':ident,'boot':'isolated'})[1]['token']
    for route in ['/home/admin/proxy','/home/admin/proxy/test','/home/admin/proxy/rollback']:
        check(ident+' denied '+route,req(route,token,{} if route!='/home/admin/proxy' else None)[0]==403)
    check(ident+' cannot save',req('/home/admin/proxy',token,{})[0]==403)
check('cross-origin admin write rejected',req('/home/admin/proxy',admin,{}, {'Origin':'https://foreign.invalid','Sec-Fetch-Site':'cross-site'})[0]==403)
original=req('/home/admin/proxy',admin)[1]
connections=[{'id':'unit','name':'Unit SOCKS5','url':'socks5://127.0.0.1:1','username':'do-not-return','password':'secret!'}]
body={'revision':original['revision'],'connections':connections,'routes':{s:'unit' for s in original['routes']}}
saved_code,saved=req('/home/admin/proxy',admin,body)
check('save succeeds',saved_code==200 and saved.get('ok') is True)
check('secrets never returned',not any(x in json.dumps(saved) for x in ['do-not-return','secret!']))
check('auth marker retained',saved['connections'][0]['has_auth'] is True)
check('TMDB changes request reload',saved['tmdb_reload'] is True)
check('stale browser cannot overwrite newer settings',req('/home/admin/proxy',admin,body)[0]==409)
check('unchanged save succeeds',req('/home/admin/proxy',admin,saved)[0]==200)
check('test-only auth and route checks pass',req('/home/admin/proxy-fixture',admin)[1]['ok'] is True)
deadline=time.monotonic()+70
while True:
    native=req('/home/admin/proxy-fixture',admin)[1]
    if native.get('native_tmdb_loaded') and native.get('native_tmdb_enabled'):break
    assert time.monotonic()<deadline,'native TMDB did not reload proxyapi from init.conf'
    time.sleep(1)
check('native TMDB reloads without restarting Lampac',True)
body={'revision':saved['revision'],'connections':saved['connections'],'routes':dict(saved['routes'])}
body['service']='youtube'
code,probe=req('/home/admin/proxy/test',admin,body)
check('failed probe is reported',code==200 and probe['ok'] is False)
check('probe never saves',req('/home/admin/proxy',admin)[1]['revision']==saved['revision'])
for bad in ['file:///tmp/proxy','http://127.0.0.1:9000/path','http://host:9000?token=secret','http://host:9000/#x','ftp://host:9000']:
    malformed=json.loads(json.dumps(body));malformed['connections'][0]['url']=bad
    check('reject '+bad,req('/home/admin/proxy',admin,malformed)[0]==400)
bad=json.loads(json.dumps(body));bad['routes']['jacred']='missing'
check('reject missing connection',req('/home/admin/proxy',admin,bad)[0]==400)
code,reverted=req('/home/admin/proxy/rollback',admin,{'revision':saved['revision']})
check('rollback survives unchanged save and restores original routes',code==200 and reverted['routes']==original['routes'])
code,saved=req('/home/admin/proxy',admin,dict(body,connections=connections,revision=reverted['revision']))
check('credentials can be reapplied after rollback',code==200 and saved['connections'][0]['has_auth'] is True)
code,saved=req('/home/admin/proxy',admin,{'revision':saved['revision'],'connections':saved['connections'],'routes':saved['routes']})
check('masked connection retains saved credentials',code==200 and saved['connections'][0]['has_auth'] is True)
for service in saved['routes']:
    body={'revision':saved['revision'],'connections':saved['connections'],'routes':dict(saved['routes'])}
    body['routes'][service]='direct'
    code,saved=req('/home/admin/proxy',admin,body)
    check(service+' can disable proxy independently',code==200 and saved['routes'][service]=='direct')
    check(service+' does not change other routes',all(saved['routes'][s]==body['routes'][s] for s in body['routes']))
code,state=req('/home/admin/proxy-fixture',admin)
check('all direct route consumers agree',code==200 and state['youtube_direct'] and not state.get('tmdb_api_enabled',False) and not state.get('tmdb_images_enabled',False) and state['same_search_client'])
check('TMDB unrelated configuration preserved',state['native_sentinel']=='preserve-me')
req('/home/admin/proxy-fixture?drift=true',admin)
state=req('/home/admin/proxy',admin)[1]
code,repaired=req('/home/admin/proxy',admin,state)
check('save repairs independently changed native TMDB config',code==200 and repaired['tmdb_reload'] is True)
class Ui(http.server.BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_GET(self):
        if self.path=='/redirect':
            self.send_response(302);self.send_header('Location','/gui/');self.end_headers();return
        if self.server.server_port==9981 and self.headers.get('Authorization')!='Basic '+base64.b64encode(b'fixture:password').decode():
            self.send_response(401);self.send_header('WWW-Authenticate','Basic realm="Tvheadend fixture"');self.end_headers();return
        if self.path=='/style.css':
            data=b'.icon {background:url(/static/icon.png)}';kind='text/css'
        elif self.path.startswith('/api/'):
            data=json.dumps({'cookie':self.headers.get('Cookie'),'session':self.headers.get('X-Home-Session'),'host':self.headers.get('Host'),'path':self.path}).encode();kind='application/json'
        else:
            data=b'<html><head></head><body><a href="/api/channels">channels</a><script>fetch("/api/config");var path="/opt/tvlink/data";</script></body></html>';kind='text/html'
        self.send_response(200);self.send_header('Content-Type',kind);self.send_header('Set-Cookie','service_cookie=1; Path=/; HttpOnly');self.end_headers();self.wfile.write(data)
    def do_POST(self):
        raw=self.rfile.read(int(self.headers.get('Content-Length','0')));self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(raw)
servers=[]
try:
    for port in [3002,8090,9981]:
        server=http.server.ThreadingHTTPServer(('127.0.0.1',port),Ui);servers.append(server);threading.Thread(target=server.serve_forever,daemon=True).start()
    def ui(path,token=admin,headers=None,data=None):
        h=dict(headers or {});h['X-Home-Session']=token
        try:r=opener.open(urllib.request.Request(base+path,headers=h,data=data),timeout=10)
        except urllib.error.HTTPError as e:r=e
        with r:return r.status,dict(r.headers),r.read().decode()
    prefix='/home/services/channel-manager/'
    status,headers,text=ui(prefix)
    check('relative service UI is served',status==200 and '<base href=\''+prefix in text and 'href="'+prefix+'api/channels"' in text)
    with browser.open(base+prefix,timeout=10) as r:check('new tab opens service by cookie without header token',r.status==200)
    check('UI proxy preserves filesystem paths', 'var path="/opt/tvlink/data"' in text)
    check('service cookies scoped to mounted path',prefix in headers.get('Set-Cookie',''))
    status,headers,text=ui(prefix+'api/channels?x=1',headers={'Cookie':'home_session=private; home_web_device=private; service_cookie=ok'})
    echoed=json.loads(text)
    check('profile credentials never reach service',echoed['session'] is None and 'private' not in (echoed['cookie'] or ''))
    check('service uses fixed container loopback',echoed['host']=='127.0.0.1:3002' and echoed['path']=='/api/channels?x=1')
    check('CSS root resource is scoped',prefix+'static/icon.png' in ui(prefix+'style.css')[2])
    check('service POST body preserved',json.loads(ui(prefix+'api/config',headers={'Content-Type':'application/json'},data=b'{"value":7}')[2])['value']==7)
    check('unknown service rejected',ui('/home/services/evil/')[0]==404)
    check('Tvheadend keeps authentication challenge',ui('/home/services/tvheadend/gui/')[0]==401)
    check('Tvheadend accepts its own authorization',ui('/home/services/tvheadend/gui/',headers={'Authorization':'Basic '+base64.b64encode(b'fixture:password').decode()})[0]==200)
    child=req('/home/session/login',body={'profile_id':'u_proxy_child','device':'proxy_ui_child','boot':'isolated'})[1]['token']
    check('child cannot open service administration UI',ui(prefix,token=child)[0]==403)
finally:
    for server in servers:server.shutdown();server.server_close()
print(json.dumps({'ok':True,'passed':passed}),flush=True)
