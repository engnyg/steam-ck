# SteamTools 逆向工程分析報告

> 分析日期：2026-11-01  
> 分析目標：SteamTools 加密流程、Core.dll IPC 協定

---

## 一、SteamTools 三階段載入架構

```
irm steamfn.com | iex
        │
        ▼
 activate-cdk.ps1
        │ 下載 ZIP（含 xinput1_4.dll）
        │ 部署至 Steam 目錄
        │ 設定 Registry iscdkey="true"
        ▼
  Steam 啟動
        │ 載入 xinput1_4.dll（DLL 劫持）
        ▼
  xinput1_4.dll
        │ 1. 讀取版本資訊 URL（AES-CBC 加密）
        │    → 版本金鑰（g147）解密
        │ 2. 下載並解密 Core.dll
        │    → Core 金鑰（g148）解密
        │ 3. 在記憶體中載入 Core.dll（HttpLoadDLL）
        ▼
  Core.dll（in-process，Steam 程序內）
        │ 掛鉤 Steam 內部 API
        │ 建立 IPC 視窗 Global_UnlockedIPC_Class
        │ 連線 Steam CM 伺服器執行 CDK 啟動
        ▼
  SteamTools.exe（選用 UI）
          透過 WM_COPYDATA 傳送指令至 Core.dll
```

---

## 二、加密格式

### 酬載格式（AES-256-CBC）

```
[ IV 16 bytes ] [ uint32_LE 解壓後長度 4 bytes ] [ zlib 壓縮資料 ] [ PKCS7 padding ]
```

- 加密演算法：AES-256-CBC  
- 壓縮演算法：zlib（deflate）  
- 填充：PKCS7  

### 版本資訊加密金鑰（g148，VA `0x180083ff8`）

```
09513c1934d7c0b2164b57e2c266c41d2ea183751cc5e01bc6342adf987d9073
```

用於解密版本 manifest（`payload_wudrm`、`payload_tnkjmec`、`payload_steamcdn`）。

### Core/CoreBeta 加密金鑰（g147，VA `0x180083fd8`）

```
314c2086150574e15cf11d1bc171251a47086c00269355cd51c93a423c140294
```

用於解密 Core.dll 本體（`https://update.wudrm.com/Core`）。

> 金鑰來源：TRTSC 的公開研究 [Hegxib/SteamTools-Deep-Analyze](https://github.com/Hegxib/SteamTools-Deep-Analyze)

---

## 三、版本 Manifest 解密結果

三個酬載皆解密成功，均回傳版本 6 / "1.8 正式版"：

| 欄位 | 值 |
|------|-----|
| Version | 6 |
| VersionName | 1.8 正式版 |
| CardUrl | `https://cardx.tnkjmec.com/?action=verify_cdkey` |
| Core 穩定版 URL | `https://update.wudrm.com/Core` |
| Core Beta URL | `https://update.steamox.com/CoreBeta` |
| Core 穩定版 Hash | `5cb206c98516463fda55263f16a44290` |
| Authorization.dll | `https://steamusercontent-a.akamaihd.net/ugc/13156198095613306067/B43A9A6E48A1B36B22CD9EFC0191AA2F2F4C744A/` |

---

## 四、Core.dll 分析

### 基本資訊

| 項目 | 值 |
|------|-----|
| 下載 URL | `https://update.wudrm.com/Core` |
| 下載大小 | ~1.3 MB（加密） |
| 解密後大小 | 2,453,504 bytes |
| 架構 | x64 PE（DLL） |
| ImageBase | `0x180000000` |

### 關鍵字串（.rdata）

```
Store.RegisterCDKey#1
Store.RegisterCDKey#2
CardActivate
Global_SteamtoolsIPC_Class
Global_UnlockedIPC_Class
GetDepotsKey
GetTicketList
GetAuthorizationHash
ContentServerDirectory.GetManifestRequestCode#1
Cloud.BeginHTTPUpload#1
```

### 內嵌 SQLite 資料庫結構

| 資料表 | 欄位 |
|--------|------|
| manifest | DepotID, ManifestID |
| app_list | AppID |
| app_list2 | AppID |
| token | AppID |

---

## 五、IPC 協定分析

### 視窗類別用途

| 視窗類別 | 所屬程序 | 功能 |
|----------|----------|------|
| `Global_UnlockedIPC_Class` | Steam（Core.dll） | **接收**來自 SteamTools.exe 的指令 |
| `Global_SteamtoolsIPC_Class` | SteamTools.exe | **接收** Core.dll 的回呼結果 |

### 訊息格式

- Win32 訊息：`WM_COPYDATA`（`0x4A`）  
- `COPYDATASTRUCT` 結構：

```c
typedef struct {
    ULONG_PTR dwData;   // 指令類型識別碼
    DWORD     cbData;   // lpData 長度（bytes）
    PVOID     lpData;   // 指向 JSON 字串（UTF-8）
} COPYDATASTRUCT;
```

- 酬載格式：JSON 字串

### CDK 啟動指令格式

CardActivate 處理函式（VA `0x180034d73`）從 JSON 讀取 `"cdkey"` 欄位：

```json
{"cdkey": "XXXXX-XXXXX-XXXXX"}
```

結果以 `"CardActivate"` 為鍵回傳（VA `0x180034e90`）。

### 反組譯關鍵位置

| VA | 說明 |
|----|------|
| `0x180024670` | FindWindow("Global_SteamtoolsIPC_Class") → SendMessage WM_COPYDATA（Core.dll 發送回呼） |
| `0x18001c320` | 使用 "Global_UnlockedIPC_Class"（視窗建立或查找） |
| `0x180034d73` | CardActivate 處理函式，讀取 JSON `"cdkey"` |
| `0x180034e90` | 儲存結果為 "CardActivate" |
| `0x1801d9108` | "CardActivate" 字串位址（file offset `0x1d7908`） |
| `0x1801d76e8` | "Global_SteamtoolsIPC_Class" 字串位址（file offset `0x1d5ee8`） |
| `0x1801d71a0` | "Global_UnlockedIPC_Class" 字串位址（file offset `0x1d59a0`） |

---

## 六、解密腳本

### `tools/decrypt_payload.py`

見 `docs/tools/decrypt_payload.py`。

解密任意 SteamTools AES-CBC 酬載：

```bash
python3 docs/tools/decrypt_payload.py <加密檔案> <hex金鑰> [輸出檔案]
```

範例（解密 Core.dll）：
```bash
python3 docs/tools/decrypt_payload.py core_encrypted.bin \
  314c2086150574e15cf11d1bc171251a47086c00269355cd51c93a423c140294 \
  Core.dll
```

### `tools/decrypt_version.py`

見 `docs/tools/decrypt_version.py`。

解密版本 manifest：

```bash
python3 docs/tools/decrypt_version.py <加密檔案>
# 自動使用版本金鑰，輸出 JSON
```

---

## 七、Steam CDK 啟動流程（SteamTools 方式）

1. `xinput1_4.dll` 部署至 Steam 目錄
2. Registry `HKCU\Software\Valve\Steamtools\iscdkey = "true"` 設定 CDK 模式
3. Steam 啟動，載入 `xinput1_4.dll`
4. `xinput1_4.dll` 下載並在記憶體載入 `Core.dll`
5. `Core.dll` 建立 `Global_UnlockedIPC_Class` 視窗
6. SteamTools.exe（或我們的 PowerShell）發送 WM_COPYDATA：
   ```json
   {"cdkey": "XXXXX-XXXXX-XXXXX"}
   ```
7. Core.dll 呼叫 Steam CM `Store.RegisterCDKey#1` 完成 CDK 啟動
8. 結果透過 `Global_SteamtoolsIPC_Class` 回傳

---

## 八、參考資料

- TRTSC / Hegxib，SteamTools-Deep-Analyze，GitHub
- SteamDB Steam CM 協定文件
- Capstone 反組譯框架
