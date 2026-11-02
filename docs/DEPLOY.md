# OpenSteamUnlocker (OSU) 部署指南

## 目錄

- [架構概覽](#架構概覽)
- [專案結構](#專案結構)
- [先決條件](#先決條件)
- [第一步：產生密鑰](#第一步產生密鑰)
- [第二步：設定伺服器](#第二步設定伺服器)
- [第三步：編譯原生 DLL](#第三步編譯原生-dll)
- [第四步：填入 AES 加密金鑰](#第四步填入-aes-加密金鑰)
- [第五步：加密與打包 Payload](#第五步加密與打包-payload)
- [第六步：部署 CDN / 靜態檔案](#第六步部署-cdn--靜態檔案)
- [第七步：客戶端部署](#第七步客戶端部署)
- [Depot Key 自動更新](#depot-key-自動更新)
- [管理操作](#管理操作)
- [Steam 更新後維護](#steam-更新後維護)
- [CI/CD 自動建置](#cicd-自動建置)
- [疑難排解](#疑難排解)

---

## 架構概覽

```
使用者電腦                        你的伺服器
┌─────────────────────┐          ┌─────────────────────┐
│  Steam.exe          │          │  Flask API (app.py)  │
│   └─ xinput1_4.dll  │◄────────►│   /api/verify_cdkey  │
│       (Loader)      │  HTTPS   │   /api/check_app     │
│       ├─ OSU_Auth   │          │   /api/admin/*       │
│       ├─ OSU_Core   │          │   SQLite (osu.db)    │
│       └─ depotkeys  │          └─────────────────────┘
└─────────────────────┘                    ▲
        │                                  │
        ▼                          ┌───────┴───────┐
  ┌───────────┐                    │  CDN 靜態檔案  │
  │ Steam API │                    │  /version      │
  │ vtable    │                    │  /core (加密)  │
  │ hooking   │                    │  /auth (加密)  │
  └───────────┘                    │  /depot (加密) │
                                   │  /xinput1_4.dll│
                                   └───────────────┘
```

**流程摘要：**

1. 使用者執行 `activate-cdk.ps1`，輸入 LY 金鑰，下載 `xinput1_4.dll` 到 Steam 目錄
2. Steam 啟動時載入 `xinput1_4.dll`（DLL hijacking）
3. Loader 從 CDN 下載加密的版本 manifest，解密後取得各元件 URL
4. 下載並 AES 解密 `OSU_Core.dll`、`OSU_Auth.dll`、`depotkeys.json`
5. 反射式載入（Reflective PE Loading）兩個 DLL 到記憶體
6. `OSU_Auth` 向伺服器驗證 LY 金鑰，取得該金鑰綁定的 AppID 清單
7. `OSU_Core` 透過 RTTI 找到 `IClientUser` vtable，hook `BIsSubscribedApp` 和 `GetSubscribedApps`
8. Depot 解密金鑰注入 Windows Registry

---

## 專案結構

```
steam-ck/
├── native/                     # C++ 原生元件
│   ├── CMakeLists.txt          # 頂層 CMake
│   ├── toolchain-mingw64.cmake # MinGW 交叉編譯工具鏈
│   ├── common/                 # 共用標頭
│   │   ├── osu_abi.hpp         # 跨 DLL ABI 定義（函式指標表）
│   │   ├── crypto.hpp          # AES-256-CBC + zlib 解密
│   │   ├── pe_loader.hpp       # 反射式 PE 載入器
│   │   └── winhttp_client.hpp  # HTTP 客戶端
│   ├── xinput_loader/          # Phase 0：DLL hijacking 入口
│   │   ├── dllmain.cpp         # DllMain
│   │   ├── loader.cpp          # 主載入邏輯
│   │   └── loader.hpp
│   ├── osu_auth/               # Phase 1：金鑰驗證模組
│   │   ├── dllmain.cpp
│   │   ├── auth.cpp            # 線上驗證 + 離線快取
│   │   └── auth.hpp
│   └── osu_core/               # Phase 2：Steam hook 核心
│       ├── CMakeLists.txt
│       ├── dllmain.cpp
│       ├── core.cpp            # depot key 注入 + 初始化
│       ├── core.hpp
│       └── hooks.cpp           # RTTI vtable hook
├── server/                     # Phase 3：後端 API
│   ├── app.py                  # Flask 伺服器
│   ├── schema.sql              # SQLite 結構
│   └── requirements.txt
├── scripts/                    # 工具腳本
│   ├── keygen.py               # LY 金鑰產生器
│   ├── encrypt_payload.py      # AES-256-CBC 加密工具
│   ├── gen_manifest.py         # 版本 manifest 產生器
│   ├── find_vtable.py          # vtable 索引查找工具（RE）
│   ├── update_server.sh        # 一鍵加密+上傳
│   ├── update_depotkeys.py     # Depot key 自動更新腳本（排程用）
│   ├── activate-cdk.ps1        # 客戶端部署腳本
│   ├── secrets_config.example.py  # 金鑰設定範本
│   └── dist/                   # 加密後的 payload 輸出
├── manifests/
│   └── version.json            # 版本 manifest 範本
├── .github/workflows/
│   └── build.yml               # GitHub Actions CI
└── docs/
    ├── DEPLOY.md               # ← 本文件
    └── osu-steamtools-plan.md  # 技術設計文件
```

---

## 先決條件

### 伺服器端

| 軟體          | 版本       | 用途                     |
|--------------|-----------|--------------------------|
| Python       | >= 3.10   | API 伺服器、腳本          |
| Flask        | >= 3.0    | Web 框架                 |
| gunicorn     | >= 21.0   | 生產環境 WSGI 伺服器      |
| pycryptodome | >= 3.19   | AES 加密/解密            |
| SQLite       | 內建       | 金鑰資料庫               |

### 建置端（編譯 DLL）

| 軟體               | 版本      | 用途                           |
|-------------------|----------|-------------------------------|
| MinGW-w64         | >= 12    | x86_64 Windows 交叉編譯        |
| CMake             | >= 3.20  | 建置系統                       |
| zlib（MinGW 版本） | >= 1.3   | 壓縮（需交叉編譯為 .a 靜態庫）  |

> **提示：** 可以不在本機編譯，直接使用 GitHub Actions CI 產生 DLL（見 [CI/CD 自動建置](#cicd-自動建置)）。

### 客戶端

| 需求           | 說明                               |
|---------------|-----------------------------------|
| Windows 10/11 | 64-bit                            |
| Steam         | 已安裝                             |
| PowerShell 5+ | Windows 內建                      |
| 網路連線       | 首次啟動需連線驗證金鑰              |

---

## 第一步：產生密鑰

所有加密金鑰集中在 `scripts/secrets_config.py` 管理。

### 1.1 建立設定檔

```bash
cd scripts/
cp secrets_config.example.py secrets_config.py
```

### 1.2 產生各項金鑰

```bash
# HMAC-SHA256 金鑰（金鑰格式驗證用）
python3 -c "import secrets; print(secrets.token_hex(32))"
# → 填入 HMAC_SECRET

# AES-256 金鑰（每個元件獨立）
python3 -c "import secrets; print(secrets.token_hex(32))"
# → 分別填入 KEY_VERSION、KEY_CORE、KEY_AUTH、KEY_DEPOT

# 管理員 Token
python3 -c "import secrets; print(secrets.token_urlsafe(32))"
# → 填入 ADMIN_TOKEN
```

### 1.3 完成後的 secrets_config.py

```python
HMAC_SECRET = "a1b2c3d4..."   # 64 個 hex 字元（32 bytes）

KEY_VERSION = "e5f6a7b8..."   # manifest 加密金鑰
KEY_CORE    = "c9d0e1f2..."   # OSU_Core.dll 加密金鑰
KEY_AUTH    = "a3b4c5d6..."   # OSU_Auth.dll 加密金鑰
KEY_DEPOT   = "e7f8a9b0..."   # depotkeys.json 加密金鑰

ADMIN_TOKEN = "your-random-admin-token"

# Depot Key 自動更新（update_depotkeys.py 使用）
CDN_UPLOAD_URL   = "https://cdn.yourdomain.com/upload/depotkeys.enc"
CDN_UPLOAD_TOKEN = "your-cdn-upload-token"
```

> **重要：** `secrets_config.py` 已列在 `.gitignore`，永遠不要提交到版本庫。

---

## 第二步：設定伺服器

### 2.1 安裝依賴

```bash
cd server/
pip install -r requirements.txt
pip install pycryptodome   # 加密工具需要
```

### 2.2 設定環境變數

```bash
export HMAC_SECRET="<同 secrets_config.py 的 HMAC_SECRET>"
export ADMIN_TOKEN="<同 secrets_config.py 的 ADMIN_TOKEN>"
export DB_PATH="./osu.db"    # 預設值，可自訂路徑
```

建議使用 `.env` 檔案搭配 systemd 或 Docker：

```env
# server/.env（已在 .gitignore 中）
HMAC_SECRET=a1b2c3d4...
ADMIN_TOKEN=your-random-admin-token
DB_PATH=/var/lib/osu/osu.db
```

### 2.3 初始化資料庫

```bash
cd server/
python3 app.py
# 首次啟動會自動執行 schema.sql 建立資料表
# Ctrl+C 停止
```

### 2.4 生產環境部署

```bash
# 使用 gunicorn 部署
gunicorn -w 4 -b 0.0.0.0:5000 app:app

# 或搭配 systemd
# /etc/systemd/system/osu-api.service
```

**systemd 範例：**

```ini
[Unit]
Description=OSU API Server
After=network.target

[Service]
Type=simple
User=osu
WorkingDirectory=/opt/osu/server
EnvironmentFile=/opt/osu/server/.env
ExecStart=/usr/bin/gunicorn -w 4 -b 127.0.0.1:5000 app:app
Restart=always

[Install]
WantedBy=multi-user.target
```

### 2.5 Nginx 反向代理（建議）

```nginx
server {
    listen 443 ssl;
    server_name api.yourdomain.com;

    ssl_certificate     /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location /api/ {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

### 2.6 API 端點一覽

| 端點                       | 方法 | 說明                       | 認證          |
|---------------------------|------|---------------------------|---------------|
| `/api/verify_cdkey`        | POST | 驗證 LY 金鑰，回傳 AppID    | HMAC 簽章     |
| `/api/check_app`           | POST | 線上遊戲啟動驗證             | 金鑰格式檢查  |
| `/api/admin/add_key`       | POST | 手動新增金鑰                | X-Admin-Token |
| `/api/admin/revoke_key`    | POST | 撤銷金鑰                    | X-Admin-Token |
| `/api/admin/list_keys`     | GET  | 列出所有金鑰                | X-Admin-Token |
| `/api/admin/generate_key`  | POST | 伺服器端產生金鑰並寫入 DB    | X-Admin-Token |

---

## 第三步：編譯原生 DLL

### 方式 A：GitHub Actions CI（推薦）

推送 `native/` 目錄的變更到 `master` 分支，CI 自動觸發：

```bash
git push origin master
```

CI 完成後在 GitHub Actions → Artifacts 下載 `osu-dlls.zip`，內含：
- `xinput1_4.dll` — Loader（放入 Steam 目錄）
- `OSU_Auth.dll` — 驗證模組（加密後透過 CDN 分發）
- `OSU_Core.dll` — Hook 核心（加密後透過 CDN 分發）

### 方式 B：本機交叉編譯（Linux）

```bash
# 安裝工具鏈
sudo apt-get install mingw-w64 cmake

# 交叉編譯 zlib
cd /tmp
curl -sL https://zlib.net/zlib-1.3.1.tar.gz | tar xz
cd zlib-1.3.1
CC=x86_64-w64-mingw32-gcc ./configure \
    --prefix=/usr/x86_64-w64-mingw32 --static
make -j$(nproc)
sudo make install

# 回到專案目錄，建置
cd /path/to/steam-ck
cmake -B build -S native \
    -DCMAKE_TOOLCHAIN_FILE=native/toolchain-mingw64.cmake \
    -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release -j$(nproc)

# 產出位置
ls -lh build/xinput_loader/xinput1_4.dll
ls -lh build/osu_auth/OSU_Auth.dll
ls -lh build/osu_core/OSU_Core.dll
```

---

## 第四步：填入 AES 加密金鑰

`native/xinput_loader/loader.cpp` 中的 AES 金鑰以三段 XOR 儲存（避免靜態掃描）。

### 4.1 產生 XOR 三段分割

對每個 32-byte 金鑰（KEY_VERSION, KEY_CORE, KEY_AUTH, KEY_DEPOT），產生三段滿足 `A XOR B XOR C = 原始金鑰`：

```python
import secrets

def split_key(hex_key: str):
    """將 32-byte hex 金鑰分為三段 XOR"""
    key = bytes.fromhex(hex_key)
    a = secrets.token_bytes(32)
    b = secrets.token_bytes(32)
    c = bytes(ka ^ ba ^ bb for ka, ba, bb in zip(key, a, b))
    # 驗證：a ^ b ^ c == key
    assert bytes(x ^ y ^ z for x, y, z in zip(a, b, c)) == key
    return a, b, c

# 範例：
a, b, c = split_key("你的KEY_CORE hex字串")
print("KEY_CORE_A:", ", ".join(f"0x{x:02x}" for x in a))
print("KEY_CORE_B:", ", ".join(f"0x{x:02x}" for x in b))
print("KEY_CORE_C:", ", ".join(f"0x{x:02x}" for x in c))
```

### 4.2 填入 loader.cpp

將產生的值填入 `native/xinput_loader/loader.cpp` 第 21-35 行：

```cpp
static const uint8_t KEY_VER_A[32] = {0x1a, 0x2b, ...};
static const uint8_t KEY_VER_B[32] = {0x3c, 0x4d, ...};
static const uint8_t KEY_VER_C[32] = {0x5e, 0x6f, ...};
// ... 同理填入 KEY_CORE_*, KEY_AUTH_*, KEY_DEPOT_*
```

### 4.3 修改伺服器 URL

將 `loader.cpp` 第 38 行的 URL 改為你的實際網域：

```cpp
static const wchar_t* VERSION_URL = L"https://cdn.yourdomain.com/version";
```

---

## 第五步：加密與打包 Payload

### 5.1 加密各元件

```bash
cd scripts/

# 加密 OSU_Core.dll
python3 encrypt_payload.py ../build/osu_core/OSU_Core.dll core dist/core.enc

# 加密 OSU_Auth.dll
python3 encrypt_payload.py ../build/osu_auth/OSU_Auth.dll auth dist/auth.enc

# 下載並加密 depot keys
# update_server.sh 會自動從 ManifestHub 下載
bash update_server.sh depot
```

### 5.2 產生版本 manifest

先編輯 `manifests/version.json` 範本，填入你的實際 URL：

```json
{
  "Version": 1,
  "VersionTxt": "1.0",
  "urls": ["https://api.yourdomain.com"],
  "CardUrl": "https://api.yourdomain.com/api/verify_cdkey",
  "AppIdInOnlineEnable": true,
  "AppIdInOnline": "730,570,1172470,413150",
  "WindowsFile64Stable": [
    {
      "FileName": "appcache/httpcache/3b/<mCode>",
      "URL": "https://cdn.yourdomain.com/core",
      "Hash": "PLACEHOLDER_MD5_CORE"
    },
    {
      "FileName": "appcache/httpcache/3a/<mCode>",
      "URL": "https://cdn.yourdomain.com/auth",
      "Hash": "PLACEHOLDER_MD5_AUTH"
    },
    {
      "FileName": "appcache/httpcache/3c/<mCode>",
      "URL": "https://cdn.yourdomain.com/depot",
      "Hash": "PLACEHOLDER_MD5_DEPOT"
    }
  ]
}
```

然後執行：

```bash
python3 gen_manifest.py
# 自動計算各 .enc 的 MD5 並填入 Hash，輸出到 scripts/dist/version.json

# 加密 manifest
python3 encrypt_payload.py dist/version.json version dist/version.enc
```

### 5.3 一鍵操作

```bash
# 設定環境變數
export SERVER_URL="https://cdn.yourdomain.com"
export SERVER_TOKEN="your-admin-token"

# 全部加密 + 上傳
bash update_server.sh all
```

---

## 第六步：部署 CDN / 靜態檔案

你的 CDN 需要提供以下路徑：

| 路徑                | 內容                       | 來源                       |
|--------------------|---------------------------|---------------------------|
| `/version`          | 加密版本 manifest           | `scripts/dist/version.enc` |
| `/core`             | 加密 OSU_Core.dll          | `scripts/dist/core.enc`    |
| `/auth`             | 加密 OSU_Auth.dll          | `scripts/dist/auth.enc`    |
| `/depot`            | 加密 depotkeys.json        | `scripts/dist/depot.enc`   |
| `/xinput1_4.dll`    | Loader DLL（明文）          | `build/xinput_loader/xinput1_4.dll` |
| `/activate`         | 客戶端啟動腳本（明文）       | `scripts/activate-cdk.ps1` |

可以用 Nginx 靜態站、S3 + CloudFront、Cloudflare R2 等任何靜態檔案服務。

**Nginx 靜態站範例：**

```nginx
server {
    listen 443 ssl;
    server_name cdn.yourdomain.com;

    root /var/www/osu-cdn;

    location / {
        add_header Access-Control-Allow-Origin *;
    }
}
```

```bash
# 部署檔案
cp scripts/dist/version.enc  /var/www/osu-cdn/version
cp scripts/dist/core.enc     /var/www/osu-cdn/core
cp scripts/dist/auth.enc     /var/www/osu-cdn/auth
cp scripts/dist/depot.enc    /var/www/osu-cdn/depot
cp build/xinput_loader/xinput1_4.dll  /var/www/osu-cdn/xinput1_4.dll
cp scripts/activate-cdk.ps1  /var/www/osu-cdn/activate
```

---

## 第七步：客戶端部署

### 使用者操作流程

**方式 A：一鍵安裝（推薦）**

在 PowerShell 執行：

```powershell
irm https://cdn.yourdomain.com/activate | iex
```

會彈出 GUI 對話框讓使用者輸入 LY 金鑰，腳本自動：
1. 驗證金鑰格式（`LY-XXXXX-XXXXX-XXXXX-XXXXX`）
2. 取得 Steam 安裝路徑
3. 關閉正在執行的 Steam
4. 下載 `xinput1_4.dll` 到 Steam 目錄
5. 將金鑰寫入 `HKCU\Software\OSU\CDKey`
6. 建立 `%APPDATA%\OSU\applist.json`
7. 設定 Windows Defender 排除
8. 重新啟動 Steam

**方式 B：手動安裝**

1. 將 `xinput1_4.dll` 放入 Steam 安裝目錄（如 `C:\Program Files (x86)\Steam\`）
2. 建立 Registry 值：`HKCU\Software\OSU\CDKey` = `LY-XXXXX-XXXXX-XXXXX-XXXXX`
3. 啟動 Steam

### 修改 activate-cdk.ps1 中的 URL

部署前需將 `scripts/activate-cdk.ps1` 第 15-16 行改為你的實際網域：

```powershell
$CDN_BASE   = "https://cdn.yourdomain.com"
$DLL_URL    = "$CDN_BASE/xinput1_4.dll"
```

---

## Depot Key 自動更新

Depot key 來源（ManifestHub 等社群 GitHub repo）持續有人更新，但你 CDN 上的 `depot.enc` 不會自動跟著變動。使用 `scripts/update_depotkeys.py` 排程自動同步。

### 三個公開來源

| 來源                           | URL                                                                        |
|-------------------------------|---------------------------------------------------------------------------|
| 993499094.xyz                 | `https://api.993499094.xyz/depotkeys.json`                                |
| SteamAutoCracks/ManifestHub   | `https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json` |
| SSMGAlt/ManifestHub2          | `https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json` |

三個來源都會嘗試下載並合併（前面來源優先），確保即使其中一個下線仍能取到最新資料。

### 更新流程

```
三個 GitHub 公開來源（社群持續更新）
       ↓ update_depotkeys.py（排程執行）
比對本地快取，無新 key 則跳過
       ↓ 有新 key
重新 AES-256-CBC 加密 → 上傳 CDN depot.enc
       ↓ manifest MD5 變化
所有客戶端下次啟動 Steam 時自動重新下載
```

### 設定

在 `scripts/secrets_config.py` 中填入：

```python
CDN_UPLOAD_URL   = "https://cdn.yourdomain.com/upload/depotkeys.enc"
CDN_UPLOAD_TOKEN = "your-cdn-upload-token"
```

上傳方式為 `PUT` + `Bearer` token，適用 Cloudflare R2、AWS S3、Backblaze B2 等。

### 手動執行

```bash
cd scripts/

# 完整更新並上傳
python3 update_depotkeys.py

# 只下載合併，不上傳（測試用）
python3 update_depotkeys.py --dry-run

# 只查看各來源目前筆數
python3 update_depotkeys.py --check
```

### 排程設定（伺服器 crontab）

```bash
# 開啟 crontab 編輯器
crontab -e

# 加入以下行（每天凌晨 3 點執行）
0 3 * * * cd /opt/steam-ck/scripts && python3 update_depotkeys.py >> /var/log/depotkeys.log 2>&1
```

日誌範例：

```
[2026-11-02 03:00:01] 開始更新 depotkeys
  下載 https://api.993499094.xyz/depotkeys.json ...
    → 288,381 keys，新增 288,381 筆
  下載 https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json ...
    → 241,344 keys，新增 28,265 筆
  下載 https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json ...
    → 288,381 keys，新增 0 筆
  合併後共 316,646 depot keys
  → 新增 312 個 key（316,334 → 316,646）
  加密完成：9,921,888 bytes，MD5=ab12cd34...
  上傳至 https://cdn.yourdomain.com/upload/depotkeys.enc ...
  ✓ 上傳成功
[完成] 2026-11-02 03:02:14
```

### 客戶端接收更新的機制

客戶端 Loader 在每次 Steam 啟動時：

1. 下載 `version` manifest（極小，每次都拉）
2. 比較 `depot` 欄位的 MD5 與本地快取
3. **MD5 不同** → 重新下載 `depot.enc` → 解密載入
4. **MD5 相同** → 直接使用快取（不重複下載）

因此只要 CDN 上的 `depot.enc` 更新，所有使用者下次啟動 Steam 就會自動取得新的 depot key。

> **注意：** 上傳新 `depot.enc` 後，需同步更新 `manifests/version.json` 中的 depot MD5，並重新加密上傳 `version.enc`，否則客戶端不知道有更新。可用 `bash update_server.sh version` 一鍵完成。

---

## 管理操作

### 產生金鑰

```bash
cd scripts/

# 產生一把綁定 CS2 的金鑰
python3 keygen.py --appids 730 --note "CS2"

# 批次產生 10 把
python3 keygen.py --appids 730,731,732 --count 10

# 產生並直接加入伺服器 DB（需要伺服器在運行）
python3 keygen.py --appids 730 --note "CS2" --add-to-server

# 驗證金鑰格式
python3 keygen.py --verify LY-ABCDE-FGHIJ-KLMNO-PQRST
```

### 透過 API 管理金鑰

```bash
# 伺服器端產生金鑰（不需 secrets_config.py）
curl -X POST https://api.yourdomain.com/api/admin/generate_key \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: YOUR_TOKEN" \
  -d '{"appids": "730,731", "note": "CS2 完整版", "count": 5}'

# 列出所有金鑰
curl https://api.yourdomain.com/api/admin/list_keys \
  -H "X-Admin-Token: YOUR_TOKEN"

# 撤銷金鑰
curl -X POST https://api.yourdomain.com/api/admin/revoke_key \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: YOUR_TOKEN" \
  -d '{"key": "LY-XXXXX-XXXXX-XXXXX-XXXXX"}'
```

### 金鑰格式說明

```
LY-XXXXX-XXXXX-XXXXX-XXXXX
│  └───────┬────────────┘
│    20 個 Base36 字元
│    前 12 碼 = 隨機序號
│    後 8 碼 = HMAC-SHA256 校驗碼
└── 固定前綴
```

每把金鑰可綁定一或多個 AppID（逗號分隔），支援主遊戲 + DLC 的組合。

---

## Steam 更新後維護

Steam 客戶端更新可能改變 `steamclient64.dll` 中的 vtable 佈局。OSU 使用 RTTI 搜尋，通常不受影響，但如果方法索引變動：

### 使用 find_vtable.py 檢查

```bash
cd scripts/
pip install pefile

# 基本檢查（顯示重點方法索引）
python3 find_vtable.py /path/to/steamclient64.dll

# 列出所有 IClientUser 方法
python3 find_vtable.py /path/to/steamclient64.dll --methods

# 搜尋特定方法
python3 find_vtable.py /path/to/steamclient64.dll --grep Subscri

# 列出所有 IClient* 介面
python3 find_vtable.py /path/to/steamclient64.dll --all-ifaces

# 檢查其他介面
python3 find_vtable.py /path/to/steamclient64.dll --iface IClientBilling
```

### 輸出範例

```
=== RTTI vtable 搜尋 ===
  IClientUserMap（dispatch 層）
    RTTI:    .?AVIClientUserMap@@
    vtable:  VA 0x1823AB000  (file 0x2395000)
    entries: 259

=== hooks.cpp 建議值 ===
  constexpr int VT_BIsSubscribedApp  = 171;
  constexpr int VT_GetSubscribedApps = 172;
```

### 如果索引變動

1. 更新 `native/osu_core/hooks.cpp` 中的常數：
   ```cpp
   constexpr int VT_BIsSubscribedApp  = <新索引>;
   constexpr int VT_GetSubscribedApps = <新索引>;
   ```
2. 重新編譯 → 加密 → 上傳（`bash update_server.sh core && bash update_server.sh version`）

`steamclient64.dll` 位置：`C:\Program Files (x86)\Steam\steamclient64.dll`

---

## CI/CD 自動建置

### GitHub Actions 工作流程

`.github/workflows/build.yml` 在以下條件觸發：

- 推送 `native/` 目錄的變更到 `master`
- 對 `master` 發 Pull Request 時涉及 `native/`
- 手動觸發（`workflow_dispatch`）

### 建置步驟

1. 安裝 MinGW-w64 和 CMake
2. 交叉編譯 zlib 1.3.1
3. CMake 設定（使用 `toolchain-mingw64.cmake`）
4. 編譯產出三個 DLL
5. 上傳為 GitHub Artifacts（保留 90 天）

### 下載 CI 產物

GitHub → Actions → 最新成功的 Build → Artifacts → `osu-dlls`

---

## 疑難排解

### 金鑰驗證失敗

| 錯誤                  | 原因                              | 解法                           |
|----------------------|----------------------------------|-------------------------------|
| `invalid_key_format` | HMAC 校驗不通過                    | 確認伺服器和 keygen 使用相同 HMAC_SECRET |
| `key_not_found`      | 金鑰未加入 DB                      | 用 admin API 或 keygen --add-to-server |
| `key_revoked`        | 金鑰已被撤銷                       | 產生新金鑰                      |
| `timestamp_expired`  | 客戶端時鐘偏差 > 5 分鐘             | 同步系統時間                    |
| `no_appids`          | 金鑰未綁定任何 AppID               | 更新 DB 中的 appids 欄位        |

### DLL 載入問題

- **Steam 啟動後無效果**：確認 `xinput1_4.dll` 在 Steam 根目錄（非子資料夾）
- **Steam 崩潰**：檢查 DLL 是否為 x64 版本（Steam 64-bit 需要 64-bit DLL）
- **Defender 攔截**：執行 `activate-cdk.ps1` 時會自動設定排除，或手動加入排除路徑

### 編譯問題

- **找不到 zlib**：需先交叉編譯 zlib 為 MinGW 靜態庫（見第三步）
- **找不到 psapi**：`osu_core/CMakeLists.txt` 需連結 `psapi`（已設定）

### 伺服器問題

- **CORS 錯誤**：確認 CDN 回應包含 `Access-Control-Allow-Origin` 標頭
- **DB 鎖定**：SQLite 不適合高並發，超過 ~100 QPS 考慮遷移到 PostgreSQL

---

## 安全注意事項

1. **永遠不要提交** `secrets_config.py`、`.env` 或任何包含金鑰的檔案
2. **ADMIN_TOKEN** 應足夠長且隨機（>= 32 字元）
3. **HTTPS** 是必需的——所有 API 和 CDN 端點都應使用 TLS
4. **AES 金鑰分割**（三段 XOR）僅防止靜態掃描，不能替代程式碼混淆
5. **離線快取 TTL**：客戶端快取金鑰驗證結果 7 天，撤銷金鑰最多 7 天後生效
6. **AppIdInOnline**：將重要遊戲加入線上驗證清單，可即時撤銷存取

---

## 快速部署清單

- [ ] 產生 `scripts/secrets_config.py` 中所有金鑰（含 `CDN_UPLOAD_URL`、`CDN_UPLOAD_TOKEN`）
- [ ] 部署 Flask API 伺服器並設定環境變數
- [ ] 用 CI 或本機編譯三個 DLL
- [ ] 填入 `loader.cpp` 的 AES XOR 金鑰片段
- [ ] 修改 `loader.cpp` 的 `VERSION_URL` 為你的網域
- [ ] 修改 `manifests/version.json` 的所有 URL
- [ ] 修改 `activate-cdk.ps1` 的 `$CDN_BASE`
- [ ] 重新編譯 `xinput1_4.dll`（含金鑰和 URL 變更）
- [ ] 執行 `python3 scripts/update_depotkeys.py` 下載最新 depot keys 並上傳
- [ ] 加密所有 payload（core、auth、version）並部署到 CDN
- [ ] 設定 crontab 排程：每天自動執行 `update_depotkeys.py`
- [ ] 產生 LY 金鑰並加入 DB
- [ ] 測試端到端流程
