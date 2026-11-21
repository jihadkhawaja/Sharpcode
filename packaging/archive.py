"""Package a self-contained Sharpcode publish directory."""

import argparse
from pathlib import Path
import tarfile
import zipfile


def unix_permissions(info):
    if info.isdir():
        info.mode = 0o755
    elif info.isfile():
        executable = Path(info.name).name == "Sharpcode" or bool(info.mode & 0o111)
        info.mode = 0o755 if executable else 0o644
    return info


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rid", choices=("win-x64", "linux-x64", "osx-arm64", "osx-x64"))
    parser.add_argument("publish", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    windows = args.rid.startswith("win-")
    macos = args.rid.startswith("osx-")
    executable = "Sharpcode.exe" if windows else "Sharpcode"
    for name in (executable, "Sharpcode.dll", "Sharpcode.deps.json"):
        if not (args.publish / name).is_file():
            parser.error(f"Publish directory is missing {name}")

    args.output.mkdir(parents=True, exist_ok=True)
    name = f"Sharpcode-{args.rid}"
    if windows:
        target = args.output / f"{name}.zip"
        with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for file in sorted(args.publish.rglob("*")):
                if file.is_file():
                    archive.write(file, Path("Sharpcode") / file.relative_to(args.publish))
    else:
        target = args.output / f"{name}.tar.gz"
        with tarfile.open(target, "w:gz", compresslevel=6) as archive:
            if macos:
                resources = Path(__file__).with_name("macos")
                archive.add(resources / "Info.plist", arcname="Sharpcode.app/Contents/Info.plist", filter=unix_permissions)
                archive.add(resources / "Sharpcode.icns", arcname="Sharpcode.app/Contents/Resources/Sharpcode.icns",
                            filter=unix_permissions)
            prefix = "Sharpcode.app/Contents/MacOS" if macos else "Sharpcode"
            archive.add(args.publish, arcname=prefix, filter=unix_permissions)
            if not macos:
                icon = Path(__file__).resolve().parents[1] / "src" / "Sharpcode" / "Assets" / "Sharpcode.png"
                archive.add(icon, arcname="Sharpcode/Sharpcode.png", filter=unix_permissions)
    print(target)


if __name__ == "__main__":
    main()
