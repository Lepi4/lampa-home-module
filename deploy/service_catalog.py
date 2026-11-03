"""Root-owned release discovery/staging. No URLs or commands from web jobs."""
import hashlib,json,os,pathlib,re,shutil,socket,subprocess,tarfile,tempfile,time,urllib.request,zipfile,io
import release_sources

def directory(c):return pathlib.Path(c.get('catalog_dir','/opt/lmpc-update-cache'))/c['id']
def read(c):
 try:return json.loads((directory(c)/'status.json').read_text())
 except FileNotFoundError:return {'installed':c.get('version')}
def save(c,data):
 p=directory(c);p.mkdir(parents=True,exist_ok=True);t=p/'status.next';t.write_text(json.dumps(data));os.replace(t,p/'status.json')
def installed(c,version):
 d=read(c);d['installed']=version;d.pop('prepared',None);save(c,d)
def version(v):return tuple(int(x) for x in re.findall(r'\d+',v or ''))
def fetch(c,request,timeout=25):
 proxy=c.get('download_proxy')
 if proxy:return urllib.request.build_opener(urllib.request.ProxyHandler({'https':proxy,'http':proxy})).open(request,timeout=timeout)
 return urllib.request.urlopen(request,timeout=timeout)
def accept_package(c,upload,expected_version=None):
 from service_agent import unpack
 if not c.get('allow_package'):raise RuntimeError('package_upload_unavailable')
 folder=directory(c);folder.mkdir(parents=True,exist_ok=True)
 private=folder/'uploaded.part';shutil.copyfile(upload,private)
 try:
  digest=hashlib.sha256(private.read_bytes()).hexdigest()
  with tempfile.TemporaryDirectory(prefix='verify-upload-',dir=folder) as temp:
   stage=pathlib.Path(temp)/'unpack';manifest=unpack(dict(c,package=str(private),sha256=digest),stage)
   if expected_version is not None and manifest['version']!=expected_version:raise RuntimeError('package_release_version_mismatch')
   if c['id']=='homemedia':stage_core(c,stage/'payload')
   if c['id']=='channel-manager':
    source=stage/'payload/channel_manager.py'
    if not source.is_file():raise RuntimeError('missing_channel_manager')
    subprocess.run(['/usr/bin/python3','-m','py_compile',str(source)],check=True,capture_output=True,timeout=20)
    env=dict(os.environ,CHANNEL_MANAGER_CONFIG=str(stage/'unused-config.json'))
    result=subprocess.run(['/usr/bin/python3',str(source),'--version'],env=env,capture_output=True,text=True,timeout=20)
    if result.returncode or version(result.stdout)!=version(manifest['version']):raise RuntimeError('candidate_version_mismatch')
   if c['id']=='tvheadend':
    binary=stage/'payload/tvheadend'
    if not binary.is_file():raise RuntimeError('missing_tvheadend_binary')
    binary.chmod(binary.stat().st_mode|0o111)
    p=subprocess.run([str(binary),'--version'],capture_output=True,text=True,timeout=20)
    if p.returncode!=0:raise RuntimeError('invalid_tvheadend_binary')
  package=folder/'prepared.tar.gz';os.replace(private,package)
  d=read(c);d.update(latest=manifest['version'],prepared={'package':str(package),'sha256':digest,'version':manifest['version']});save(c,d)
  return {'ok':True,'prepared_version':manifest['version'],'current_unchanged':True}
 finally:private.unlink(missing_ok=True)
def discover(c,force=False):
 d=read(c);src=c.get('source')
 if not src:return d
 key=release_sources.source_key(src)
 if not force and time.time()-d.get('checked',0)<900 and d.get('source_key',key)==key:return d
 release=release_sources.discover_component(src,c['id'],lambda request:fetch(c,request))
 d.update(checked=time.time(),latest=release['version'],download=release['url'],download_urls=release['urls'],digest=release['sha256'],bytes=release['bytes'],source_key=key,source=release['provider'])
 d.pop('error',None)
 save(c,d);return d

def stage_core(c,payload):
 """Run candidate + real custom module, isolated DB/port and cloud writes disabled."""
 directory(c).mkdir(parents=True,exist_ok=True)
 if c.get('isolate_network',True) and not c.get('_network_isolated'):
  # The probe and the candidate must share their private loopback namespace.
  # No copied module can contact production services or cloud accounts.
  code="import sys,json,subprocess;subprocess.run(['ip','link','set','lo','up'],check=True);sys.path.insert(0,sys.argv[1]);import service_catalog;c=json.loads(sys.argv[2]);c['_network_isolated']=True;service_catalog.stage_core(c,sys.argv[3])"
  p=subprocess.run(['unshare','--net','python3','-c',code,str(pathlib.Path(__file__).parent),json.dumps(c),str(payload)],capture_output=True,text=True,timeout=300)
  if p.returncode:raise RuntimeError('compatibility_failed: '+(p.stderr or p.stdout)[-2000:])
  return
 current=pathlib.Path(c.get('core_root','/opt/lampac'))
 if not (current/'Core.dll').exists():raise RuntimeError('core_not_installed')
 with tempfile.TemporaryDirectory(prefix='lmpc-compat-',dir=directory(c)) as name:
  target=pathlib.Path(name)
  skipped={'database','cache','.cache','logs','data','yt','releases','.playwright','.local','.config','.aspnet'}
  shutil.copytree(current,target,dirs_exist_ok=True,ignore=lambda p,n:[x for x in n if pathlib.Path(p)==current and (x in skipped or x.startswith('init.conf') or x in ['current.conf','passwd'])])
  destination=target/'module' if c['id']=='parsers' else target/'mods/HomeMedia' if c['id']=='homemedia' else target
  from service_agent import is_protected
  shutil.copytree(payload,destination,dirs_exist_ok=True,ignore=lambda p,n:[x for x in n if is_protected(c,(pathlib.Path(p)/x).relative_to(payload).as_posix())])
  # Lampac reads this static fallback even with WAF disabled; an offline
  # sandbox otherwise crashes while logging cloudflare_ips.Count.
  cloudflare=current/'data/cloudflare'
  if cloudflare.is_dir():shutil.copytree(cloudflare,target/'data/cloudflare',dirs_exist_ok=True)
  if c.get('compatibility_fixture_dir'):
   shutil.copytree(c['compatibility_fixture_dir'],target/'database',dirs_exist_ok=True)
  with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
  config={'listen':{'ip':'127.0.0.1','port':port},'BaseModule':{'LoadModules':['.*'],'Middlewares':{'staticFiles':True,'anonymousRequest':True}},'DynamicModule':False,'accsdb':{'enable':False},'WAF':{'enable':False},'chromium':{'enable':False},'firefox':{'enable':False}}
  (target/'init.conf').write_text(json.dumps(config))
  env={k:v for k,v in os.environ.items() if not k.startswith('HOME_')}
  env.update(HOME_PROFILE_TEST='1',HOME_PROFILES_ENABLED='1',ASPNETCORE_URLS='http://127.0.0.1:'+str(port))
  logfile=directory(c)/'compatibility.log'
  with logfile.open('w') as log:
   p=subprocess.Popen([c.get('dotnet','/usr/share/dotnet/dotnet'),str(target/'Core.dll')],cwd=target,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
   try:
    for _ in range(120):
     if p.poll() is not None:break
     output=logfile.read_text(errors='replace')
     if re.search(r'\berror CS\d{4}\b',output):raise RuntimeError('candidate_compile_error: '+output[-1800:])
     try:
      with urllib.request.urlopen('http://127.0.0.1:'+str(port)+'/home/health',timeout=2) as r:
       health=json.load(r)
       if health.get('ok') is True and health.get('module')=='HomeMedia':
        # HomeMedia can be ready while another dynamic module is still compiling.
        # A broken OnlineAnime must not be approved just because /home/health works.
        required=c.get('required_modules',['AniLibria','AnimeLib','Kodik'])
        missing=[name for name in required if not re.search(r'loaded module:\s*'+re.escape(name)+r'\b',output)]
        if 'Configure complete' in output and not missing:
         tests=c.get('compatibility_tests',[])
         if tests:
          tests_dir=target/'compatibility-tests';tests_dir.mkdir()
          test_env=dict(env,PROFILE_TEST_URL='http://127.0.0.1:'+str(port),NO_PROXY='*',no_proxy='*')
          with (directory(c)/'compatibility-tests.log').open('w') as test_log:
           for test in tests:
            test=pathlib.Path(test);copy=tests_dir/test.name;shutil.copy2(test,copy)
            result=subprocess.run(['python3',str(copy)],cwd=target,env=test_env,stdout=test_log,stderr=subprocess.STDOUT,timeout=90)
            if result.returncode:raise RuntimeError('compatibility_contract_failed: '+test.name)
         return
     except (urllib.error.URLError,TimeoutError,json.JSONDecodeError):pass
     time.sleep(1)
    raise RuntimeError('compatibility_failed: candidate cannot load HomeMedia and required anime modules; current service unchanged')
   finally:
    import signal
    if p.poll() is None:
     os.killpg(p.pid,signal.SIGTERM)
     try:p.wait(timeout=12)
     except subprocess.TimeoutExpired:os.killpg(p.pid,signal.SIGKILL);p.wait(timeout=5)

def prepare(c):
 try:return _prepare(c)
 except Exception as e:
  d=read(c);d['error']=str(e);d.pop('prepared',None);save(c,d);(directory(c)/'download.part').unlink(missing_ok=True);raise

def _prepare(c):
 from service_agent import is_protected
 d=discover(c);src=c.get('source')
 if not src:raise RuntimeError('upload_a_reviewed_package')
 if version(d.get('latest'))<=version(d.get('installed')):raise RuntimeError('already_current')
 folder=directory(c);folder.mkdir(parents=True,exist_ok=True)
 if shutil.disk_usage(folder).free<max(512*1024**2,d['bytes']*4):raise RuntimeError('insufficient_disk_space')
 # Lampac and parser bundles come from the same release archive. Retain one
 # checksum-addressed download, rather than spending two downloads per update.
 downloads=folder.parent/'downloads';downloads.mkdir(exist_ok=True)
 raw=downloads/d['digest'];partial=folder/'download.part'
 valid=raw.is_file() and raw.stat().st_size==d['bytes'] and hashlib.sha256(raw.read_bytes()).hexdigest()==d['digest']
 if not valid:
  release={'url':d['download'],'urls':d.get('download_urls'),'bytes':d['bytes'],'sha256':d['digest']}
  release_sources.download(release,partial,lambda request:fetch(c,request,timeout=30))
  os.replace(partial,raw)
 # Keep at most two validated upstream downloads; prepared packages are separate.
 for obsolete in sorted(downloads.iterdir(),key=lambda p:p.stat().st_mtime,reverse=True)[2:]:
  if obsolete!=raw:obsolete.unlink()
 if src.get('format')=='package':
  return accept_package(c,raw,d['latest'])
 with tempfile.TemporaryDirectory(prefix='prepare-',dir=folder) as temp:
  payload=pathlib.Path(temp)/'payload'
  if src.get('format')=='zip':
   payload.mkdir()
   with zipfile.ZipFile(raw) as archive:
    if len(archive.infolist())>100000 or sum(x.file_size for x in archive.infolist())>2*1024**3:raise RuntimeError('archive_too_large')
    names=[pathlib.PurePosixPath(n) for n in archive.namelist()]
    core=next((p for p in names if p.name=='Core.dll'),None)
    if core is None:raise RuntimeError('bad_core_layout')
    prefix=core.parent
    for info in archive.infolist():
     p=pathlib.PurePosixPath(info.filename)
     if p.is_absolute() or '..' in p.parts or info.external_attr>>16&0o170000==0o120000:raise RuntimeError('unsafe_archive')
     try:rel=p.relative_to(prefix)
     except ValueError:continue
     if not rel.parts or info.is_dir() or is_protected(c,rel):continue
     # Core and Online parsers form one release. Keep all local/non-Online
     # modules in place instead of overwriting their installed customizations.
     if c['id']=='lampac' and rel.parts[0]=='module' and (len(rel.parts)<2 or not rel.parts[1].startswith('Online')):continue
     if c['id']=='parsers' and (len(rel.parts)<2 or rel.parts[0]!='module' or not rel.parts[1].startswith('Online')):continue
     if c['id']=='parsers':rel=pathlib.PurePosixPath(*rel.parts[1:])
     to=payload/pathlib.Path(*rel.parts);to.parent.mkdir(parents=True,exist_ok=True)
     with archive.open(info) as inp,to.open('wb') as dest:shutil.copyfileobj(inp,dest)
   # Upstream archives may omit this marker. Keep the status page and rollback
   # snapshot consistent with the release that was actually verified.
   if c['id']=='lampac':(payload/'version.txt').write_bytes((d['latest']+'\n').encode('utf-8'))
   stage_core(c,payload)
  else:
   shutil.copy2(raw,payload);payload.chmod(0o755)
   p=subprocess.run([str(payload),'--version'],capture_output=True,text=True,timeout=20)
   if p.returncode or version(d['latest'])!=version(p.stdout):raise RuntimeError('candidate_version_mismatch')
  package=folder/'prepared.tar.gz';temporary=folder/'prepared.next'
  manifest={'component':c['id'],'version':d['latest'],'compatibility_verified':True}
  with tarfile.open(temporary,'w:gz') as tf:
   content=json.dumps(manifest).encode();item=tarfile.TarInfo('manifest.json');item.size=len(content);tf.addfile(item,io.BytesIO(content))
   tf.add(payload,arcname='payload',recursive=True)
  os.replace(temporary,package)
 d['prepared']={'package':str(package),'sha256':hashlib.sha256(package.read_bytes()).hexdigest(),'version':d['latest']};d['prepare_ok']=True;d.pop('error',None);save(c,d)
 return {'ok':True,'prepared_version':d['latest'],'current_unchanged':True}
