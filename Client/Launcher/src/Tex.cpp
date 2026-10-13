#include "Tex.h"
#include <windows.h>
#include <d3d11.h>
#include <wincodec.h>
#include <vector>

static ID3D11Device* g_dev = nullptr;
void TexSetDevice(ID3D11Device* dev) { g_dev = dev; }

bool DecodeImage(const void* data, size_t size, std::vector<uint8_t>& rgba, int& w, int& h)
{
    IWICImagingFactory* wic = nullptr;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)))) return false;
    bool ok = false;
    IWICStream* stream = nullptr;
    IWICBitmapDecoder* dec = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICFormatConverter* conv = nullptr;
    if (SUCCEEDED(wic->CreateStream(&stream)) &&
        SUCCEEDED(stream->InitializeFromMemory((BYTE*)data, (DWORD)size)) &&
        SUCCEEDED(wic->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnLoad, &dec)) &&
        SUCCEEDED(dec->GetFrame(0, &frame)) &&
        SUCCEEDED(wic->CreateFormatConverter(&conv)) &&
        SUCCEEDED(conv->Initialize(frame, GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
    {
        UINT uw = 0, uh = 0;
        conv->GetSize(&uw, &uh);
        if (uw && uh && uw <= 8192 && uh <= 8192)
        {
            rgba.assign((size_t)uw * uh * 4, 0);
            ok = SUCCEEDED(conv->CopyPixels(nullptr, uw * 4, (UINT)rgba.size(), rgba.data()));
            w = (int)uw; h = (int)uh;
        }
    }
    if (conv) conv->Release();
    if (frame) frame->Release();
    if (dec) dec->Release();
    if (stream) stream->Release();
    wic->Release();
    return ok;
}

bool Tex::Load(const void* data, size_t size)
{
    Release();
    if (!g_dev || !data || !size) return false;
    std::vector<uint8_t> px;
    int pw = 0, ph = 0;
    if (!DecodeImage(data, size, px, pw, ph)) return false;
    return LoadPixels(px.data(), pw, ph);
}

bool Tex::LoadPixels(const uint8_t* rgba, int pw, int ph)
{
    Release();
    if (!g_dev || !rgba || pw <= 0 || ph <= 0) return false;
    D3D11_TEXTURE2D_DESC td{};
    td.Width = pw; td.Height = ph; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA sd{ rgba, (UINT)pw * 4, 0 };
    ID3D11Texture2D* tex = nullptr;
    if (FAILED(g_dev->CreateTexture2D(&td, &sd, &tex))) return false;
    D3D11_SHADER_RESOURCE_VIEW_DESC vd{};
    vd.Format = td.Format; vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; vd.Texture2D.MipLevels = 1;
    HRESULT hr = g_dev->CreateShaderResourceView(tex, &vd, &srv);
    tex->Release();
    if (FAILED(hr)) { srv = nullptr; return false; }
    w = pw; h = ph;
    return true;
}

void Tex::Release()
{
    if (srv) srv->Release();
    srv = nullptr; w = h = 0;
}

const void* ResourceData(int id, size_t& size)
{
    size = 0;
    HRSRC r = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    if (!r) return nullptr;
    HGLOBAL g = LoadResource(nullptr, r);
    const void* p = g ? LockResource(g) : nullptr;
    if (p) size = SizeofResource(nullptr, r);
    return p;
}
