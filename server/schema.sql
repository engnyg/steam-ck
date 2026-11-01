-- OSU API 伺服器資料庫結構

CREATE TABLE IF NOT EXISTS keys (
    key         TEXT    PRIMARY KEY,
    created_at  INTEGER NOT NULL DEFAULT (strftime('%s','now')),
    note        TEXT    DEFAULT '',
    revoked     INTEGER NOT NULL DEFAULT 0,
    -- 逗號分隔的 AppID，例如 "730,731,732"
    -- 空字串 = 不包含任何遊戲（無效金鑰）
    appids      TEXT    NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS verify_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    key         TEXT    NOT NULL,
    timestamp   INTEGER NOT NULL DEFAULT (strftime('%s','now')),
    ip          TEXT    DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_verify_log_key ON verify_log(key);
CREATE INDEX IF NOT EXISTS idx_verify_log_ts  ON verify_log(timestamp);
