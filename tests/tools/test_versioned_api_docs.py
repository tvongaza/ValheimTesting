"""Focused offline checks for the 0.1 release's immutable API reference."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
import zipfile


SOURCE = Path(__file__).resolve().parents[2] / "scripts" / "versioned-api-docs.py"
spec = importlib.util.spec_from_file_location("versioned_api_docs", SOURCE)
docs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(docs)


class VersionedApiDocsTests(unittest.TestCase):
    def test_nine_package_release_requires_one_rc_or_final_version(self):
        with tempfile.TemporaryDirectory() as directory:
            files = []
            for index in range(9):
                path = Path(directory) / f"package{index}.nupkg"
                with zipfile.ZipFile(path, "w") as package:
                    package.writestr(f"package{index}.nuspec", "<package><metadata><id>Test</id>"
                                         "<version>0.1.0-rc.1</version></metadata></package>")
                files.append(str(path))
            self.assertEqual("0.1.0-rc.1", docs.package_version(files))
            with zipfile.ZipFile(files[0], "w") as package:
                package.writestr("package0.nuspec", "<package><metadata><id>Test</id>"
                                     "<version>0.1.0-preview.1</version></metadata></package>")
            with self.assertRaisesRegex(ValueError, "cannot mix package versions"):
                docs.package_version(files)
            for file in files[1:]:
                with zipfile.ZipFile(file, "w") as package:
                    package.writestr("test.nuspec", "<package><metadata><id>Test</id>"
                                         "<version>0.1.0-preview.2</version></metadata></package>")
            self.assertEqual("", docs.package_version(files))
            with self.assertRaisesRegex(ValueError, "all nine"):
                docs.package_version(files[:8])

    def test_archive_round_trip_and_checksum(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            site = root / "site"
            (site / "api").mkdir(parents=True)
            (site / "index.html").write_text('<title>ValheimTesting API reference (preview)</title><h1>current</h1>'
                    'This reference is generated from the current package sources.'
                    '<a href="https://github.com/tvongaza/ValheimTesting/blob/main/docs/adopting.md">adopt</a>')
            (site / "api" / "GameActor.html").write_text('<a href="https://github.com/tvongaza/ValheimTesting/tree/main/examples/FullLifecycle">owned game</a>')
            (site / "versions").mkdir()
            (site / "versions" / "index.html").write_text("not nested")
            archive = root / "api-reference-0.1.0-rc.1.tgz"
            docs.make_archive(site, "0.1.0-rc.1", "a" * 40, archive)
            data = archive.read_bytes()
            destination = root / "published"
            docs.install_archive(data, hashlib.sha256(data).hexdigest(), "0.1.0-rc.1", destination)
            docs.write_index(destination)
            actor_page = (destination / "versions" / "0.1.0-rc.1" / "api" / "GameActor.html").read_text()
            self.assertIn("tree/" + "a" * 40 + "/examples/FullLifecycle", actor_page)
            release_home = (destination / "versions" / "0.1.0-rc.1" / "index.html").read_text()
            self.assertIn("Release <strong>0.1.0-rc.1</strong>", release_home)
            self.assertIn("ValheimTesting API reference (0.1.0-rc.1)", release_home)
            self.assertIn("generated from release 0.1.0-rc.1's package sources", release_home)
            self.assertIn("blob/" + "a" * 40 + "/docs/adopting.md", release_home)
            self.assertFalse((destination / "versions" / "0.1.0-rc.1" / "versions").exists())
            self.assertIn("0.1.0-rc.1/", (destination / "versions" / "index.html").read_text())
            with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                docs.install_archive(data, "0" * 64, "0.1.0", root / "bad")

    def test_rejects_path_escape_and_missing_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            payload = io.BytesIO()
            with tarfile.open(fileobj=payload, mode="w:gz") as archive:
                item = tarfile.TarInfo("../outside.txt")
                item.size = 3
                archive.addfile(item, io.BytesIO(b"bad"))
            data = payload.getvalue()
            with self.assertRaisesRegex(ValueError, "missing version metadata"):
                docs.install_archive(data, hashlib.sha256(data).hexdigest(), "0.1.0-rc.1", root / "site")
            self.assertFalse((root / "outside.txt").exists())

            payload = io.BytesIO()
            with tarfile.open(fileobj=payload, mode="w:gz") as archive:
                metadata = json.dumps({"version": "0.1.0-rc.1", "sourceCommit": "a" * 40}).encode()
                item = tarfile.TarInfo("version.json")
                item.size = len(metadata)
                archive.addfile(item, io.BytesIO(metadata))
                item = tarfile.TarInfo("../outside.txt")
                item.size = 3
                archive.addfile(item, io.BytesIO(b"bad"))
            data = payload.getvalue()
            with self.assertRaisesRegex(ValueError, "unsafe member"):
                docs.install_archive(data, hashlib.sha256(data).hexdigest(), "0.1.0-rc.1", root / "site")
            self.assertFalse((root / "outside.txt").exists())

    def test_manifest_and_symlinks_do_not_enter_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            site = root / "site"
            (site / "api").mkdir(parents=True)
            (site / "index.html").write_text("<h1>home</h1>")
            (site / "manifest.json").write_text("private path")
            with self.assertRaisesRegex(ValueError, "absolute-path manifest"):
                docs.make_archive(site, "0.1.0", "a" * 40, root / "reference.tgz")
            (site / "manifest.json").unlink()
            (site / "api" / "link.html").symlink_to(site / "index.html")
            with self.assertRaisesRegex(ValueError, "symlink"):
                docs.make_archive(site, "0.1.0", "a" * 40, root / "reference.tgz")

    def test_empty_release_listing_has_a_page(self):
        with tempfile.TemporaryDirectory() as directory:
            site = Path(directory) / "not-yet-created"
            docs.write_index(site)
            self.assertIn("No 0.1 release candidate", (site / "versions" / "index.html").read_text())

    def test_release_download_retries_server_errors_without_sending_token_to_assets(self):
        with (patch.object(docs.urllib.request, "urlopen", side_effect=[
                  HTTPError("https://example.test", 503, "busy", {}, None), io.BytesIO(b"asset")]) as opened,
              patch.object(docs.time, "sleep") as paused):
            self.assertEqual(b"asset", docs.get("https://example.test/asset"))
            self.assertEqual(2, opened.call_count)
            paused.assert_called_once_with(1)
            self.assertNotIn("Authorization", opened.call_args.args[0].headers)
        with patch.object(docs.urllib.request, "urlopen", side_effect=HTTPError(
                "https://example.test", 404, "missing", {}, None)) as opened:
            with self.assertRaises(HTTPError):
                docs.get("https://example.test/asset", "token")
            opened.assert_called_once()


if __name__ == "__main__":
    unittest.main()
