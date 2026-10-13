"""Convert the retail VIGIL (Windows) data so the iOS player can load it.

  python tools/convert_data.py [--game "C:/.../Vigil"] [--out C:/Users/<you>/vigil-work]

Produces <out>/VigilData/ - the folder the user copies to the app's Documents with Filza (see README).
For every serialized player file:
  * the target-platform tag changes from StandaloneWindows64 (19) to iOS (9);
  * every Shader gets its DX11 programs translated to Metal (convert_shader.py);
  * every ComputeShader gets its D3D11 kernels translated to Metal (convert_compute.py);
  * BCn textures become ASTC, BC6H / half-float lightmaps ASTC HDR, 4096 maps 2048 (transcode_tex.py);
  * .resS streams that hold re-encoded textures are rebuilt, all others are copied unchanged.
StreamingAssets (FMOD banks, probe volume data) are copied, except PBP/ (the Windows blink helper).
Resources/ is not produced: the iOS player brings its own "unity default resources" and an iOS-compiled
unity_builtin_extra with the same objects (same path ids) as the retail one.

Work is done one file at a time into <out>/VigilData, so the extra disk space is about the size of the
result (~1.5 GB) plus a small shader cache. Nothing produced here may be committed to the public repo -
it is derived from the game.
"""
import argparse, hashlib, json, os, pickle, shutil, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_shader import convert_shader as convert_one
from convert_compute import convert_compute
from transcode_tex import transcode_file

UnityPy.config.FALLBACK_UNITY_VERSION = '6000.0.26f1'
IOS_PLATFORM = 9
PLAYER_FILES = ['globalgamemanagers', 'globalgamemanagers.assets', 'level0', 'level1', 'level2', 'level3',
                'resources.assets', 'sharedassets0.assets', 'sharedassets1.assets', 'sharedassets2.assets',
                'sharedassets3.assets']
SKIP_STREAMING = {'PBP'}
DEFAULT_GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Vigil'
CONVERTER_VERSION = 'vigil-1'   # bump to invalidate the shader cache


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def cache_key(kind, name, blob):
    h = hashlib.sha1()
    h.update(CONVERTER_VERSION.encode())
    h.update(kind.encode())
    h.update(name.encode())
    h.update(blob)
    return h.hexdigest()


def cached(cache_dir, key, make):
    cp = os.path.join(cache_dir, key + '.pkl')
    if os.path.exists(cp):
        with open(cp, 'rb') as f:
            return pickle.load(f)
    res = make()
    with open(cp, 'wb') as f:
        pickle.dump(res, f)
    return res


def convert_shaders_in(sf, cache_dir, work_dir, report):
    n = 0
    for obj in sf.objects.values():
        kind = obj.type.name
        if kind not in ('Shader', 'ComputeShader'):
            continue
        tt = obj.read_typetree()
        if kind == 'Shader':
            blob = bytes(tt['compressedBlob'])
            name = tt['m_ParsedForm']['m_Name']
        else:
            name = tt['m_Name']
            blob = pickle.dumps(tt['variants'])
        key = cache_key(kind, name, blob)
        wd = os.path.join(work_dir, 'hlslcc', key)
        msgs = []

        def make():
            if kind == 'Shader':
                tt['compressedBlob'] = b''
                r = convert_one(tt, blob, wd, log=msgs.append)
            else:
                r = convert_compute(tt, wd, log=msgs.append)
            shutil.rmtree(wd, ignore_errors=True)
            return r, msgs[:50]

        res, msgs = cached(cache_dir, key, make)
        if res is None:
            report.setdefault('no_dx11', []).append(name)
            continue
        nt, stats = res
        report.setdefault('compute_shaders' if kind == 'ComputeShader' else 'shaders', {})[name] = stats
        if stats.get('fail'):
            report.setdefault('shader_msgs', {})[name] = msgs
        obj.save_typetree(nt)
        n += 1
    return n


def streams_of(src):
    """Stream files that belong to a player file: <file>.resS and, for x.assets, x.resource."""
    out = []
    for p in (src + '.resS', os.path.splitext(src)[0] + '.resource', src + '.resource'):
        if os.path.exists(p) and p not in out:
            out.append(p)
    return out


def process_player_file(name, data, out_data, cache_dir, work_dir):
    t0 = time.time()
    report = {}
    src = os.path.join(data, name)
    env = UnityPy.load(src)
    sf = list(env.files.values())[0]
    report['shaders_converted'] = convert_shaders_in(sf, cache_dir, work_dir, report)
    report['textures_converted'] = transcode_file(sf, data, out_data, report)
    sf._m_target_platform = IOS_PLATFORM
    dst = os.path.join(out_data, name)
    with open(dst + '.tmp', 'wb') as f:
        f.write(sf.save())
    os.replace(dst + '.tmp', dst)
    rebuilt = set(report.get('rebuilt_streams', []))
    for s in streams_of(src):
        rel = os.path.relpath(s, data)
        if rel not in rebuilt:
            shutil.copyfile(s, os.path.join(out_data, rel))
    report['seconds'] = round(time.time() - t0)
    return report


def job(*args):
    try:
        return process_player_file(*args)
    except Exception:
        return {'error': traceback.format_exc()}


def tree_size(p):
    return sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(p) for f in fs)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--out', default=os.path.join(os.path.expanduser('~'), 'vigil-work'))
    ap.add_argument('--only', default=None, help='comma separated player file names (testing)')
    ap.add_argument('--jobs', type=int, default=2)
    a = ap.parse_args()

    data = os.path.join(a.game, 'Vigil_Data')
    out_data = os.path.join(a.out, 'VigilData')
    cache_dir = os.path.join(a.out, 'cache', 'shaders')
    work_dir = os.path.join(a.out, 'tmp')
    os.makedirs(cache_dir, exist_ok=True)
    os.makedirs(out_data, exist_ok=True)

    files = [f for f in PLAYER_FILES if os.path.exists(os.path.join(data, f))]
    if a.only:
        files = [f for f in files if f in a.only.split(',')]
    files.sort(key=lambda f: -sum(os.path.getsize(s) for s in [os.path.join(data, f)] + streams_of(os.path.join(data, f))))

    if not a.only:
        sa = os.path.join(data, 'StreamingAssets')
        dst_sa = os.path.join(out_data, 'StreamingAssets')
        shutil.rmtree(dst_sa, ignore_errors=True)
        shutil.copytree(sa, dst_sa, ignore=lambda d, names: [n for n in names if d == sa and n in SKIP_STREAMING])
        for f in ('RuntimeInitializeOnLoads.json', 'ScriptingAssemblies.json'):
            shutil.copyfile(os.path.join(data, f), os.path.join(a.out, 'retail-' + f))

    log('converting', len(files), 'files with', a.jobs, 'jobs')
    rp = os.path.join(a.out, 'convert_report.json')
    report = json.load(open(rp)) if (a.only and os.path.exists(rp)) else {}
    t0 = time.time()
    ok = True
    with ProcessPoolExecutor(max_workers=a.jobs) as ex:
        futs = {ex.submit(job, f, data, out_data, cache_dir, work_dir): f for f in files}
        for fu in as_completed(futs):
            f = futs[fu]
            rep = fu.result()
            report[f] = rep
            if rep.get('error'):
                ok = False
                log('FAILED', f, rep['error'])
                continue
            sh = dict(rep.get('shaders', {}), **rep.get('compute_shaders', {}))
            bad = sorted(n for n, s in sh.items() if s.get('fail'))
            log('done %-26s %4ds  shaders %3d (with failures: %d)  textures %4d (errors %d)' % (
                f, rep['seconds'], rep['shaders_converted'], len(bad), rep['textures_converted'],
                len(rep.get('texture_errors', []))))
            json.dump(report, open(rp, 'w'), indent=1)
    shutil.rmtree(work_dir, ignore_errors=True)
    log('finished in %.0fs, VigilData is %.2f GB' % (time.time() - t0, tree_size(out_data) / 2 ** 30))
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
