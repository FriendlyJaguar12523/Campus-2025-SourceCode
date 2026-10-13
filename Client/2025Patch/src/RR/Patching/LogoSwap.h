#pragma once

// ================================================================================
// LogoSwap - replaces Rec Room's logo with the Campus one.
//
// The logo is two 1024x1024 BC3 (DXT5) textures in the UI bundle
// StreamingAssets/aa/StandaloneWindows64/91aca73acb86d6607f0efa1f803348be.bundle:
//   RecRoomLogo_Sprite_Shadow  (the white-outlined sticker with its drop shadow)
//   RecRoomLogo_Sprite         (the flat orange one)
// Each has ONE mip and Unity streams its bytes straight to the GPU, unchanged.
//
// So instead of touching the bundle (the catalog may check its CRC, and an on-disk edit would
// have to be shipped to every player) or finding Unity's texture objects through il2cpp (this
// build keeps its UnityEngine modules out of the metadata), the swap happens where the pixels
// are handed to Direct3D 11: ID3D11Device::CreateTexture2D (initial data) and
// ID3D11DeviceContext::UpdateSubresource / UpdateSubresource1. A 1024x1024 BC3 upload whose
// bytes hash to one of the two ORIGINAL logos is given the Campus logo's bytes instead.
// Nothing else is touched: other textures are never read past their size/format check.
//
//  * Nothing here calls into the game's own code, so no RVA, no il2cpp, and it survives a
//    game rebuild as long as the logo textures themselves are unchanged.
//  * Everything that reads game memory is SEH-guarded; a fault falls through to the original.
//  * campus_nologo.txt next to the DLL switches it off (the call site checks it).
//  * Image data: LogoArt.h (generated; 1 MiB of BC3 in Unity's bottom-row-first order).
// ================================================================================

#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <cstdint>
#include <cstdio>
#include "../../Utils/deps/minhook/include/MinHook.h"
#include "LogoArt.h"

#pragma comment(lib, "d3d11.lib")

namespace LogoSwap {

    inline volatile LONG g_installed = 0;
    inline volatile LONG g_swapped = 0;     // uploads that were given the Campus logo
    inline volatile LONG g_seen = 0;        // 1024x1024 BC3 uploads examined (diagnostics)

    constexpr UINT kRowPitch = (LogoArt::kSize / 4) * 16;     // one row of 4x4 BC3 blocks

    // FNV-1a over 64-bit words, tail folded bytewise. tools: scratchpad logo_extract.py computes the same.
    inline uint64_t Fnv(const void* p, size_t n) {
        const uint64_t* w = static_cast<const uint64_t*>(p);
        const size_t cnt = n / 8;
        uint64_t h = 0xcbf29ce484222325ULL;
        for (size_t i = 0; i < cnt; i++) h = (h ^ w[i]) * 0x100000001b3ULL;
        const uint8_t* t = static_cast<const uint8_t*>(p) + cnt * 8;
        for (size_t i = 0; i < n - cnt * 8; i++) h = (h ^ t[i]) * 0x100000001b3ULL;
        return h;
    }

    // True if `data` (a 1024x1024 BC3 mip 0) is one of Rec Room's two original logo textures.
    inline bool IsOriginalLogo(const void* data) {
        if (!data) return false;
        __try {
            const uint64_t h = Fnv(data, LogoArt::kBytes);
            return h == LogoArt::kRecRoomLogoSpriteShadowHash || h == LogoArt::kRecRoomLogoSpriteHash;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    inline bool IsBc3(DXGI_FORMAT f) {
        return f == DXGI_FORMAT_BC3_UNORM || f == DXGI_FORMAT_BC3_UNORM_SRGB || f == DXGI_FORMAT_BC3_TYPELESS;
    }

    inline void NoteSwap(const char* how) {
        const LONG n = InterlockedIncrement(&g_swapped);
        printf("[Logo] Rec Room logo texture replaced (%s) #%ld\n", how, (long)n);
    }

    // Is `res` a 1024x1024 BC3 2D texture? (Cheap: GetType + GetDesc, no GPU work.)
    inline bool IsLogoSizedTexture(ID3D11Resource* res) {
        if (!res) return false;
        __try {
            D3D11_RESOURCE_DIMENSION dim{};
            res->GetType(&dim);
            if (dim != D3D11_RESOURCE_DIMENSION_TEXTURE2D) return false;
            D3D11_TEXTURE2D_DESC td{};
            static_cast<ID3D11Texture2D*>(res)->GetDesc(&td);
            return td.Width == (UINT)LogoArt::kSize && td.Height == (UINT)LogoArt::kSize && IsBc3(td.Format);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // A box that is null or covers the whole 1024x1024 image counts as "the whole texture".
    inline bool WholeImage(const D3D11_BOX* b) {
        return !b || (b->left == 0 && b->top == 0 && b->front == 0 &&
                      b->right == (UINT)LogoArt::kSize && b->bottom == (UINT)LogoArt::kSize && b->back == 1);
    }

    // ---- ID3D11Device::CreateTexture2D ---------------------------------------------------

    using CreateTexture2D_t = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_TEXTURE2D_DESC*,
                                                          const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);
    inline CreateTexture2D_t oCreateTexture2D = nullptr;

    inline HRESULT STDMETHODCALLTYPE CreateTexture2D_H(ID3D11Device* self, const D3D11_TEXTURE2D_DESC* d,
                                                       const D3D11_SUBRESOURCE_DATA* init, ID3D11Texture2D** out) {
        if (d && init && d->Width == (UINT)LogoArt::kSize && d->Height == (UINT)LogoArt::kSize && d->ArraySize == 1 &&
            IsBc3(d->Format) && init[0].pSysMem && init[0].SysMemPitch == kRowPitch) {
            InterlockedIncrement(&g_seen);
            if (IsOriginalLogo(init[0].pSysMem)) {
                // Same array of subresources, mip 0 pointing at the Campus logo. (The originals have one mip;
                // if a texture ever came with more, those lower mips keep their data.)
                D3D11_SUBRESOURCE_DATA repl[16];
                UINT n = d->MipLevels ? d->MipLevels : 1;
                if (n > 16) n = 16;
                for (UINT i = 0; i < n; i++) repl[i] = init[i];
                repl[0].pSysMem = LogoArt::kBc3;
                repl[0].SysMemPitch = kRowPitch;
                NoteSwap("CreateTexture2D");
                return oCreateTexture2D(self, d, repl, out);
            }
        }
        return oCreateTexture2D(self, d, init, out);
    }

    // ---- ID3D11DeviceContext::UpdateSubresource / UpdateSubresource1 ----------------------

    using UpdateSubresource_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, const D3D11_BOX*,
                                                         const void*, UINT, UINT);
    inline UpdateSubresource_t oUpdateSubresource = nullptr;
    inline UpdateSubresource_t oUpdateSubresourceDeferred = nullptr;      // a deferred context has its own vtable

    inline const void* MaybeSwap(ID3D11Resource* res, UINT sub, const D3D11_BOX* box, const void* src, UINT rowPitch) {
        if (sub == 0 && src && rowPitch == kRowPitch && WholeImage(box) && IsLogoSizedTexture(res)) {
            InterlockedIncrement(&g_seen);
            if (IsOriginalLogo(src)) { NoteSwap("UpdateSubresource"); return LogoArt::kBc3; }
        }
        return src;
    }

    inline void STDMETHODCALLTYPE UpdateSubresource_H(ID3D11DeviceContext* c, ID3D11Resource* res, UINT sub, const D3D11_BOX* box,
                                                      const void* src, UINT rowPitch, UINT depthPitch) {
        src = MaybeSwap(res, sub, box, src, rowPitch);
        oUpdateSubresource(c, res, sub, box, src, rowPitch, depthPitch);
    }
    inline void STDMETHODCALLTYPE UpdateSubresourceDeferred_H(ID3D11DeviceContext* c, ID3D11Resource* res, UINT sub, const D3D11_BOX* box,
                                                              const void* src, UINT rowPitch, UINT depthPitch) {
        src = MaybeSwap(res, sub, box, src, rowPitch);
        oUpdateSubresourceDeferred(c, res, sub, box, src, rowPitch, depthPitch);
    }

    using UpdateSubresource1_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11Resource*, UINT, const D3D11_BOX*,
                                                          const void*, UINT, UINT, UINT);
    inline UpdateSubresource1_t oUpdateSubresource1 = nullptr;
    inline UpdateSubresource1_t oUpdateSubresource1Deferred = nullptr;

    inline void STDMETHODCALLTYPE UpdateSubresource1_H(ID3D11DeviceContext1* c, ID3D11Resource* res, UINT sub, const D3D11_BOX* box,
                                                       const void* src, UINT rowPitch, UINT depthPitch, UINT copyFlags) {
        src = MaybeSwap(res, sub, box, src, rowPitch);
        oUpdateSubresource1(c, res, sub, box, src, rowPitch, depthPitch, copyFlags);
    }
    inline void STDMETHODCALLTYPE UpdateSubresource1Deferred_H(ID3D11DeviceContext1* c, ID3D11Resource* res, UINT sub, const D3D11_BOX* box,
                                                               const void* src, UINT rowPitch, UINT depthPitch, UINT copyFlags) {
        src = MaybeSwap(res, sub, box, src, rowPitch);
        oUpdateSubresource1Deferred(c, res, sub, box, src, rowPitch, depthPitch, copyFlags);
    }

    // ---- install -------------------------------------------------------------------------

    inline bool HookAt(void* target, void* detour, void** original, const char* what) {
        if (!target) return false;
        MH_STATUS s = MH_CreateHook(target, detour, original);
        if (s == MH_OK) s = MH_EnableHook(target);
        printf("[Logo] hook %-28s -> %s\n", what, s == MH_OK ? "OK" : (s == MH_ERROR_ALREADY_CREATED ? "shared" : "FAIL"));
        return s == MH_OK;
    }

    // The d3d11 methods are shared by every device in the process, so a throwaway device is enough to
    // read their addresses - the game's own device does not have to exist yet (it usually does not).
    // MinHook must already be initialised (RR::Patches::Patch does that).
    inline bool Install() {
        if (InterlockedCompareExchange(&g_installed, 1, 0)) return true;

        ID3D11Device* dev = nullptr;
        ID3D11DeviceContext* ctx = nullptr;
        D3D_FEATURE_LEVEL fl{};
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
        if (FAILED(hr)) hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
        if (FAILED(hr) || !dev || !ctx) {
            printf("[Logo] no D3D11 device to read the method table from (0x%08X) - logo swap off\n", (unsigned)hr);
            InterlockedExchange(&g_installed, 0);
            return false;
        }

        void** dvt = *reinterpret_cast<void***>(dev);
        void** cvt = *reinterpret_cast<void***>(ctx);
        bool ok = HookAt(dvt[5], (void*)CreateTexture2D_H, (void**)&oCreateTexture2D, "Device::CreateTexture2D");
        HookAt(cvt[48], (void*)UpdateSubresource_H, (void**)&oUpdateSubresource, "Context::UpdateSubresource");

        ID3D11DeviceContext1* ctx1 = nullptr;
        if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&ctx1)) && ctx1) {
            void** c1 = *reinterpret_cast<void***>(ctx1);
            HookAt(c1[116], (void*)UpdateSubresource1_H, (void**)&oUpdateSubresource1, "Context1::UpdateSubresource1");
            ctx1->Release();
        }

        // Deferred contexts (Unity can record uploads on a worker) are separate classes with their own methods.
        ID3D11DeviceContext* def = nullptr;
        if (SUCCEEDED(dev->CreateDeferredContext(0, &def)) && def) {
            void** dv = *reinterpret_cast<void***>(def);
            if (dv[48] != cvt[48])
                HookAt(dv[48], (void*)UpdateSubresourceDeferred_H, (void**)&oUpdateSubresourceDeferred, "DeferredContext::UpdateSubresource");
            ID3D11DeviceContext1* def1 = nullptr;
            if (SUCCEEDED(def->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&def1)) && def1) {
                void** d1 = *reinterpret_cast<void***>(def1);
                void** i1 = nullptr; ID3D11DeviceContext1* c1b = nullptr;
                if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&c1b)) && c1b) { i1 = *reinterpret_cast<void***>(c1b); c1b->Release(); }
                if (!i1 || d1[116] != i1[116])
                    HookAt(d1[116], (void*)UpdateSubresource1Deferred_H, (void**)&oUpdateSubresource1Deferred, "Deferred::UpdateSubresource1");
                def1->Release();
            }
            def->Release();
        }

        ctx->Release();
        dev->Release();
        printf("[Logo] Campus logo swap %s\n", ok ? "armed" : "NOT armed");
        return ok;
    }
}
