"""Build the source ZIP with Linux-readable file permissions for Azure remote build."""
import argparse
import pathlib
import stat
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('output', type=pathlib.Path)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parent
with zipfile.ZipFile(args.output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name in ('function_app.py', 'host.json', 'requirements.txt'):
        entry = zipfile.ZipInfo(name)
        entry.create_system = 3
        entry.external_attr = (stat.S_IFREG | 0o644) << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, (root / name).read_bytes())
print(args.output)
