#!/usr/bin/env python3
"""
SteamTools 版本 Manifest 解密工具

自動使用版本金鑰（g148）解密，輸出 JSON。

用法：
    python3 decrypt_version.py <加密檔案>

測試 URL（需搭配 xinput1_4.dll 截取）：
    https://update.wudrm.com/     （版本資訊）
    https://update.steamox.com/   （beta 版本資訊）
"""

import sys, struct, zlib, json
from pathlib import Path

# g148 @ VA 0x180083ff8
VERSION_KEY = bytes.fromhex('09513c1934d7c0b2164b57e2c266c41d2ea183751cc5e01bc6342adf987d9073')

# g147 @ VA 0x180083fd8
CORE_KEY = bytes.fromhex('314c2086150574e15cf11d1bc171251a47086c00269355cd51c93a423c140294')


def decrypt_aes_cbc_zlib(data: bytes, key: bytes) -> bytes:
    from Crypto.Cipher import AES

    iv      = data[:16]
    payload = data[16:]

    dec = AES.new(key, AES.MODE_CBC, iv).decrypt(payload)

    pad = dec[-1]
    if 1 <= pad <= 16:
        dec = dec[:-pad]

    orig_len = struct.unpack_from('<I', dec, 0)[0]
    compressed = dec[4:]

    try:
        result = zlib.decompress(compressed)
    except zlib.error:
        result = zlib.decompress(compressed, wbits=-15)

    return result


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    in_path = Path(sys.argv[1])
    data = in_path.read_bytes()

    # 先嘗試版本金鑰
    for label, key in [('版本 manifest（g148）', VERSION_KEY), ('Core（g147）', CORE_KEY)]:
        try:
            result = decrypt_aes_cbc_zlib(data, key)
            print(f"[OK] 金鑰：{label}")
            try:
                obj = json.loads(result.decode('utf-8'))
                print(json.dumps(obj, ensure_ascii=False, indent=2))
            except Exception:
                print(f"[非 JSON，前64 bytes] {result[:64].hex()}")
            return
        except Exception as e:
            print(f"[失敗] {label}: {e}", file=sys.stderr)

    print("[錯誤] 兩個金鑰均無法解密", file=sys.stderr)
    sys.exit(1)


if __name__ == '__main__':
    main()
