import importlib.util,pathlib,tempfile,unittest,hashlib,json,io,types,sys,zipfile,tarfile
from unittest.mock import patch
ROOT=pathlib.Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT/'deploy'));import service_catalog as catalog

class Catalog(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.base=pathlib.Path(self.temp.name);self.live=self.base/'live';self.live.write_bytes(b'current')
  self.c={'id':'yt-dlp','root':str(self.live),'version':'2026.1.1','catalog_dir':str(self.base/'catalog'),'source':{'repo':'yt-dlp/yt-dlp','asset':'yt-dlp'}}
 def tearDown(self):self.temp.cleanup()
 def seed(self,data=b'next',version='2026.2.1',digest=None):
  catalog.save(self.c,{'installed':self.c['version'],'latest':version,'checked':__import__('time').time(),'download':'https://github.com/yt-dlp/yt-dlp/releases/download/test/yt-dlp','bytes':len(data),'digest':digest or hashlib.sha256(data).hexdigest()})
  return patch.object(catalog.urllib.request,'urlopen',return_value=io.BytesIO(data))
 def test_prepare_does_not_replace_running_binary(self):
  with self.seed(),patch.object(catalog.subprocess,'run',return_value=types.SimpleNamespace(returncode=0,stdout='2026.2.1')):
   result=catalog.prepare(self.c)
  self.assertTrue(result['ok']);self.assertEqual(self.live.read_bytes(),b'current');self.assertTrue(catalog.read(self.c)['prepared'])
 def test_bad_digest_cannot_be_applied(self):
  with self.seed(digest='0'*64),self.assertRaisesRegex(RuntimeError,'checksum'):catalog.prepare(self.c)
  self.assertEqual(self.live.read_bytes(),b'current');self.assertNotIn('prepared',catalog.read(self.c));self.assertFalse((catalog.directory(self.c)/'download.part').exists())
 def test_downgrade_is_not_prepared(self):
  with self.seed(version='2025.1.1'),self.assertRaisesRegex(RuntimeError,'already_current'):catalog.prepare(self.c)
  self.assertEqual(self.live.read_bytes(),b'current')
 def test_zip_traversal_rejected_before_compatibility(self):
  raw=io.BytesIO()
  with zipfile.ZipFile(raw,'w') as z:z.writestr('Core.dll','core');z.writestr('../escape','bad')
  self.c['source']['format']='zip'
  with self.seed(raw.getvalue()),patch.object(catalog,'stage_core') as stage,self.assertRaisesRegex(RuntimeError,'unsafe_archive'):catalog.prepare(self.c)
  stage.assert_not_called();self.assertFalse((self.base/'escape').exists())
 def test_core_archive_without_version_gets_verified_release_marker(self):
  raw=io.BytesIO()
  with zipfile.ZipFile(raw,'w') as z:z.writestr('Core.dll','core')
  self.c.update(id='lampac');self.c['source']['format']='zip'
  markers=[]
  def check_marker(c,payload):markers.append((payload/'version.txt').read_text())
  with self.seed(raw.getvalue()),patch.object(catalog,'stage_core',side_effect=check_marker):result=catalog.prepare(self.c)
  self.assertTrue(result['ok']);self.assertEqual(markers,['2026.2.1\n'])
  with tarfile.open(catalog.read(self.c)['prepared']['package']) as package:
   self.assertEqual(package.extractfile('payload/version.txt').read(),b'2026.2.1\n')
  self.assertEqual(self.live.read_bytes(),b'current')
 def bundle(self,version):
  raw=io.BytesIO()
  with tarfile.open(fileobj=raw,mode='w:gz') as tf:
   for name,data in [('manifest.json',json.dumps({'component':'yt-dlp','version':version}).encode()),('payload',b'new-program')]:
    m=tarfile.TarInfo(name);m.size=len(data);tf.addfile(m,io.BytesIO(data))
  self.c.update(allow_package=True);self.c['source']['format']='package';return raw.getvalue()
 def test_release_package_is_prepared_without_touching_live_program(self):
  with self.seed(self.bundle('2026.2.1')):result=catalog.prepare(self.c)
  self.assertTrue(result['ok']);self.assertEqual(self.live.read_bytes(),b'current')
 def test_package_version_must_match_release(self):
  with self.seed(self.bundle('2025.1.1')),self.assertRaisesRegex(RuntimeError,'package_release_version_mismatch'):catalog.prepare(self.c)
  self.assertNotIn('prepared',catalog.read(self.c));self.assertEqual(self.live.read_bytes(),b'current')

if __name__=='__main__':unittest.main()
