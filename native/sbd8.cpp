// Direct3D 8: показники прямо в кадрі гри. Окрема одиниця компіляції: заголовки d3d8.h і d3d9.h визначають однакові імена.
// Малюємо чотирикутник із текстурою (керована пам'ять, тож скидання пристрою їй не шкодить), стан гри зберігаємо блоком стану.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <d3d8.h>

struct D8Res { IDirect3DDevice8* dev; IDirect3DTexture8* tex; int w, h; uint32_t gen; };
static D8Res r8 = {};

extern "C" void SbD8Free()
{
    if (r8.tex) r8.tex->Release();
    if (r8.dev) r8.dev->Release();
    r8 = D8Res();
}

// адреса ЯЧЕЙКИ IDirect3D8::CreateDevice (індекс 15) у таблиці методів: без створення пристрою, тож безпечно; nullptr - не вдалося.
// Таблиця методів спільна для всіх об'єктів класу, тож підміна ячейки діє і на об'єкт, який створить сама гра.
extern "C" void* SbD8CreateAddr()
{
    HMODULE m = nullptr;                                  // закріплюємо: гра не вивантажить d3d8.dll з-під нас
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, L"d3d8.dll", &m)) m = nullptr;
    if (!m) return nullptr;
    typedef IDirect3D8* (WINAPI* Create_t)(UINT);
    Create_t create = (Create_t)(void*)GetProcAddress(m, "Direct3DCreate8");
    if (!create) return nullptr;
    IDirect3D8* d3d = create(D3D_SDK_VERSION);
    if (!d3d) return nullptr;
    void* res = (void*)&(*(void***)d3d)[15];
    d3d->Release();
    return res;
}

// адреса ячейки IDirect3DDevice8::Present (індекс 15) пристрою, який щойно створила гра
extern "C" void* SbD8PresentOf(void* dev) { return dev ? (void*)&(*(void***)dev)[15] : nullptr; }

// адреса IDirect3DDevice8::Present через тимчасовий пристрій; nullptr - не вдалося, *hr - код помилки.
// Гра може тримати адаптер у повноекрані або саме створювати свій пристрій, тому пробуємо лише один раз і найлегшим способом.
extern "C" void* SbD8PresentAddr(HWND w, long* hr)
{
    *hr = 0;
    HMODULE m = nullptr;                                  // закріплюємо: гра не вивантажить d3d8.dll з-під нас
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, L"d3d8.dll", &m)) m = nullptr;
    if (!m || !w) { *hr = -1; return nullptr; }
    typedef IDirect3D8* (WINAPI* Create_t)(UINT);
    Create_t create = (Create_t)(void*)GetProcAddress(m, "Direct3DCreate8");
    if (!create) { *hr = -2; return nullptr; }
    IDirect3D8* d3d = create(D3D_SDK_VERSION);
    if (!d3d) { *hr = -3; return nullptr; }
    void* res = nullptr;
    D3DDISPLAYMODE dm = {};
    HRESULT h = d3d->GetAdapterDisplayMode(D3DADAPTER_DEFAULT, &dm);
    if (SUCCEEDED(h))
    {
        D3DPRESENT_PARAMETERS pp = {};
        pp.Windowed = TRUE;
        pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
        pp.BackBufferFormat = dm.Format;
        pp.BackBufferWidth = 64;
        pp.BackBufferHeight = 64;
        pp.hDeviceWindow = w;
        IDirect3DDevice8* dev = nullptr;
        h = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, w, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, &dev);
        if (SUCCEEDED(h) && dev)
        {
            res = (void*)&(*(void***)dev)[15];
            dev->Release();
        }
    }
    *hr = (long)h;
    d3d->Release();
    return res;
}

// розмір буфера кадру; 0 - добре
extern "C" int SbD8Size(void* d, int* W, int* H)
{
    IDirect3DDevice8* dev = (IDirect3DDevice8*)d;
    IDirect3DSurface8* bb = nullptr;
    if (FAILED(dev->GetBackBuffer(0, D3DBACKBUFFER_TYPE_MONO, &bb)) || !bb) return 31;
    D3DSURFACE_DESC sd = {};
    int r = FAILED(bb->GetDesc(&sd)) ? 32 : 0;
    bb->Release();
    *W = (int)sd.Width; *H = (int)sd.Height;
    return r;
}

// 0 - намальовано; 1 - формат буфера не підтримується; інше - код помилки
extern "C" int SbD8Draw(void* d, const uint32_t* bits, int w, int h, uint32_t gen, int x, int y)
{
    IDirect3DDevice8* dev = (IDirect3DDevice8*)d;
    if (FAILED(dev->TestCooperativeLevel())) return 33;
    if (r8.dev != dev) { SbD8Free(); r8.dev = dev; dev->AddRef(); }
    IDirect3DSurface8 *bb = nullptr, *rt = nullptr;
    if (FAILED(dev->GetBackBuffer(0, D3DBACKBUFFER_TYPE_MONO, &bb)) || !bb) return 31;
    D3DSURFACE_DESC sd = {};
    bb->GetDesc(&sd);
    int ret = 0;
    // малюємо лише в той буфер, що зараз є ціллю візуалізації (на момент Present це майже завжди так)
    if (FAILED(dev->GetRenderTarget(&rt)) || !rt || rt != bb) ret = 34;
    if (rt) rt->Release();
    bb->Release();
    if (ret) return ret;
    switch (sd.Format)
    {
        case D3DFMT_A8R8G8B8: case D3DFMT_X8R8G8B8: case D3DFMT_R5G6B5: case D3DFMT_X1R5G5B5: case D3DFMT_A1R5G5B5: break;
        default: return 1;
    }
    if (!r8.tex || r8.w != w || r8.h != h)
    {
        if (r8.tex) { r8.tex->Release(); r8.tex = nullptr; }
        if (FAILED(dev->CreateTexture(w, h, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &r8.tex)) || !r8.tex) { r8.tex = nullptr; return 35; }
        r8.w = w; r8.h = h; r8.gen = 0xFFFFFFFFu;
    }
    if (r8.gen != gen)
    {
        D3DLOCKED_RECT lr;
        if (FAILED(r8.tex->LockRect(0, &lr, nullptr, 0))) return 36;
        for (int r = 0; r < h; r++) memcpy((BYTE*)lr.pBits + (size_t)r * lr.Pitch, bits + (size_t)r * w, (size_t)w * 4);
        r8.tex->UnlockRect(0);
        r8.gen = gen;
    }
    DWORD tok = 0;
    if (FAILED(dev->CreateStateBlock(D3DSBT_ALL, &tok)) || !tok) return 37;
    bool began = SUCCEEDED(dev->BeginScene());
    D3DVIEWPORT8 vp = { 0, 0, sd.Width, sd.Height, 0.0f, 1.0f };
    dev->SetViewport(&vp);
    dev->SetVertexShader(D3DFVF_XYZRHW | D3DFVF_TEX1);
    dev->SetPixelShader(0);
    dev->SetTexture(0, r8.tex);
    dev->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
    dev->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
    dev->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
    dev->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
    dev->SetTextureStageState(0, D3DTSS_TEXCOORDINDEX, 0);
    dev->SetTextureStageState(0, D3DTSS_TEXTURETRANSFORMFLAGS, D3DTTFF_DISABLE);
    dev->SetTextureStageState(0, D3DTSS_MINFILTER, D3DTEXF_POINT);
    dev->SetTextureStageState(0, D3DTSS_MAGFILTER, D3DTEXF_POINT);
    dev->SetTextureStageState(0, D3DTSS_MIPFILTER, D3DTEXF_NONE);
    dev->SetTextureStageState(0, D3DTSS_ADDRESSU, D3DTADDRESS_CLAMP);
    dev->SetTextureStageState(0, D3DTSS_ADDRESSV, D3DTADDRESS_CLAMP);
    dev->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE);
    dev->SetTextureStageState(1, D3DTSS_ALPHAOP, D3DTOP_DISABLE);
    dev->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
    dev->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);
    dev->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
    dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);
    dev->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
    dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
    dev->SetRenderState(D3DRS_LIGHTING, FALSE);
    dev->SetRenderState(D3DRS_FOGENABLE, FALSE);
    dev->SetRenderState(D3DRS_STENCILENABLE, FALSE);
    dev->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);
    dev->SetRenderState(D3DRS_COLORWRITEENABLE, 0xF);
    dev->SetRenderState(D3DRS_FILLMODE, D3DFILL_SOLID);
    dev->SetRenderState(D3DRS_SPECULARENABLE, FALSE);
    dev->SetRenderState(D3DRS_CLIPPING, FALSE);
    float x0 = (float)x - 0.5f, y0 = (float)y - 0.5f, x1 = (float)(x + w) - 0.5f, y1 = (float)(y + h) - 0.5f;
    struct Vtx { float x, y, z, rhw, u, v; };
    Vtx q[4] = { { x0, y0, 0.0f, 1.0f, 0.0f, 0.0f }, { x1, y0, 0.0f, 1.0f, 1.0f, 0.0f }, { x0, y1, 0.0f, 1.0f, 0.0f, 1.0f }, { x1, y1, 0.0f, 1.0f, 1.0f, 1.0f } };
    HRESULT hr = dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, q, sizeof(Vtx));
    if (began) dev->EndScene();
    dev->ApplyStateBlock(tok);
    dev->DeleteStateBlock(tok);
    return SUCCEEDED(hr) ? 0 : 38;
}
