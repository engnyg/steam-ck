#!/usr/bin/env python3
"""
LY 金鑰產生器

用法：
    python keygen.py                                 # 產生一把金鑰（不指定遊戲）
    python keygen.py --appids 730,731,732            # CS2 完整版
    python keygen.py --appids 730 --note "CS2 單人"  # 附備註
    python keygen.py --appids 730,570 --count 10     # 批次產生 10 把
    python keygen.py --verify LY-ABCDE-FGHIJ-KLMNO-PQRST
    python keygen.py --add-to-server --appids 730 --note "CS2"  # 直接加入 DB
"""

import argparse, hashlib, hmac, secrets, sys
from pathlib import Path

# ── 從 secrets_config.py 讀取金鑰 ─────────────────────────────────────────────
try:
    sys.path.insert(0, str(Path(__file__).parent))
    from secrets_config import HMAC_SECRET
    SECRET = bytes.fromhex(HMAC_SECRET)
except ImportError:
    print("[錯誤] 找不到 scripts/secrets_config.py，請從 secrets_config.example.py 複製並填入金鑰")
    sys.exit(1)

CHARSET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"

# ── 核心函式 ──────────────────────────────────────────────────────────────────

def _to_base36(n: int, length: int) -> str:
    result = []
    for _ in range(length):
        result.append(CHARSET[n % 36])
        n //= 36
    return "".join(reversed(result))

def generate_key() -> str:
    serial = "".join(secrets.choice(CHARSET) for _ in range(12))
    mac    = hmac.new(SECRET, serial.encode(), hashlib.sha256).digest()
    check  = _to_base36(int.from_bytes(mac[:6], "big"), 8)
    full   = serial + check  # 20 chars
    return f"LY-{full[0:5]}-{full[5:10]}-{full[10:15]}-{full[15:20]}"

def verify_key(key: str) -> bool:
    clean = key.upper().replace("LY-", "").replace("-", "")
    if len(clean) != 20 or not all(c in CHARSET for c in clean):
        return False
    serial, check = clean[:12], clean[12:]
    mac = hmac.new(SECRET, serial.encode(), hashlib.sha256).digest()
    return check == _to_base36(int.from_bytes(mac[:6], "big"), 8)

# ── 伺服器整合（需要 server/secrets_config.py）───────────────────────────────

def add_to_server(key: str, appids: str, note: str):
    """呼叫本機 Flask API 將金鑰加入資料庫"""
    import json, urllib.request, time
    try:
        from secrets_config import ADMIN_TOKEN, HMAC_SECRET as HS
    except ImportError:
        print("[錯誤] 需要 ADMIN_TOKEN 才能加入伺服器")
        return False

    url = "http://127.0.0.1:5000/api/admin/add_key"
    payload = json.dumps({"key": key, "appids": appids, "note": note}).encode()
    req = urllib.request.Request(url, data=payload,
                                  headers={"Content-Type": "application/json",
                                           "X-Admin-Token": ADMIN_TOKEN})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            resp = json.loads(r.read())
            return resp.get("ok", False)
    except Exception as e:
        print(f"[伺服器錯誤] {e}")
        return False

# ── CLI ───────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="LY 金鑰產生器")
    parser.add_argument("--verify", metavar="KEY", help="驗證金鑰格式")
    parser.add_argument("--appids", metavar="730,731,732",
                        help="此金鑰解鎖的 AppID（逗號分隔）")
    parser.add_argument("--note",   metavar="TEXT", default="",
                        help="備註（例如遊戲名稱）")
    parser.add_argument("--count",  type=int, default=1,
                        help="批次產生數量（預設 1）")
    parser.add_argument("--add-to-server", action="store_true",
                        help="產生後直接加入本機伺服器 DB")
    args = parser.parse_args()

    # 驗證模式
    if args.verify:
        ok = verify_key(args.verify)
        print(f"{'✓ 有效' if ok else '✗ 無效'}: {args.verify}")
        sys.exit(0 if ok else 1)

    if args.add_to_server and not args.appids:
        print("[錯誤] --add-to-server 需要搭配 --appids")
        sys.exit(1)

    # 產生金鑰
    keys = [generate_key() for _ in range(args.count)]

    appids_str = args.appids or ""
    note_str   = args.note

    for key in keys:
        suffix = f"  AppIDs={appids_str}" if appids_str else ""
        suffix += f"  note={note_str}"   if note_str   else ""
        print(f"{key}{suffix}")

        if args.add_to_server:
            ok = add_to_server(key, appids_str, note_str)
            print(f"  → 伺服器: {'已加入' if ok else '失敗'}")

if __name__ == "__main__":
    main()
