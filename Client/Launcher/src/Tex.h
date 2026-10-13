#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

struct ID3D11Device;
struct ID3D11ShaderResourceView;

// A picture on the GPU for ImGui::Image / AddImage. PNG, JPEG, BMP... decoded by Windows (WIC).
struct Tex
{
    ID3D11ShaderResourceView* srv = nullptr;
    int w = 0, h = 0;
    bool Valid() const { return srv != nullptr; }
    void* Id() const { return srv; }
    bool Load(const void* data, size_t size);     // replaces what it held
    bool LoadPixels(const uint8_t* rgba, int w, int h);
    void Release();
};

void TexSetDevice(ID3D11Device* dev);

// PNG / JPEG / ... -> RGBA8 pixels (WIC).
#include <vector>
bool DecodeImage(const void* data, size_t size, std::vector<uint8_t>& rgba, int& w, int& h);

// The exe's RCDATA resource `id` (no copy; null when missing).
const void* ResourceData(int id, size_t& size);
