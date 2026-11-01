# OpenSteamUnlocker — 自製 SteamTools 計畫書

> 撰寫日期：2026-11-01  
> 狀態：草稿

---

## 一、目標

打造一套完全自主的 Steam 遊戲解鎖工具，純 DLL 架構，不需要任何管理 UI。功能：

- **授權金鑰系統**（`LY-XXXXX-XXXXX-XXXXX-XXXXX`，手動發行，離線驗證）
- **遊戲擁有權偽造**（`BIsSubscribedApp` hook）
- **Depot / Manifest 注入**（離線 manifest 安裝）
- **DLC 解鎖**（depot hook）

---

## 二、系統架構

```
我們（手動）
  └─ keygen.py → 生成 LY-XXXXX-XXXXX-XXXXX-XXXXX → 送給使用者

使用者執行 irm ... | iex
  ├─ 輸入 LY 金鑰
  ├─ ps1 離線 HMAC 驗證
  ├─ 寫入 HKCU\Software\OSU\LicenseKey
  ├─ 複製 xinput1_4.dll + OSU_Core.dll 至 Steam 目錄
  └─ 寫入 applist.json（解鎖清單）

Steam.exe 啟動
  └─ xinput1_4.dll（DLL 劫持）
       └─ LoadLibrary("OSU_Core.dll")
            ├─ 讀 HKCU\Software\OSU\LicenseKey
            ├─ HMAC 驗證 → 失敗則靜默退出
            ├─ Hook BIsSubscribedApp → 讀 applist.json
            └─ Hook GetManifest     → 讀本地 depotcache
```

---

## 三、元件清單

| 元件 | 語言 | 職責 |
|------|------|------|
| `keygen.py` | Python | 我們用來手動生成 LY 金鑰 |
| `xinput1_4.dll` | C++ | DLL 劫持入口，轉發 xinput exports，載入 Core |
| `OSU_Core.dll` | C++ | 授權驗證、Steam hook、manifest 注入 |
| `activate-cdk.ps1` | PowerShell | 一鍵部署：驗證金鑰、寫設定、部署 DLL、啟動 Steam |

---

## 四、授權金鑰系統

### 4-1 金鑰格式

```
LY-XXXXX-XXXXX-XXXXX-XXXXX
   └──────────────────────┘
         20 碼（A-Z 0-9）
```

內部結構：

```
[12 碼 serial（隨機）][8 碼 check（HMAC 截斷）]
拆為 4 組 5 碼：SSSSS-SSSSS-SSHHH-HHHHH
```

### 4-2 驗證演算法

```
check = BASE36( HMAC-SHA256(SECRET, serial) )[0:8]
驗證：重算 check 並與金鑰末 8 碼比對
```

- `SECRET`：硬編碼於 ps1 + DLL（XOR 混淆拆分）
- 無需網路、無需資料庫、永久有效
- 無 HWID 綁定（可多台使用）

### 4-3 金鑰生成工具（`scripts/keygen.py`）

```python
# 用法：python keygen.py [數量]
# 輸出：LY-ABCDE-FGHIJ-KLMNO-PQRST
```

只有我們持有此腳本；使用者無法自行生成有效金鑰。

### 4-4 設定來源

```
HKCU\Software\OSU\LicenseKey   = "LY-XXXXX-XXXXX-XXXXX-XXXXX"
%APPDATA%\OSU\applist.json      = { "apps": [730, 570] }
%APPDATA%\OSU\manifests\        = *.manifest 離線文件
```

---

## 五、OSU_Core.dll 功能規劃

### 5-1 啟動流程

```
DllMain(DLL_PROCESS_ATTACH)
  1. 讀 HKCU\Software\OSU\LicenseKey
  2. HMAC 驗證 → 失敗 → return TRUE（靜默，不 crash Steam）
  3. 讀 %APPDATA%\OSU\applist.json
  4. 複製 manifests → depotcache
  5. 掛載 hooks（MinHook）
```

### 5-2 Hook 目標

從 `steamclient64.dll` pattern scan 找 vtable：

| 函式 | 行為 |
|------|------|
| `IClientUser::BIsSubscribedApp(AppId_t)` | applist.json 命中 → `true` |
| `IClientApps::BIsAppInstalled(AppId_t)` | 同上 |

Hook 框架：[MinHook](https://github.com/TsudaKageyu/minhook)（MIT）

### 5-3 Manifest 注入

1. 啟動時掃描 `%APPDATA%\OSU\manifests\*.manifest`
2. 複製至 `<SteamPath>\appcache\depotcache\`
3. Hook `GetManifestForApp` 優先讀本地

---

## 六、實作階段

### Phase 0 — 授權金鑰系統（1 天）

- [ ] `scripts/keygen.py`：HMAC-SHA256 金鑰生成
- [ ] `activate-cdk.ps1` 加入 LY 金鑰輸入 + 離線驗證
- [ ] 驗證：合法金鑰通過、偽造金鑰拒絕

### Phase 1 — xinput1_4.dll 載入器（1-2 天）

- [ ] 建立 C++ 專案（CMake + MinGW-w64 cross-compile）
- [ ] `.def` 匯出所有 xinput 函式（轉發至真實 DLL）
- [ ] `DllMain` → `LoadLibrary("OSU_Core.dll")`
- [ ] 驗證：Steam 啟動不崩潰

### Phase 2 — 授權驗證 + 基礎 Hook（2-3 天）

- [ ] `OSU_Core.dll` 讀 registry LicenseKey + HMAC 驗證
- [ ] 整合 MinHook
- [ ] Pattern scan `steamclient64.dll`，掛鉤 `BIsSubscribedApp`
- [ ] 驗證：Steam 圖書館顯示目標遊戲

### Phase 3 — Manifest 注入（3-5 天）

- [ ] 解析 `.manifest` VDF protobuf 格式
- [ ] 啟動時自動複製至 `depotcache/`
- [ ] Hook `GetManifestForApp` 優先讀本地
- [ ] 驗證：離線遊戲可啟動

### Phase 4 — 打包部署（1 天）

- [ ] GitHub Actions：MinGW cross-compile → 輸出 zip
- [ ] `activate-cdk.ps1` 下載並部署最新 zip
- [ ] 版本加密（AES-CBC-zlib，沿用現有格式）

---

## 七、技術選型

| 面向 | 選擇 | 理由 |
|------|------|-------|
| 金鑰驗證 | HMAC-SHA256 | 離線可驗、無需 DB、Python/C++ 都容易實作 |
| Native DLL | C++ 17 | ABI 相容、MinHook 生態 |
| Cross-compile | MinGW-w64 | Linux CI 可直接 build |
| Hook 框架 | MinHook | 單一 .lib，MIT，成熟 |
| JSON | nlohmann/json | header-only，無依賴 |
| 設定 | Registry + JSON 檔 | 輕量，ps1 容易讀寫 |

---

## 八、已知風險

| 風險 | 嚴重度 | 對策 |
|------|--------|------|
| DLL 被逆向、SECRET 被提取 | 中 | XOR 拆分混淆；金鑰免費發，逆向誘因低 |
| Steam 更新改變 vtable 偏移 | 高 | pattern scan，不用固定偏移 |
| VAC 偵測 hook | 高 | 僅非 VAC 遊戲；VAC 遊戲跳過 hook |
| AV 誤報 | 高 | Defender 排除（已有）；長期做代碼簽章 |

---

## 九、里程碑

```
Day 1-2   [Phase 0]    keygen.py + ps1 金鑰驗證
Day 3-5   [Phase 1+2]  DLL 載入 + hook + 授權驗證
Week 2    [Phase 3]    Manifest 注入可用
Week 3    [Phase 4]    CI 打包 + ps1 整合
```
