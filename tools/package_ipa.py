"""Make the two files that go on the iPhone.

  python tools/package_ipa.py [--work C:/Users/<you>/vigil-work] [--shell path/to/Vigil-shell.ipa] [--serve]

  * Vigil.ipa       - the app (player + patched game code + FMOD / Steam Audio), from the `shell-latest`
                      release of the PRIVATE repo (downloaded with the GitHub CLI unless --shell is given).
                      Install it with TrollStore.
  * VigilData.zip   - the converted game data (tools/convert_data.py -> <work>/VigilData), stored without
                      compression (the data is already compressed). In the Files app put it in
                      On My iPhone > VIGIL and tap it; the app finds the unpacked folder by itself.
--serve starts a small web server on this PC so both files can be downloaded with Safari over Wi-Fi.
Both files contain game-derived data: never upload them anywhere public.
"""
import argparse, functools, http.server, os, shutil, socket, subprocess, sys, zipfile

PRIVATE_REPO = 'm7830380-cyber/vigil-ios-private'
SHELL_ASSET = 'Vigil-shell.ipa'
ZIP_ROOT = 'VigilData'


def zip_data(src, out):
    tmp = out + '.tmp'
    n = 0
    with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_STORED, allowZip64=True) as z:
        for root, dirs, files in os.walk(src):
            dirs.sort()
            for f in sorted(files):
                p = os.path.join(root, f)
                z.write(p, (ZIP_ROOT + '/' + os.path.relpath(p, src)).replace('\\', '/'))
                n += 1
    os.replace(tmp, out)
    return n


def lan_ip():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(('10.255.255.255', 1))     # no packet is sent; picks the interface of the default route
        return s.getsockname()[0]
    finally:
        s.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default=os.path.join(os.path.expanduser('~'), 'vigil-work'))
    ap.add_argument('--shell')
    ap.add_argument('--serve', action='store_true')
    ap.add_argument('--port', type=int, default=8642)
    a = ap.parse_args()

    out_dir = os.path.join(a.work, 'deliver')
    os.makedirs(out_dir, exist_ok=True)
    ipa = os.path.join(out_dir, 'Vigil.ipa')
    if a.shell:
        shutil.copyfile(a.shell, ipa)
    else:
        dl = os.path.join(a.work, 'dl-shell')
        shutil.rmtree(dl, ignore_errors=True)
        subprocess.check_call(['gh', 'release', 'download', 'shell-latest', '--repo', PRIVATE_REPO, '-p', SHELL_ASSET, '-D', dl])
        os.replace(os.path.join(dl, SHELL_ASSET), ipa)
        shutil.rmtree(dl, ignore_errors=True)
    with zipfile.ZipFile(ipa) as z:
        bad = z.testzip()
        if bad:
            sys.exit('broken ipa entry: %s' % bad)
    print('app :', ipa, '%.0f MB' % (os.path.getsize(ipa) / 2 ** 20))

    data = os.path.join(a.work, 'VigilData')
    if not os.path.exists(os.path.join(data, 'globalgamemanagers')):
        sys.exit('no converted data in %s - run tools/convert_data.py first' % data)
    dz = os.path.join(out_dir, 'VigilData.zip')
    n = zip_data(data, dz)
    print('data:', dz, '%.2f GB, %d files' % (os.path.getsize(dz) / 2 ** 30, n))

    if a.serve:
        url = 'http://%s:%d/' % (lan_ip(), a.port)
        print('\nOn the iPhone (same Wi-Fi), open in Safari:\n  %sVigil.ipa\n  %sVigilData.zip\nCtrl+C to stop.' % (url, url))
        handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=out_dir)
        http.server.ThreadingHTTPServer(('', a.port), handler).serve_forever()


if __name__ == '__main__':
    main()
