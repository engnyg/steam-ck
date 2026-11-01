# OpenSteamUnlocker — 自製 SteamTools 計畫書

> 撰寫日期：2026-11-01  
> 狀態：草稿

---

## 一、目標

打造一套完全自主的 Steam 遊戲解鎖工具，純 DLL 架構，不需要任何管理 UI。功能：

- **授權金鑰系統**（`LY-XXXXX-XXXXX-XXXXX-XXXXX`，手動發行，離線驗證）
- **遊戲擁有權偽造**（`BIsSubscribedApp` hook）
- **Depot Key 注入**（從我們的伺服器動態下載）
- **Manifest 注入**（從我們的伺服器動態下載）
- **核心模組每次啟動從伺服器下載解密**（永不落地）

---

## 二、完整解鎖技術鏈

| 需求 | 我們的替代 | 來源 |
|------|-----------|------|
| 擁有遊戲 | `BIsSubscribedApp` hook | OSU_Core（in-memory） |
| 遊戲文件清單 | `.manifest` 注入 depotcache | 我們的伺服器 |
| 解密遊戲文件 | `GetDepotDecryptionKey` hook | 我們的伺服器 |

---

## 三、系統架構

```
我們的伺服器 (ourdomain.com)
  ├─ /Core           → AES-CBC 加密的 OSU_Core.dll
  ├─ /depotkeys      → AES-CBC 加密的 depotkeys.json
  └─ /manifests/{id} → AES-CBC 加密的 .manifest 文件

使用者執行 irm ... | iex
  ├─ 輸入 LY 金鑰
  ├─ ps1 離線 HMAC 驗證
  ├─ 寫入 HKCU\Software\OSU\LicenseKey
  ├─ 寫入 %APPDATA%\OSU\applist.json（解鎖清單）
  ├─ 複製 xinput1_4.dll 至 Steam 目錄  ← 唯一靜態部署的檔案
  └─ 啟動 Steam

Steam.exe 啟動
  └─ xinput1_4.dll（DLL 劫持，靜態部署）
       ├─ GET ourdomain.com/Core
       ├─ AES-CBC 解密（金鑰硬編碼於 xinput1_4.dll）
       ├─ 記憶體載入 PE（reflective load，永不落地）
       └─ 呼叫 OSU_Core 初始化函式
            ├─ 讀 LicenseKey → HMAC 驗證 → 失敗靜默退出
            ├─ GET ourdomain.com/depotkeys → 解密 → 快取
            ├─ 複製 manifests → depotcache/
            ├─ Hook: BIsSubscribedApp       → applist 命中 → true
            └─ Hook: GetDepotDecryptionKey  → depotkeys 命中 → hex key
```

---

## 四、元件清單

| 元件 | 語言 | 部署方式 | 職責 |
|------|------|----------|------|
| `keygen.py` | Python | 只有我們持有 | 手動生成 LY 金鑰 |
| `xinput1_4.dll` | C++ | 靜態部署至 Steam 目錄 | 劫持入口、下載解密載入 Core |
| `OSU_Core.dll` | C++ | **記憶體載入，永不落地** | 授權驗證、hook、manifest 注入 |
| 伺服器 `/Core` | — | 我們的 domain | AES 加密的 OSU_Core.dll |
| 伺服器 `/depotkeys` | — | 我們的 domain | AES 加密的 depotkeys.json |
| `activate-cdk.ps1` | PowerShell | `irm \| iex` | 驗證金鑰、部署 xinput1_4.dll、啟動 Steam |

---

## 五、授權金鑰系統

### 5-1 金鑰格式

```
LY-XXXXX-XXXXX-XXXXX-XXXXX  （20 碼，A-Z 0-9，4 組 5 碼）
   [12 碼 serial][8 碼 HMAC check]
```

### 5-2 驗證演算法

```
check = BASE36( HMAC-SHA256(SECRET, serial) )[0:8]
```

- `SECRET` 硬編碼於 ps1 + xinput1_4.dll（XOR 拆分混淆）
- 離線可驗、無需資料庫、永久有效、無 HWID 綁定

### 5-3 驗證流程

```
ps1 → 使用者輸入 LY 金鑰 → 離線 HMAC 驗證
    → 通過 → 寫 HKCU\Software\OSU\LicenseKey
    → 失敗 → 中止，顯示錯誤

xinput1_4.dll 載入 OSU_Core（in-memory）
OSU_Core → 讀 registry → HMAC 再次驗證
         → 失敗 → 靜默 return（不 crash Steam）
```

---

## 六、加密格式（沿用 SteamTools 規格）

```
[ IV 16 bytes ][ uint32_LE 解壓後長度 4 bytes ][ zlib 壓縮資料 ][ PKCS7 padding ]
```

- 演算法：AES-256-CBC + zlib + PKCS7
- 加密金鑰：32 bytes，硬編碼於 xinput1_4.dll（XOR 混淆）
- 伺服器端用 `scripts/encrypt_payload.py` 加密後上傳

---

## 七、xinput1_4.dll 技術規劃

### 7-1 xinput export 轉發

```cpp
// xinput1_4.def — 全部轉發至真實 DLL
EXPORTS
  XInputGetState          = real_xinput.XInputGetState
  XInputSetState          = real_xinput.XInputSetState
  XInputGetCapabilities   = real_xinput.XInputGetCapabilities
  ...
```

### 7-2 Core 下載與記憶體載入

```
DllMain(DLL_PROCESS_ATTACH)
  1. WinHTTP GET https://ourdomain.com/Core
  2. AES-CBC 解密 → zlib 解壓 → PE bytes in memory
  3. Manual PE mapping（reflective load）：
     a. VirtualAlloc 分配空間
     b. 複製 PE headers + sections
     c. 修正 relocations
     d. 解析並填充 IAT（imports）
     e. 設定 section 記憶體保護
     f. 呼叫 DllMain(DLL_PROCESS_ATTACH)
  4. GetProcAddress("OSU_Init") → call
```

---

## 八、OSU_Core.dll 功能規劃

### 8-1 啟動流程（OSU_Init）

```
1. 讀 HKCU\Software\OSU\LicenseKey → HMAC 驗證
2. WinHTTP GET ourdomain.com/depotkeys → AES 解密 → 快取至 %TEMP%\osu_dk
3. 讀 %APPDATA%\OSU\applist.json
4. 掃描 %APPDATA%\OSU\manifests\ → 複製至 depotcache/
5. 掛載 MinHook
```

### 8-2 Hook 目標

| Hook | 函式 | 行為 |
|------|------|------|
| H1 | `IClientUser::BIsSubscribedApp(AppId_t)` | applist.json 命中 → `true` |
| H2 | `IClientUser::GetDepotDecryptionKey(DepotId_t, …)` | depotkeys 命中 → hex key |

vtable 偏移透過 pattern scan `steamclient64.dll` 取得，不用固定偏移。

---

## 九、伺服器端工具（`scripts/`）

| 腳本 | 功能 |
|------|------|
| `keygen.py` | 生成 LY 金鑰 |
| `encrypt_payload.py` | AES-CBC-zlib 加密任意 payload |
| `update_core.sh` | 加密 OSU_Core.dll 並上傳至伺服器 |
| `update_depotkeys.sh` | 抓 ManifestHub → 加密 → 上傳 |

---

## 十、實作階段

### Phase 0 — 授權 + 伺服器工具（1-2 天）

- [ ] `scripts/keygen.py`：LY 金鑰生成
- [ ] `scripts/encrypt_payload.py`：AES-CBC-zlib 加密工具
- [ ] `activate-cdk.ps1` 加入 LY 金鑰輸入 + 離線驗證
- [ ] 設定伺服器端點（domain + 上傳流程）

### Phase 1 — xinput1_4.dll（2-3 天）

- [ ] CMake + MinGW-w64 專案
- [ ] xinput export 轉發（`.def`）
- [ ] WinHTTP 下載 + AES-CBC 解密
- [ ] Manual PE loader（reflective load）
- [ ] 驗證：Steam 啟動不崩潰、Core 成功載入

### Phase 2 — OSU_Core.dll 授權 + Hook（3-4 天）

- [ ] `OSU_Init`：HMAC 驗證 + depotkeys 下載
- [ ] MinHook + nlohmann/json
- [ ] Pattern scan → Hook H1 `BIsSubscribedApp`
- [ ] Pattern scan → Hook H2 `GetDepotDecryptionKey`
- [ ] 驗證：圖書館顯示遊戲、depot key 正確注入

### Phase 3 — Manifest 注入（2-3 天）

- [ ] 啟動時複製 manifests → depotcache
- [ ] `update_depotkeys.sh`：ManifestHub → 加密 → 上傳
- [ ] 驗證：遊戲可下載、可啟動

### Phase 4 — 打包部署（1 天）

- [ ] GitHub Actions：build xinput1_4.dll → 加密 → 上傳伺服器
- [ ] `activate-cdk.ps1` 只部署 xinput1_4.dll（Core 由 DLL 自行拉取）

---

## 十一、技術選型

| 面向 | 選擇 | 理由 |
|------|------|-------|
| 網路請求 | WinHTTP | Windows 內建，無額外依賴 |
| 記憶體載入 | Manual PE mapping | Core 永不落地，難以提取 |
| 加密格式 | AES-256-CBC + zlib | 與 SteamTools 相同，已有解密工具 |
| Hook 框架 | MinHook | MIT，成熟穩定 |
| JSON | nlohmann/json | header-only |
| 金鑰驗證 | HMAC-SHA256 | 離線可驗、無需 DB |
| Cross-compile | MinGW-w64 | Linux CI 可 build |

---

## 十二、已知風險

| 風險 | 嚴重度 | 對策 |
|------|--------|------|
| 伺服器下線 → Core 無法載入 | 高 | 快取上次成功的加密 Core 於本地 |
| 記憶體載入被 AV 誤報 | 高 | Defender 排除；xinput1_4.dll 代碼簽章（長期） |
| AES 金鑰從 xinput1_4.dll 提取 | 中 | XOR 拆分混淆；Core 每版更換金鑰 |
| Steam 更新改變 vtable 偏移 | 高 | Pattern scan；Core 可獨立更新 |
| VAC 偵測 hook | 高 | 非 VAC 遊戲才掛 hook |

---

## 十三、里程碑

```
Day 1-2   [Phase 0]    keygen + encrypt 工具 + ps1 驗證
Day 3-5   [Phase 1]    xinput1_4.dll（下載 + 記憶體載入）
Day 6-9   [Phase 2]    OSU_Core.dll hook × 2
Week 2    [Phase 3]    Manifest + depotkeys 伺服器整合
Week 3    [Phase 4]    CI 自動 build + 上傳
```
