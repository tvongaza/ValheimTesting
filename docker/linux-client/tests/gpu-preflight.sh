#!/usr/bin/env bash
# Exercise the host's GPU readiness check without Docker, root or a game.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin"
cat > "$tmp/bin/nvidia-smi" <<'EOF'
#!/usr/bin/env bash
case "$VT_TEST_GPU_STATE" in
  ready) echo 'GPU 0: Test NVIDIA GPU' ;;
  unavailable) echo 'NVML driver not loaded' >&2; exit 1 ;;
  late)
    count=$(cat "$VT_TEST_GPU_COUNT" 2>/dev/null || echo 0)
    echo $((count + 1)) > "$VT_TEST_GPU_COUNT"
    if (( count == 0 )); then echo 'NVML driver still starting' >&2; exit 1; fi
    echo 'GPU 0: Test NVIDIA GPU' ;;
esac
EOF
chmod +x "$tmp/bin/nvidia-smi"
cat > "$tmp/bin/nvidia-container-cli" <<'EOF'
#!/usr/bin/env bash
test "$1" = info || exit 2
if [[ ${VT_TEST_RUNTIME_STATE:-ready} == unavailable ]]; then
  echo 'runtime driver unavailable' >&2
  exit 1
fi
echo 'test runtime ready'
EOF
chmod +x "$tmp/bin/nvidia-container-cli"
export PATH="$tmp/bin:$PATH"

VT_TEST_GPU_STATE=ready VT_GPU_READY_SECONDS=0 bash "$root/host/vm-bootstrap.sh" --preflight-gpu > "$tmp/ready.log"
grep -q 'NVIDIA GPU ready' "$tmp/ready.log"
if VT_TEST_GPU_STATE=unavailable VT_GPU_READY_SECONDS=0 bash "$root/host/vm-bootstrap.sh" --preflight-gpu > "$tmp/unavailable.log" 2>&1; then
    echo 'unavailable driver was accepted' >&2; exit 1
fi
grep -q 'NVML driver not loaded' "$tmp/unavailable.log"
grep -q 'bootstrap stopped before Docker' "$tmp/unavailable.log"

export VT_TEST_GPU_COUNT="$tmp/count"
VT_TEST_GPU_STATE=late VT_GPU_READY_SECONDS=5 bash "$root/host/vm-bootstrap.sh" --preflight-gpu > "$tmp/late.log"
test "$(cat "$tmp/count")" = 2
grep -q 'NVIDIA GPU ready' "$tmp/late.log"

if VT_TEST_GPU_STATE=ready VT_GPU_READY_SECONDS=invalid bash "$root/host/vm-bootstrap.sh" --preflight-gpu > "$tmp/invalid.log" 2>&1; then
    echo 'invalid readiness budget was accepted' >&2; exit 1
fi
grep -q 'VT_GPU_READY_SECONDS must be 0 to 300' "$tmp/invalid.log"
VT_TEST_RUNTIME_STATE=ready bash "$root/host/vm-bootstrap.sh" --preflight-runtime > "$tmp/runtime-ready.log"
grep -q 'NVIDIA container runtime ready' "$tmp/runtime-ready.log"
if VT_TEST_RUNTIME_STATE=unavailable bash "$root/host/vm-bootstrap.sh" --preflight-runtime > "$tmp/runtime-unavailable.log" 2>&1; then
    echo 'unavailable runtime was accepted' >&2; exit 1
fi
grep -q 'runtime driver unavailable' "$tmp/runtime-unavailable.log"
grep -q 'stopped before pulling the client image' "$tmp/runtime-unavailable.log"
echo 'GPU readiness checks pass'
