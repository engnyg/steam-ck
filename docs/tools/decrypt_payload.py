#!/usr/bin/env python3
"""
SteamTools AES-256-CBC 酬載解密工具

酬載格式：IV[16] + uint32_LE(解壓後長度)[4] + zlib壓縮資料 + PKCS7 padding

用法：
    python3 decrypt_payload.py <加密檔案> <hex金鑰> [輸出檔案]

金鑰：
    版本 manifest（g148）：09513c1934d7c0b2164b57e2c266c41d2ea183751cc5e01bc6342adf987d9073
    Core/CoreBeta（g147）：314c2086150574e15cf11d1bc171251a47086c00269355cd51c93a423c140294
"""

import sys, struct, zlib
from pathlib import Path

def decrypt(data: bytes, key_hex: str) -> bytes:
    from Crypto.Cipher import AES

    key = bytes.fromhex(key_hex)
    iv  = data[:16]
    payload = data[16:]

    cipher = AES.new(key, AES.MODE_CBC, iv)
    dec = cipher.decrypt(payload)

    # PKCS7 去填充
    pad = dec[-1]
    if 1 <= pad <= 16:
        dec = dec[:-pad]

    # 讀取解壓後長度
    orig_len = struct.unpack_from('<I', dec, 0)[0]
    compressed = dec[4:]

    try:
        result = zlib.decompress(compressed)
    except zlib.error:
        # 嘗試 wbits=-15（raw deflate）
        result = zlib.decompress(compressed, wbits=-15)

    if len(result) != orig_len:
        print(f"[警告] 解壓後長度 {len(result)} != 預期 {orig_len}", file=sys.stderr)

    return result


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)

    in_path  = Path(sys.argv[1])
    key_hex  = sys.argv[2].strip()
    out_path = Path(sys.argv[3]) if len(sys.argv) > 3 else in_path.with_suffix('.dec')

    data = in_path.read_bytes()
    result = decrypt(data, key_hex)

    out_path.write_bytes(result)
    print(f"[OK] 解密完成：{out_path}（{len(result):,} bytes）")

    # 嘗試以文字顯示（JSON manifest）
    try:
        text = result.decode('utf-8')
        if text.startswith('{'):
            import json
            print(json.dumps(json.loads(text), ensure_ascii=False, indent=2)[:2000])
    except Exception:
        pass


if __name__ == '__main__':
    main()
