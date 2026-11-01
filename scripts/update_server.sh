#!/usr/bin/env bash
# 加密 payload 並上傳至伺服器
# 用法：./update_server.sh [core|auth|depot|version|all]

set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
NATIVE_BUILD="$ROOT/native/build"
SERVER_URL="${SERVER_URL:-https://cdn.ourdomain.com}"
SERVER_TOKEN="${SERVER_TOKEN:-}"

upload() {
    local name="$1"
    local src="$2"
    local enc="$SCRIPT_DIR/dist/${name}.enc"

    echo "=== 加密 $name ==="
    mkdir -p "$SCRIPT_DIR/dist"
    python3 "$SCRIPT_DIR/encrypt_payload.py" "$src" "$name" "$enc"

    echo "=== 上傳 $name ==="
    curl -sf -X PUT "$SERVER_URL/upload/$name" \
        -H "X-Admin-Token: $SERVER_TOKEN" \
        --data-binary "@$enc"
    echo " → 上傳完成"
}

update_manifest() {
    echo "=== 更新版本 manifest ==="
    python3 "$SCRIPT_DIR/gen_manifest.py"
    upload version "$SCRIPT_DIR/dist/version.json"
}

TARGET="${1:-all}"

case "$TARGET" in
    core)    upload core    "$NATIVE_BUILD/osu_core/OSU_Core.dll" ;;
    auth)    upload auth    "$NATIVE_BUILD/osu_auth/OSU_Auth.dll" ;;
    depot)
        # 從 ManifestHub 下載最新 depotkeys
        python3 - << 'PYEOF'
import urllib.request, json, pathlib
sources = [
    "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json",
    "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json",
]
merged = {}
for url in sources:
    try:
        with urllib.request.urlopen(url, timeout=30) as r:
            data = json.loads(r.read())
            for k, v in data.items():
                if k not in merged:
                    merged[k] = v
        print(f"[OK] {url}  ({len(merged)} keys)")
        break
    except Exception as e:
        print(f"[跳過] {url}: {e}")
out = pathlib.Path("scripts/dist/depotkeys.json")
out.parent.mkdir(exist_ok=True)
out.write_text(json.dumps(merged))
print(f"[完成] {len(merged)} depot keys")
PYEOF
        upload depot "$SCRIPT_DIR/dist/depotkeys.json"
        ;;
    version) update_manifest ;;
    all)
        upload core  "$NATIVE_BUILD/osu_core/OSU_Core.dll"
        upload auth  "$NATIVE_BUILD/osu_auth/OSU_Auth.dll"
        $0 depot
        update_manifest
        ;;
    *)
        echo "用法：$0 [core|auth|depot|version|all]"
        exit 1
        ;;
esac
