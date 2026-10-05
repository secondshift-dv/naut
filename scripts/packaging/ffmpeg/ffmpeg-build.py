import hashlib, json, os, pathlib, re, shutil, sys, tarfile, urllib.request, zipfile

recipe = pathlib.Path('/recipe')
stage = pathlib.Path(sys.argv[2])
out = pathlib.Path(sys.argv[3])
inputs = json.loads((recipe / 'ffmpeg-build-inputs.json').read_text())

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def deterministic_zip(root, target):
    with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for path in sorted(root.rglob('*')):
            if path.is_file():
                info = zipfile.ZipInfo(path.relative_to(root).as_posix(), (2026, 10, 3, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                z.writestr(info, path.read_bytes())

if sys.argv[1] == 'prepare':
    stage.mkdir(parents=True, exist_ok=True)
    source = out / 'source'
    source.mkdir(parents=True, exist_ok=True)
    for item in inputs['sources']:
        archive = source / item['archive']
        with urllib.request.urlopen(item['url'], timeout=120) as response:
            archive.write_bytes(response.read())
        if sha(archive) != item['sha256']:
            raise RuntimeError(f"Source hash mismatch: {item['name']}")
        dest = stage / item['name']
        dest.mkdir()
        with tarfile.open(archive) as tar:
            prefix = tar.getmembers()[0].name.split('/')[0] + '/'
            for member in tar:
                if member.name.startswith(prefix) and member.name != prefix:
                    member.name = member.name[len(prefix):]
                    if member.name:
                        target = dest / member.name
                        if not target.resolve().is_relative_to(dest.resolve()):
                            raise RuntimeError('Source archive path escapes extraction root')
                        target.parent.mkdir(parents=True, exist_ok=True)
                        if member.isdir():
                            target.mkdir(exist_ok=True)
                        elif member.isfile():
                            target.write_bytes(tar.extractfile(member).read())
                            target.chmod(member.mode & 0o777)
                        elif member.issym() and (target.parent / member.linkname).resolve().is_relative_to(dest.resolve()):
                            target.symlink_to(member.linkname)
                        else:
                            raise RuntimeError('Unsupported or escaping source archive member')
    # Exact BtbN source recipes are retained; the adaptation restricts optional libraries.
    for name in ('build-ffmpeg.sh', 'ffmpeg-build.py', 'ffmpeg-build-inputs.json'):
        shutil.copyfile(recipe / name, source / name)
    (source / 'recipe-commit.txt').write_text(os.environ['NAUT_RECIPE_COMMIT']+'\n')
    sys.exit(0)

config = (out / 'evidence/config-1.mak').read_text()
for forbidden in ('CONFIG_CHROMAPRINT=yes', 'CONFIG_GPL=yes', 'CONFIG_NONFREE=yes'):
    if re.search(r"^" + re.escape(forbidden) + r"$", config, re.M):
        raise RuntimeError(f'Forbidden linked configuration: {forbidden}')
for required in ('CONFIG_LIBOPENH264=yes', 'CONFIG_MJPEG_ENCODER=yes', 'CONFIG_H264_DECODER=yes', 'CONFIG_LIBDAV1D=yes', 'CONFIG_ZLIB=yes'):
    if not re.search(r"^" + re.escape(required) + r"$", config, re.M):
        raise RuntimeError(f'Missing required configuration: {required}')
linked = {}
for tool in ('ffmpeg', 'ffprobe'):
    linkmap = (out / f'evidence/{tool}-1.map').read_text(errors='replace')
    names = sorted(set(re.findall(r'([^\s()]+\.a)\(', linkmap)))
    if not names:
        raise RuntimeError('Linker archive inventory is empty')
    for name in names:
        if re.search(r'fftw|chromaprint|x264|x265|xvid|vidstab|rubberband', name, re.I):
            raise RuntimeError(f'Forbidden linked library {name}')
        p = pathlib.Path(name)
        if name.startswith('libav') or name.startswith('libsw'):
            owner = 'ffmpeg'
            p = stage / 'ffmpeg' / p
        elif name.startswith('/opt/naut/'):
            owner = {'libopenh264.a':'openh264', 'libz.a':'zlib', 'libdav1d.a':'dav1d'}.get(p.name)
        elif name.startswith('/usr/') and ('mingw' in name or '/gcc/' in name):
            owner = 'debian-toolchain-runtime'
        else:
            owner = None
        if owner is None or not p.is_file():
            raise RuntimeError(f'Unclassified linked archive: {name}')
        linked[name] = dict(source=owner, sha256=sha(p))
    pe = (out / f'evidence/{tool}-pe.txt').read_text()
    if re.search(r'DLL Name:.*(?:openh264|chromaprint|fftw|libgcc|libstdc|libwinpthread)', pe, re.I):
        raise RuntimeError('Unexpected non-system dynamic dependency')

payload = out / 'payload'
(payload / 'bin').mkdir(parents=True)
(payload / 'LICENSES').mkdir()
licenses = [('ffmpeg','COPYING.LGPLv3','FFmpeg-LGPL-3.0.txt'), ('ffmpeg','COPYING.GPLv3','FFmpeg-GPL-3.0.txt'), ('openh264','LICENSE','OpenH264-BSD-2-Clause.txt'), ('zlib','LICENSE','zlib-LICENSE.txt'), ('dav1d','COPYING','dav1d-BSD-2-Clause.txt'), ('btbn','LICENSE','BtbN-FFmpeg-Builds-LICENSE.txt')]
for component, source, target in licenses:
    shutil.copyfile(stage / component / source, payload / 'LICENSES' / target)
for path in (out / 'licenses').glob('*'):
    shutil.copyfile(path, payload / 'LICENSES' / path.name)
shutil.copyfile(stage / 'dav1d/doc/PATENTS', payload / 'LICENSES/dav1d-PATENTS.txt')
headers = {}
for component in ('ffmpeg', 'openh264', 'zlib', 'dav1d'):
    component_root = stage / component
    if component == 'ffmpeg':
        paths = set()
        for dependency in component_root.rglob('*.d'):
            for name in re.findall(r'[^\s\\:]+\.(?:c|h|S|asm|inc)', dependency.read_text(errors='replace')):
                path = component_root / name
                if not path.resolve().is_relative_to(component_root.resolve()):
                    # Installed library headers are covered by their exact source inventories;
                    # compiler headers are covered by the accompanying toolchain copyright/source.
                    if name.startswith('/opt/naut/include/') or name.startswith('/usr/x86_64-w64-mingw32/include/') or name.startswith('/usr/lib/gcc/x86_64-w64-mingw32/'):
                        continue
                    raise RuntimeError('Unclassified compiled source/header dependency: '+name)
                if path.is_file():
                    paths.add(path)
    else:
        paths = {p for p in component_root.rglob('*') if p.suffix in ('.c', '.h', '.cpp', '.S', '.asm', '.inc') and not any(part in ('build','test','tests','contrib','autotest') for part in p.relative_to(component_root).parts)}
    for path in sorted(paths):
        content = path.read_text(errors='replace').lstrip('\ufeff\r\n ')
        match = re.match(r'(?s)(?:(?:/\*.*?\*/|//[^\n]*|;[^\n]*)\s*)+', content)
        if match and re.search(r'copyright|redistribution|license|public domain', match[0], re.I):
            headers.setdefault(match[0].strip(), []).append(component+'/'+path.relative_to(component_root).as_posix())
if not headers:
    raise RuntimeError('Source copyright notice inventory is empty')
(payload/'LICENSES/Source-Copyright-Notices.txt').write_text('\n\n'.join('Files: '+', '.join(names)+'\n'+header for header,names in sorted(headers.items()))+'\n')
shutil.copyfile(stage / 'ffmpeg/COPYING.LGPLv3', payload / 'LICENSE.txt')
for tool in ('ffmpeg.exe','ffprobe.exe'):
    shutil.copyfile(out / 'pass-1' / tool, payload / 'bin' / tool)
notices = 'FFmpeg/ffprobe: LGPL-3.0-or-later, exact source and configuration in provenance.json.\nOpenH264: BSD-2-Clause, exact source commit '+inputs['sources'][2]['commit']+'; statically linked.\nzlib: zlib license; statically linked.\ndav1d: BSD-2-Clause; statically linked.\nBtbN recipe: MIT; exact revision recorded in provenance.json.\nMinGW-w64 runtime/import libraries and GCC runtime with runtime-library exceptions: see Debian copyright files.\n'+inputs['openH264PatentStatement']+'\nComplete copyright/license texts: LICENSES/.\n'
(payload / 'THIRD-PARTY-NOTICES.txt').write_text(notices)
proof = dict(schemaVersion=1, inputs=inputs, recipeCommit=os.environ['NAUT_RECIPE_COMMIT'],
             recipeHashes={n:sha(recipe/n) for n in ('build-ffmpeg.sh','ffmpeg-build.py','ffmpeg-build-inputs.json')},
             reproducibility='Two complete FFmpeg/ffprobe builds compared byte-for-byte; both equal.',
             binaryHashes={n:sha(payload/'bin'/n) for n in ('ffmpeg.exe','ffprobe.exe')},
             license=inputs['license'], licenseHashes={p.name:sha(p) for p in (payload/'LICENSES').glob('*')},
             linkedArchives=linked,
             sourceFiles={p.relative_to(out/'source').as_posix():sha(p) for p in (out/'source').rglob('*') if p.is_file()})
(payload/'provenance.json').write_text(json.dumps(proof, indent=2)+'\n')
# Preserve deterministic linker/compiler/configuration records in the source companion.
# Parallel compilation transcripts and configure scratch logs are diagnostic outputs.
(out/'source/evidence').mkdir()
for evidence in sorted((out/'evidence').iterdir()):
    if evidence.suffix != '.log':
        shutil.copyfile(evidence, out/'source/evidence'/evidence.name)
shutil.copyfile(payload/'provenance.json', out/'source/provenance.json')
deterministic_zip(out/'source', out/'ffmpeg-naut-corresponding-source.zip')
proof['sourceCompanionSha256'] = sha(out/'ffmpeg-naut-corresponding-source.zip')
(payload/'provenance.json').write_text(json.dumps(proof, indent=2)+'\n')
deterministic_zip(payload, out/'ffmpeg-naut-win64-lgpl.zip')
(out/'checksums.json').write_text(json.dumps({p.name:sha(p) for p in (out/'ffmpeg-naut-win64-lgpl.zip',out/'ffmpeg-naut-corresponding-source.zip')},indent=2)+'\n')
