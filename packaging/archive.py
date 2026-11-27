"""Package a self-contained Sharpcode publish directory."""

import argparse
import json
from pathlib import Path
import tarfile
import tempfile
import zipfile


def unix_permissions(info):
    if info.isdir():
        info.mode = 0o755
    elif info.isfile():
        executable = Path(info.name).name in {"Sharpcode", "createdump"} or bool(info.mode & 0o111)
        info.mode = 0o755 if executable else 0o644
    return info


def validate_publish(publish, rid):
    executable = "Sharpcode.exe" if rid.startswith("win-") else "Sharpcode"
    runtime_files = {
        "win": ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll"),
        "linux": ("libcoreclr.so", "libhostfxr.so", "libhostpolicy.so"),
        "osx": ("libcoreclr.dylib", "libhostfxr.dylib", "libhostpolicy.dylib"),
    }
    required = (
        executable, "Sharpcode.dll", "Sharpcode.deps.json", "Sharpcode.runtimeconfig.json", "LICENSE",
        *runtime_files[rid.split("-")[0]],
    )
    missing = [name for name in required if not (publish / name).is_file()]
    if missing:
        raise ValueError(f"Publish directory is missing {', '.join(missing)}")

    with (publish / "Sharpcode.deps.json").open(encoding="utf-8") as manifest:
        runtime_target = json.load(manifest).get("runtimeTarget", {}).get("name", "")
    if not runtime_target.endswith(f"/{rid}"):
        raise ValueError(f"Publish directory targets {runtime_target or 'no runtime'}, expected {rid}")


def write_archive(publish, rid, target):
    windows = rid.startswith("win-")
    macos = rid.startswith("osx-")
    if windows:
        with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for file in sorted(publish.rglob("*")):
                if file.is_file():
                    archive.write(file, Path("Sharpcode") / file.relative_to(publish))
    else:
        with tarfile.open(target, "w:gz", compresslevel=6) as archive:
            if macos:
                resources = Path(__file__).with_name("macos")
                archive.add(resources / "Info.plist", arcname="Sharpcode.app/Contents/Info.plist", filter=unix_permissions)
                archive.add(resources / "Sharpcode.icns", arcname="Sharpcode.app/Contents/Resources/Sharpcode.icns",
                            filter=unix_permissions)
            prefix = "Sharpcode.app/Contents/MacOS" if macos else "Sharpcode"
            archive.add(publish, arcname=prefix, filter=unix_permissions)
            if not macos:
                icon = Path(__file__).resolve().parents[1] / "src" / "Sharpcode" / "Assets" / "Sharpcode.png"
                archive.add(icon, arcname="Sharpcode/Sharpcode.png", filter=unix_permissions)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rid", choices=("win-x64", "linux-x64", "osx-arm64", "osx-x64"))
    parser.add_argument("publish", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        validate_publish(args.publish, args.rid)
    except (OSError, ValueError) as error:
        parser.error(str(error))

    if args.output.resolve().is_relative_to(args.publish.resolve()):
        parser.error("The output directory must be outside the publish directory")

    args.output.mkdir(parents=True, exist_ok=True)
    extension = ".zip" if args.rid.startswith("win-") else ".tar.gz"
    target = args.output / f"Sharpcode-{args.rid}{extension}"
    with tempfile.TemporaryDirectory(dir=args.output) as staging:
        temporary = Path(staging) / target.name
        write_archive(args.publish, args.rid, temporary)
        temporary.replace(target)
    print(target)


if __name__ == "__main__":
    main()
