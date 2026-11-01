# OpenSteamUnlocker — 自製 SteamTools 計畫書

> 撰寫日期：2026-11-01  
> 狀態：草稿

---

## 一、目標

打造一套完全自主的 Steam 遊戲解鎖工具，純 DLL 架構，不需要任何管理 UI。功能：

- **授權金鑰系統**（`LY-XXXXX-XXXXX-XXXXX-XXXXX`，手動發行，離線驗證）
- **遊戲擁有權偽造**（`BIsSubscribedApp` hook）
- **Depot Key 注入**（從 ManifestHub 公開 repo 取得，攔截 Steam 查詢）
- **Manifest 注入**（離線 manifest 安裝至 depotcache）
- **DLC 解鎖**（depot hook）

---

## 二、完整解鎖技術鏈

Steam 讓遊戲可玩需要三樣東西，我們全部提供：

| 需求 | Steam 原始做法 | 我們的替代 | 狀態 |
|------|----------------|-----------|------|
| 擁有遊戲 | Steam 帳號購買記錄 | `BIsSubscribedApp` hook | 計畫中 |
| 知道遊戲文件清單 | Steam CDN manifest | `.manifest` 注入 depotcache | 計畫中 |
| 解密下載的遊戲文件 | Steam CM 頒發 depot key | ManifestHub 公開 key 資料庫 | **已有 C# 實作** |

---

## 三、系統架構

```
我們（手動）
  └─ keygen.py → 生成 LY-XXXXX-XXXXX-XXXXX-XXXXX → 送給使用者

使用者執行 irm ... | iex
  ├─ 輸入 LY 金鑰
  ├─ ps1 離線 HMAC 驗證
  ├─ 下載 depotkeys.json（ManifestHub）→ %LOCALAPPDATA%\OSU\cache\
  ├─ 下載 manifests → %APPDATA%\OSU\manifests\
  ├─ 寫入 HKCU\Software\OSU\LicenseKey
  ├─ 寫入 %APPDATA%\OSU\applist.json
  ├─ 複製 xinput1_4.dll + OSU_Core.dll 至 Steam 目錄
  └─ 啟動 Steam

Steam.exe 啟動
  └─ xinput1_4.dll（DLL 劫持）
       └─ LoadLibrary("OSU_Core.dll")
            ├─ 讀 LicenseKey → HMAC 驗證 → 失敗則靜默退出
            ├─ 讀 applist.json + depotkeys.json
            ├─ 複製 manifests → depotcache/
            ├─ Hook: BIsSubscribedApp(AppId)       → applist 命中 → true
            └─ Hook: GetDepotDecryptionKey(DepotId) → depotkeys 命中 → hex key

Steam 正常向 CDN 下載遊戲文件並解密 ✓
```

---

## 四、元件清單

| 元件 | 語言 | 職責 |
|------|------|------|
| `keygen.py` | Python | 手動生成 LY 金鑰（只有我們持有） |
| `xinput1_4.dll` | C++ | DLL 劫持入口，轉發 xinput exports，載入 Core |
| `OSU_Core.dll` | C++ | 授權驗證、Steam hook × 2、manifest 注入 |
| `activate-cdk.ps1` | PowerShell | 一鍵部署：驗證金鑰、下載資料、部署 DLL、啟動 Steam |

---

## 五、授權金鑰系統

### 5-1 金鑰格式

```
LY-XXXXX-XXXXX-XXXXX-XXXXX
   └──────────────────────┘
         20 碼（A-Z 0-9）
```

內部結構：

```
[12 碼 serial（隨機）][8 碼 check（HMAC 截斷）]
拆為 4 組 5 碼顯示
```

### 5-2 驗證演算法

```
check = BASE36( HMAC-SHA256(SECRET, serial) )[0:8]
驗證：重算 check 並與金鑰末 8 碼比對
```

- `SECRET` 硬編碼於 ps1 + DLL（XOR 拆分混淆）
- 無需網路、無需資料庫、永久有效、無 HWID 綁定

### 5-3 驗證流程

```
activate-cdk.ps1 → 使用者輸入 LY 金鑰
  → 離線 HMAC 驗證（合法 → 繼續；偽造 → 中止）
  → 寫入 HKCU\Software\OSU\LicenseKey

OSU_Core.dll 啟動
  → 讀 registry → HMAC 再次驗證
  → 失敗 → return TRUE 靜默退出（不 crash Steam）
```

---

## 六、OSU_Core.dll 功能規劃

### 6-1 啟動流程

```
DllMain(DLL_PROCESS_ATTACH)
  1. 讀 HKCU\Software\OSU\LicenseKey → HMAC 驗證
  2. 載入 %APPDATA%\OSU\applist.json
  3. 載入 %LOCALAPPDATA%\OSU\cache\depotkeys.json
  4. 掃描 %APPDATA%\OSU\manifests\ → 複製至 depotcache/
  5. 掛載 hooks（MinHook）
```

### 6-2 Hook 目標

從 `steamclient64.dll` pattern scan 找 vtable：

| Hook | 函式 | 行為 |
|------|------|------|
| H1 | `IClientUser::BIsSubscribedApp(AppId_t)` | applist.json 命中 → `true` |
| H2 | `IClientUser::GetDepotDecryptionKey(DepotId_t, …)` | depotkeys.json 命中 → hex key |

Hook 框架：[MinHook](https://github.com/TsudaKageyu/minhook)（MIT）

### 6-3 Manifest 注入

- 掃描 `%APPDATA%\OSU\manifests\*.manifest`
- 複製至 `<SteamPath>\appcache\depotcache\`
- DLL 啟動時執行，無需 hook（Steam 自然讀取）

---

## 七、Depot Key 資料庫

### 來源（現有 C# 實作，搬移至 ps1）

```
① https://api.993499094.xyz/depotkeys.json
② https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json
③ https://raw.githubusercontent.com/SSMGAlt/ManifestHub2/main/depotkeys.json
```

- 格式：`{ "depotId": "hexKey", … }`（數萬條）
- ps1 下載並快取至 `%LOCALAPPDATA%\OSU\cache\depotkeys.json`
- OSU_Core.dll 啟動時直接讀取，無需網路

### 與 SteamTools 的比較

| | SteamTools | 我們 |
|--|--|--|
| Depot key 來源 | Steam CM（需擁有或 CDK） | ManifestHub 公開 repo |
| 更新 | 即時 | 跟隨 repo 更新 |
| 覆蓋率 | 完整 | 熱門遊戲為主（足夠） |

---

## 八、實作階段

### Phase 0 — 授權金鑰系統（1 天）

- [ ] `scripts/keygen.py`：HMAC-SHA256 金鑰生成
- [ ] `activate-cdk.ps1` 加入 LY 金鑰輸入 + 離線驗證
- [ ] 驗證：合法金鑰通過、偽造金鑰拒絕

### Phase 1 — xinput1_4.dll 載入器（1-2 天）

- [ ] 建立 C++ 專案（CMake + MinGW-w64 cross-compile）
- [ ] `.def` 匯出所有 xinput 函式（轉發至真實 DLL）
- [ ] `DllMain` → `LoadLibrary("OSU_Core.dll")`
- [ ] 驗證：Steam 啟動不崩潰

### Phase 2 — OSU_Core.dll 授權 + Hook（3-4 天）

- [ ] 讀 registry LicenseKey + HMAC 驗證
- [ ] 整合 MinHook + nlohmann/json
- [ ] Pattern scan `steamclient64.dll` → Hook H1 `BIsSubscribedApp`
- [ ] Pattern scan → Hook H2 `GetDepotDecryptionKey`
- [ ] 驗證：Steam 圖書館顯示目標遊戲、depot key 正確注入

### Phase 3 — Manifest 注入（2-3 天）

- [ ] 啟動時掃描 `%APPDATA%\OSU\manifests\` 複製至 depotcache
- [ ] ps1：下載 depotkeys.json + manifests（沿用現有 C# 邏輯改寫）
- [ ] 驗證：離線遊戲可啟動、下載完整

### Phase 4 — 打包部署（1 天）

- [ ] GitHub Actions：MinGW cross-compile → 輸出 zip
- [ ] `activate-cdk.ps1` 下載並部署最新 zip
- [ ] 版本加密（AES-CBC-zlib，沿用現有格式）

---

## 九、技術選型

| 面向 | 選擇 | 理由 |
|------|------|-------|
| 金鑰驗證 | HMAC-SHA256 | 離線可驗、無需 DB |
| Native DLL | C++ 17 | ABI 相容、MinHook 生態 |
| Cross-compile | MinGW-w64 | Linux CI 可直接 build |
| Hook 框架 | MinHook | MIT，成熟穩定 |
| JSON | nlohmann/json | header-only，無依賴 |
| Depot keys | ManifestHub 公開 repo | 數萬條，足夠覆蓋熱門遊戲 |
| 設定 | Registry + JSON 檔 | 輕量，ps1 容易讀寫 |

---

## 十、已知風險

| 風險 | 嚴重度 | 對策 |
|------|--------|------|
| DLL 被逆向、SECRET 被提取 | 中 | XOR 拆分混淆；金鑰免費發，逆向誘因低 |
| Steam 更新改變 vtable 偏移 | 高 | pattern scan，不用固定偏移 |
| VAC 偵測 hook | 高 | 僅非 VAC 遊戲；VAC 遊戲跳過 hook |
| ManifestHub repo 被下架 | 中 | 多個備援 URL；可快取舊版本 |
| AV 誤報 | 高 | Defender 排除（已有）；長期做代碼簽章 |

---

## 十一、里程碑

```
Day 1     [Phase 0]    keygen.py + ps1 LY 金鑰驗證
Day 2-4   [Phase 1]    xinput1_4.dll 載入器
Day 5-8   [Phase 2]    OSU_Core.dll hook × 2 + 授權驗證
Week 2    [Phase 3]    Manifest + depot key 注入完整
Week 3    [Phase 4]    CI 打包 + ps1 整合
```
