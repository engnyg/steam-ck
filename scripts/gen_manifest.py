#!/usr/bin/env python3
"""
產生伺服器用版本 manifest（JSON，供 /version 回傳）。

工作：
  1. 讀 manifests/version.json 作為樣板（版本號、URL、AppIdInOnline 等由人工維護）
  2. 掃 scripts/dist/{core,auth,depot}.enc 計算 MD5，覆寫 WindowsFile64Stable 中各條目 Hash
  3. 計算整份 manifest 的 SHA1 填入 Hash 欄位（SteamTools 相同作法）
  4. 寫到 scripts/dist/version.json

後續由 update_server.sh 以 encrypt_payload.py version 加密後上傳。
"""

import hashlib
import json
from pathlib import Path

ROOT        = Path(__file__).resolve().parent.parent
TEMPLATE    = ROOT / "manifests" / "version.json"
DIST_DIR    = ROOT / "scripts" / "dist"
OUT_PATH    = DIST_DIR / "version.json"

# FileName 後綴 → 加密 payload 檔名
TARGETS = {
    "appcache/httpcache/3b/<mCode>": "core.enc",   # OSU_Core.dll
    "appcache/httpcache/3a/<mCode>": "auth.enc",   # OSU_Auth.dll
    "appcache/httpcache/3c/<mCode>": "depot.enc",  # depotkeys.json
}


def md5_of(path: Path) -> str:
    h = hashlib.md5()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    manifest = json.loads(TEMPLATE.read_text(encoding="utf-8"))

    missing = []
    for entry in manifest.get("WindowsFile64Stable", []):
        name = entry.get("FileName", "")
        enc_name = TARGETS.get(name)
        if enc_name is None:
            continue
        enc_path = DIST_DIR / enc_name
        if not enc_path.exists():
            missing.append(enc_name)
            continue
        entry["Hash"] = md5_of(enc_path)
        print(f"[OK] {name} -> {enc_name}  MD5={entry['Hash']}")

    if missing:
        print(f"[警告] 下列加密檔不存在，Hash 未更新：{missing}")

    # 整份 manifest 的 SHA1（Hash 欄位先設 0）
    manifest["Hash"] = "0" * 40
    body_for_hash = json.dumps(manifest, separators=(",", ":"), sort_keys=False)
    manifest["Hash"] = hashlib.sha1(body_for_hash.encode("utf-8")).hexdigest()

    DIST_DIR.mkdir(exist_ok=True)
    OUT_PATH.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[完成] 寫出 {OUT_PATH}  SHA1={manifest['Hash']}")


if __name__ == "__main__":
    main()
