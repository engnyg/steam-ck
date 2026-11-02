#include "auth.hpp"
#include <winhttp_client.hpp>

#include <windows.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <string>
#include <vector>
#include <ctime>
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <sstream>

// ── 設定（TODO：填入實際值） ──────────────────────────────────────────────
static const uint8_t HMAC_SECRET[32] = {0};
static const wchar_t* VERIFY_URL  = L"https://api.ourdomain.com/api/verify_cdkey";
static const wchar_t* CHECK_URL   = L"https://api.ourdomain.com/api/check_app";

// Registry
static const char* REG_PATH   = "SOFTWARE\\OSU";
static const char* REG_KEY    = "CDKey";
static const char* REG_APPIDS = "AppIds";   // 逗號分隔，例如 "730,731,732"

// ── Base36 ────────────────────────────────────────────────────────────────
static const char CHARSET[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

static std::string to_base36(uint64_t n, int len)
{
    std::string r(len, '0');
    for (int i = len - 1; i >= 0; i--) {
        r[i] = CHARSET[n % 36]; n /= 36;
    }
    return r;
}

// ── HMAC-SHA256（Windows CNG） ────────────────────────────────────────────
static std::vector<uint8_t> hmac_sha256(
    const uint8_t* key, size_t klen,
    const uint8_t* msg, size_t mlen)
{
    BCRYPT_ALG_HANDLE  hAlg  = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM,
                                 nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG);
    DWORD obj_len = 0, dummy = 0;
    BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH,
                      reinterpret_cast<PUCHAR>(&obj_len), sizeof(DWORD), &dummy, 0);
    std::vector<uint8_t> obj(obj_len);
    BCryptCreateHash(hAlg, &hHash, obj.data(), obj_len,
                     const_cast<PUCHAR>(key), static_cast<ULONG>(klen), 0);
    BCryptHashData(hHash, const_cast<PUCHAR>(msg), static_cast<ULONG>(mlen), 0);
    std::vector<uint8_t> digest(32);
    BCryptFinishHash(hHash, digest.data(), 32, 0);
    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);
    return digest;
}

// ── LY 格式驗證 ───────────────────────────────────────────────────────────
static bool verify_format(const std::string& key)
{
    std::string clean;
    for (char c : key) if (c != '-') clean += static_cast<char>(toupper(c));
    if (clean.size() >= 2 && clean[0]=='L' && clean[1]=='Y') clean = clean.substr(2);
    if (clean.size() != 20) return false;
    for (char c : clean) {
        bool ok = false;
        for (int i = 0; i < 36; i++) if (CHARSET[i]==c) { ok=true; break; }
        if (!ok) return false;
    }
    std::string serial = clean.substr(0,12), check = clean.substr(12,8);
    auto mac = hmac_sha256(HMAC_SECRET,32,
                            reinterpret_cast<const uint8_t*>(serial.data()),serial.size());
    uint64_t n=0; for(int i=0;i<6;i++) n=(n<<8)|mac[i];
    return to_base36(n,8)==check;
}

// ── Registry ──────────────────────────────────────────────────────────────
static std::string reg_read(const char* name)
{
    HKEY hk;
    if (RegOpenKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, KEY_READ, &hk) != ERROR_SUCCESS)
        return {};
    char buf[512]={};
    DWORD len=sizeof(buf);
    RegQueryValueExA(hk, name, nullptr, nullptr, reinterpret_cast<LPBYTE>(buf), &len);
    RegCloseKey(hk);
    return std::string(buf);
}

static void reg_write(const char* name, const std::string& val)
{
    HKEY hk;
    RegCreateKeyExA(HKEY_CURRENT_USER, REG_PATH, 0, nullptr,
                    REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hk, nullptr);
    RegSetValueExA(hk, name, 0, REG_SZ,
                   reinterpret_cast<const BYTE*>(val.c_str()),
                   static_cast<DWORD>(val.size()+1));
    RegCloseKey(hk);
}

// ── 簡易 JSON 陣列解析（取 appids / online_verify 欄位）─────────────────
// 只解析 "appids":[730,731,732] 格式
static std::vector<int> parse_int_array(const std::string& json, const std::string& key)
{
    std::vector<int> result;
    std::string search = "\"" + key + "\"";
    auto pos = json.find(search);
    if (pos == std::string::npos) return result;
    pos = json.find('[', pos);
    if (pos == std::string::npos) return result;
    auto end = json.find(']', pos);
    if (end == std::string::npos) return result;
    std::string arr = json.substr(pos+1, end-pos-1);
    std::stringstream ss(arr);
    std::string tok;
    while (std::getline(ss, tok, ',')) {
        try { result.push_back(std::stoi(tok)); } catch(...) {}
    }
    return result;
}

// ── 線上驗證（取得 appids）────────────────────────────────────────────────
static bool verify_online(const std::string& lkey, std::vector<int>& out_appids)
{
    try {
        long long ts = static_cast<long long>(std::time(nullptr));
        std::string ts_str = std::to_string(ts);
        std::string sig_msg = lkey + ts_str;
        auto sig = hmac_sha256(HMAC_SECRET,32,
                                reinterpret_cast<const uint8_t*>(sig_msg.data()),sig_msg.size());
        char sig_hex[65]={};
        static const char HX[]="0123456789abcdef";
        for(int i=0;i<32;i++){sig_hex[i*2]=HX[sig[i]>>4];sig_hex[i*2+1]=HX[sig[i]&0xF];}

        std::string body = "{\"key\":\""+lkey+"\",\"ts\":"+ts_str+",\"sig\":\""+sig_hex+"\"}";
        auto resp = osu::http::post_json(VERIFY_URL, body);
        if (resp.status != 200) return false;

        std::string r = resp.body_str();
        bool valid = r.find("\"valid\":true") != std::string::npos ||
                     r.find("\"valid\": true") != std::string::npos;
        if (!valid) return false;

        out_appids = parse_int_array(r, "appids");
        return !out_appids.empty();
    } catch(...) { return false; }
}

// ── appids <-> 字串轉換 ───────────────────────────────────────────────────
static std::string appids_to_str(const std::vector<int>& ids)
{
    std::string s;
    for (size_t i=0;i<ids.size();i++){
        if(i) s+=',';
        s+=std::to_string(ids[i]);
    }
    return s;
}

static std::vector<int> str_to_appids(const std::string& s)
{
    std::vector<int> r;
    std::stringstream ss(s);
    std::string tok;
    while(std::getline(ss,tok,',')){
        try { r.push_back(std::stoi(tok)); } catch(...) {}
    }
    return r;
}

// ── 公開函式 ──────────────────────────────────────────────────────────────

bool auth_verify_key(const char* key, int* out_appids, int* out_count)
{
    std::string k(key ? key : "");
    if (!verify_format(k)) return false;

    std::vector<int> appids;

    // 線上驗證
    if (verify_online(k, appids)) {
        reg_write(REG_KEY,    k);
        reg_write(REG_APPIDS, appids_to_str(appids));
    } else {
        // 回退快取
        std::string cached_key    = reg_read(REG_KEY);
        std::string cached_appids = reg_read(REG_APPIDS);
        if (cached_key != k || !verify_format(cached_key)) return false;
        appids = str_to_appids(cached_appids);
    }

    if (appids.empty()) return false;

    // 填入輸出陣列
    if (out_appids && out_count) {
        int cap = *out_count;
        int n   = static_cast<int>(appids.size());
        *out_count = n;
        for (int i=0; i<n && i<cap; i++) out_appids[i] = appids[i];
    }
    return true;
}

bool auth_check_cached_key()
{
    std::string k = reg_read(REG_KEY);
    return !k.empty() && verify_format(k);
}

int auth_get_cached_appids(int* out_appids, int max_count)
{
    std::string s = reg_read(REG_APPIDS);
    auto ids = str_to_appids(s);
    int n = static_cast<int>(ids.size());
    if (out_appids) {
        for (int i=0; i<n && i<max_count; i++) out_appids[i] = ids[i];
    }
    return n;
}

bool auth_check_app_online(int appid)
{
    std::string k = reg_read(REG_KEY);
    if (k.empty() || !verify_format(k)) return false;
    try {
        std::string body = "{\"key\":\""+k+"\",\"appid\":"+std::to_string(appid)+"}";
        auto resp = osu::http::post_json(CHECK_URL, body);
        if (resp.status != 200) return false;
        std::string r = resp.body_str();
        return r.find("\"allowed\":true") != std::string::npos ||
               r.find("\"allowed\": true") != std::string::npos;
    } catch(...) { return false; }
}
