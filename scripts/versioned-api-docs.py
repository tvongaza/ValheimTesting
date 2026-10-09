#!/usr/bin/env python3
"""Keep immutable 0.1 API snapshots in GitHub releases and assemble them for Pages.

Release tags build the reference from their own source commit and attach an archive.
The Pages workflow builds today's reference, then installs the checked archives from
published releases under versions/<package-version>/. No generated site is committed.
"""

import argparse
import hashlib
import html
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import tarfile
import time
from urllib.error import HTTPError, URLError
import urllib.request
import zipfile
from xml.etree import ElementTree


VERSION = re.compile(r"0\.1\.0(?:-rc\.[1-9][0-9]*)?\Z")
ASSET = re.compile(r"api-reference-(0\.1\.0(?:-rc\.[1-9][0-9]*)?)\.tgz\Z")


def package_version(files: list[str]) -> str:
    """Return one coordinated 0.1 RC/final version, or nothing for previews."""
    versions = set()
    if len(files) != 9:
        raise ValueError(f"Expected all nine release packages, found {len(files)}")
    for file in files:
        with zipfile.ZipFile(file) as package:
            nuspecs = [name for name in package.namelist() if name.endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise ValueError(f"{file}: expected exactly one nuspec")
            tree = ElementTree.fromstring(package.read(nuspecs[0]))
            metadata = next((e for e in tree if e.tag.rsplit("}", 1)[-1] == "metadata"), None)
            version = next((e.text for e in metadata if e.tag.rsplit("}", 1)[-1] == "version"), None) if metadata is not None else None
            if not version:
                raise ValueError(f"{file}: missing nuspec version")
            versions.add(version)
    if len(versions) != 1 and any(VERSION.fullmatch(version) for version in versions):
        raise ValueError("A coordinated 0.1 RC/final release cannot mix package versions: " + ", ".join(sorted(versions)))
    if len(versions) != 1:
        return ""
    version = versions.pop()
    return version if VERSION.fullmatch(version) else ""


def make_archive(site: Path, version: str, commit: str, output: Path) -> None:
    if not VERSION.fullmatch(version) or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("An archive needs a coordinated 0.1 version and full source commit")
    if not (site / "index.html").is_file() or not (site / "api").is_dir():
        raise ValueError("Build the DocFX site before archiving it")
    if (site / "manifest.json").exists():
        raise ValueError("DocFX's absolute-path manifest must not enter a release archive")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(output, "w:gz", format=tarfile.PAX_FORMAT) as archive:
        metadata = json.dumps({"version": version, "sourceCommit": commit}, sort_keys=True).encode()
        item = tarfile.TarInfo("version.json")
        item.size = len(metadata)
        item.mode = 0o644
        archive.addfile(item, io.BytesIO(metadata))
        for file in sorted(site.rglob("*")):
            if file.is_symlink():
                raise ValueError(f"Site contains a symlink: {file}")
            if file.is_dir():
                continue
            relative = file.relative_to(site).as_posix()
            if relative == "version.json" or relative.startswith("versions/"):
                continue  # Never put the current site's snapshot index inside a snapshot.
            if file.suffix == ".html":
                page = file.read_bytes()
                # Generated XML examples and the site home can link to current main.
                # A release snapshot must instead point to the source that produced it.
                for kind in (b"blob", b"tree"):
                    page = page.replace(
                        b"https://github.com/tvongaza/ValheimTesting/" + kind + b"/main/",
                        b"https://github.com/tvongaza/ValheimTesting/" + kind + b"/" + commit.encode() + b"/",
                    )
                if relative != "index.html":
                    item = tarfile.TarInfo(relative)
                    item.size = len(page)
                    item.mode = 0o644
                    archive.addfile(item, io.BytesIO(page))
                    continue
                page = page.replace(b"ValheimTesting API reference (preview)",
                                    f"ValheimTesting API reference ({version})".encode())
                page = page.replace(b"This reference is generated from the current package sources.",
                                    f"This reference is generated from release {version}'s package sources.".encode())
                if b"</h1>" not in page:
                    raise ValueError("DocFX's API home has no heading for the release banner")
                banner = (f'<p>Release <strong>{version}</strong>, built from source '
                          f'<code>{commit[:12]}</code>. This reference is immutable; '
                          '<a href="../../">the unversioned site</a> follows current source.</p>').encode()
                page = page.replace(b"</h1>", b"</h1>" + banner, 1)
                item = tarfile.TarInfo(relative)
                item.size = len(page)
                item.mode = 0o644
                archive.addfile(item, io.BytesIO(page))
                continue
            archive.add(file, arcname=relative, recursive=False)


def checksum(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def install_archive(data: bytes, expected_sha256: str, version: str, site: Path) -> None:
    if not VERSION.fullmatch(version) or checksum(data) != expected_sha256.lower():
        raise ValueError(f"API archive {version}: version or SHA-256 mismatch")
    destination = site / "versions" / version
    if destination.exists():
        raise ValueError(f"Duplicate API reference version: {version}")
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        members = archive.getmembers()
        metadata = next((member for member in members if member.name == "version.json"), None)
        if metadata is None or not metadata.isfile():
            raise ValueError(f"API archive {version}: missing version metadata")
        recorded = json.load(archive.extractfile(metadata))
        if recorded.get("version") != version or not re.fullmatch(r"[0-9a-f]{40}", recorded.get("sourceCommit", "")):
            raise ValueError(f"API archive {version}: invalid version metadata")
        names = set()
        for member in members:
            path = PurePosixPath(member.name)
            if (path.is_absolute() or ".." in path.parts or "\\" in member.name
                    or str(path) in names or str(path) == "." or not (member.isfile() or member.isdir())):
                raise ValueError(f"API archive {version}: unsafe member {member.name}")
            names.add(str(path))
        destination.mkdir(parents=True)
        for member in members:
            target = destination.joinpath(*PurePosixPath(member.name).parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as source, target.open("wb") as written:
                    written.write(source.read())
    if not (destination / "index.html").is_file() or not (destination / "api").is_dir():
        raise ValueError(f"API archive {version}: missing reference pages")


def get(url: str, token: str | None = None) -> bytes:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "ValheimTesting-versioned-api-docs",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.read()
        except HTTPError as error:
            if error.code not in (429, 500, 502, 503, 504) or attempt == 3:
                raise
        except (URLError, TimeoutError):
            if attempt == 3:
                raise
        time.sleep(2 ** attempt)
    raise AssertionError("unreachable")


def published_archives(repository: str, token: str):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Expected GITHUB_REPOSITORY=owner/repo")
    page = 1
    while True:
        releases = json.loads(get(f"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}", token))
        if not releases:
            break
        for release in releases:
            if release.get("draft"):
                continue
            assets = {asset["name"]: asset for asset in release.get("assets", [])}
            for name, asset in assets.items():
                match = ASSET.fullmatch(name)
                if not match:
                    continue
                sums = assets.get("SHA256SUMS")
                if sums is None:
                    raise ValueError(f"{release['tag_name']}: reference archive lacks SHA256SUMS")
                # These public download URLs redirect to another host: never forward the API token.
                lines = get(sums["browser_download_url"]).decode().splitlines()
                digests = [line.split(maxsplit=1)[0] for line in lines if line.endswith("  " + name)]
                if len(digests) != 1 or not re.fullmatch(r"[0-9a-fA-F]{64}", digests[0]):
                    raise ValueError(f"{release['tag_name']}: no unique SHA-256 for {name}")
                yield match.group(1), get(asset["browser_download_url"]), digests[0]
        page += 1


def write_index(site: Path) -> None:
    (site / "versions").mkdir(parents=True, exist_ok=True)
    versions = sorted((entry.name for entry in (site / "versions").iterdir() if entry.is_dir()),
                      key=lambda v: (v == "0.1.0", int(v.rsplit(".", 1)[-1]) if "-rc." in v else 0), reverse=True)
    links = "\n".join(f'<li><a href="{html.escape(v)}/">{html.escape(v)}</a></li>' for v in versions)
    (site / "versions" / "index.html").write_text(
        '<!doctype html><html lang="en"><meta charset="utf-8"><title>Versioned ValheimTesting API references</title>'
        '<h1>Versioned ValheimTesting API references</h1><p><a href="../">Current reference</a></p>'
        + (f"<ul>{links}</ul>" if links else "<p>No 0.1 release candidate has been published yet.</p>") + "</html>\n"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    version = sub.add_parser("version", help="Print one coordinated 0.1 package version; blank for previews")
    version.add_argument("packages", nargs="+")
    archive = sub.add_parser("archive", help="Archive an already-checked DocFX site")
    archive.add_argument("site", type=Path)
    archive.add_argument("version")
    archive.add_argument("commit")
    archive.add_argument("output", type=Path)
    install = sub.add_parser("install", help="Install one local archive (for tests and offline verification)")
    install.add_argument("archive", type=Path)
    install.add_argument("sha256")
    install.add_argument("version")
    install.add_argument("site", type=Path)
    assemble = sub.add_parser("assemble", help="Install published 0.1 archives beside the current Pages site")
    assemble.add_argument("site", type=Path)
    args = parser.parse_args()
    if args.command == "version":
        print(package_version(args.packages))
    elif args.command == "archive":
        make_archive(args.site, args.version, args.commit, args.output)
        print(f"Archived {args.version}: {args.output} ({checksum(args.output.read_bytes())})")
    elif args.command == "install":
        install_archive(args.archive.read_bytes(), args.sha256, args.version, args.site)
        write_index(args.site)
    else:
        token = os.environ["GH_TOKEN"]
        repository = os.environ["GITHUB_REPOSITORY"]
        seen = set()
        for version, data, digest in published_archives(repository, token):
            if version in seen:
                raise ValueError(f"More than one published release contains API reference {version}")
            seen.add(version)
            install_archive(data, digest, version, args.site)
        write_index(args.site)
        print("Versioned API references: " + (", ".join(sorted(seen)) if seen else "none yet"))


if __name__ == "__main__":
    main()
