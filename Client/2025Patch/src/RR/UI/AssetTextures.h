#pragma once

// ================================================================================
// PNG -> D3D11 texture loading for the custom menu.
//
// Decoding goes through WIC (Windows Imaging Component), which ships with Windows.
// No stb_image, no new third-party header - the project already carries enough
// vendored code, and WIC is what the D3D sample loaders (WICTextureLoader) use.
//
// Textures are handed to ImGui as ImTextureID. That is NOT the same as building the
// UI out of ImGui widgets: the DX11 backend's ImTextureID is literally an
// ID3D11ShaderResourceView*, so drawing an artist's PNG through ImDrawList::AddImage
// gives a pixel-exact sprite with nothing of ImGui's own look. ImGui here is only a
// textured-quad renderer plus hit-testing - which saves writing a sprite pipeline,
// vertex buffers and input handling from scratch.
// ================================================================================

#include <windows.h>
#include <d3d11.h>
#include <wincodec.h>
#include <string>
#include <unordered_map>
#include <iostream>
#include "../Patching/Console.h"

#pragma comment(lib, "windowscodecs.lib")

namespace AssetTextures {

    struct Texture {
        ID3D11ShaderResourceView* srv = nullptr;
        int width = 0;
        int height = 0;
        explicit operator bool() const { return srv != nullptr; }
    };

    inline std::unordered_map<std::string, Texture> g_cache;
    inline std::string g_assetDir;
    inline bool g_dirResolved = false;

    // The DLL lives in <install>\CampusPatch\Patch\ and the art in
    // <install>\CampusPatch\Assets\. Resolve relative to THIS module rather than the
    // game exe - the two are in different folders, and UIThemes' GetConfigPath
    // (which uses the exe) deliberately points somewhere else.
    inline std::string ResolveAssetDir() {
        if (g_dirResolved) return g_assetDir;
        g_dirResolved = true;

        HMODULE self = nullptr;
        GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCSTR)&ResolveAssetDir, &self);

        char buf[MAX_PATH]{};
        GetModuleFileNameA(self, buf, MAX_PATH);
        std::string dir(buf);
        size_t slash = dir.find_last_of("\\/");
        dir = (slash == std::string::npos) ? "." : dir.substr(0, slash);

        // Try the layouts this could ship in, nearest first.
        const std::string candidates[] = {
            dir + "\\Assets",          // Patch\Assets
            dir + "\\..\\Assets",      // CampusPatch\Assets  <- the real one
            dir + "\\..\\..\\Assets",
        };

        for (const auto& c : candidates) {
            DWORD attr = GetFileAttributesA(c.c_str());
            if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) {
                g_assetDir = c + "\\";
                std::cout << TAG_INIT() << "assets: " << g_assetDir << "\n";
                return g_assetDir;
            }
        }

        std::cout << TAG_WARN() << "assets folder not found near " << dir
            << " - the menu will draw untextured\n";
        g_assetDir.clear();
        return g_assetDir;
    }

    // Decode one PNG to a 32bpp RGBA texture. Returns an empty Texture on any failure;
    // callers must handle that rather than assuming art is present.
    inline Texture LoadFile(ID3D11Device* device, const std::string& filename) {
        Texture out;
        if (!device) return out;

        auto it = g_cache.find(filename);
        if (it != g_cache.end()) return it->second;

        std::string dir = ResolveAssetDir();
        if (dir.empty()) { g_cache[filename] = out; return out; }

        std::string full = dir + filename;
        std::wstring wide(full.begin(), full.end());

        // COM may already be initialised on this thread by the game; both S_OK and
        // S_FALSE / RPC_E_CHANGED_MODE mean "usable", so don't treat those as errors.
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        IWICImagingFactory* factory = nullptr;
        IWICBitmapDecoder* decoder = nullptr;
        IWICBitmapFrameDecode* frame = nullptr;
        IWICFormatConverter* converter = nullptr;

        auto cleanup = [&] {
            if (converter) converter->Release();
            if (frame)     frame->Release();
            if (decoder)   decoder->Release();
            if (factory)   factory->Release();
            };

        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&factory)))) {
            std::cout << TAG_WARN() << "WIC factory unavailable\n";
            cleanup(); g_cache[filename] = out; return out;
        }

        if (FAILED(factory->CreateDecoderFromFilename(wide.c_str(), nullptr, GENERIC_READ,
            WICDecodeMetadataCacheOnDemand, &decoder))
            || FAILED(decoder->GetFrame(0, &frame))
            || FAILED(factory->CreateFormatConverter(&converter))
            || FAILED(converter->Initialize(frame, GUID_WICPixelFormat32bppRGBA,
                WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom))) {
            std::cout << TAG_WARN() << "could not decode " << filename << "\n";
            cleanup(); g_cache[filename] = out; return out;
        }

        UINT w = 0, h = 0;
        converter->GetSize(&w, &h);
        if (w == 0 || h == 0 || w > 8192 || h > 8192) {
            cleanup(); g_cache[filename] = out; return out;
        }

        const UINT stride = w * 4;
        std::string pixels(size_t(stride) * h, '\0');
        if (FAILED(converter->CopyPixels(nullptr, stride, (UINT)pixels.size(),
            reinterpret_cast<BYTE*>(&pixels[0])))) {
            cleanup(); g_cache[filename] = out; return out;
        }

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w;
        desc.Height = h;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        D3D11_SUBRESOURCE_DATA init{};
        init.pSysMem = pixels.data();
        init.SysMemPitch = stride;

        ID3D11Texture2D* tex = nullptr;
        if (SUCCEEDED(device->CreateTexture2D(&desc, &init, &tex)) && tex) {
            D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
            srvDesc.Format = desc.Format;
            srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            srvDesc.Texture2D.MipLevels = 1;

            if (SUCCEEDED(device->CreateShaderResourceView(tex, &srvDesc, &out.srv))) {
                out.width = (int)w;
                out.height = (int)h;
                std::cout << TAG_INIT() << "loaded " << filename
                    << " (" << w << "x" << h << ")\n";
            }
            tex->Release();   // the SRV holds its own reference
        }

        cleanup();
        g_cache[filename] = out;
        return out;
    }

    inline void ReleaseAll() {
        for (auto& kv : g_cache)
            if (kv.second.srv) kv.second.srv->Release();
        g_cache.clear();
    }
}
