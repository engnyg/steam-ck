#!/usr/bin/env python3
"""
AES-256-CBC + zlib 加密工具（與 SteamTools 相同格式）

格式：[ IV 16B ][ uint32_LE 解壓後長度 ][ zlib 資料 ][ PKCS7 padding ]

用法：
    python encrypt_payload.py <輸入檔> <金鑰名稱> [輸出檔]

金鑰名稱：version | core | auth | depot

範例：
    python encrypt_payload.py OSU_Core.dll core OSU_Core.enc
    python encrypt_payload.py version.json version
"""

import sys, os, struct, zlib, secrets
from pathlib import Path

try:
    from Crypto.Cipher import AES
except ImportError:
    print("[錯誤] 請安裝 pycryptodome：pip install pycryptodome")
    sys.exit(1)

try:
    from secrets_config import KEY_VERSION, KEY_CORE, KEY_AUTH, KEY_DEPOT
    KEY_MAP = {
        "version": bytes.fromhex(KEY_VERSION),
        "core":    bytes.fromhex(KEY_CORE),
        "auth":    bytes.fromhex(KEY_AUTH),
        "depot":   bytes.fromhex(KEY_DEPOT),
    }
except ImportError:
    print("[錯誤] 找不到 secrets_config.py，請先設定金鑰")
    sys.exit(1)


def encrypt(data: bytes, key: bytes) -> bytes:
    # zlib 壓縮
    compressed = zlib.compress(data, level=9)
    orig_len   = struct.pack("<I", len(data))

    # PKCS7 補齊
    payload = orig_len + compressed
    pad_len = 16 - (len(payload) % 16)
    payload += bytes([pad_len] * pad_len)

    # AES-CBC
    iv = secrets.token_bytes(16)
    cipher = AES.new(key, AES.MODE_CBC, iv)
    return iv + cipher.encrypt(payload)


def decrypt(data: bytes, key: bytes) -> bytes:
    iv      = data[:16]
    dec     = AES.new(key, AES.MODE_CBC, iv).decrypt(data[16:])
    pad     = dec[-1]
    dec     = dec[:-pad]
    orig    = struct.unpack_from("<I", dec, 0)[0]
    result  = zlib.decompress(dec[4:])
    assert len(result) == orig
    return result


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)

    in_path  = Path(sys.argv[1])
    key_name = sys.argv[2].lower()
    out_path = Path(sys.argv[3]) if len(sys.argv) > 3 else in_path.with_suffix(".enc")

    if key_name not in KEY_MAP:
        print(f"[錯誤] 未知金鑰名稱：{key_name}，可用：{list(KEY_MAP)}")
        sys.exit(1)

    key  = KEY_MAP[key_name]
    data = in_path.read_bytes()
    enc  = encrypt(data, key)
    out_path.write_bytes(enc)

    # 計算 MD5（供 manifest hash 欄位使用）
    import hashlib
    md5 = hashlib.md5(enc).hexdigest()
    print(f"[OK] {out_path}  {len(enc):,} bytes  MD5: {md5}")


if __name__ == "__main__":
    main()
