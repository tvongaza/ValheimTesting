#!/usr/bin/env python3
"""Local test pyramid layers only; never launches Valheim."""
import pathlib, subprocess
ROOT = pathlib.Path(__file__).resolve().parents[1]
def run(*args): subprocess.run(args, cwd=ROOT, check=True)
run('dotnet', 'test', 'tests/Valheim.Testing.Tests/Valheim.Testing.Tests.csproj', '-c', 'Release', '-m:1')
for project in sorted((ROOT / 'examples').glob('*/*.csproj')):
    run('dotnet', 'build', str(project), '-c', 'Release', '-m:1')
run('dotnet', 'run', '--project', 'examples/NoGameTerrain', '-c', 'Release', '--no-build')
run('dotnet', 'run', '--project', 'examples/SharedWorld', '-c', 'Release', '--no-build')
for name in ('Valheim.Testing', 'Valheim.Testing.Game'):
    run('dotnet', 'pack', f'src/{name}/{name}.csproj', '-c', 'Release', '--no-restore', '-m:1', '-o', str(ROOT / '.packages'))
