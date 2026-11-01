#!/usr/bin/env python3
"""
Core.dll IPC 分析輔助工具

列出 Core.dll 中 Global_UnlockedIPC_Class / Global_SteamtoolsIPC_Class
相關的函式引用，並輸出反組譯片段。

需要：pip install capstone pycryptodome

用法：
    python3 find_ipc_window.py <Core.dll 路徑>
"""

import sys, struct
from pathlib import Path

IMAGE_BASE = 0x180000000
WM_COPYDATA = 0x4A


def parse_pe(data):
    e_lfanew = struct.unpack_from('<I', data, 0x3c)[0]
    num_sec  = struct.unpack_from('<H', data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from('<H', data, e_lfanew + 20)[0]
    sbase    = e_lfanew + 24 + opt_size
    sections = []
    for i in range(num_sec):
        s    = sbase + i * 40
        name = data[s:s+8].rstrip(b'\x00').decode('ascii', 'replace')
        vs   = struct.unpack_from('<I', data, s + 8)[0]
        va   = struct.unpack_from('<I', data, s + 12)[0]
        ro   = struct.unpack_from('<I', data, s + 20)[0]
        sections.append((name, va, vs, ro))
    return sections


def va_to_off(sections, va):
    for name, sec_va, size, raw in sections:
        if sec_va <= va - IMAGE_BASE < sec_va + size:
            return raw + (va - IMAGE_BASE - sec_va)
    return None


def find_utf16_refs(data, sections, target_str):
    """找所有 LEA [rip+disp] 指向目標 UTF-16LE 字串的指令位址"""
    pattern = target_str.encode('utf-16-le')
    rdata   = next(s for s in sections if s[0] == '.rdata')
    rd_off, rd_va, rd_size = rdata[3], rdata[1], rdata[2]

    results = []
    pos = rd_off
    while True:
        pos = data.find(pattern, pos, rd_off + rd_size)
        if pos == -1:
            break
        str_va = IMAGE_BASE + rd_va + (pos - rd_off)
        results.append((pos, str_va))
        pos += 1

    text = next(s for s in sections if s[0] == '.text')
    txt_off, txt_va, txt_size = text[3], text[1], text[2]

    refs = []
    for _, str_va in results:
        for i in range(txt_off, txt_off + txt_size - 7):
            if data[i] not in (0x48, 0x4C):
                continue
            if data[i+1] != 0x8D:
                continue
            if (data[i+2] & 0xC7) != 0x05:
                continue
            disp       = struct.unpack_from('<i', data, i + 3)[0]
            target_va  = IMAGE_BASE + txt_va + (i - txt_off) + 7 + disp
            if target_va == str_va:
                instr_va = IMAGE_BASE + txt_va + (i - txt_off)
                refs.append(instr_va)
    return refs


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    try:
        import capstone
    except ImportError:
        print("[錯誤] 需要安裝 capstone：pip install capstone")
        sys.exit(1)

    data     = Path(sys.argv[1]).read_bytes()
    sections = parse_pe(data)
    text     = next(s for s in sections if s[0] == '.text')

    cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

    for cls_name in ('Global_UnlockedIPC_Class', 'Global_SteamtoolsIPC_Class'):
        refs = find_utf16_refs(data, sections, cls_name)
        print(f"\n=== {cls_name} ===")
        for va in refs:
            print(f"  引用於 VA {va:#x}")
            off = va_to_off(sections, va)
            # 顯示前後 0x30 bytes
            start_off = max(off - 0x20, text[3])
            chunk     = data[start_off:off + 0x80]
            base_va   = IMAGE_BASE + text[1] + (start_off - text[3])
            for ins in cs.disasm(chunk, base_va):
                marker = ' <<<<' if ins.address == va else ''
                print(f"    {ins.address:#x}: {ins.mnemonic} {ins.op_str}{marker}")


if __name__ == '__main__':
    main()
