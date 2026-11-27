"""Launch an extracted desktop build against an isolated workspace."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time


def smoke_startup(archive):
    executables = {
        "win32": "Sharpcode/Sharpcode.exe",
        "linux": "Sharpcode/Sharpcode",
        "darwin": "Sharpcode.app/Contents/MacOS/Sharpcode",
    }
    with tempfile.TemporaryDirectory(prefix="sharpcode-smoke-") as temporary:
        sandbox = Path(temporary)
        package = sandbox / "package"
        extract_filter = None if archive.suffix == ".zip" else "data"
        shutil.unpack_archive(archive, package, filter=extract_filter)
        executable = package / executables[sys.platform]
        workspace = sandbox / "Smoke Workspace"
        workspace.mkdir()
        document = workspace / "Launch Fixture.txt"
        document.write_text("Sharpcode startup smoke test\n", encoding="utf-8")

        data_directory = sandbox / "settings"
        preferences = data_directory / "settings.json"
        environment = {**os.environ, "SHARPCODE_DATA_DIR": str(data_directory)}
        log = sandbox / "startup.log"
        with log.open("w+b") as output:
            process = subprocess.Popen([executable, document], cwd=executable.parent, env=environment,
                                       stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 45
                while time.monotonic() < deadline:
                    if process.poll() is not None:
                        raise RuntimeError(f"Sharpcode exited during startup with code {process.returncode}")
                    if preferences.is_file():
                        saved_workspace = json.loads(preferences.read_text(encoding="utf-8")).get("LastWorkspace")
                        if saved_workspace and Path(saved_workspace).resolve() == workspace.resolve():
                            print("Sharpcode opened the fixture workspace from the packaged executable.")
                            return
                    time.sleep(0.2)
                raise RuntimeError("Sharpcode did not open the fixture workspace within 45 seconds")
            except RuntimeError:
                output.seek(0)
                sys.stderr.write(output.read().decode(errors="replace"))
                raise
            finally:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    smoke_startup(parser.parse_args().archive)
