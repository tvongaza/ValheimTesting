"""Game-free controls for the rented VM campaign. Run with python3 -m unittest discover."""

import argparse
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "host" / "vm-campaign.py"
spec = importlib.util.spec_from_file_location("vm_campaign", SCRIPT)
campaign = importlib.util.module_from_spec(spec)
spec.loader.exec_module(campaign)


class CampaignTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.bootstrap = root / "bootstrap.sh"
        self.bootstrap.write_text("#!/bin/bash\n# VT_HOST_NETWORK\n")
        self.test = root / "test.sh"
        self.test.write_text("#!/bin/bash\ncat >/dev/null\nprintf '{\"status\":\"passed\"}' > \"$VT_VM_EVIDENCE/check-result.json\"\n")
        self.key = root / "identity"
        self.key.write_text("fake private key\n")
        self.evidence = root / "evidence"
        self.args = argparse.Namespace(max_price=.10, gpu="RTX 2060S", ssh_key=self.key,
                                       evidence=self.evidence, bootstrap=self.bootstrap,
                                       run_script=self.test, asset=[], vast="fake-vast", image="test/image:1",
                                       vm_image="test/vm:1", vm_timeout=1, key_timeout=1,
                                       docker_timeout=1, bootstrap_timeout=1, run_timeout=1)
        self.commands = []
        self.states = []

    def provider(self, _binary, *args):
        self.states.append(args)
        if args[:2] == ("search", "offers"):
            return [{"id": 4, "gpu_name": "RTX 2060S", "dph": .07, "reliability": .99},
                    {"id": 5, "gpu_name": "RTX 2060S", "dph": .11, "reliability": .999}]
        if args[:2] == ("create", "instance"):
            return {"new_contract": 42}
        if args[:2] == ("show", "instances"):
            return []
        raise AssertionError(args)

    def command(self, argv, **_kwargs):
        self.commands.append(argv)
        if argv[-1] == "docker version --format '{{.Server.APIVersion}}'":
            return b"1.43\n"
        if argv[0] == "docker" and "version" in argv:
            return b"1.43\n"
        if argv[-1].startswith("sha256sum "):
            return (campaign.digest(self.bootstrap.read_bytes()) + "  remote-bootstrap\n").encode()
        if argv[-1] == "docker inspect -f '{{.HostConfig.NetworkMode}}' vt":
            return b"host\n"
        return b""

    def patches(self):
        stack = self.enterContext(mock.patch.object(campaign, "provider", side_effect=self.provider))
        self.enterContext(mock.patch.object(campaign, "command", side_effect=self.command))
        self.enterContext(mock.patch.object(campaign, "wait_for_endpoint", return_value=("127.0.0.1", 2222)))
        self.enterContext(mock.patch.object(campaign, "stable_host_key", return_value=b"[127.0.0.1]:2222 ssh-ed25519 AAAA\n"))
        self.enterContext(mock.patch.object(campaign, "wait_for_docker"))
        return stack

    def test_offer_selection_is_exact_and_capped(self):
        offers = self.provider("fake", "search", "offers")
        self.assertEqual("4", campaign.select_offer(offers, "RTX 2060S", .10))
        with self.assertRaisesRegex(RuntimeError, "no verified"):
            campaign.select_offer(offers, "RTX 3070", .10)
        with self.assertRaisesRegex(RuntimeError, "no verified"):
            campaign.select_offer(offers, "RTX 2060S", .05)

    def test_modified_snapshot_is_refused(self):
        directory = Path(self.temp.name) / "snap"
        original = self.test.read_bytes()
        hashes = campaign.snapshot([self.test], directory)
        self.test.write_text("changed source")
        self.assertEqual(original, campaign.frozen(directory, "0-test.sh", hashes["0-test.sh"]))
        (directory / "0-test.sh").chmod(0o600)
        (directory / "0-test.sh").write_text("changed snapshot")
        with self.assertRaisesRegex(RuntimeError, "snapshotted input changed"):
            campaign.frozen(directory, "0-test.sh", hashes["0-test.sh"])

    def test_wrong_bootstrap_refuses_before_renting(self):
        self.bootstrap.write_text("#!/bin/bash\nexit 0\n")
        self.patches()
        with self.assertRaisesRegex(ValueError, "host-network"):
            campaign.run(self.args)
        self.assertFalse(any(args[:2] == ("create", "instance") for args in self.states))

    def test_unready_ssh_destroys_the_owned_vm(self):
        self.patches()
        with mock.patch.object(campaign, "wait_for_endpoint", side_effect=TimeoutError("SSH unavailable")):
            with self.assertRaisesRegex(TimeoutError, "SSH unavailable"):
                campaign.run(self.args)
        self.assertTrue(any(args[:3] == ["fake-vast", "destroy", "instance"] for args in self.commands))
        self.assertTrue(json.loads((self.evidence / "result.json").read_text())["destroyed"])

    def test_driver_refusal_destroys_before_bootstrap(self):
        self.patches()
        old = self.command

        def fail_driver(argv, **kwargs):
            if "--preflight-gpu" in argv[-1]:
                raise RuntimeError("GPU driver unavailable")
            return old(argv, **kwargs)

        with mock.patch.object(campaign, "command", side_effect=fail_driver):
            with self.assertRaisesRegex(RuntimeError, "GPU driver unavailable"):
                campaign.run(self.args)
        phases = json.loads((self.evidence / "result.json").read_text())["phases"]
        self.assertNotIn("container-ready", phases)
        self.assertTrue(any(args[:3] == ["fake-vast", "destroy", "instance"] for args in self.commands))

    def test_incompatible_controller_docker_refuses_before_image_pull(self):
        self.patches()
        old = self.command

        def incompatible(argv, **kwargs):
            if argv[0] == "docker" and "version" in argv:
                raise RuntimeError("client API is too new")
            return old(argv, **kwargs)

        with mock.patch.object(campaign, "command", side_effect=incompatible):
            with self.assertRaisesRegex(RuntimeError, "cannot use the VM's API 1.43"):
                campaign.run(self.args)
        phases = json.loads((self.evidence / "result.json").read_text())["phases"]
        self.assertNotIn("container-ready", phases)
        self.assertTrue(json.loads((self.evidence / "result.json").read_text())["destroyed"])

    def test_success_executes_only_snapshotted_test_and_confirms_destroy(self):
        self.test_original = self.test.read_bytes()
        asset = Path(self.temp.name) / "fixture.tar.gz"
        asset.write_bytes(b"pinned fixture")
        self.args.asset = [asset]
        self.patches()
        campaign.run(self.args)
        report = json.loads((self.evidence / "result.json").read_text())
        self.assertEqual("test-passed", report["phases"][-1])
        self.assertTrue(report["destroyed"])
        self.assertEqual("1.43", report["dockerServerApi"])
        self.assertEqual(self.test_original, (self.evidence / "snapshot" / "2-test.sh").read_bytes())
        self.assertEqual(b"pinned fixture", (self.evidence / "snapshot" / "3-fixture.tar.gz").read_bytes())
        wrapper = (self.evidence / "ssh-bin" / "ssh").read_text()
        self.assertIn("-F", wrapper)
        self.assertIn(str(self.evidence / "ssh_config"), wrapper)

    def test_unproven_teardown_fails_even_after_a_passing_test(self):
        self.patches()
        with mock.patch.object(campaign, "confirm_destroyed", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "teardown is unproven"):
                campaign.run(self.args)
        self.assertFalse(json.loads((self.evidence / "result.json").read_text())["destroyed"])

    def test_zero_exit_without_assertion_artifact_is_not_a_pass(self):
        self.test.write_text("#!/bin/bash\nexit 0\n")
        self.patches()
        with self.assertRaisesRegex(RuntimeError, "explicit passing check-result"):
            campaign.run(self.args)
        self.assertTrue(json.loads((self.evidence / "result.json").read_text())["destroyed"])


if __name__ == "__main__":
    unittest.main()
