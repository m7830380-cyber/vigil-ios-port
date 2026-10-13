"""Extract selected files from a .unitypackage (gzip tar of <guid>/{pathname,asset}).

  python tools/unitypackage_extract.py <package> <outDir> <regex> [--list]

Every asset whose original path matches <regex> is written to <outDir>/<basename>.
Used to take only the iOS static libraries out of SDK packages (Steam Audio, FMOD for Unity).
"""
import os, re, sys, tarfile


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    list_only = '--list' in sys.argv
    pkg, out, pattern = args[0], args[1], re.compile(args[2], re.I)
    paths, assets = {}, {}
    with tarfile.open(pkg, 'r:gz') as tf:
        # first pass: pathnames (small)
        for m in tf:
            parts = m.name.strip('./').split('/')
            if len(parts) == 2 and parts[1] == 'pathname':
                paths[parts[0]] = tf.extractfile(m).read().decode('utf-8', 'replace').splitlines()[0].strip()
    wanted = {g for g, p in paths.items() if pattern.search(p)}
    for g in sorted(wanted, key=lambda g: paths[g]):
        print(paths[g])
    if list_only:
        return
    os.makedirs(out, exist_ok=True)
    with tarfile.open(pkg, 'r:gz') as tf:
        for m in tf:
            parts = m.name.strip('./').split('/')
            if len(parts) == 2 and parts[1] == 'asset' and parts[0] in wanted:
                dst = os.path.join(out, os.path.basename(paths[parts[0]]))
                with tf.extractfile(m) as src, open(dst, 'wb') as fh:
                    while True:
                        b = src.read(1 << 20)
                        if not b:
                            break
                        fh.write(b)
                print('  ->', dst, os.path.getsize(dst))


if __name__ == '__main__':
    main()
