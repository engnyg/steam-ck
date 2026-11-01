#!/usr/bin/env python3
"""
OSU API 伺服器

環境變數：
    HMAC_SECRET   32-byte hex（與 keygen.py 相同）
    ADMIN_TOKEN   管理員 token
    DB_PATH       SQLite 路徑（預設 ./osu.db）
"""

import os, hmac, hashlib, sqlite3, time, functools
from pathlib import Path
from flask import Flask, request, jsonify, abort, g

app = Flask(__name__)

# ── 設定 ──────────────────────────────────────────────────────────────────────

HMAC_SECRET = bytes.fromhex(os.environ["HMAC_SECRET"])
ADMIN_TOKEN = os.environ["ADMIN_TOKEN"]
DB_PATH     = os.environ.get("DB_PATH", "./osu.db")

CHARSET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"

# AppID 清單：這些遊戲在啟動時需要線上驗證
ONLINE_APP_IDS = {
    730, 570, 1172470, 413150, 3097560, 717690,
    2737070, 2336220, 1084600, 1203620, 632360,
}

# ── 資料庫 ────────────────────────────────────────────────────────────────────

def get_db():
    if "db" not in g:
        g.db = sqlite3.connect(DB_PATH)
        g.db.row_factory = sqlite3.Row
    return g.db

@app.teardown_appcontext
def close_db(exc):
    db = g.pop("db", None)
    if db:
        db.close()

def init_db():
    schema = Path(__file__).parent / "schema.sql"
    db = sqlite3.connect(DB_PATH)
    db.executescript(schema.read_text())
    db.commit()
    db.close()

# ── 工具函式 ──────────────────────────────────────────────────────────────────

def _to_base36(n: int, length: int) -> str:
    result = []
    for _ in range(length):
        result.append(CHARSET[n % 36])
        n //= 36
    return "".join(reversed(result))

def _verify_hmac(key: str) -> bool:
    clean = key.upper().replace("LY-", "").replace("-", "")
    if len(clean) != 20 or not all(c in CHARSET for c in clean):
        return False
    serial, check = clean[:12], clean[12:]
    mac = hmac.new(HMAC_SECRET, serial.encode(), hashlib.sha256).digest()
    return check == _to_base36(int.from_bytes(mac[:6], "big"), 8)

def require_admin(f):
    @functools.wraps(f)
    def wrapper(*a, **kw):
        if request.headers.get("X-Admin-Token") != ADMIN_TOKEN:
            abort(403)
        return f(*a, **kw)
    return wrapper

# ── 公開端點 ──────────────────────────────────────────────────────────────────

@app.post("/api/verify_cdkey")
def verify_cdkey():
    """
    驗證 LY 金鑰。
    Body: { "key": "LY-...", "ts": <unix>, "sig": <hmac(key+ts)> }
    回應: { "valid": bool, "features": [...], "online_apps": [...] }
    """
    body = request.get_json(silent=True) or {}
    key  = str(body.get("key", "")).strip().upper()
    ts   = body.get("ts", 0)
    sig  = body.get("sig", "")

    # 防重放：timestamp 不能超過 5 分鐘
    if abs(time.time() - int(ts)) > 300:
        return jsonify({"valid": False, "reason": "timestamp_expired"}), 400

    # 驗證請求簽章（防止請求偽造）
    expected_sig = hmac.new(
        HMAC_SECRET, f"{key}{ts}".encode(), hashlib.sha256
    ).hexdigest()
    if not hmac.compare_digest(sig, expected_sig):
        return jsonify({"valid": False, "reason": "invalid_sig"}), 400

    # HMAC 格式驗證
    if not _verify_hmac(key):
        return jsonify({"valid": False, "reason": "invalid_key_format"}), 200

    # 資料庫查詢
    db  = get_db()
    row = db.execute("SELECT revoked FROM keys WHERE key = ?", (key,)).fetchone()
    if row is None:
        return jsonify({"valid": False, "reason": "key_not_found"}), 200
    if row["revoked"]:
        return jsonify({"valid": False, "reason": "key_revoked"}), 200

    # 記錄驗證
    db.execute(
        "INSERT INTO verify_log (key, ip) VALUES (?, ?)",
        (key, request.remote_addr)
    )
    db.commit()

    return jsonify({
        "valid": True,
        "features": ["unlock", "depot", "manifest"],
        "online_apps": sorted(ONLINE_APP_IDS),
    })


@app.post("/api/check_app")
def check_app():
    """
    線上遊戲驗證（AppIdInOnline 清單中的遊戲）。
    Body: { "key": "LY-...", "appid": 730 }
    回應: { "allowed": bool }
    """
    body   = request.get_json(silent=True) or {}
    key    = str(body.get("key", "")).strip().upper()
    appid  = int(body.get("appid", 0))

    if not _verify_hmac(key):
        return jsonify({"allowed": False}), 200

    db  = get_db()
    row = db.execute("SELECT revoked FROM keys WHERE key = ?", (key,)).fetchone()
    if row is None or row["revoked"]:
        return jsonify({"allowed": False}), 200

    return jsonify({"allowed": True})


# ── 管理端點 ──────────────────────────────────────────────────────────────────

@app.post("/api/admin/add_key")
@require_admin
def add_key():
    """新增金鑰至 DB。Body: { "key": "LY-...", "note": "..." }"""
    body = request.get_json(silent=True) or {}
    key  = str(body.get("key", "")).strip().upper()
    note = str(body.get("note", ""))

    if not _verify_hmac(key):
        return jsonify({"ok": False, "reason": "invalid_key"}), 400

    db = get_db()
    try:
        db.execute("INSERT INTO keys (key, note) VALUES (?, ?)", (key, note))
        db.commit()
    except sqlite3.IntegrityError:
        return jsonify({"ok": False, "reason": "duplicate"}), 409

    return jsonify({"ok": True})


@app.post("/api/admin/revoke_key")
@require_admin
def revoke_key():
    """撤銷金鑰。Body: { "key": "LY-..." }"""
    body = request.get_json(silent=True) or {}
    key  = str(body.get("key", "")).strip().upper()

    db = get_db()
    db.execute("UPDATE keys SET revoked = 1 WHERE key = ?", (key,))
    db.commit()
    return jsonify({"ok": True})


@app.get("/api/admin/list_keys")
@require_admin
def list_keys():
    db   = get_db()
    rows = db.execute("SELECT key, created_at, note, revoked FROM keys ORDER BY created_at DESC").fetchall()
    return jsonify([dict(r) for r in rows])


# ── 啟動 ──────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    init_db()
    app.run(host="0.0.0.0", port=5000, debug=False)
