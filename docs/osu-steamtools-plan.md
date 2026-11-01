# OpenSteamUnlocker — 自製 SteamTools 計畫書

> 撰寫日期：2026-11-01  
> 狀態：草稿

---

## 一、目標

完整模仿 SteamTools 架構，打造自主可控的 Steam 遊戲解鎖工具。

| SteamTools | 我們 |
|------------|------|
| `xinput1_4.dll` | `xinput1_4.dll`（自製） |
| `Core.dll` | `OSU_Core.dll` |
| `Authorization.dll` | `OSU_Auth.dll` |
| `cardx.tnkjmec.com` CDK 驗證 | 我們的 API 伺服器 |
| `update.wudrm.com/Core` | 我們的 CDN |
| 版本 manifest（AES 加密） | 相同格式 |
| `<mCode>` 機器碼快取 | 相同機制 |
| `AppIdInOnline` 線上驗證 | 相同機制 |

---

## 二、系統架構

```
我們的伺服器
  ├─ /version             AES 加密版本 manifest（JSON）
  ├─ /core                AES 加密 OSU_Core.dll
  ├─ /auth                AES 加密 OSU_Auth.dll
  ├─ /depotkeys           AES 加密 depotkeys.json
  ├─ /api/verify_cdkey    LY 金鑰驗證 API
  └─ /api/check_app       線上遊戲驗證 API

我們（手動）
  └─ keygen.py → 生成 LY-XXXXX-XXXXX-XXXXX-XXXXX → 寫入伺服器 DB → 送給使用者

activate-cdk.ps1（irm ... | iex）
  ├─ 部署 xinput1_4.dll 至 Steam 目錄（唯一靜態部署）
  ├─ 寫入 HKCU\Software\OSU\CDKey = "LY-XXXXX-XXXXX-XXXXX-XXXXX"
  ├─ 寫入 %APPDATA%\OSU\applist.json
  └─ 啟動 Steam

Steam.exe 啟動
  └─ xinput1_4.dll（DLL 劫持）
       ├─ 計算 <mCode>（機器碼雜湊）
       ├─ GET /version → AES 解密 → 取得 manifest
       ├─ 比對 manifest 中各檔案 hash：
       │    hash 相同 → 從 appcache/httpcache/3b/<mCode> 載入
       │    hash 不同 → 重新下載 → 存回 → 載入
       ├─ 記憶體載入 OSU_Core.dll
       └─ 記憶體載入 OSU_Auth.dll

OSU_Auth.dll（授權驗證）
  ├─ 讀 HKCU\Software\OSU\CDKey
  ├─ POST /api/verify_cdkey { key, timestamp, sig }
  │    有網路 → 伺服器驗證 → 快取結果至本地
  │    無網路 → 讀本地快取（HMAC 離線驗證備援）
  └─ 回傳 { valid, features[], online_apps[] }

OSU_Core.dll（Steam 功能核心）
  ├─ 從 OSU_Auth 取得授權資訊
  ├─ 載入 depotkeys.json
  ├─ 複製 manifests → depotcache/
  ├─ Hook: BIsSubscribedApp(AppId)
  │    AppIdInOnline 內的 AppId → POST /api/check_app 驗證
  │    其餘 → 查 applist.json
  └─ Hook: GetDepotDecryptionKey(DepotId) → 查 depotkeys
```

---

## 三、元件清單

| 元件 | 語言 | 儲存位置 | 職責 |
|------|------|----------|------|
| `keygen.py` | Python | 只有我們持有 | 生成 LY 金鑰，寫入伺服器 DB |
| `xinput1_4.dll` | C++ | Steam 目錄（靜態） | 版本檢查、下載更新、載入 Core+Auth |
| `OSU_Auth.dll` | C++ | `appcache/httpcache/3a/<mCode>` | 線上/離線 LY 金鑰驗證 |
| `OSU_Core.dll` | C++ | `appcache/httpcache/3b/<mCode>` | Steam hook、manifest 注入 |
| API 伺服器 | Python/Node | VPS | 金鑰驗證、APP 線上驗證 |
| `activate-cdk.ps1` | PowerShell | `irm \| iex` | 部署 xinput1_4.dll、寫設定 |

---

## 四、版本 Manifest 格式（對齊 SteamTools）

```json
{
  "Version": 1,
  "VersionTxt": "1.0 正式版",
  "urls": [
    "https://api.ourdomain.com"
  ],
  "Hash": "<sha1_of_this_manifest>",
  "CardUrl": "https://api.ourdomain.com/verify_cdkey",
  "AppIdInOnlineEnable": true,
  "AppIdInOnline": "730,570,1172470",
  "WindowsFile64Stable": [
    {
      "FileName": "appcache/httpcache/3b/<mCode>",
      "URL": "https://cdn.ourdomain.com/core",
      "Hash": "<md5>",
      "default": 0
    },
    {
      "FileName": "appcache/httpcache/3a/<mCode>",
      "URL": "https://cdn.ourdomain.com/auth",
      "Hash": "<md5>",
      "default": 0
    },
    {
      "FileName": "appcache/httpcache/depot/<mCode>",
      "URL": "https://cdn.ourdomain.com/depotkeys",
      "Hash": "<md5>",
      "default": 0
    }
  ]
}
```

加密方式：`AES-256-CBC + zlib`（與 SteamTools 相同格式）

---

## 五、LY 金鑰系統

### 5-1 金鑰格式

```
LY-XXXXX-XXXXX-XXXXX-XXXXX  （20 碼，A-Z 0-9，4 組 5 碼）
   [12 碼 serial][8 碼 HMAC check]
```

### 5-2 雙層驗證

**線上驗證（主要）：**
```
POST /api/verify_cdkey
Body: { "key": "LY-...", "ts": 1234567890, "sig": HMAC(key+ts, secret) }
回應: { "valid": true, "features": ["unlock", "depot"], "online_apps": [730, 570] }
```

**離線備援（無網路時）：**
```
本地快取上次驗證結果（AES 加密存於 registry）
+ HMAC 離線二次確認（防竄改）
快取有效期：7 天
```

### 5-3 keygen.py 流程

```
python keygen.py [數量]
  → 生成 LY-... 金鑰
  → POST /api/admin/add_key { key, note }  （寫入伺服器 DB）
  → 輸出金鑰清單
```

---

## 六、API 伺服器設計

### 端點

| 端點 | 方法 | 用途 |
|------|------|------|
| `/api/verify_cdkey` | POST | LY 金鑰驗證 |
| `/api/check_app` | POST | 線上遊戲驗證（AppIdInOnline） |
| `/api/admin/add_key` | POST | 新增金鑰（管理員，需 admin token） |
| `/api/admin/revoke_key` | POST | 撤銷金鑰 |

### 資料庫結構（SQLite）

```sql
CREATE TABLE keys (
    key         TEXT PRIMARY KEY,   -- LY-XXXXX-XXXXX-XXXXX-XXXXX
    created_at  INTEGER,
    note        TEXT,               -- 備註（送給誰）
    revoked     INTEGER DEFAULT 0
);

CREATE TABLE verify_log (
    key         TEXT,
    timestamp   INTEGER,
    ip          TEXT
);
```

---

## 七、`<mCode>` 機器碼

用途：**隱藏快取檔名**（讓 Core.dll 不顯眼），不做 HWID 授權綁定。

```
mCode = SHA256( MachineGuid + ComputerName )[0:16]  → 32 hex 字元
```

- `MachineGuid`：`HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`
- 結果作為 httpcache 目錄下的檔名（`appcache/httpcache/3b/<mCode>`）

---

## 八、OSU_Core.dll Hook 規劃

| Hook | 函式 | 行為 |
|------|------|------|
| H1 | `IClientUser::BIsSubscribedApp(AppId_t)` | AppIdInOnline → 打 API；其餘查 applist.json |
| H2 | `IClientUser::GetDepotDecryptionKey(DepotId_t)` | 查 depotkeys.json → 回傳 hex key |

vtable 偏移：pattern scan `steamclient64.dll`，不用固定偏移。

---

## 九、加密格式（全系統統一）

```
[ IV 16 bytes ][ uint32_LE 解壓後長度 ][ zlib 壓縮資料 ][ PKCS7 padding ]
```

演算法：AES-256-CBC  
各元件使用獨立金鑰（硬編碼於 xinput1_4.dll，XOR 混淆）：

| 用途 | 金鑰 |
|------|------|
| 版本 manifest | `KEY_VERSION` |
| OSU_Core.dll | `KEY_CORE` |
| OSU_Auth.dll | `KEY_AUTH` |
| depotkeys.json | `KEY_DEPOT` |

---

## 十、實作階段

### Phase 0 — 伺服器 + 金鑰系統（2-3 天）

- [ ] SQLite DB + `/api/verify_cdkey` + `/api/check_app`
- [ ] `/api/admin/add_key` + `/api/admin/revoke_key`
- [ ] `keygen.py`：生成 LY 金鑰 + 寫入伺服器 DB
- [ ] `encrypt_payload.py`：AES-CBC-zlib 加密工具
- [ ] 版本 manifest JSON 範本 + 加密上傳腳本

### Phase 1 — xinput1_4.dll（3-4 天）

- [ ] CMake + MinGW-w64 專案
- [ ] xinput export 轉發（`.def`）
- [ ] `<mCode>` 計算（SHA256 機器碼）
- [ ] WinHTTP GET /version → AES 解密 → 解析 manifest
- [ ] Hash 比對 → 按需下載各元件
- [ ] 儲存至 `appcache/httpcache/3[ab]/<mCode>`
- [ ] 記憶體載入 OSU_Core.dll + OSU_Auth.dll

### Phase 2 — OSU_Auth.dll（2-3 天）

- [ ] 讀 registry CDKey
- [ ] WinHTTP POST /api/verify_cdkey
- [ ] 本地快取（AES 加密，7 天有效）
- [ ] 離線 HMAC 備援驗證

### Phase 3 — OSU_Core.dll（3-4 天）

- [ ] MinHook + nlohmann/json
- [ ] 載入 depotkeys.json
- [ ] Pattern scan → Hook H1 `BIsSubscribedApp`（含線上驗證分支）
- [ ] Pattern scan → Hook H2 `GetDepotDecryptionKey`
- [ ] manifest 注入 depotcache

### Phase 4 — activate-cdk.ps1 整合（1 天）

- [ ] 部署 xinput1_4.dll
- [ ] 寫 CDKey 至 registry
- [ ] 寫 applist.json
- [ ] 啟動 Steam

### Phase 5 — CI / 打包（1-2 天）

- [ ] GitHub Actions：MinGW build → 加密 → 上傳伺服器
- [ ] 版本 manifest 自動更新 hash

---

## 十一、技術選型

| 面向 | 選擇 |
|------|------|
| API 伺服器 | Python Flask + SQLite |
| Native DLL | C++ 17 + MinGW-w64 |
| Hook 框架 | MinHook |
| JSON | nlohmann/json |
| 網路 | WinHTTP |
| 加密 | AES-256-CBC + zlib（全系統統一） |
| 機器碼 | SHA256(MachineGuid + ComputerName) |

---

## 十二、已知風險

| 風險 | 嚴重度 | 對策 |
|------|--------|------|
| 伺服器下線 | 高 | 離線快取 7 天備援 |
| AES 金鑰被提取 | 中 | XOR 混淆；各元件獨立金鑰 |
| Steam vtable 偏移變動 | 高 | Pattern scan |
| VAC 偵測 | 高 | 非 VAC 遊戲才掛 hook |
| AV 誤報 | 高 | Defender 排除；長期代碼簽章 |

---

## 十三、里程碑

```
Day 1-3   [Phase 0]  API 伺服器 + keygen + 加密工具
Day 4-7   [Phase 1]  xinput1_4.dll（版本更新 + 載入）
Day 8-10  [Phase 2]  OSU_Auth.dll（線上/離線驗證）
Day 11-14 [Phase 3]  OSU_Core.dll（hook + manifest）
Day 15    [Phase 4]  ps1 整合
Day 16-17 [Phase 5]  CI 打包
```
