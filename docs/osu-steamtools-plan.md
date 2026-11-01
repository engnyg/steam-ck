# OpenSteamUnlocker — 自製 SteamTools 計畫書

> 撰寫日期：2026-11-01  
> 狀態：草稿

---

## 一、目標

打造一套完全自主的 Steam 遊戲解鎖工具，純 DLL 架構，不需要任何管理 UI。功能：

- **遊戲擁有權偽造**（`BIsSubscribedApp` hook）
- **Depot / Manifest 注入**（離線 manifest 安裝）
- **DLC 解鎖**（depot hook）
- **CDK 序號啟動**（`Store.RegisterCDKey`，透過 `activate-cdk.ps1` 觸發）

---

## 二、系統架構

```
activate-cdk.ps1  (部署 + 設定)
  │ 寫入 applist.json / registry
  │ 複製 DLL 至 Steam 目錄
  ▼
Steam.exe 啟動
  │ 載入 xinput1_4.dll  (DLL 劫持)
  ▼
xinput1_4.dll  (C++，轉發 xinput export)
  └─ DllMain → LoadLibrary("OSU_Core.dll")
       ▼
OSU_Core.dll  (C++，注入 Steam 程序)
  ├─ Hook BIsSubscribedApp   → 讀 applist.json 白名單
  ├─ Hook GetManifest        → 回傳假 manifest
  └─ CDK 模式：呼叫 Steam CM RegisterCDKey
```

### 設定來源

```
%APPDATA%\OSU\applist.json     解鎖遊戲清單
HKCU\Software\Valve\Steamtools iscdkey = "true"   CDK 模式旗標
```

---

## 三、元件清單

| 元件 | 語言 | 職責 |
|------|------|------|
| `xinput1_4.dll` | C++ | DLL 劫持入口，轉發 xinput exports，載入 Core |
| `OSU_Core.dll` | C++ | Steam 函式 hook、manifest 注入、CM 通訊 |
| `activate-cdk.ps1` | PowerShell | 一鍵部署、設定 applist、啟動 CDK |

---

## 四、OSU_Core.dll 功能規劃

### 4-1 Hook 目標

從 `steamclient64.dll` pattern scan 找以下 vtable：

| 函式 | 行為 |
|------|------|
| `IClientUser::BIsSubscribedApp(AppId_t)` | 讀 `applist.json`，命中回傳 `true` |
| `IClientApps::BIsAppInstalled(AppId_t)` | 同上 |
| `IClientUser::GetSteamID()` | 透傳 |

Hook 框架：[MinHook](https://github.com/TsudaKageyu/minhook)（MIT）

### 4-2 Manifest 注入

1. 啟動時讀取 `%APPDATA%\OSU\manifests\` 目錄下的 `.manifest` 文件
2. 寫入 `<SteamPath>\appcache\depotcache\`
3. Hook `GetManifestForApp` 讓 Steam 優先讀本地 manifest

### 4-3 CDK 啟動（Steam CM 協定）

當 `iscdkey = "true"` 且 `applist.json` 含 `cdkey` 欄位時，在 Steam 登入後自動觸發：

```
TCP → CM 伺服器 (cm0.steampowered.com:27017)
  → CMsgClientRegisterKey { key: "XXXXX-XXXXX-XXXXX" }
  ← CMsgClientPurchaseResponse
```

Protobuf 定義：[SteamDatabase/Protobufs](https://github.com/SteamDatabase/Protobufs)

### 4-4 applist.json 格式

```json
{
  "apps": [730, 570, 1172470],
  "cdkey": "XXXXX-XXXXX-XXXXX"
}
```

由 `activate-cdk.ps1` 寫入；Core.dll 啟動時讀取。

---

## 五、實作階段

### Phase 1 — xinput1_4.dll 載入器（1-2 天）

- [ ] 建立 C++ 專案（CMake + MinGW-w64 cross-compile）
- [ ] `.def` 匯出所有 xinput 函式（轉發至真實 DLL）
- [ ] `DllMain` → `LoadLibrary("OSU_Core.dll")`
- [ ] 驗證：Steam 啟動不崩潰

### Phase 2 — 基礎 Hook 框架（2-3 天）

- [ ] 整合 MinHook
- [ ] 載入後 pattern scan `steamclient64.dll` vtable
- [ ] 掛鉤 `BIsSubscribedApp`，讀 `applist.json`
- [ ] 驗證：Steam 圖書館顯示目標遊戲

### Phase 3 — Manifest 注入（3-5 天）

- [ ] 解析 `.manifest` VDF protobuf 格式
- [ ] 啟動時自動複製至 `depotcache/`
- [ ] Hook `GetManifest` 優先讀本地
- [ ] 驗證：離線遊戲可啟動

### Phase 4 — CDK 自動啟動（3-5 天）

- [ ] 實作 Steam CM TCP + TLS 連線
- [ ] Protobuf 序列化 `CMsgClientRegisterKey`
- [ ] 登入後自動送出，寫入結果至 log
- [ ] 更新 `activate-cdk.ps1`：寫入 `cdkey` 至 `applist.json`

### Phase 5 — 打包部署（1 天）

- [ ] GitHub Actions：MinGW cross-compile → 輸出 zip
- [ ] `activate-cdk.ps1` 改為下載並部署最新 zip
- [ ] 版本加密（沿用 AES-CBC-zlib，金鑰存 registry）

---

## 六、技術選型

| 面向 | 選擇 | 理由 |
|------|------|-------|
| Native DLL | C++ 17 | ABI 相容、MinHook 生態 |
| Cross-compile | MinGW-w64 | Linux CI 可直接 build |
| Hook 框架 | MinHook | 單一 .lib，MIT，成熟 |
| Protobuf | protobuf-lite | Steam CM 協定 |
| JSON | nlohmann/json | header-only，無依賴 |
| 設定 | JSON 檔 + Registry | 輕量，ps1 容易讀寫 |

---

## 七、已知風險

| 風險 | 嚴重度 | 對策 |
|------|--------|------|
| Steam 更新改變 vtable 偏移 | 高 | pattern scan，不用固定偏移 |
| VAC 偵測 hook | 高 | 僅非 VAC 遊戲；VAC 遊戲跳過 hook |
| Steam CM 協定版本變更 | 中 | 追蹤 SteamDatabase/Protobufs |
| AV 誤報 | 高 | Defender 排除（已有）；長期做代碼簽章 |

---

## 八、里程碑

```
Week 1  [Phase 1+2]  DLL 載入 + BIsSubscribedApp hook
Week 2  [Phase 3]    Manifest 注入可用
Week 3  [Phase 4]    CDK 自動啟動
Week 4  [Phase 5]    CI 打包 + ps1 整合
```
