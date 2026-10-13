"""Try the iOS-patched assemblies + the iOS mod in a copy of the Windows game (no phone needed).

  python tools/pc_test.py setup  [--game "C:/.../Vigil"] [--work C:/Users/v0id/vigil-pctest]
  python tools/pc_test.py run    [--seconds 90] [--work ...] [extra player args]

setup: builds the shim / patcher / mod, patches the retail assemblies in --pc-test mode (FMOD / Steam Audio
       keep their Windows DLLs, everything else exactly as on iOS), and creates <work>/Vigil.exe with the game
       data hard-linked (no extra disk space) and the patched Managed folder. RuntimeInitializeOnLoads.json /
       ScriptingAssemblies.json get the VigilIOS + VigilShim entries so the mod starts.
run:   starts it with -simulate-touch (left mouse button = one finger, right button = a second finger),
       waits, then closes it and prints the [Vigil-iOS] lines + errors from the player log.
The work folder contains game files: it is local only and never committed anywhere.
"""
import argparse, json, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Vigil'
WORK = r'C:\Users\v0id\vigil-pctest'

MOD_INITS = [
    {"assemblyName": "VigilIOS", "nameSpace": "VigilIOS", "className": "Bootstrap", "methodName": "EarlyInit", "loadTypes": 4, "isUnityClass": False},
    {"assemblyName": "VigilIOS", "nameSpace": "VigilIOS", "className": "Bootstrap", "methodName": "Init", "loadTypes": 1, "isUnityClass": False},
]


def run(*cmd):
    print('>', ' '.join(cmd), flush=True)
    subprocess.check_call(cmd)


def link_tree(src, dst, skip_dirs=()):
    os.makedirs(dst, exist_ok=True)
    for name in os.listdir(src):
        s, d = os.path.join(src, name), os.path.join(dst, name)
        if os.path.isdir(s):
            if name in skip_dirs:
                continue
            link_tree(s, d)
        elif not os.path.exists(d):
            try:
                os.link(s, d)
            except OSError:
                shutil.copy2(s, d)


def setup(a):
    managed = os.path.join(a.game, 'Vigil_Data', 'Managed')
    data = os.path.join(a.work, 'Vigil_Data')
    out = os.path.join(data, 'Managed')
    if os.path.commonpath([os.path.abspath(a.work), ROOT]) == ROOT:
        sys.exit('the work folder must not be inside the repo')
    for f in ('Vigil.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe'):
        d = os.path.join(a.work, f)
        os.makedirs(a.work, exist_ok=True)
        if not os.path.exists(d):
            os.link(os.path.join(a.game, f), d)
    link_tree(os.path.join(a.game, 'MonoBleedingEdge'), os.path.join(a.work, 'MonoBleedingEdge'))
    link_tree(os.path.join(a.game, 'D3D12'), os.path.join(a.work, 'D3D12'))
    link_tree(os.path.join(a.game, 'Vigil_Data'), data, skip_dirs=('Managed',))
    # json files are rewritten below: never edit the hard-linked retail copies
    for j in ('RuntimeInitializeOnLoads.json', 'ScriptingAssemblies.json'):
        p = os.path.join(data, j)
        if os.path.exists(p):
            os.remove(p)
        shutil.copy2(os.path.join(a.game, 'Vigil_Data', j), p)
    if os.path.isdir(out):
        shutil.rmtree(out)
    shutil.copytree(managed, out)

    run('dotnet', 'build', os.path.join(ROOT, 'shim'), '-c', 'Release', '-nologo', '-v', 'q', f'-p:ManagedDir={managed}')
    run('dotnet', 'build', os.path.join(ROOT, 'tools', 'patcher'), '-c', 'Release', '-nologo', '-v', 'q')
    shim = os.path.join(ROOT, 'shim', 'bin', 'Release', 'netstandard2.1', 'VigilShim.dll')
    patcher = os.path.join(ROOT, 'tools', 'patcher', 'bin', 'Release', 'net8.0', 'VigilPatcher.dll')
    staged = os.path.join(a.work, 'patched')
    run('dotnet', patcher, 'patch', managed, shim, staged, '--pc-test')
    run('dotnet', 'build', os.path.join(ROOT, 'mod'), '-c', 'Release', '-nologo', '-v', 'q', '-t:Rebuild',
        f'-p:ManagedDir={managed}', f'-p:GameAsmDir={staged}')
    for f in os.listdir(staged):
        shutil.copy2(os.path.join(staged, f), os.path.join(out, f))
    shutil.copy2(os.path.join(ROOT, 'mod', 'bin', 'Release', 'netstandard2.1', 'VigilIOS.dll'), os.path.join(out, 'VigilIOS.dll'))

    p = os.path.join(data, 'RuntimeInitializeOnLoads.json')
    j = json.load(open(p, encoding='utf-8'))
    j['root'] = [e for e in j['root'] if e['assemblyName'] != 'VigilIOS'] + MOD_INITS
    json.dump(j, open(p, 'w', encoding='utf-8'), separators=(',', ':'))
    p = os.path.join(data, 'ScriptingAssemblies.json')
    j = json.load(open(p, encoding='utf-8'))
    for name in ('VigilShim.dll', 'VigilIOS.dll'):
        if name not in j['names']:
            j['names'].append(name)
            j['types'].append(16)
    json.dump(j, open(p, 'w', encoding='utf-8'), separators=(',', ':'))
    print('ready:', os.path.join(a.work, 'Vigil.exe'))


def run_game(a, extra):
    log = os.path.join(a.work, 'player.log')
    if os.path.exists(log):
        os.remove(log)
    args = [os.path.join(a.work, 'Vigil.exe'), '-simulate-touch', '-logFile', log,
            '-screen-fullscreen', '0', '-screen-width', '1290', '-screen-height', '600'] + extra
    print('>', ' '.join(args), flush=True)
    proc = subprocess.Popen(args, cwd=a.work)
    try:
        proc.wait(timeout=a.seconds)
    except subprocess.TimeoutExpired:
        subprocess.call(['taskkill', '/PID', str(proc.pid), '/T', '/F'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1)
    if not os.path.exists(log):
        print('no player log written')
        return
    lines = open(log, encoding='utf-8', errors='replace').read().splitlines()
    keep = [l for l in lines if '[Vigil-iOS]' in l or 'Exception' in l or 'rror' in l or 'FMOD' in l]
    print(f'--- {len(lines)} log lines, {len(keep)} interesting:')
    for l in keep[:300]:
        print(l[:300])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', choices=['setup', 'run'])
    ap.add_argument('--game', default=GAME)
    ap.add_argument('--work', default=WORK)
    ap.add_argument('--seconds', type=int, default=90)
    a, extra = ap.parse_known_args()
    if a.cmd == 'setup':
        setup(a)
    else:
        run_game(a, extra)


if __name__ == '__main__':
    main()
