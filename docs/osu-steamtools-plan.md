# OpenSteamUnlocker — 自製 SteamTools 計畫書

> 撰寫日期：2026-11-01  
> 狀態：草稿

---

## 一、目標

打造一套完全自主的 Steam 遊戲解鎖工具，不依賴任何第三方二進位（SteamTools、GreenLuma 等），功能覆蓋：

- **CDK 序號啟動**（`Store.RegisterCDKey`）
- **遊戲擁有權偽造**（`BIsSubscribedApp`）
- **Depot / Manifest 注入**（離線 manifest 安裝）
- **DLC 解鎖**（`BIsSubscribedApp` + depot hook）
- **WPF 管理介面**（已有雛形）

---

## 二、系統架構

```
使用者
  │
  ▼
OpenSteamUnlocker.exe  (C# WPF，管理介面)
  │  WM_COPYDATA / named-pipe
  ▼
OSU_Core.dll  (C/C++，注入 Steam 程序)
  │  釣鉤 steamclient64.dll 內部函式
  ▼
Steam.exe 程序
  ├── BIsSubscribedApp  → 永遠回傳 true
  ├── GetManifest       → 回傳假 manifest
  └── RegisterCDKey     → 直接呼叫 Steam CM
```

### 載入鏈

```
xinput1_4.dll  (DLL 劫持，C++)
  └─ LoadLibrary("OSU_Core.dll")
       └─ 掛鉤 + 建立 IPC 視窗 "OSU_IPC_Class"
```

---

## 三、元件清單

| 元件 | 語言 | 職責 |
|------|------|------|
| `xinput1_4.dll` | C++ | DLL 劫持入口，轉發 xinput exports，載入 Core |
| `OSU_Core.dll` | C++ | Steam 函式 hook、IPC 視窗、CM 通訊 |
| `OpenSteamUnlocker.exe` | C# / WPF | 使用者介面、管理設定、IPC 客戶端 |
| `activate-cdk.ps1` | PowerShell | 一鍵部署腳本（`irm steamfn.com \| iex`） |

---

## 四、OSU_Core.dll 功能規劃

### 4-1 Hook 目標

從 `steamclient64.dll` 找以下函式（pattern scan）：

| 函式 | 攔截方式 | 行為 |
|------|----------|------|
| `IClientUser::BIsSubscribedApp(AppId_t)` | vtable hook | 讀本地白名單，命中則回傳 `true` |
| `IClientApps::GetCurrentGameLanguage(AppId_t)` | vtable hook | 回傳設定語言 |
| `IClientUser::GetSteamID()` | vtable hook | 透傳（不改） |
| `IClientInventory::GetResultItems` | vtable hook | 注入假物品（選用） |
| `ISteamMatchmaking::GetLobbyData` | vtable hook | 多人連線相容（選用） |

Hook 框架：[MinHook](https://github.com/TsudaKageyu/minhook)（單一標頭，MIT 授權）

### 4-2 Manifest 注入

1. 在 `appcache/depotcache/` 寫入 `.manifest` 文件  
2. Hook `GetManifestForApp` 或直接修改 `appcache/appinfo.vdf`  
3. 詳細 manifest 格式已在 `docs/steamtools-re.md` 記錄

### 4-3 CDK 啟動（Steam CM 協定）

直接實作 Steam CM protobuf 流程：

```
TCP 連線 → CM 伺服器 (cm0.steampowered.com:27017)
  → Hello
  → LogonAnonymous 或 LogonWithCredentials
  → CMsgClientRegisterKey { key: "XXXXX-XXXXX-XXXXX" }
  ← CMsgClientPurchaseResponse { purchase_result_details }
```

Protobuf 定義參考：[SteamDatabase/Protobufs](https://github.com/SteamDatabase/Protobufs)

### 4-4 IPC 協定

視窗類別：`OSU_IPC_Class`（Core.dll 在 Steam 程序中建立）

```c
// 呼叫端（OpenSteamUnlocker.exe）
COPYDATASTRUCT cds = {
    .dwData = 1,                          // 指令版本
    .cbData = (DWORD)strlen(json),
    .lpData = json                        // UTF-8 JSON
};
SendMessage(FindWindow("OSU_IPC_Class", NULL), WM_COPYDATA, 0, &cds);
```

JSON 指令格式：

```json
// CDK 啟動
{ "cmd": "ActivateCDKey", "key": "XXXXX-XXXXX-XXXXX" }

// 解鎖遊戲
{ "cmd": "UnlockApp", "appid": 730 }

// 查詢狀態
{ "cmd": "Status" }
```

回呼視窗：`OSU_CB_Class`（OpenSteamUnlocker.exe 端）

---

## 五、實作階段

### Phase 1 — xinput1_4.dll 載入器（1-2 天）

- [ ] 建立 C++ 專案（CMake + MinGW cross-compile）
- [ ] 匯出所有 xinput 函式（轉發至真實 `xinput1_4.dll`）
- [ ] `DllMain` 中 `LoadLibrary("OSU_Core.dll")`
- [ ] 驗證：Steam 啟動後不崩潰

### Phase 2 — IPC 框架（1 天）

- [ ] `OSU_Core.dll` 建立隱藏視窗 `OSU_IPC_Class`
- [ ] WndProc 解析 JSON 指令
- [ ] `Status` 指令回傳版本號
- [ ] C# WPF 端 IPC 客戶端（`SteamIpcClient.cs`）

### Phase 3 — 遊戲解鎖 Hook（3-5 天）

- [ ] 載入 `steamclient64.dll`，找 vtable 偏移（pattern scan）
- [ ] MinHook 掛鉤 `BIsSubscribedApp`
- [ ] 讀取白名單（JSON 或 SQLite，存於 `%APPDATA%\OSU\applist.json`）
- [ ] WPF 介面：新增遊戲 AppID、同步清單

### Phase 4 — Manifest 注入（3-5 天）

- [ ] 解析 `.manifest` 二進位格式（VDF protobuf）
- [ ] 從 GitHub manifest 倉庫下載對應版本
- [ ] 寫入 `depotcache/` 並通知 Steam 重新掃描
- [ ] WPF 介面：搜尋遊戲 → 一鍵安裝 manifest（現有 ManifestInstaller 整合）

### Phase 5 — CDK 自動啟動（2-3 天）

- [ ] 實作 Steam CM TCP 連線（TLS + 加密）
- [ ] Protobuf 序列化 `CMsgClientRegisterKey`
- [ ] 透過 IPC 讓 Core.dll 在 Steam 已登入的 session 中送出請求
- [ ] WPF CDK 頁面：輸入序號 → 一鍵啟動（不需 UI 互動）

### Phase 6 — 打包與部署（1 天）

- [ ] 更新 `activate-cdk.ps1`：部署 `xinput1_4.dll` + `OSU_Core.dll`
- [ ] 版本更新機制（加密下載，沿用現有 AES-CBC-zlib 格式）
- [ ] CI：GitHub Actions 自動 build（MinGW cross-compile）

---

## 六、技術選型

| 面向 | 選擇 | 理由 |
|------|------|-------|
| Native DLL | C++ 17 | ABI 相容、MinHook 生態 |
| Cross-compile | MinGW-w64 | Linux CI 環境可直接 build |
| Hook 框架 | MinHook | 單一 .lib，MIT，成熟穩定 |
| Protobuf | protobuf-lite | Steam CM 協定 |
| 序列化 | nlohmann/json | IPC JSON 解析 |
| 管理 UI | C# / WPF-UI | 現有基礎 |
| 部署 | PowerShell `irm \| iex` | 現有基礎 |

---

## 七、已知風險與挑戰

| 風險 | 嚴重度 | 對策 |
|------|--------|------|
| Steam 更新改變 vtable 偏移 | 高 | pattern scan（不用固定偏移）+ 版本偵測 |
| VAC 偵測 DLL 注入 | 高 | 僅在非 VAC 遊戲上測試；非 VAC 遊戲不送 hook |
| Steam CM 協定版本變更 | 中 | 追蹤 SteamDatabase/Protobufs |
| MinHook 與 Steam 內部 hook 衝突 | 中 | 先裝我們的 hook；偵測衝突後 fallback |
| OS / AV 誤報 | 高 | Defender 排除（已有）；代碼簽章（長期） |

---

## 八、現有資源盤點

| 資源 | 位置 | 狀態 |
|------|------|------|
| WPF 管理介面 | `src/OpenSteamUnlocker.App` | 完成大部分頁面 |
| Manifest 安裝器 | `src/OpenSteamUnlocker.Core/Manifest` | 可用 |
| activate-cdk.ps1 | 根目錄 | 可用（xinput1_4.dll 部署） |
| AES 解密文件 | `docs/steamtools-re.md` | 完成 |
| IPC 協定分析 | `docs/steamtools-re.md` | 完成 |
| 解密工具 | `docs/tools/` | 完成 |

---

## 九、里程碑總覽

```
Week 1  [Phase 1+2]  xinput1_4.dll 載入 + IPC 框架
Week 2  [Phase 3]    遊戲解鎖 Hook 可用版
Week 3  [Phase 4]    Manifest 注入整合
Week 4  [Phase 5+6]  CDK 自動啟動 + 打包部署
```
