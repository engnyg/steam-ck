#pragma once
// WinHTTP 輕量包裝：同步 GET/POST，支援 HTTPS

#include <windows.h>
#include <winhttp.h>
#include <string>
#include <vector>
#include <stdexcept>

#pragma comment(lib, "winhttp.lib")

namespace osu::http {

struct Response {
    DWORD               status = 0;
    std::vector<uint8_t> body;
    std::string          body_str() const {
        return std::string(reinterpret_cast<const char*>(body.data()), body.size());
    }
};

// 解析 URL 為主機、路徑、port 及是否 HTTPS
struct ParsedUrl {
    std::wstring host;
    std::wstring path;
    INTERNET_PORT port = 443;
    bool secure = true;
};

inline ParsedUrl parse_url(const std::wstring& url)
{
    ParsedUrl r;
    URL_COMPONENTS comps = {};
    comps.dwStructSize = sizeof(comps);

    wchar_t host[256] = {}, path[1024] = {}, scheme[16] = {};
    comps.lpszHostName     = host;  comps.dwHostNameLength     = 256;
    comps.lpszUrlPath      = path;  comps.dwUrlPathLength      = 1024;
    comps.lpszScheme       = scheme; comps.dwSchemeLength       = 16;

    if (!WinHttpCrackUrl(url.c_str(), 0, 0, &comps))
        throw std::runtime_error("WinHttpCrackUrl failed");

    r.host   = host;
    r.path   = path;
    r.port   = comps.nPort;
    r.secure = (comps.nScheme == INTERNET_SCHEME_HTTPS);
    return r;
}

// 同步請求（GET 或 POST）
inline Response request(
    const std::wstring& url,
    const std::string&  method   = "GET",
    const std::string&  body     = "",
    const std::wstring& ua       = L"OSU/1.0")
{
    auto p = parse_url(url);

    HINTERNET hSession = WinHttpOpen(ua.c_str(),
                                     WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                     WINHTTP_NO_PROXY_NAME,
                                     WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hSession) throw std::runtime_error("WinHttpOpen");

    struct SGuard {
        HINTERNET h;
        ~SGuard() { if (h) WinHttpCloseHandle(h); }
    };
    SGuard gs{hSession};

    HINTERNET hConnect = WinHttpConnect(hSession, p.host.c_str(), p.port, 0);
    if (!hConnect) throw std::runtime_error("WinHttpConnect");
    SGuard gc{hConnect};

    DWORD flags = p.secure ? WINHTTP_FLAG_SECURE : 0;
    std::wstring wmethod(method.begin(), method.end());
    HINTERNET hReq = WinHttpOpenRequest(hConnect, wmethod.c_str(),
                                         p.path.c_str(), nullptr,
                                         WINHTTP_NO_REFERER,
                                         WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!hReq) throw std::runtime_error("WinHttpOpenRequest");
    SGuard gr{hReq};

    // Content-Type for POST
    if (method == "POST" && !body.empty()) {
        WinHttpAddRequestHeaders(hReq,
            L"Content-Type: application/json",
            (DWORD)-1L, WINHTTP_ADDREQ_FLAG_ADD);
    }

    BOOL sent = WinHttpSendRequest(hReq,
                                   WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                                   body.empty() ? nullptr : (LPVOID)body.data(),
                                   (DWORD)body.size(),
                                   (DWORD)body.size(), 0);
    if (!sent) throw std::runtime_error("WinHttpSendRequest");
    if (!WinHttpReceiveResponse(hReq, nullptr))
        throw std::runtime_error("WinHttpReceiveResponse");

    Response resp;
    DWORD status_size = sizeof(DWORD);
    WinHttpQueryHeaders(hReq,
        WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        nullptr, &resp.status, &status_size, nullptr);

    DWORD bytes_avail = 0;
    while (WinHttpQueryDataAvailable(hReq, &bytes_avail) && bytes_avail > 0) {
        size_t prev = resp.body.size();
        resp.body.resize(prev + bytes_avail);
        DWORD bytes_read = 0;
        WinHttpReadData(hReq, resp.body.data() + prev, bytes_avail, &bytes_read);
        resp.body.resize(prev + bytes_read);
    }

    return resp;
}

inline Response get(const std::wstring& url) {
    return request(url, "GET");
}

inline Response post_json(const std::wstring& url, const std::string& json_body) {
    return request(url, "POST", json_body);
}

} // namespace osu::http
