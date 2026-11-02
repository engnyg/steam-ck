#!/usr/bin/env python3
"""
IClientUser vtable 搜尋工具

用法：
    python find_vtable.py <steamclient64.dll>
    python find_vtable.py <steamclient64.dll> --methods       # 列出所有方法名+索引
    python find_vtable.py <steamclient64.dll> --grep Subscri  # 搜尋方法名
    python find_vtable.py <steamclient64.dll> --all-ifaces    # 列出所有 IClient* 介面
"""

import argparse, struct, sys
from pathlib import Path

try:
    import pefile
except ImportError:
    print("[錯誤] 需要 pefile：pip install pefile")
    sys.exit(1)


def find_sections(pe):
    sections = {}
    for sec in pe.sections:
        name = sec.Name.rstrip(b"\x00").decode()
        sections[name] = (sec.VirtualAddress, sec.VirtualAddress + sec.Misc_VirtualSize)
    return sections


def find_rtti_vtable(data, image_base, type_name):
    """透過 MSVC x64 RTTI 找 vtable"""
    encoded = type_name.encode("ascii") + b"\x00"
    idx = data.find(encoded)
    if idx == -1:
        return None

    # TypeDescriptor = { void* pVFTable(8), void* spare(8), char name[] }
    td_offset = idx - 16
    td_rva = td_offset

    # 搜尋 Complete Object Locator 引用此 TD
    # COL = { uint32 sig=1, uint32 offset, uint32 cdOffset,
    #         uint32 pTD_rva, uint32 pCHD_rva, uint32 pSelf_rva }
    for i in range(0, len(data) - 24, 4):
        sig = struct.unpack_from("<I", data, i)[0]
        if sig != 1:
            continue
        col_td = struct.unpack_from("<I", data, i + 12)[0]
        if col_td != td_rva:
            continue
        self_rva = struct.unpack_from("<I", data, i + 20)[0]
        if self_rva != i:
            continue

        # vtable[-1] 存放指向此 COL 的指標
        col_va = image_base + i
        col_bytes = struct.pack("<Q", col_va)
        j = data.find(col_bytes)
        if j == -1:
            continue
        vtable_offset = j + 8
        vtable_va = image_base + vtable_offset
        return {
            "type_name": type_name,
            "td_offset": td_offset,
            "col_offset": i,
            "vtable_offset": vtable_offset,
            "vtable_va": vtable_va,
        }
    return None


def count_vtable_entries(data, vtable_offset, text_start, text_end, image_base):
    """計算 vtable 中連續指向 .text 的函式指標數量"""
    count = 0
    pos = vtable_offset
    while pos + 8 <= len(data):
        val = struct.unpack_from("<Q", data, pos)[0]
        code_off = val - image_base
        if text_start <= code_off < text_end:
            count += 1
            pos += 8
        else:
            break
    return count


def find_method_names(data, iface_name):
    """從方法名表找出指定介面的所有方法名和索引"""
    # 先找介面名在方法名表區域的位置
    target = iface_name.encode("ascii") + b"\x00"

    # 掃描 .rdata 中連續的 null-terminated 字串區域
    # 介面名和方法名交替出現
    results = []
    pos = 0
    while True:
        idx = data.find(target, pos)
        if idx == -1:
            break

        # 確認這是方法名表中的位置（前面是 \x00 或起始）
        if idx > 0 and data[idx - 1] != 0:
            pos = idx + 1
            continue

        # 從這裡往後讀方法名
        cursor = idx + len(target)
        methods = []
        method_idx = 0

        while cursor < len(data):
            # 跳過 null padding
            while cursor < len(data) and data[cursor] == 0:
                cursor += 1
            if cursor >= len(data):
                break

            str_end = data.find(b"\x00", cursor)
            if str_end == -1 or str_end - cursor > 80:
                break

            s = data[cursor:str_end].decode("ascii", errors="replace")
            if not s or not s[0].isalpha():
                break

            # 遇到下一個 IClient* 介面就停
            if s.startswith("IClient"):
                break

            methods.append((method_idx, s))
            method_idx += 1
            cursor = str_end + 1

        if methods:
            results.append({"name_offset": idx, "methods": methods})
        pos = idx + 1

    # 取方法數最多的那組（通常是主表）
    if results:
        return max(results, key=lambda r: len(r["methods"]))
    return None


def find_all_interfaces(data):
    """列出所有 IClient* 介面名"""
    ifaces = []
    pos = 0
    seen = set()
    while True:
        idx = data.find(b"IClient", pos)
        if idx == -1:
            break
        if idx > 0 and data[idx - 1] != 0:
            pos = idx + 1
            continue
        str_end = data.find(b"\x00", idx)
        if str_end == -1 or str_end - idx > 60:
            pos = idx + 1
            continue
        s = data[idx:str_end].decode("ascii", errors="replace")
        if s.isidentifier() and s not in seen:
            seen.add(s)
            ifaces.append(s)
        pos = idx + 1
    return sorted(ifaces)


def main():
    parser = argparse.ArgumentParser(description="IClientUser vtable 搜尋工具")
    parser.add_argument("dll", help="steamclient64.dll 路徑")
    parser.add_argument("--methods", action="store_true", help="列出所有方法名+索引")
    parser.add_argument("--grep", metavar="KEYWORD", help="搜尋方法名（不分大小寫）")
    parser.add_argument("--all-ifaces", action="store_true", help="列出所有 IClient* 介面")
    parser.add_argument("--iface", default="IClientUser", help="指定介面名（預設 IClientUser）")
    args = parser.parse_args()

    dll_path = Path(args.dll)
    if not dll_path.exists():
        print(f"[錯誤] 找不到 {dll_path}")
        sys.exit(1)

    print(f"載入 {dll_path.name} ...")
    pe = pefile.PE(str(dll_path))
    data = pe.get_memory_mapped_image()
    image_base = pe.OPTIONAL_HEADER.ImageBase
    sections = find_sections(pe)
    text_start, text_end = sections.get(".text", (0, 0))

    print(f"  ImageBase: 0x{image_base:X}")
    print(f"  .text:     0x{text_start:X} - 0x{text_end:X}")
    print(f"  映像大小:   {len(data) / 1024 / 1024:.1f} MB")
    print()

    # 列出所有介面
    if args.all_ifaces:
        ifaces = find_all_interfaces(data)
        print(f"找到 {len(ifaces)} 個 IClient* 介面：")
        for name in ifaces:
            table = find_method_names(data, name)
            count = len(table["methods"]) if table else 0
            print(f"  {name:45s} {count:4d} methods")
        return

    # RTTI vtable 搜尋
    print(f"=== RTTI vtable 搜尋 ===")
    rtti_targets = [
        (f".?AV{args.iface}Map@@", f"{args.iface}Map（dispatch 層）"),
        (f".?AVC{''.join(w.capitalize() for w in args.iface.replace('IClient','').split())}@@",
         "CUser（實作層）") if args.iface == "IClientUser" else None,
        (f".?AV?$InterfaceMapBase@V{args.iface}@@@@",
         f"InterfaceMapBase<{args.iface}>（轉發層）"),
    ]

    for item in rtti_targets:
        if item is None:
            continue
        rtti_name, label = item
        result = find_rtti_vtable(data, image_base, rtti_name)
        if result:
            entries = count_vtable_entries(
                data, result["vtable_offset"], text_start, text_end, image_base
            )
            print(f"  {label}")
            print(f"    RTTI:    {rtti_name}")
            print(f"    vtable:  VA 0x{result['vtable_va']:X}  (file 0x{result['vtable_offset']:X})")
            print(f"    entries: {entries}")
            print()
        else:
            print(f"  {label}: 未找到 ({rtti_name})")
            print()

    # CUser 特殊處理
    if args.iface == "IClientUser":
        cuser = find_rtti_vtable(data, image_base, ".?AVCUser@@")
        if cuser:
            entries = count_vtable_entries(
                data, cuser["vtable_offset"], text_start, text_end, image_base
            )
            print(f"  CUser（實作層）")
            print(f"    RTTI:    .?AVCUser@@")
            print(f"    vtable:  VA 0x{cuser['vtable_va']:X}  (file 0x{cuser['vtable_offset']:X})")
            print(f"    entries: {entries}")
            print()

    # 方法名表
    table = find_method_names(data, args.iface)
    if not table:
        print(f"[警告] 找不到 {args.iface} 的方法名表")
        return

    methods = table["methods"]
    print(f"=== {args.iface} 方法名表（{len(methods)} 個方法）===")
    print()

    if args.grep:
        kw = args.grep.lower()
        matched = [(i, n) for i, n in methods if kw in n.lower()]
        if matched:
            for idx, name in matched:
                print(f"  [{idx:3d}] {name}")
        else:
            print(f"  找不到包含 \"{args.grep}\" 的方法")
        print()
        print(f"共 {len(matched)} 個匹配")
    elif args.methods:
        for idx, name in methods:
            print(f"  [{idx:3d}] {name}")
    else:
        # 預設只印重點方法
        keywords = [
            "Subscri", "CDKey", "Depot", "Activate", "Purchase",
            "Redeem", "Ticket", "Auth", "License",
        ]
        print("  重點方法（用 --methods 看全部，--grep 搜尋）：")
        print()
        for idx, name in methods:
            if any(kw in name for kw in keywords):
                print(f"  [{idx:3d}] {name}")

    print()
    print("=== hooks.cpp 建議值 ===")
    bis = next((i for i, n in methods if n == "BIsSubscribedApp"), None)
    gsub = next((i for i, n in methods if n == "GetSubscribedApps"), None)
    if bis is not None:
        print(f"  constexpr int VT_BIsSubscribedApp  = {bis};")
    if gsub is not None:
        print(f"  constexpr int VT_GetSubscribedApps = {gsub};")
    if bis is None and gsub is None:
        print("  [警告] 未找到 BIsSubscribedApp / GetSubscribedApps")


if __name__ == "__main__":
    main()
