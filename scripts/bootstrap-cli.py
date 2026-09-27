#!/usr/bin/env python3
"""Build the exact fork dependency into a local feed. No game/build installation needed."""
import argparse, json, pathlib, subprocess, tempfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
def run(*args, **kwargs): subprocess.run(args, check=True, **kwargs)
def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=pathlib.Path, help='Optional existing Git repository; exports the pinned commit, never its working tree')
    args = parser.parse_args()
    pin = json.loads((ROOT / 'cli-dependency.json').read_text())
    feed = ROOT / '.packages'; feed.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='valheim-cli-dependency-') as temp:
        temp = pathlib.Path(temp); repo = args.source.resolve() if args.source else temp / 'checkout'
        if not args.source:
            run('git', 'init', '-q', str(repo))
            run('git', '-C', str(repo), 'fetch', '--depth=1', pin['repository'], pin['commit'])
        actual = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', pin['commit']+'^{commit}'], text=True).strip()
        if actual != pin['commit']: raise RuntimeError('CLI revision mismatch')
        archive = temp / 'source.tar'
        run('git', '-C', str(repo), 'archive', '--format=tar', '-o', str(archive), actual)
        source = temp / 'source'; source.mkdir()
        # git archive contains tracked source only, never a dirty worktree or artifacts.
        import tarfile
        with tarfile.open(archive) as tar: tar.extractall(source, filter='data')
        project = source / 'Toolkit/Valheim.Cli.Testing/Valheim.Cli.Testing.csproj'
        if '<Version>'+pin['version']+'</Version>' not in project.read_text(): raise RuntimeError('CLI package version mismatch')
        run('dotnet', 'pack', str(project), '-c', 'Release', '-m:1', '-o', str(feed))
    print('Pinned CLI dependency ready:', feed)
if __name__ == '__main__': main()
