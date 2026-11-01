#pragma once
// 手動 PE 映射（Reflective PE loading）
// 將記憶體中的 DLL 映射為可執行模組，不寫入磁碟

#include <windows.h>
#include <cstdint>
#include <stdexcept>
#include <vector>

namespace osu::pe {

struct LoadedModule {
    HMODULE base = nullptr;
    void    unload() { if (base) { FreeLibrary(base); base = nullptr; } }
};

// 從記憶體緩衝區映射 PE DLL
// 回傳映射後的 HMODULE（可透過 GetProcAddress 呼叫）
inline HMODULE map_from_memory(const uint8_t* data, size_t size)
{
    if (size < sizeof(IMAGE_DOS_HEADER))
        throw std::runtime_error("PE: too small");

    auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(data);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        throw std::runtime_error("PE: bad DOS signature");

    auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(data + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        throw std::runtime_error("PE: bad NT signature");

    const auto& opt = nt->OptionalHeader;

    // 分配虛擬記憶體
    auto* base = reinterpret_cast<uint8_t*>(
        VirtualAlloc(reinterpret_cast<LPVOID>(opt.ImageBase),
                     opt.SizeOfImage, MEM_RESERVE | MEM_COMMIT,
                     PAGE_EXECUTE_READWRITE));
    if (!base) {
        // 讓系統選位置
        base = reinterpret_cast<uint8_t*>(
            VirtualAlloc(nullptr, opt.SizeOfImage,
                         MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE));
    }
    if (!base) throw std::runtime_error("PE: VirtualAlloc failed");

    // 複製 PE 標頭
    std::memcpy(base, data, opt.SizeOfHeaders);

    // 複製各 section
    auto* sec = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, sec++) {
        if (sec->SizeOfRawData == 0) continue;
        std::memcpy(base + sec->VirtualAddress,
                    data + sec->PointerToRawData,
                    sec->SizeOfRawData);
    }

    // 重定基址（若未在偏好位置）
    ptrdiff_t delta = base - reinterpret_cast<uint8_t*>(opt.ImageBase);
    if (delta != 0) {
        auto* reloc_dir = &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC];
        auto* reloc = reinterpret_cast<IMAGE_BASE_RELOCATION*>(
            base + reloc_dir->VirtualAddress);
        DWORD remaining = reloc_dir->Size;

        while (remaining > 0 && reloc->VirtualAddress) {
            DWORD  block_size  = reloc->SizeOfBlock;
            WORD*  entries     = reinterpret_cast<WORD*>(reloc + 1);
            DWORD  entry_count = (block_size - sizeof(IMAGE_BASE_RELOCATION)) / sizeof(WORD);

            for (DWORD j = 0; j < entry_count; j++) {
                WORD  type   = entries[j] >> 12;
                DWORD offset = entries[j] & 0xFFF;
                if (type == IMAGE_REL_BASED_DIR64) {
                    auto* ptr = reinterpret_cast<int64_t*>(
                        base + reloc->VirtualAddress + offset);
                    *ptr += delta;
                } else if (type == IMAGE_REL_BASED_HIGHLOW) {
                    auto* ptr = reinterpret_cast<int32_t*>(
                        base + reloc->VirtualAddress + offset);
                    *ptr += static_cast<int32_t>(delta);
                }
            }
            remaining -= block_size;
            reloc = reinterpret_cast<IMAGE_BASE_RELOCATION*>(
                reinterpret_cast<uint8_t*>(reloc) + block_size);
        }
    }

    // 處理 Import Table
    auto* imp_dir = &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (imp_dir->Size > 0) {
        auto* desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(
            base + imp_dir->VirtualAddress);
        for (; desc->Name; desc++) {
            const char* mod_name = reinterpret_cast<const char*>(base + desc->Name);
            HMODULE hMod = LoadLibraryA(mod_name);
            if (!hMod) continue;

            auto* thunk_ref  = reinterpret_cast<IMAGE_THUNK_DATA*>(
                base + (desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk));
            auto* func_ref   = reinterpret_cast<IMAGE_THUNK_DATA*>(
                base + desc->FirstThunk);

            for (; thunk_ref->u1.AddressOfData; thunk_ref++, func_ref++) {
                FARPROC fn = nullptr;
                if (IMAGE_SNAP_BY_ORDINAL(thunk_ref->u1.Ordinal)) {
                    fn = GetProcAddress(hMod,
                        MAKEINTRESOURCEA(IMAGE_ORDINAL(thunk_ref->u1.Ordinal)));
                } else {
                    auto* ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(
                        base + thunk_ref->u1.AddressOfData);
                    fn = GetProcAddress(hMod, ibn->Name);
                }
                func_ref->u1.Function = reinterpret_cast<ULONG_PTR>(fn);
            }
        }
    }

    // 呼叫 DllMain (DLL_PROCESS_ATTACH)
    if (opt.AddressOfEntryPoint) {
        using DllMain_t = BOOL(WINAPI*)(HINSTANCE, DWORD, LPVOID);
        auto entry = reinterpret_cast<DllMain_t>(base + opt.AddressOfEntryPoint);
        entry(reinterpret_cast<HINSTANCE>(base), DLL_PROCESS_ATTACH, nullptr);
    }

    return reinterpret_cast<HMODULE>(base);
}

// 從已映射模組取得函式指標
// 反射載入的模組不在 OS 模組表內，GetProcAddress 不會找到；
// 這裡手動解析 IMAGE_EXPORT_DIRECTORY。
inline void* resolve_export(HMODULE h, const char* name)
{
    auto* base = reinterpret_cast<uint8_t*>(h);
    auto* dos  = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto* nt   = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    auto& dir  = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.Size == 0) return nullptr;

    auto* exp  = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + dir.VirtualAddress);
    auto* names    = reinterpret_cast<DWORD*>(base + exp->AddressOfNames);
    auto* ordinals = reinterpret_cast<WORD*> (base + exp->AddressOfNameOrdinals);
    auto* funcs    = reinterpret_cast<DWORD*>(base + exp->AddressOfFunctions);

    for (DWORD i = 0; i < exp->NumberOfNames; i++) {
        const char* n = reinterpret_cast<const char*>(base + names[i]);
        if (std::strcmp(n, name) == 0) {
            WORD  ord = ordinals[i];
            DWORD rva = funcs[ord];
            return base + rva;
        }
    }
    return nullptr;
}

template <typename FnPtr>
inline FnPtr get_proc(HMODULE h, const char* name)
{
    return reinterpret_cast<FnPtr>(resolve_export(h, name));
}

} // namespace osu::pe
