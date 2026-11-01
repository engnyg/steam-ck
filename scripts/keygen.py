#!/usr/bin/env python3
"""
LY 金鑰生成工具

用法：
    python keygen.py [數量] [--add-to-server] [--note "備註"]

範例：
    python keygen.py 5
    python keygen.py 1 --add-to-server --note "測試用"
"""

import sys, hmac, hashlib, secrets, argparse, json
from pathlib import Path

# 載入金鑰設定
try:
    from secrets_config import HMAC_SECRET, ADMIN_TOKEN
    SECRET = bytes.fromhex(HMAC_SECRET)
except ImportError:
    print("[錯誤] 找不到 secrets_config.py")
    print("  請先執行：cp secrets_config.example.py secrets_config.py")
    print("  然後填入真實金鑰")
    sys.exit(1)

CHARSET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"


def _to_base36(n: int, length: int) -> str:
    result = []
    for _ in range(length):
        result.append(CHARSET[n % 36])
        n //= 36
    return "".join(reversed(result))


def generate_key() -> str:
    """生成一組合法的 LY 金鑰"""
    serial = "".join(secrets.choice(CHARSET) for _ in range(12))
    mac = hmac.new(SECRET, serial.encode(), hashlib.sha256).digest()
    check = _to_base36(int.from_bytes(mac[:6], "big"), 8)
    full = serial + check  # 20 chars
    return f"LY-{full[0:5]}-{full[5:10]}-{full[10:15]}-{full[15:20]}"


def verify_key(key: str) -> bool:
    """驗證 LY 金鑰格式與 HMAC"""
    clean = key.upper().replace("LY-", "").replace("-", "")
    if len(clean) != 20:
        return False
    if not all(c in CHARSET for c in clean):
        return False
    serial, check = clean[:12], clean[12:]
    mac = hmac.new(SECRET, serial.encode(), hashlib.sha256).digest()
    expected = _to_base36(int.from_bytes(mac[:6], "big"), 8)
    return check == expected


def add_to_server(key: str, note: str, server_url: str, token: str) -> bool:
    """將金鑰加入伺服器資料庫"""
    import urllib.request, urllib.error
    payload = json.dumps({"key": key, "note": note}).encode()
    req = urllib.request.Request(
        f"{server_url}/api/admin/add_key",
        data=payload,
        headers={"Content-Type": "application/json", "X-Admin-Token": token},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status == 200
    except urllib.error.URLError as e:
        print(f"  [警告] 無法連線伺服器：{e}")
        return False


def main():
    parser = argparse.ArgumentParser(description="LY 金鑰生成工具")
    parser.add_argument("count", nargs="?", type=int, default=1, help="生成數量")
    parser.add_argument("--add-to-server", action="store_true", help="自動加入伺服器 DB")
    parser.add_argument("--server", default="https://api.ourdomain.com", help="伺服器 URL")
    parser.add_argument("--note", default="", help="備註")
    parser.add_argument("--verify", metavar="KEY", help="驗證單一金鑰")
    args = parser.parse_args()

    if args.verify:
        ok = verify_key(args.verify)
        print(f"{'[合法]' if ok else '[無效]'} {args.verify}")
        sys.exit(0 if ok else 1)

    keys = [generate_key() for _ in range(args.count)]

    for key in keys:
        status = ""
        if args.add_to_server:
            ok = add_to_server(key, args.note, args.server, ADMIN_TOKEN)
            status = " [已加入伺服器]" if ok else " [伺服器失敗]"
        print(f"{key}{status}")


if __name__ == "__main__":
    main()
