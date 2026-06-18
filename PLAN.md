# OpenSteamTool + Fluent-Install 合併計畫（WPF UI）

## 0. 目標與定位

把兩個來源專案合併成 **單一 Windows 桌面程式**，使用 **WPF UI (lepoco/wpfui)** 的 Fluent Design：

| 來源 | 角色 | 在新專案中的去向 |
|------|------|------------------|
| `OpenSteam001/OpenSteamTool` (C++) | DLL 注入式「解鎖引擎」(runtime) | **保留為原生引擎**，由新程式部署 DLL + 寫入 lua |
| `zhouchentao666/Fluent-Install` (Python/PyQt6) | Fluent GUI + 清單抓取後端 (cai_backend) | **重寫為 C#/WPF**（GUI）+ **移植為 C# 服務**（後端） |

最終產物：一個 .NET 應用程式，既能「入庫」（抓 manifest/lua 並安裝），又內建 OpenSteamTool 作為解鎖後端，不再需要使用者另外裝 SteamTools。

---

## 1. 技術選型（建議預設）

- **執行環境**：.NET 8 (LTS)、Windows 10/11、x64
- **UI 框架**：WPF + **WPF-UI (lepoco/wpfui)** NuGet — 提供 NavigationView、Mica/Acrylic、Fluent 控制項
- **架構模式**：MVVM，使用 `CommunityToolkit.Mvvm`（`ObservableObject` / `RelayCommand`）
- **依賴注入 / 啟動**：`Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection`
- **HTTP**：`HttpClient`（`IHttpClientFactory`，支援代理鏡像切換、重試）
- **VDF / KeyValues 解析**：`ValveKeyValue` NuGet（取代 Python 的 `vdf`）
- **JSON**：`System.Text.Json`
- **Lua 產生**：字串模板（不需要 Lua runtime，OpenSteamTool 端才執行 lua）
- **原生引擎**：OpenSteamTool 以 **git submodule** 形式納入，CMake 建置產生 DLL（或 CI 預編譯後置入 `Assets/engine/`）

### 後端策略（關鍵決策）
**建議：把 `cai_backend.py` 重寫為 C# 服務**（乾淨、單一安裝包、無 Python 執行階段）。
替代方案：用 `pythonnet` 或子行程嵌入既有 Python 後端 — 上線快但要打包 Python，較不推薦。

---

## 2. 解決方案結構

```
OpenSteamUnlocker.sln
├─ src/
│  ├─ OpenSteamUnlocker.App/          # WPF UI 主程式 (Views + ViewModels)
│  │  ├─ Views/Pages/                 # HomePage, SearchPage, SettingsPage, MultiplayerPage, TrainersPage
│  │  ├─ ViewModels/
│  │  ├─ Controls/                    # GameCard, GameCardGrid
│  │  ├─ Resources/                   # 主題、i18n .resx (zh-CN/zh-TW/en/fr/ru/de...)
│  │  └─ App.xaml / MainWindow.xaml
│  ├─ OpenSteamUnlocker.Core/         # 後端服務（cai_backend 的 C# 移植）
│  │  ├─ Manifest/                    # 各來源 provider（見 §4）
│  │  ├─ Steam/                       # Steam 路徑偵測、library 解析、檔案管理
│  │  ├─ Engine/                      # OpenSteamTool 部署 / lua 寫入 / 設定
│  │  ├─ Models/  Config/  Http/
│  └─ OpenSteamUnlocker.Engine.Native/ # OpenSteamTool submodule + 建置整合
└─ PLAN.md
```

---

## 3. 分階段實作

### Phase 0 — Scaffold（基礎建置）
- 建 solution、三個專案、NuGet 套件
- WPF-UI `FluentWindow` + `NavigationView` 骨架，左側導覽：Home / Search / Settings / Multiplayer / Trainers
- DI 容器、設定檔讀寫（對應原 `Resource.json` / toml）、主題（light/dark/auto + 主題色 + Mica/Acrylic/Aero）

### Phase 1 — Core 後端移植（最大工作量）
從 `cai_backend.py`（4025 行 / 116 函式）逐塊移植：
1. HTTP 基礎層：安全請求 + 重試 + GitHub 代理鏡像清單（`ghproxy`、`gh-proxy.org`、`ghfast.top`… 中國鏡像自動偵測，對應 `mips.kugou.com/check/iscn`）
2. Steam metadata：`store.steampowered.com/api/appdetails`、`api.steamcmd.net/v1/info`
3. DLC：`api.9178666.xyz/dlcs`、`/search`、`/cmd`
4. 各 manifest provider（見 §4）
5. Lua 腳本產生（depot key / manifest / DLC）
6. Steam library 解析（`libraryfolders.vdf`）、已入庫檔案管理（list/delete）、版本模式切換（固定版 ↔ 自動更新）

### Phase 2 — GUI 頁面
- **HomePage**：遊戲庫（list/grid 雙視圖）、排序、統計列、GameCard 右鍵選單（複製封面/AppID/名稱、商店頁、SteamDB、刪除）、版本模式切換
- **SearchPage**：名稱/AppID 輸入 → 來源下拉 → 選項（全部 DLC / 修補 depot key / 修補 manifest）→ 搜尋 → 加入庫
- **SettingsPage**：除錯模式、log、解鎖器模式、逾時、主題/色彩/視窗效果、語言（多語）、進階（側欄隱藏項）

### Phase 3 — OpenSteamTool 引擎整合
- 偵測 Steam 安裝路徑
- 部署/移除 `OpenSteamTool.dll` + `dwmapi.dll` + `xinput1_4.dll` 到 Steam 根目錄（含備份/還原）
- 產生 `opensteamtool.toml`，把 lua 寫入設定的 lua 目錄（如 `steam\config\lua`）
- 引擎狀態頁/卡：已安裝、版本、啟用/停用
- 讓「入庫」流程直接寫進 OpenSteamTool 的 lua 目錄（取代原本寫 SteamTools/GreenLuma）

### Phase 4 — 進階頁（可後做）
- **MultiplayerPage**：DLL 注入 vs BAT 腳本、AppID（預設 480）、服務啟停、log
- **TrainersPage**：修改器搜尋/下載/啟動/刪除、GCM 整合（依賴 `trainer_backend.py`）

### Phase 5 — 打包與發佈
- 單一 exe（`PublishSingleFile` / 自含執行階段）
- 圖示、版本、自動更新檢查
- GitHub Actions：建 OpenSteamTool DLL + 發佈 WPF 應用

---

## 4. Manifest 來源清單（需逐一移植的 provider）

來自 cai_backend 的端點，每個對應一個 C# provider：

- CaiGames：`api.9178666.xyz`（search / dlcs / cmd）
- SteamCMD：`api.steamcmd.net/v1/info`
- Sudama / Cysaw：`cysaw.pw`(+ proxy)
- GMRC：`gmrc.wudrm.com/manifest`
- ManifestHub：`api.manifesthub1.filegear-sg.me`
- manifest.steam.run：`/api/manifest`、`/api/depot`、`/api/session`、`/api/request-code`（含 workshop）
- SteamHub：`steamhub.156354.xyz/api/games`
- depot keys / access tokens：`api.993499094.xyz`
- GitHub repo 清單庫（`api.github.com/repos`、`raw.githubusercontent.com`、`codeload.github.com`）+ 代理鏡像
- Workshop：`api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails`
- 封面圖：`cdn.cloudflare.steamstatic.com/steam/apps`

> 建議定義 `IManifestProvider` 介面（`SearchAsync` / `GetManifestAsync` / `GetDepotKeysAsync`），各來源實作之，UI 來源下拉即列舉這些 provider。

---

## 5. 風險與注意事項

- **法律/條款**：解鎖未擁有遊戲違反 Steam 服務條款，僅作技術整合；不處理付費/破解 DRM 的惡意用途
- **工作量**：後端移植（Phase 1）是主要成本；4025 行 Python，建議先做 MVP（CaiGames + SteamCMD + GitHub 兩三個來源 + 入庫）再擴充
- **OpenSteamTool 仍須 C++ 編譯**：需 CMake 工具鏈或 CI 預編譯 DLL；C# 端只負責部署與設定
- **版權/授權**：合併前確認兩專案 LICENSE 相容（OpenSteamTool 與 Fluent-Install 各自授權），並保留原作者標示

---

## 6. 建議起步（MVP）

1. Phase 0 scaffold + 主題/導覽可跑
2. Phase 1 只做：Steam 路徑偵測、CaiGames 搜尋、SteamCMD depot、GitHub manifest 抓取、lua 產生
3. Phase 3 引擎部署（裝 DLL + 寫 lua）
4. HomePage + SearchPage 串起完整「搜尋 → 入庫 → 解鎖」閉環

之後再補來源、進階頁、多語、打包。
