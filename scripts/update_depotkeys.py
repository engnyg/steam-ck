#!/usr/bin/env python3
"""
自動更新 depotkeys.enc 並同步 version.enc

從多個公開來源下載最新 depot key，合併後加密上傳至 CDN，
再自動重算 version manifest 並上傳 version.enc。

用法：
    python3 update_depotkeys.py                   # 更新靜態庫並上傳
    python3 update_depotkeys.py --dry-run         # 只下載合併，不上傳
    python3 update_depotkeys.py --check           # 只顯示各來源筆數
    python3 update_depotkeys.py --app 730         # 動態查詢單款遊戲 ManifestHub
    python3 update_depotkeys.py --app 730,570,1172470  # 一次查多款

排程（crontab，每天凌晨 3 點）：
    0 3 * * * cd /path/to/steam-ck/scripts && python3 update_depotkeys.py >> /var/log/depotkeys.log 2>&1
"""

import sys, json, zlib, struct, secrets as _secrets, hashlib, time, re
import urllib.request, urllib.error, importlib.util, io, zipfile
from pathlib import Path
from dataclasses import dataclass, field
from typing import Optional

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

# ── 靜態 depot key 來源 ───────────────────────────────────────────────────────

STATIC_SOURCES = [
    "https://api.993499094.xyz/depotkeys.json",
    "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json",
    "https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json",
]

# ManifestHub（按 AppID 分 branch 存 key.vdf + *.manifest）
MANIFEST_HUB_REPO  = "SteamAutoCracks/ManifestHub"
GITHUB_API_BASE    = "https://api.github.com"
CODELOAD_BASE      = "https://codeload.github.com"
RAW_GH_BASE        = "https://raw.githubusercontent.com"

ROOT         = SCRIPT_DIR.parent
DIST_DIR     = SCRIPT_DIR / "dist"
CACHE_JSON   = DIST_DIR / "depotkeys.json"
DEPOT_ENC    = DIST_DIR / "depot.enc"
VERSION_JSON = DIST_DIR / "version.json"
VERSION_ENC  = DIST_DIR / "version.enc"
DIST_DIR.mkdir(parents=True, exist_ok=True)


# ── 資料結構 ──────────────────────────────────────────────────────────────────

@dataclass
class AppManifestData:
    """單款遊戲從 ManifestHub 拿到的資料"""
    app_id:      str
    depot_keys:  dict = field(default_factory=dict)   # {depotId: hexKey}
    manifests:   dict = field(default_factory=dict)   # {depotId: manifestId}


# ── 加密 ──────────────────────────────────────────────────────────────────────

def encrypt(data: bytes, key: bytes) -> bytes:
    compressed = zlib.compress(data, level=9)
    orig_len   = struct.pack("<I", len(data))
    payload    = orig_len + compressed
    pad_len    = 16 - (len(payload) % 16)
    payload   += bytes([pad_len] * pad_len)
    iv = _secrets.token_bytes(16)
    return iv + AES.new(key, AES.MODE_CBC, iv).encrypt(payload)


# ── HTTP ──────────────────────────────────────────────────────────────────────

def http_get(url: str, timeout: int = 60,
             headers: Optional[dict] = None) -> Optional[bytes]:
    try:
        req = urllib.request.Request(
            url,
            headers={"User-Agent": "steam-ck/1.0", **(headers or {})},
        )
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.read()
    except Exception as e:
        print(f"    [GET 失敗] {url}: {e}")
    return None


def fetch_json(url: str) -> Optional[dict]:
    raw = http_get(url)
    if raw is None:
        return None
    try:
        data = json.loads(raw)
        if isinstance(data, dict) and len(data) > 1000:
            return data
    except Exception:
        pass
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


# ── VDF 解析（key.vdf） ───────────────────────────────────────────────────────
#
# 格式：
#   "depots"
#   {
#       "731"  { "DecryptionKey"  "aabbcc..." }
#   }
#
# 只需要提取 DecryptionKey，用正則足夠。

def parse_key_vdf(text: str) -> dict:
    """從 key.vdf / config.vdf 文字中提取 {depotId: hexKey}"""
    result = {}
    # 找每個 "數字ID" { ... "DecryptionKey" "hex" ... } 區塊
    depot_block = re.compile(
        r'"(\d+)"\s*\{[^}]*?"DecryptionKey"\s+"([0-9a-fA-F]{64})"',
        re.DOTALL | re.IGNORECASE,
    )
    for m in depot_block.finditer(text):
        result[m.group(1)] = m.group(2).lower()
    return result


def parse_lua_keys(text: str) -> dict:
    """從 Lua 腳本中提取 addappid(depotId, 1, "key") 格式"""
    result = {}
    for m in re.finditer(
        r'addappid\((\d+)\s*,\s*1\s*,\s*"([0-9a-fA-F]{64})"',
        text, re.IGNORECASE
    ):
        result[m.group(1)] = m.group(2).lower()
    return result


def parse_manifest_filename(name: str) -> Optional[tuple]:
    """depotId_manifestId.manifest → (depotId, manifestId)"""
    m = re.match(r'^(\d+)_(\w+)\.manifest$', name, re.IGNORECASE)
    return (m.group(1), m.group(2)) if m else None


# ── ManifestHub 動態查詢 ──────────────────────────────────────────────────────

class ManifestHubClient:
    """
    按 AppID 從 ManifestHub 拉 depot key + manifest ID。

    流程（與 steamtool- GitHubManifestProvider 相同）：
      1. GitHub API：GET /repos/{repo}/branches/{appId} → SHA
                     GET /git/trees/{sha}?recursive=1    → 檔案列表
                     逐一下載 key.vdf / *.manifest
      2. API 被限速（403/429）→ 改走 codeload ZIP
      3. ZIP 解壓後解析 key.vdf（或 *.lua）+ *.manifest 檔名
    """

    def __init__(self, repo: str = MANIFEST_HUB_REPO,
                 github_token: Optional[str] = None):
        self._repo  = repo
        self._token = github_token
        self._api_ok = True  # 一旦被限速就切 ZIP 模式

    def _gh_headers(self) -> dict:
        h = {"Accept": "application/vnd.github+json", "User-Agent": "steam-ck/1.0"}
        if self._token:
            h["Authorization"] = f"Bearer {self._token}"
        return h

    def fetch(self, app_id: str) -> Optional[AppManifestData]:
        result = None
        if self._api_ok:
            result = self._via_api(app_id)
        if result is None:
            result = self._via_zip(app_id)
        return result

    # ── API 路徑 ──────────────────────────────────────────────────────────────

    def _via_api(self, app_id: str) -> Optional[AppManifestData]:
        # 1. 取 commit SHA
        branch_url = f"{GITHUB_API_BASE}/repos/{self._repo}/branches/{app_id}"
        raw = http_get(branch_url, headers=self._gh_headers())
        if raw is None:
            return None
        try:
            info = json.loads(raw)
        except Exception:
            return None

        # 被限速
        if "message" in info and not "commit" in info:
            msg = info.get("message", "")
            if "rate limit" in msg.lower() or "403" in str(msg):
                print(f"    [GitHub API 限速] 切換 ZIP 模式")
                self._api_ok = False
            return None

        try:
            sha      = info["commit"]["sha"]
            tree_url = info["commit"]["commit"]["tree"]["url"]
        except (KeyError, TypeError):
            return None

        # 2. 取檔案列表
        raw_tree = http_get(f"{tree_url}?recursive=1", headers=self._gh_headers())
        if raw_tree is None:
            return None
        try:
            tree_info = json.loads(raw_tree)
            paths = [
                e["path"] for e in tree_info.get("tree", [])
                if e.get("type") == "blob"
            ]
        except Exception:
            return None

        if not paths:
            return None

        # 3. 下載各檔案
        depot_keys: dict = {}
        manifests:  dict = {}

        for p in paths:
            name = Path(p).name
            raw_file = http_get(
                f"{RAW_GH_BASE}/{self._repo}/{sha}/{p}",
                headers=self._gh_headers(),
            )
            if name.lower() in ("key.vdf", "config.vdf") and raw_file:
                depot_keys.update(parse_key_vdf(raw_file.decode("utf-8", errors="ignore")))
            elif name.lower().endswith(".lua") and raw_file:
                depot_keys.update(parse_lua_keys(raw_file.decode("utf-8", errors="ignore")))
            elif name.lower().endswith(".manifest"):
                parsed = parse_manifest_filename(name)
                if parsed:
                    manifests[parsed[0]] = parsed[1]

        return AppManifestData(app_id=app_id, depot_keys=depot_keys, manifests=manifests)

    # ── ZIP 路徑 ──────────────────────────────────────────────────────────────

    def _via_zip(self, app_id: str) -> Optional[AppManifestData]:
        zip_url = f"{CODELOAD_BASE}/{self._repo}/zip/refs/heads/{app_id}"
        print(f"    [ZIP] {zip_url}")
        raw = http_get(zip_url, timeout=120)
        if raw is None:
            return None
        try:
            return self._parse_zip(app_id, raw)
        except Exception as e:
            print(f"    [ZIP 解析失敗] {e}")
            return None

    def _parse_zip(self, app_id: str, raw: bytes) -> Optional[AppManifestData]:
        depot_keys: dict = {}
        manifests:  dict = {}

        with zipfile.ZipFile(io.BytesIO(raw)) as zf:
            for name in zf.namelist():
                base = Path(name).name
                if not base:
                    continue
                if base.lower() in ("key.vdf", "config.vdf"):
                    text = zf.read(name).decode("utf-8", errors="ignore")
                    depot_keys.update(parse_key_vdf(text))
                elif base.lower().endswith(".lua"):
                    text = zf.read(name).decode("utf-8", errors="ignore")
                    depot_keys.update(parse_lua_keys(text))
                elif base.lower().endswith(".manifest"):
                    parsed = parse_manifest_filename(base)
                    if parsed:
                        manifests[parsed[0]] = parsed[1]

        if not depot_keys and not manifests:
            return None
        return AppManifestData(app_id=app_id, depot_keys=depot_keys, manifests=manifests)


# ── Manifest ──────────────────────────────────────────────────────────────────

def rebuild_version_manifest() -> bool:
    spec = importlib.util.spec_from_file_location(
        "gen_manifest", SCRIPT_DIR / "gen_manifest.py")
    gm = importlib.util.module_from_spec(spec)
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


# ── 靜態庫更新 ────────────────────────────────────────────────────────────────

def update_static(dry_run: bool):
    """從三個公開靜態來源更新 depot key 資料庫"""
    merged: dict = {}
    for url in STATIC_SOURCES:
        print(f"  下載 {url} ...")
        data = fetch_json(url)
        if data:
            before = len(merged)
            for k, v in data.items():
                if k not in merged:
                    merged[k] = v
            print(f"    → {len(data):,} keys，新增 {len(merged)-before:,} 筆")

    print(f"  合併後共 {len(merged):,} depot keys")

    # 比較快取
    if CACHE_JSON.exists():
        old  = json.loads(CACHE_JSON.read_bytes())
        diff = len(merged) - len(old)
        if diff == 0:
            print("  → 無新增 key，跳過更新")
            return False
        print(f"  → 新增 {diff:,} 個 key（{len(old):,} → {len(merged):,}）")
    else:
        print("  → 首次建立")

    _save_and_encrypt(merged, dry_run)
    return True


# ── AppID 動態查詢 ────────────────────────────────────────────────────────────

def update_app(app_ids: list[str], dry_run: bool,
               github_token: Optional[str] = None):
    """從 ManifestHub 動態查詢指定 AppID 的 depot key"""
    # 載入現有快取
    if CACHE_JSON.exists():
        merged = json.loads(CACHE_JSON.read_bytes())
    else:
        merged = {}

    client  = ManifestHubClient(github_token=github_token)
    added   = 0
    results = []

    for app_id in app_ids:
        print(f"  查詢 AppID {app_id} ...")
        data = client.fetch(app_id.strip())
        if data is None:
            print(f"    → ManifestHub 無此 AppID 或下載失敗")
            continue

        new_keys = 0
        for depot_id, key in data.depot_keys.items():
            if depot_id not in merged:
                merged[depot_id] = key
                new_keys += 1
        added += new_keys

        mf_count = len(data.manifests)
        print(f"    → depot keys: {len(data.depot_keys)} 筆（新增 {new_keys}），"
              f"manifest: {mf_count} 筆")
        if data.manifests:
            for depot_id, manifest_id in list(data.manifests.items())[:5]:
                print(f"      depot {depot_id} → manifest {manifest_id}")
            if mf_count > 5:
                print(f"      ... 共 {mf_count} 筆")
        results.append(data)

    if added == 0:
        print("  → 無新增 key")
        return results

    print(f"  共新增 {added} 個 depot key，資料庫總計 {len(merged):,}")
    _save_and_encrypt(merged, dry_run)
    return results


# ── 共用：存檔 + 加密 + 上傳 ─────────────────────────────────────────────────

def _save_and_encrypt(merged: dict, dry_run: bool):
    raw = json.dumps(merged, separators=(',', ':')).encode()
    CACHE_JSON.write_bytes(raw)

    enc      = encrypt(raw, bytes.fromhex(KEY_DEPOT))
    DEPOT_ENC.write_bytes(enc)
    depot_md5 = hashlib.md5(enc).hexdigest()
    print(f"  depot.enc：{len(enc):,} bytes，MD5={depot_md5}")

    if dry_run:
        print("  [dry-run] 跳過上傳")
        return

    print(f"  上傳 depot.enc ...")
    depot_ok = upload(DEPOT_ENC, CDN_UPLOAD_URL, CDN_UPLOAD_TOKEN, "depot")
    print(f"  {'✓' if depot_ok else '✗'} depot.enc 上傳{'成功' if depot_ok else '失敗'}")

    print("  重建 version manifest ...")
    ver_ok = rebuild_version_manifest()
    if ver_ok:
        print(f"  上傳 version.enc ...")
        ver_ok2 = upload(VERSION_ENC, CDN_VERSION_UPLOAD_URL, CDN_VERSION_UPLOAD_TOKEN, "version")
        print(f"  {'✓' if ver_ok2 else '✗'} version.enc 上傳{'成功' if ver_ok2 else '失敗'}")
    else:
        print("  ✗ version manifest 重建失敗，請手動執行 update_server.sh version")


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    args    = sys.argv[1:]
    dry_run = "--dry-run" in args
    check   = "--check"   in args

    # 取 --app 參數
    app_ids = []
    for i, a in enumerate(args):
        if a == "--app" and i + 1 < len(args):
            app_ids = [x.strip() for x in args[i+1].split(",") if x.strip()]
            break
        elif a.startswith("--app="):
            app_ids = [x.strip() for x in a[6:].split(",") if x.strip()]
            break

    # 取 --token 參數（GitHub PAT，可選）
    gh_token = None
    for i, a in enumerate(args):
        if a == "--token" and i + 1 < len(args):
            gh_token = args[i+1]
            break
        elif a.startswith("--token="):
            gh_token = a[8:]
            break

    print(f"\n[{time.strftime('%Y-%m-%d %H:%M:%S')}] 開始更新 depotkeys")

    if check:
        print("  [check 模式] 顯示各來源筆數")
        for url in STATIC_SOURCES:
            print(f"  {url}")
            data = fetch_json(url)
            print(f"    → {len(data):,} keys" if data else "    → 失敗")
        return

    if app_ids:
        print(f"  [動態查詢模式] AppID: {', '.join(app_ids)}")
        update_app(app_ids, dry_run, github_token=gh_token)
    else:
        print("  [靜態庫更新模式]")
        update_static(dry_run)

    print(f"[完成] {time.strftime('%Y-%m-%d %H:%M:%S')}")


if __name__ == "__main__":
    main()
