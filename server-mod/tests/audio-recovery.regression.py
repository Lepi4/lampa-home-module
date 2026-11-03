"""Check the installed APK's audio recovery with simulated HDMI, on an explicit device.

Does not install an APK or change app settings. Builds a temporary shell DEX,
loads the installed Media3/factory classes, and always removes that DEX.
"""
import argparse
import json
import os
import pathlib
import re
import shlex
import shutil
import subprocess
import tempfile
import uuid

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--device', required=True, help='Explicit adb serial; no default device')
p.add_argument('--sdk', type=pathlib.Path, default=os.environ.get('ANDROID_HOME'))
p.add_argument('--jdk', type=pathlib.Path, default=os.environ.get('JAVA_HOME'))
p.add_argument('--package', default='app.lampac.home.mc')
a = p.parse_args()
assert a.sdk and a.jdk and re.fullmatch(r'[A-Za-z0-9_.]+', a.package)
adb = ['adb', '-s', a.device]

def call(args):
    return subprocess.check_output(args, text=True, encoding='utf-8', errors='replace', timeout=45).strip()

assert call(adb + ['get-state']) == 'device'
apk = call(adb + ['shell', 'pm', 'path', a.package]).split('package:', 1)[1]
assert apk.startswith('/data/app/') and apk.endswith('/base.apk')
source = pathlib.Path(__file__).parent / 'fixtures/android/AudioRecoveryProbe.java'
android_jar = a.sdk / 'platforms/android-34/android.jar'
mock_jar = a.sdk / 'platforms/android-34/optional/android.test.mock.jar'
remote = '/data/local/tmp/lmpc-audio-recovery-' + uuid.uuid4().hex + '.dex'
env = dict(os.environ, JAVA_HOME=str(a.jdk))
with tempfile.TemporaryDirectory(prefix='lmpc-audio-recovery-') as temp:
    root = pathlib.Path(temp)
    shutil.copy2(source, root / source.name)
    subprocess.run([str(a.jdk / ('bin/javac.exe' if os.name == 'nt' else 'bin/javac')), '-source', '8', '-target', '8',
                    '-cp', os.pathsep.join(map(str, [android_jar, mock_jar])),
                    '-d', str(root), str(root / source.name)], env=env, check=True)
    subprocess.run([str(a.sdk / ('build-tools/34.0.0/d8.bat' if os.name == 'nt' else 'build-tools/34.0.0/d8')), '--lib', str(android_jar),
                    '--output', str(root), *map(str, root.glob('AudioRecoveryProbe*.class'))],
                   env=env, check=True)
    try:
        call(adb + ['push', str(root / 'classes.dex'), remote])
        cp = apk + ':/system/framework/android.test.mock.jar:' + remote
        result = json.loads(call(adb + ['shell', 'CLASSPATH=' + shlex.quote(cp) +
                                      ' app_process /system/bin AudioRecoveryProbe']))
        assert result['recover_pcm_to_ac3'] and result['disabled_remains_pcm']
        print(json.dumps({'device': a.device, **result}))
    finally:
        call(adb + ['shell', 'rm', '-f', remote])
