#!/usr/bin/env python3
"""
自動更新 depotkeys.enc

從多個公開來源下載最新 depot key，合併後加密上傳至 CDN。

用法：
    python3 update_depotkeys.py            # 更新並上傳
    python3 update_depotkeys.py --dry-run  # 只下載合併，不上傳
    python3 update_depotkeys.py --check    # 只顯示目前各來源筆數

排程（crontab，每天凌晨 3 點）：
    0 3 * * * cd /path/to/steam-ck/scripts && python3 update_depotkeys.py >> /var/log/depotkeys.log 2>&1
"""

import sys, os, json, zlib, struct, secrets, hashlib, time, urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
try:
    from secrets_config import KEY_DEPOT, CDN_UPLOAD_URL, CDN_UPLOAD_TOKEN
except ImportError:
    print("[錯誤] 找不到 secrets_config.py 或缺少 KEY_DEPOT / CDN_UPLOAD_URL / CDN_UPLOAD_TOKEN")
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

CACHE_DIR  = Path(__file__).parent.parent / "server" / ".cache"
OUTPUT_ENC = CACHE_DIR / "depotkeys.enc"
OUTPUT_JSON = CACHE_DIR / "depotkeys.json"
CACHE_DIR.mkdir(parents=True, exist_ok=True)


def fetch(url: str, timeout: int = 60) -> dict | None:
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "steam-ck/1.0"})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            body = r.read().decode()
        data = json.loads(body)
        if isinstance(data, dict) and len(data) > 1000:
            return data
    except Exception as e:
        print(f"  [fetch 失敗] {url}: {e}")
    return None


def encrypt(data: bytes, key: bytes) -> bytes:
    compressed = zlib.compress(data, level=9)
    orig_len   = struct.pack("<I", len(data))
    payload    = orig_len + compressed
    pad_len    = 16 - (len(payload) % 16)
    payload   += bytes([pad_len] * pad_len)
    iv = secrets.token_bytes(16)
    return iv + AES.new(key, AES.MODE_CBC, iv).encrypt(payload)


def upload(path: Path, url: str, token: str) -> bool:
    data = path.read_bytes()
    md5  = hashlib.md5(data).hexdigest()
    try:
        req = urllib.request.Request(
            url,
            data=data,
            method="PUT",
            headers={
                "Content-Type":   "application/octet-stream",
                "Authorization":  f"Bearer {token}",
                "X-File-MD5":     md5,
                "X-File-Name":    "depotkeys.enc",
            }
        )
        with urllib.request.urlopen(req, timeout=60) as r:
            status = r.status
        if status in (200, 201, 204):
            return True
        print(f"  [上傳失敗] HTTP {status}")
    except Exception as e:
        print(f"  [上傳失敗] {e}")
    return False


def main():
    dry_run = "--dry-run" in sys.argv
    check   = "--check"   in sys.argv

    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    print(f"\n[{ts}] 開始更新 depotkeys")

    # 下載並合併
    merged = {}
    for url in SOURCES:
        print(f"  下載 {url} ...")
        data = fetch(url)
        if data:
            before = len(merged)
            for k, v in data.items():
                if k not in merged:
                    merged[k] = v
            print(f"    → {len(data):,} keys，新增 {len(merged)-before:,} 筆")
        if check:
            continue

    print(f"  合併後共 {len(merged):,} depot keys")

    if check:
        return

    # 比較現有快取
    if OUTPUT_JSON.exists():
        old = json.loads(OUTPUT_JSON.read_text())
        diff = len(merged) - len(old)
        if diff == 0:
            print("  → 無新增 key，跳過更新")
            return
        print(f"  → 新增 {diff:,} 個 key（{len(old):,} → {len(merged):,}）")
    else:
        print(f"  → 首次建立")

    # 寫入 JSON 快取
    OUTPUT_JSON.write_text(json.dumps(merged, separators=(',', ':')))

    # 加密
    key_bytes = bytes.fromhex(KEY_DEPOT)
    raw       = OUTPUT_JSON.read_bytes()
    enc       = encrypt(raw, key_bytes)
    OUTPUT_ENC.write_bytes(enc)
    md5 = hashlib.md5(enc).hexdigest()
    print(f"  加密完成：{len(enc):,} bytes，MD5={md5}")

    if dry_run:
        print("  [dry-run] 跳過上傳")
        print(f"  輸出：{OUTPUT_ENC}")
        return

    # 上傳
    print(f"  上傳至 {CDN_UPLOAD_URL} ...")
    ok = upload(OUTPUT_ENC, CDN_UPLOAD_URL, CDN_UPLOAD_TOKEN)
    if ok:
        print(f"  ✓ 上傳成功")
    else:
        print(f"  ✗ 上傳失敗，本地檔案仍更新")

    print(f"[完成] {time.strftime('%Y-%m-%d %H:%M:%S')}")


if __name__ == "__main__":
    main()
