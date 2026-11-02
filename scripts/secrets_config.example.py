# 複製此檔案為 secrets_config.py 並填入真實金鑰
# cp secrets_config.example.py secrets_config.py
# secrets_config.py 已加入 .gitignore，不會上傳

# HMAC-SHA256 金鑰（32 bytes hex）
# 生成方式：python3 -c "import secrets; print(secrets.token_hex(32))"
HMAC_SECRET = "REPLACE_WITH_YOUR_32_BYTE_HEX_SECRET"

# AES-256 加密金鑰（各元件獨立）
KEY_VERSION = "REPLACE_WITH_32_BYTE_HEX"   # 版本 manifest
KEY_CORE    = "REPLACE_WITH_32_BYTE_HEX"   # OSU_Core.dll
KEY_AUTH    = "REPLACE_WITH_32_BYTE_HEX"   # OSU_Auth.dll
KEY_DEPOT   = "REPLACE_WITH_32_BYTE_HEX"   # depotkeys.json

# 伺服器管理員 token
ADMIN_TOKEN = "REPLACE_WITH_RANDOM_TOKEN"

# Depot Key 自動更新（update_depotkeys.py 使用）
CDN_UPLOAD_URL          = "REPLACE_WITH_CDN_UPLOAD_ENDPOINT"  # e.g. https://cdn.yourdomain.com/upload/depot.enc
CDN_UPLOAD_TOKEN        = "REPLACE_WITH_CDN_UPLOAD_TOKEN"     # PUT Bearer token
CDN_VERSION_UPLOAD_URL  = "REPLACE_WITH_CDN_VERSION_ENDPOINT" # e.g. https://cdn.yourdomain.com/upload/version.enc
CDN_VERSION_UPLOAD_TOKEN = "REPLACE_WITH_CDN_VERSION_TOKEN"   # 通常與 CDN_UPLOAD_TOKEN 相同
