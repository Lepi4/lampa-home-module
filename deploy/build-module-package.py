#!/usr/bin/env python3
"""Build a HomeMedia panel-upload package without including runtime state."""
import argparse, hashlib, io, json, pathlib, tarfile

def build(source, output, version):
    source = pathlib.Path(source).resolve(); output = pathlib.Path(output).resolve()
    if not source.is_dir() or output.is_relative_to(source): raise SystemExit('invalid source/output')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(output, 'w:gz') as tf:
        manifest = {'component':'homemedia','version':version,'compatibility_verified':True}
        raw = json.dumps(manifest, separators=(',',':')).encode()
        m = tarfile.TarInfo('manifest.json'); m.size = len(raw); tf.addfile(m, io.BytesIO(raw))
        blocked = {'.git','__pycache__','.idea','bin','obj'}
        for p in sorted(source.rglob('*')):
            rel = p.relative_to(source)
            if any(x in blocked for x in rel.parts) or p.is_symlink() or not p.is_file(): continue
            tf.add(p, arcname=str(pathlib.PurePosixPath('payload', *rel.parts)), recursive=False)
    return hashlib.sha256(output.read_bytes()).hexdigest()

if __name__ == '__main__':
    ap=argparse.ArgumentParser();ap.add_argument('--source',default='../server-mod/HomeMedia');ap.add_argument('--output',default='../release/homemedia-built.tar.gz');ap.add_argument('--version',required=True);a=ap.parse_args()
    print(json.dumps({'file':str(pathlib.Path(a.output).resolve()),'sha256':build(a.source,a.output,a.version)}))
