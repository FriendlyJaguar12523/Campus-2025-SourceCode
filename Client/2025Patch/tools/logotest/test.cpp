// Offline test of LogoSwap against a real D3D11 device (hardware or WARP).
#include "RR/Patching/LogoSwap.h"
#include <vector>
#include <string>
#include <cstdlib>
#include <cstring>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

static std::vector<uint8_t> ReadFile(const char* p) {
    std::vector<uint8_t> v; FILE* f = fopen(p, "rb"); if (!f) { printf("cannot open %s\n", p); exit(2); }
    fseek(f, 0, SEEK_END); v.resize(ftell(f)); fseek(f, 0, SEEK_SET); fread(v.data(), 1, v.size(), f); fclose(f); return v;
}
static int g_fail = 0;
static void Check(bool ok, const char* what) { printf("%s  %s\n", ok ? "PASS" : "FAIL", what); if (!ok) g_fail++; }

static ComPtr<ID3D11Device> dev; static ComPtr<ID3D11DeviceContext> ctx;

static D3D11_TEXTURE2D_DESC Desc(UINT w, UINT h, DXGI_FORMAT f) {
    D3D11_TEXTURE2D_DESC d{}; d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1; d.Format = f;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE; return d;
}
// copy mip 0 of `tex` back to the CPU
static std::vector<uint8_t> ReadBack(ID3D11Texture2D* tex, UINT w, UINT h, DXGI_FORMAT f) {
    D3D11_TEXTURE2D_DESC d = Desc(w, h, f); d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> st; if (FAILED(dev->CreateTexture2D(&d, nullptr, &st))) { printf("staging create failed\n"); exit(3); }
    ctx->CopyResource(st.Get(), tex);
    D3D11_MAPPED_SUBRESOURCE m{}; if (FAILED(ctx->Map(st.Get(), 0, D3D11_MAP_READ, 0, &m))) { printf("map failed\n"); exit(3); }
    std::vector<uint8_t> out; UINT rows = h / 4, rowBytes = (w / 4) * 16;
    for (UINT r = 0; r < rows; r++) { auto* s = (const uint8_t*)m.pData + (size_t)r * m.RowPitch; out.insert(out.end(), s, s + rowBytes); }
    ctx->Unmap(st.Get(), 0); return out;
}
static bool IsArt(const std::vector<uint8_t>& v) { return v.size() == LogoArt::kBytes && memcmp(v.data(), LogoArt::kBc3, LogoArt::kBytes) == 0; }
static bool Same(const std::vector<uint8_t>& v, const std::vector<uint8_t>& o) { return v.size() == o.size() && memcmp(v.data(), o.data(), v.size()) == 0; }

int main(int argc, char** argv) {
    std::string dir = argc > 1 ? argv[1] : ".";
    auto shadow = ReadFile((dir + "/RecRoomLogo_Sprite_Shadow.raw").c_str());
    auto plain = ReadFile((dir + "/RecRoomLogo_Sprite.raw").c_str());
    printf("original shadow hash %016llx (header %016llx)\n", (unsigned long long)LogoSwap::Fnv(shadow.data(), shadow.size()), (unsigned long long)LogoArt::kRecRoomLogoSpriteShadowHash);
    Check(LogoSwap::Fnv(shadow.data(), shadow.size()) == LogoArt::kRecRoomLogoSpriteShadowHash, "header hash == hash of the bundle's Shadow data");
    Check(LogoSwap::Fnv(plain.data(), plain.size()) == LogoArt::kRecRoomLogoSpriteHash, "header hash == hash of the bundle's plain data");

    if (MH_Initialize() != MH_OK) { printf("MH_Initialize failed\n"); return 2; }
    Check(LogoSwap::Install(), "LogoSwap::Install()");

    // the "game's" device, created AFTER the hooks like Unity's would be
    D3D_FEATURE_LEVEL fl{};
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr)) hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr)) { printf("no device\n"); return 2; }

    const UINT pitch = 4096;
    for (DXGI_FORMAT f : { DXGI_FORMAT_BC3_UNORM_SRGB, DXGI_FORMAT_BC3_UNORM }) {
        const char* fn = f == DXGI_FORMAT_BC3_UNORM_SRGB ? "BC3_UNORM_SRGB" : "BC3_UNORM";
        char msg[160];
        // 1) CreateTexture2D with initial data: both originals
        for (int which = 0; which < 2; which++) {
            auto& src = which ? plain : shadow;
            D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); D3D11_SUBRESOURCE_DATA sd{ src.data(), pitch, 0 };
            ComPtr<ID3D11Texture2D> t; hr = dev->CreateTexture2D(&d, &sd, &t);
            snprintf(msg, sizeof msg, "%s CreateTexture2D(original %s) -> Campus art", fn, which ? "plain" : "Shadow");
            Check(SUCCEEDED(hr) && IsArt(ReadBack(t.Get(), 1024, 1024, f)), msg);
        }
        // 2) an unrelated 1024x1024 BC3 texture is left alone
        {
            std::vector<uint8_t> other(1024 * 1024); for (size_t i = 0; i < other.size(); i++) other[i] = (uint8_t)(i * 31 + 7);
            D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); D3D11_SUBRESOURCE_DATA sd{ other.data(), pitch, 0 };
            ComPtr<ID3D11Texture2D> t; hr = dev->CreateTexture2D(&d, &sd, &t);
            snprintf(msg, sizeof msg, "%s CreateTexture2D(unrelated 1024 BC3) untouched", fn);
            Check(SUCCEEDED(hr) && Same(ReadBack(t.Get(), 1024, 1024, f), other), msg);
        }
        // 3) same bytes at another size/format are left alone (a 512x512 crop of the Shadow data)
        {
            D3D11_TEXTURE2D_DESC d = Desc(512, 512, f); D3D11_SUBRESOURCE_DATA sd{ shadow.data(), 2048, 0 };
            ComPtr<ID3D11Texture2D> t; hr = dev->CreateTexture2D(&d, &sd, &t);
            std::vector<uint8_t> want(shadow.begin(), shadow.begin() + 512 * 512);
            snprintf(msg, sizeof msg, "%s CreateTexture2D(512x512) untouched", fn);
            Check(SUCCEEDED(hr) && Same(ReadBack(t.Get(), 512, 512, f), want), msg);
        }
        // 4) UpdateSubresource (null box)
        {
            D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); ComPtr<ID3D11Texture2D> t; dev->CreateTexture2D(&d, nullptr, &t);
            ctx->UpdateSubresource(t.Get(), 0, nullptr, shadow.data(), pitch, 0);
            snprintf(msg, sizeof msg, "%s UpdateSubresource(original Shadow) -> Campus art", fn);
            Check(IsArt(ReadBack(t.Get(), 1024, 1024, f)), msg);
        }
        // 5) UpdateSubresource with an explicit full box
        {
            D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); ComPtr<ID3D11Texture2D> t; dev->CreateTexture2D(&d, nullptr, &t);
            D3D11_BOX b{ 0, 0, 0, 1024, 1024, 1 };
            ctx->UpdateSubresource(t.Get(), 0, &b, plain.data(), pitch, 0);
            snprintf(msg, sizeof msg, "%s UpdateSubresource(full box, original plain) -> Campus art", fn);
            Check(IsArt(ReadBack(t.Get(), 1024, 1024, f)), msg);
        }
        // 6) UpdateSubresource1
        {
            ComPtr<ID3D11DeviceContext1> c1; if (SUCCEEDED(ctx.As(&c1))) {
                D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); ComPtr<ID3D11Texture2D> t; dev->CreateTexture2D(&d, nullptr, &t);
                c1->UpdateSubresource1(t.Get(), 0, nullptr, plain.data(), pitch, 0, 0);
                snprintf(msg, sizeof msg, "%s UpdateSubresource1(original plain) -> Campus art", fn);
                Check(IsArt(ReadBack(t.Get(), 1024, 1024, f)), msg);
            } else printf("SKIP  ID3D11DeviceContext1 not available\n");
        }
        // 7) deferred context UpdateSubresource, then executed
        {
            ComPtr<ID3D11DeviceContext> def; ComPtr<ID3D11CommandList> cl;
            D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); ComPtr<ID3D11Texture2D> t; dev->CreateTexture2D(&d, nullptr, &t);
            if (SUCCEEDED(dev->CreateDeferredContext(0, &def))) {
                def->UpdateSubresource(t.Get(), 0, nullptr, shadow.data(), pitch, 0);
                def->FinishCommandList(FALSE, &cl); ctx->ExecuteCommandList(cl.Get(), FALSE);
                snprintf(msg, sizeof msg, "%s deferred-context UpdateSubresource(original Shadow) -> Campus art", fn);
                Check(IsArt(ReadBack(t.Get(), 1024, 1024, f)), msg);
            }
        }
        // 8) UpdateSubresource of an unrelated image into a logo-sized texture is left alone
        {
            std::vector<uint8_t> other(1024 * 1024, 0x5A);
            D3D11_TEXTURE2D_DESC d = Desc(1024, 1024, f); ComPtr<ID3D11Texture2D> t; dev->CreateTexture2D(&d, nullptr, &t);
            ctx->UpdateSubresource(t.Get(), 0, nullptr, other.data(), pitch, 0);
            snprintf(msg, sizeof msg, "%s UpdateSubresource(unrelated) untouched", fn);
            Check(Same(ReadBack(t.Get(), 1024, 1024, f), other), msg);
        }
    }
    printf("swapped %ld, examined %ld 1024 BC3 uploads\n", (long)LogoSwap::g_swapped, (long)LogoSwap::g_seen);
    printf(g_fail ? "\n%d FAILED\n" : "\nALL PASSED\n", g_fail);
    return g_fail ? 1 : 0;
}
