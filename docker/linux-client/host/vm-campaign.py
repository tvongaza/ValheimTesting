#!/usr/bin/env python3
"""Owned, bounded Vast VM campaign for a remote-container Valheim client.

The Vast command may be the official ``vastai`` executable or a credential-safe
wrapper. No credentials, Steam files, or game binaries are copied by this tool.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shlex
import signal
import subprocess
import sys
import time
import uuid


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def command(argv: list[str], *, timeout: int = 30, data: bytes | None = None,
            env: dict[str, str] | None = None) -> bytes:
    result = subprocess.run(argv, input=data, stdin=subprocess.DEVNULL if data is None else None,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=timeout, env=env, check=False)
    if result.returncode:
        # Provider and SSH failures can contain account details; keep their logs private.
        raise RuntimeError(f"{Path(argv[0]).name} exited {result.returncode}: " +
                           result.stderr.decode(errors="replace")[-500:])
    return result.stdout


def provider(binary: str, *args: str) -> object:
    return json.loads(command([binary, *args], timeout=45))


def snapshot(paths: list[Path], directory: Path) -> dict[str, str]:
    """Copy all inputs before renting; only bytes in this directory are executed."""
    directory.mkdir(mode=0o700, parents=True, exist_ok=False)
    hashes: dict[str, str] = {}
    for index, path in enumerate(paths):
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"campaign input must be a regular file: {path}")
        data = path.read_bytes()
        if not data:
            raise ValueError(f"campaign input is empty: {path}")
        name = f"{index}-{path.name}"
        target = directory / name
        target.write_bytes(data)
        target.chmod(0o400)
        hashes[name] = digest(data)
    (directory / "sha256.json").write_text(json.dumps(hashes, indent=2) + "\n")
    return hashes


def frozen(directory: Path, name: str, expected: str) -> bytes:
    data = (directory / name).read_bytes()
    if digest(data) != expected:
        raise RuntimeError(f"snapshotted input changed: {name}")
    return data


def select_offer(offers: object, gpu: str, max_price: float) -> str:
    if not isinstance(offers, list):
        raise ValueError("provider offer list was not a JSON array")
    matches = [o for o in offers if isinstance(o, dict)
               and o.get("gpu_name") == gpu
               and o.get("id") is not None
               and o.get("verified", True) is not False
               and o.get("rentable", True) is not False
               and o.get("vms_enabled", True) is not False
               and float(o.get("dph_total", o.get("dph", 999))) <= max_price
               and float(o.get("reliability", 0)) >= .98]
    if not matches:
        raise RuntimeError(f"no verified {gpu} offer within the hourly price cap")
    matches.sort(key=lambda o: (-float(o["reliability"]), float(o.get("dph_total", o.get("dph", 999)))))
    offer_id = str(matches[0]["id"])
    if not offer_id.isdecimal():
        raise ValueError("provider returned a nonnumeric offer id")
    return offer_id


def billed_price(state: object, max_price: float) -> float:
    """Refuse a rental whose actual price (including disk) exceeds the cap."""
    if not isinstance(state, dict):
        raise RuntimeError("rented VM did not report its billed price")
    try:
        price = float(state["dph_total"])
    except (KeyError, TypeError, ValueError) as error:
        raise RuntimeError("rented VM did not report its billed price") from error
    if not math.isfinite(price) or price < 0:
        raise RuntimeError("rented VM reported an invalid billed price")
    if price > max_price:
        raise RuntimeError(f"rented VM bills ${price:.3f}/hour above the ${max_price:.3f}/hour cap")
    return price


def endpoint(state: object) -> tuple[str, int] | None:
    if not isinstance(state, dict) or state.get("actual_status") != "running":
        return None
    host = state.get("public_ipaddr") or state.get("ssh_host")
    ports = state.get("ports") or {}
    mapped = ports.get("22/tcp") if isinstance(ports, dict) else None
    port = mapped[0].get("HostPort") if isinstance(mapped, list) and mapped else state.get("ssh_port")
    if not isinstance(host, str) or not re.fullmatch(r"[A-Za-z0-9.:-]+", host):
        raise ValueError("provider returned an invalid SSH host")
    if not str(port).isdecimal() or not 1 <= int(port) <= 65535:
        raise ValueError("provider returned an invalid SSH port")
    return host, int(port)


def wait_for_endpoint(binary: str, instance_id: str, seconds: int) -> tuple[str, int]:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        state = provider(binary, "show", "instance", instance_id, "--raw")
        found = endpoint(state)
        if found:
            return found
        if isinstance(state, dict) and state.get("actual_status") in ("exited", "offline"):
            raise RuntimeError("rented VM exited before SSH became ready")
        time.sleep(15)  # Vast has no state event stream.
    raise TimeoutError("rented VM did not expose an SSH endpoint before deadline")


def stable_host_key(host: str, port: int, seconds: int) -> bytes:
    deadline = time.monotonic() + seconds
    previous = b""
    while time.monotonic() < deadline:
        scan = subprocess.run(["ssh-keyscan", "-T", "5", "-t", "ed25519", "-p", str(port), host],
                              capture_output=True, timeout=8, check=False)
        lines = sorted(set(line for line in scan.stdout.splitlines() if b" ssh-ed25519 " in line))
        current = b"\n".join(lines) + (b"\n" if lines else b"")
        if current and current == previous:
            return current
        previous = current
        time.sleep(5)
    raise TimeoutError("SSH host key did not produce two matching nonempty scans")


def wait_for_docker(ssh: list[str], seconds: int) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            command([*ssh, "docker info >/dev/null 2>&1"], timeout=12)
            return
        except (RuntimeError, subprocess.TimeoutExpired):
            time.sleep(5)
    raise TimeoutError("VM Docker daemon was not ready before deadline")


def confirm_destroyed(binary: str, instance_id: str, seconds: int = 90) -> bool:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        instances = provider(binary, "show", "instances", "--raw")
        if not isinstance(instances, list):
            raise ValueError("provider instance census was not an array")
        if not any(str(i.get("id")) == instance_id for i in instances if isinstance(i, dict)):
            return True
        time.sleep(5)
    return False


def run(args: argparse.Namespace) -> None:
    if not math.isfinite(args.max_price) or args.max_price <= 0 or args.max_price > 10:
        raise ValueError("--max-price must be greater than zero and at most 10 USD/hour")
    if not re.fullmatch(r"[A-Za-z0-9_. -]+", args.gpu):
        raise ValueError("--gpu must be an exact GPU name")
    if not re.fullmatch(r"[A-Za-z0-9_./:@-]+", args.image):
        raise ValueError("--image must be a container image reference")
    if not args.ssh_key.is_file():
        raise ValueError("SSH identity file is missing")
    evidence = args.evidence.resolve()
    if evidence.exists():
        raise FileExistsError("evidence directory already exists; choose a new run")
    evidence.mkdir(mode=0o700, parents=True)
    source = evidence / "snapshot"
    hashes = snapshot([Path(__file__).resolve(), args.bootstrap.resolve(), args.run_script.resolve(),
                       *(asset.resolve() for asset in args.asset)], source)
    # A failed snapshot or invalid setup cannot rent a VM.
    command([args.vast, "--help"], timeout=10)
    offer_query = ("vms_enabled=true verified=true rentable=true num_gpus=1 "
                   f"dph<={args.max_price} inet_down>=500 reliability>=0.98 "
                   "disk_space>=60 cpu_ram>=16")
    offer_id = select_offer(provider(args.vast, "search", "offers", offer_query, "--raw"),
                            args.gpu, args.max_price)
    # Check the exact frozen bootstrap before a paid resource exists.
    bootstrap = frozen(source, f"1-{args.bootstrap.name}", hashes[f"1-{args.bootstrap.name}"])
    if b"VT_HOST_NETWORK" not in bootstrap:
        raise ValueError("bootstrap does not support host-network containers")
    test = frozen(source, f"2-{args.run_script.name}", hashes[f"2-{args.run_script.name}"])
    instance_id: str | None = None
    label = "vt-campaign-" + uuid.uuid4().hex[:12]
    started = time.monotonic()
    result: dict[str, object] = {"gpu": args.gpu, "maxHourlyUsd": args.max_price,
                                 "image": args.image, "label": label,
                                 "phases": [], "destroyed": False}
    def phase(name: str) -> None:
        result["phases"].append(name)
        print(f"vm-campaign: {name} (+{round(time.monotonic() - started)}s)", flush=True)

    try:
        created = provider(args.vast, "create", "instance", offer_id, "--image", args.vm_image,
                           "--disk", "60", "--ssh", "--direct", "--label", label, "--raw")
        instance_id = str(created.get("new_contract", "")) if isinstance(created, dict) else ""
        if not instance_id.isdecimal():
            instance_id = None
            raise RuntimeError("provider did not return a numeric instance id")
        result["instanceId"] = instance_id
        phase("rented")
        result["billedHourlyUsd"] = billed_price(provider(args.vast, "show", "instance", instance_id, "--raw"),
                                                  args.max_price)
        host, port = wait_for_endpoint(args.vast, instance_id, args.vm_timeout)
        key = stable_host_key(host, port, args.key_timeout)
        (evidence / "known_hosts").write_bytes(key)
        config = evidence / "ssh_config"
        config.write_text("Host vt-campaign\n"
                          f"    HostName {host}\n    Port {port}\n    User root\n"
                          f"    IdentityFile {args.ssh_key.resolve()}\n    IdentitiesOnly yes\n"
                          f"    UserKnownHostsFile {evidence / 'known_hosts'}\n"
                          "    StrictHostKeyChecking yes\n    BatchMode yes\n"
                          "    ForwardAgent no\n    LogLevel ERROR\n")
        ssh = ["ssh", "-F", str(config), "vt-campaign"]
        # Docker's ssh:// transport and the runner's tunnel launch `ssh` themselves.
        # Give both the exact pinned alias without editing the operator's SSH config.
        ssh_bin = evidence / "ssh-bin"
        ssh_bin.mkdir(mode=0o700)
        ssh_wrapper = ssh_bin / "ssh"
        ssh_wrapper.write_text("#!/bin/sh\nexec /usr/bin/ssh -F " + shlex.quote(str(config)) + " \"$@\"\n")
        ssh_wrapper.chmod(0o700)
        env = os.environ.copy()
        env.update(VT_VM_SSH_CONFIG=str(config), VT_VM_ALIAS="vt-campaign",
                   VT_VM_INSTANCE_ID=instance_id, VT_VM_EVIDENCE=str(evidence),
                   VT_VM_CONTAINER="vt", VT_VM_SNAPSHOT_DIR=str(source),
                   PATH=str(ssh_bin) + os.pathsep + env.get("PATH", ""))
        wait_for_docker(ssh, args.docker_timeout)
        phase("pinned-ssh-and-docker")
        # A new controller CLI can outrun a rented host's older Docker Engine.
        # Pin the daemon's own API version only for this campaign, before any image pull.
        remote_api = command([*ssh, "docker version --format '{{.Server.APIVersion}}'"], timeout=15).decode().strip()
        if not re.fullmatch(r"1\.[0-9]+", remote_api):
            raise RuntimeError("VM Docker daemon did not report a usable API version")
        env["DOCKER_API_VERSION"] = remote_api
        try:
            local_api = command(["docker", "--host", "ssh://vt-campaign", "version",
                                 "--format", "{{.Server.APIVersion}}"], env=env, timeout=20).decode().strip()
        except RuntimeError as error:
            raise RuntimeError(f"controller Docker CLI cannot use the VM's API {remote_api}: {error}") from error
        if local_api != remote_api:
            raise RuntimeError("controller Docker CLI reported a different remote API version")
        result["dockerServerApi"] = remote_api
        phase("docker-api-compatible")
        remote_bootstrap = f"/root/{label}-bootstrap.sh"
        command([*ssh, f"cat > {remote_bootstrap}"], data=bootstrap, timeout=30)
        remote_hash = command([*ssh, f"sha256sum {remote_bootstrap}"], timeout=15)
        if remote_hash.split(maxsplit=1)[0].decode() != digest(bootstrap):
            raise RuntimeError("VM bootstrap hash differs from the pre-rental snapshot")
        command([*ssh, f"VT_GPU_READY_SECONDS=60 bash {remote_bootstrap} --preflight-gpu </dev/null"],
                timeout=90)
        phase("gpu-ready")
        command([*ssh, f"VT_HOST_NETWORK=1 bash {remote_bootstrap} {shlex.quote(args.image)} </dev/null"],
                timeout=args.bootstrap_timeout)
        mode = command([*ssh, "docker inspect -f '{{.HostConfig.NetworkMode}}' vt"], timeout=15)
        if mode.strip() != b"host":
            raise RuntimeError("client container is not in host-network mode")
        phase("container-ready")
        with (evidence / "test.stdout").open("wb") as out, (evidence / "test.stderr").open("wb") as err:
            completed = subprocess.run(["bash", str(source / f"2-{args.run_script.name}")],
                                       stdin=subprocess.DEVNULL, cwd=evidence,
                                       env=env, stdout=out, stderr=err,
                                       timeout=args.run_timeout, check=False)
        if completed.returncode:
            raise RuntimeError(f"campaign test exited {completed.returncode}")
        if frozen(source, f"2-{args.run_script.name}", hashes[f"2-{args.run_script.name}"]) != test:
            raise RuntimeError("campaign test snapshot changed while running")
        for name, expected in hashes.items():
            frozen(source, name, expected)
        marker = evidence / "check-result.json"
        if not marker.is_file() or json.loads(marker.read_text()).get("status") != "passed":
            raise RuntimeError("campaign test ended without an explicit passing check-result.json")
        phase("test-passed")
    except BaseException as error:
        result["failure"] = str(error)
        raise
    finally:
        if not instance_id:
            try:
                census = provider(args.vast, "show", "instances", "--raw")
                owned = [str(i.get("id")) for i in census
                         if isinstance(i, dict) and i.get("label") == label] if isinstance(census, list) else []
                if len(owned) == 1 and owned[0].isdecimal():
                    instance_id = owned[0]
                    result["instanceId"] = instance_id
                elif owned:
                    result["censusError"] = "ambiguous instances with campaign label"
            except Exception as error:
                result["censusError"] = str(error)
        if instance_id:
            try:
                command([args.vast, "destroy", "instance", instance_id, "-y"], timeout=60)
            except Exception as error:
                result["destroyError"] = str(error)
            try:
                result["destroyed"] = confirm_destroyed(args.vast, instance_id)
            except Exception as error:
                result["censusError"] = str(error)
        result["elapsedSeconds"] = round(time.monotonic() - started, 1)
        (evidence / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        if instance_id and not result["destroyed"]:
            raise RuntimeError("VM teardown is unproven; inspect the provider account immediately")
        if result.get("censusError"):
            raise RuntimeError("VM census is unproven; inspect the provider account immediately")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vast", required=True, help="Vast CLI or credential-safe wrapper")
    parser.add_argument("--gpu", required=True, help="exact Vast GPU name, for example RTX 2060S")
    parser.add_argument("--max-price", required=True, type=float, help="USD per hour ceiling")
    parser.add_argument("--ssh-key", required=True, type=Path)
    parser.add_argument("--bootstrap", type=Path, default=Path(__file__).with_name("vm-bootstrap.sh"))
    parser.add_argument("--run-script", required=True, type=Path, help="local test body, snapshotted before rent")
    parser.add_argument("--asset", action="append", default=[], type=Path,
                        help="additional file to snapshot before renting; may be repeated")
    parser.add_argument("--evidence", required=True, type=Path, help="new private evidence directory")
    parser.add_argument("--image", required=True, help="identified client image (prefer an immutable digest)")
    parser.add_argument("--vm-image", default="docker.io/vastai/kvm:ubuntu_terminal")
    parser.add_argument("--vm-timeout", type=int, default=480)
    parser.add_argument("--key-timeout", type=int, default=120)
    parser.add_argument("--docker-timeout", type=int, default=300)
    parser.add_argument("--bootstrap-timeout", type=int, default=600)
    parser.add_argument("--run-timeout", type=int, default=1800)
    args = parser.parse_args()
    try:
        run(args)
        return 0
    except (ValueError, RuntimeError, TimeoutError, FileExistsError, subprocess.TimeoutExpired) as error:
        print(f"vm-campaign: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    def interrupted(_signal: int, _frame: object) -> None:
        raise RuntimeError("campaign interrupted; tearing down owned VM")

    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    sys.exit(main())
