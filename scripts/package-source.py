"""Package reproducible project inputs, excluding local SDKs, caches and generated binaries."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parents[1]
output = root / 'artifacts' / 'PlotPick-0.3.0-source.zip'
folders = {'src', 'tests', 'scripts', 'assets', 'samples', 'docs', 'installer'}
files = {'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'Directory.Build.props',
         'global.json', 'NuGet.Config', '.gitignore'}
with ZipFile(output, 'w', ZIP_DEFLATED, compresslevel=6) as archive:
    for path in sorted(root.rglob('*')):
        relative = path.relative_to(root)
        if relative.parts[0] not in folders and str(relative) not in files:
            continue
        if not path.is_file() or any(p in {'bin', 'obj', '.tools', '__pycache__'} for p in relative.parts):
            continue
        if path.suffix in {'.bak', '.tmp', '.pyc'}:
            continue
        archive.write(path, 'plotpick/' + relative.as_posix())
print(output)
