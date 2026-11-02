#!/usr/bin/env python3
"""
自動更新 depotkeys.enc 並同步 version.enc

從多個公開來源下載最新 depot key，合併後加密上傳至 CDN，
再自動重算 version manifest 並上傳 version.enc。

用法：
    python3 update_depotkeys.py            # 更新並上傳
    python3 update_depotkeys.py --dry-run  # 只下載合併，不上傳
    python3 update_depotkeys.py --check    # 只顯示目前各來源筆數

排程（crontab，每天凌晨 3 點）：
    0 3 * * * cd /path/to/steam-ck/scripts && python3 update_depotkeys.py >> /var/log/depotkeys.log 2>&1
"""

import sys, json, zlib, struct, secrets, hashlib, time, urllib.request, importlib.util
from pathlib import Path

SCRIPT_DIR = Path(__file__).parent
sys.path.insert(0, str(SCRIPT_DIR))

try:
    from secrets_config import (
        KEY_DEPOT, KEY_VERSION,
        CDN_UPLOAD_URL, CDN_UPLOAD_TOKEN,
        CDN_VERSION_UPLOAD_URL, CDN_VERSION_UPLOAD_TOKEN,
    )
except ImportError as e:
    print(f"[錯誤] secrets_config.py 缺少欄位：{e}")
    print("  需要：KEY_DEPOT, KEY_VERSION, CDN_UPLOAD_URL, CDN_UPLOAD_TOKEN,")
    print("        CDN_VERSION_UPLOAD_URL, CDN_VERSION_UPLOAD_TOKEN")
    sys.exit(1)

try:
    from Crypto.Cipher import AES
except ImportError:
    print("[錯誤] pip install pycryptodome")
    sys.exit(1)

SOURCES = [
    "https://api.993499094.xyz/depotkeys.json",
    "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json",
    "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json",
]

ROOT        = SCRIPT_DIR.parent
DIST_DIR    = SCRIPT_DIR / "dist"
CACHE_JSON  = DIST_DIR / "depotkeys.json"   # 供 gen_manifest.py 比較快取用
DEPOT_ENC   = DIST_DIR / "depot.enc"        # gen_manifest.py 期望的路徑
VERSION_JSON = DIST_DIR / "version.json"
VERSION_ENC  = DIST_DIR / "version.enc"
DIST_DIR.mkdir(parents=True, exist_ok=True)


# ── 加密 ──────────────────────────────────────────────────────────────────────

def encrypt(data: bytes, key: bytes) -> bytes:
    compressed = zlib.compress(data, level=9)
    orig_len   = struct.pack("<I", len(data))
    payload    = orig_len + compressed
    pad_len    = 16 - (len(payload) % 16)
    payload   += bytes([pad_len] * pad_len)
    iv = secrets.token_bytes(16)
    return iv + AES.new(key, AES.MODE_CBC, iv).encrypt(payload)


# ── HTTP ──────────────────────────────────────────────────────────────────────

def fetch_json(url: str, timeout: int = 60) -> dict | None:
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "steam-ck/1.0"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            data = json.loads(r.read().decode())
        return data if isinstance(data, dict) and len(data) > 1000 else None
    except Exception as e:
        print(f"  [fetch 失敗] {url}: {e}")
    return None


def upload(path: Path, url: str, token: str, label: str) -> bool:
    data = path.read_bytes()
    try:
        req = urllib.request.Request(
            url, data=data, method="PUT",
            headers={
                "Content-Type":  "application/octet-stream",
                "Authorization": f"Bearer {token}",
                "X-File-MD5":    hashlib.md5(data).hexdigest(),
                "X-File-Name":   path.name,
            },
        )
        with urllib.request.urlopen(req, timeout=60) as r:
            if r.status in (200, 201, 204):
                return True
            print(f"  [{label} 上傳失敗] HTTP {r.status}")
    except Exception as e:
        print(f"  [{label} 上傳失敗] {e}")
    return False


# ── Manifest ──────────────────────────────────────────────────────────────────

def rebuild_version_manifest() -> bool:
    """
    呼叫 gen_manifest.py 重算各 .enc 的 MD5，
    輸出 dist/version.json，再加密為 dist/version.enc。
    回傳是否成功。
    """
    # 動態載入 gen_manifest 而非 subprocess，減少依賴
    spec = importlib.util.spec_from_file_location("gen_manifest", SCRIPT_DIR / "gen_manifest.py")
    gm   = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(gm)
        gm.main()
    except Exception as e:
        print(f"  [gen_manifest 失敗] {e}")
        return False

    if not VERSION_JSON.exists():
        print("  [錯誤] gen_manifest 未產生 version.json")
        return False

    enc = encrypt(VERSION_JSON.read_bytes(), bytes.fromhex(KEY_VERSION))
    VERSION_ENC.write_bytes(enc)
    md5 = hashlib.md5(enc).hexdigest()
    print(f"  version.enc：{len(enc):,} bytes，MD5={md5}")
    return True


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    dry_run = "--dry-run" in sys.argv
    check   = "--check"   in sys.argv

    print(f"\n[{time.strftime('%Y-%m-%d %H:%M:%S')}] 開始更新 depotkeys")

    # 1. 下載並合併
    merged: dict = {}
    for url in SOURCES:
        print(f"  下載 {url} ...")
        data = fetch_json(url)
        if data:
            before = len(merged)
            for k, v in data.items():
                if k not in merged:
                    merged[k] = v
            print(f"    → {len(data):,} keys，新增 {len(merged)-before:,} 筆")

    print(f"  合併後共 {len(merged):,} depot keys")

    if check:
        return

    # 2. 比較快取，無新 key 則跳過
    if CACHE_JSON.exists():
        old  = json.loads(CACHE_JSON.read_text())
        diff = len(merged) - len(old)
        if diff == 0:
            print("  → 無新增 key，跳過更新")
            return
        print(f"  → 新增 {diff:,} 個 key（{len(old):,} → {len(merged):,}）")
    else:
        print("  → 首次建立")

    # 3. 寫快取 JSON
    raw = json.dumps(merged, separators=(',', ':')).encode()
    CACHE_JSON.write_bytes(raw)

    # 4. 加密 depot.enc
    depot_enc = encrypt(raw, bytes.fromhex(KEY_DEPOT))
    DEPOT_ENC.write_bytes(depot_enc)
    depot_md5 = hashlib.md5(depot_enc).hexdigest()
    print(f"  depot.enc：{len(depot_enc):,} bytes，MD5={depot_md5}")

    if dry_run:
        print("  [dry-run] 跳過上傳")
        return

    # 5. 上傳 depot.enc
    print(f"  上傳 depot.enc → {CDN_UPLOAD_URL} ...")
    depot_ok = upload(DEPOT_ENC, CDN_UPLOAD_URL, CDN_UPLOAD_TOKEN, "depot")
    print(f"  {'✓' if depot_ok else '✗'} depot.enc 上傳{'成功' if depot_ok else '失敗'}")

    # 6. 重建 version manifest（含新 depot MD5）並上傳 version.enc
    print("  重建 version manifest ...")
    ver_ok = rebuild_version_manifest()
    if ver_ok:
        print(f"  上傳 version.enc → {CDN_VERSION_UPLOAD_URL} ...")
        ver_upload_ok = upload(VERSION_ENC, CDN_VERSION_UPLOAD_URL, CDN_VERSION_UPLOAD_TOKEN, "version")
        print(f"  {'✓' if ver_upload_ok else '✗'} version.enc 上傳{'成功' if ver_upload_ok else '失敗'}")
    else:
        print("  ✗ version manifest 重建失敗，請手動執行 update_server.sh version")

    print(f"[完成] {time.strftime('%Y-%m-%d %H:%M:%S')}")


if __name__ == "__main__":
    main()
