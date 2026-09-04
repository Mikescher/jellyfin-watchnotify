#!/usr/bin/env python3

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "Jellyfin.Plugin.WatchNotify" / "Jellyfin.Plugin.WatchNotify.csproj"
PROPS = ROOT / "Directory.Build.props"
MANIFEST = ROOT / "manifest.json"
ARTIFACTS = ROOT / "artifacts"
RELEASES = ROOT / "releases"
ASSEMBLY = "Jellyfin.Plugin.WatchNotify.dll"

GUID = "295982d0-fc94-4c98-9bf4-54bc7cf05b30"
NAME = "WatchNotify"
OWNER = "Mikescher"
CATEGORY = "Notifications"
OVERVIEW = "Notifies SCN and Joplin when a movie or episode is watched"
DESCRIPTION = (
    "Watches playback events and, when a movie or episode is watched to completion, "
    "sends a Simple Cloud Notifier push and appends a line to a Joplin watch-log note."
)
TARGET_ABI = "10.11.0.0"
FRAMEWORK = "net9.0"
REPO_URL = "https://github.com/Mikescher/jellyfin-watchnotify"
RAW_URL = "https://raw.githubusercontent.com/Mikescher/jellyfin-watchnotify/master"

VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")


def read_version() -> str:
    match = re.search(r"<Version>(.*?)</Version>", PROPS.read_text(encoding="utf-8"))
    if not match:
        sys.exit(f"no <Version> element in {PROPS}")
    return match.group(1)


def set_version(version: str) -> None:
    text = PROPS.read_text(encoding="utf-8")
    for tag in ("Version", "AssemblyVersion", "FileVersion"):
        text = re.sub(rf"<{tag}>.*?</{tag}>", f"<{tag}>{version}</{tag}>", text)
    PROPS.write_text(text, encoding="utf-8")


def publish(version: str) -> Path:
    out = ARTIFACTS / "publish"
    if out.exists():
        shutil.rmtree(out)
    subprocess.run(
        ["dotnet", "publish", str(PROJECT), "-c", "Release", "-f", FRAMEWORK, "-o", str(out)],
        check=True,
    )
    dll = out / ASSEMBLY
    if not dll.is_file():
        sys.exit(f"{dll} was not produced")
    return dll


def write_meta(version: str, changelog: str, timestamp: str) -> Path:
    meta = {
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "description": DESCRIPTION,
        "owner": OWNER,
        "category": CATEGORY,
        "targetAbi": TARGET_ABI,
        "version": version,
        "timestamp": timestamp,
        "changelog": changelog,
        "assemblies": [ASSEMBLY],
    }
    path = ARTIFACTS / "publish" / "meta.json"
    path.write_text(json.dumps(meta, indent=4) + "\n", encoding="utf-8")
    return path


def write_zip(version: str, dll: Path, meta: Path) -> Path:
    path = ARTIFACTS / f"watchnotify_{version}.zip"
    # Both files sit at the zip root; the server rejects a nested layout.
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(dll, ASSEMBLY)
        archive.write(meta, "meta.json")
    return path


def md5(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def update_manifest(version: str, changelog: str, timestamp: str, checksum: str, source_url: str) -> None:
    packages = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else []

    package = next((p for p in packages if p.get("guid", "").lower() == GUID), None)
    if package is None:
        package = {
            "guid": GUID,
            "name": NAME,
            "description": DESCRIPTION,
            "overview": OVERVIEW,
            "owner": OWNER,
            "category": CATEGORY,
            "versions": [],
        }
        packages.append(package)

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": timestamp,
    }

    # Newest first; the server picks the highest entry its ABI satisfies.
    package["versions"] = [entry] + [v for v in package["versions"] if v.get("version") != version]

    MANIFEST.write_text(json.dumps(packages, indent=4) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="Package the plugin and update the repository manifest.")
    parser.add_argument("--version", help="four-part version to build; defaults to Directory.Build.props")
    parser.add_argument("--changelog", default="", help="changelog text for this version")
    parser.add_argument("--manifest", action="store_true", help="also add the version to manifest.json")
    parser.add_argument(
        "--in-repo",
        action="store_true",
        help="keep the zip in releases/ and point the manifest at the raw file instead of a release asset",
    )
    args = parser.parse_args()

    version = args.version or read_version()
    if not VERSION_RE.match(version):
        sys.exit(f"version must be four numeric parts, got {version!r}")

    if args.version:
        set_version(version)

    ARTIFACTS.mkdir(exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    dll = publish(version)
    meta = write_meta(version, args.changelog, timestamp)
    archive = write_zip(version, dll, meta)

    if args.in_repo:
        RELEASES.mkdir(exist_ok=True)
        archive = Path(shutil.copy2(archive, RELEASES / archive.name))
        source_url = f"{RAW_URL}/releases/{archive.name}"
    else:
        source_url = f"{REPO_URL}/releases/download/v{version}/{archive.name}"

    checksum = md5(archive)

    if args.manifest:
        update_manifest(version, args.changelog, timestamp, checksum, source_url)

    print(f"{archive}  md5={checksum}")


if __name__ == "__main__":
    main()
