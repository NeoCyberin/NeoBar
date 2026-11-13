// ======================================================================
//  NeoBarWall - рушій слайд-шоу й живих шпалер. Це DLL: NeoBar запускає її окремим процесом через
//  системний rundll32.exe (rundll32 "NeoBarWall64.dll",RunW <аргументи>), тож окремого .exe немає.
//
//  Чому окремий процес, а не вікно всередині NeoBar:
//   * NeoBar часто працює від адміністратора (оверлей і ліміт FPS для ігор), а Провідник - зі
//     звичайними правами. Вікно процесу адміністратора, вбудоване у вікно Провідника, Windows
//     може не малювати й не пропускати до нього повідомлення. NeoBar запускає цей процес зі
//     звичайними правами (батьком стає Провідник), тож вікно шпалер «своє» для Провідника.
//   * Падіння відеодекодера чи драйвера не зачіпає панель; NeoBar просто перезапускає рушій.
//
//  Що робить:
//   * знаходить шар робочого столу під іконками (класичний WorkerW або «піднятий» робочий стіл
//     Windows 11 24H2+, де іконки й WorkerW - дочірні вікна Progman) і створює там по вікну
//     на монітор;
//   * малює через Direct3D 11 + Direct2D: зображення (WIC, з поворотом за EXIF) і переходи
//     (згасання, зсув, наближення, шторки), GIF (WIC, власне складання кадрів), відео - Media
//     Foundation Media Engine з апаратним декодуванням (кадр одразу у текстуру вікна);
//   * між переходами нічого не малює; відео й GIF - лише нові кадри, у паузі - нічого.
//
//  Керування - WM_COPYDATA від NeoBar (рядок UTF-16, поля через табуляцію):
//     image <шлях> <розміщення 0..3> <перехід -1/0..4> <мс>
//     video <шлях> <розміщення> <без звуку 0/1>        (.gif - анімація, інше - відео)
//     pause <0/1>     refresh     quit
//  Стан - у текстовий файл (--status), журнал - у журнал панелі NeoBar (--log), якщо ввімкнено.
// ======================================================================
#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <d2d1_1.h>
#include <wincodec.h>
#include <mfapi.h>
#include <mfobjects.h>
#include <oleauto.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <wchar.h>

#ifndef WS_EX_NOREDIRECTIONBITMAP
#define WS_EX_NOREDIRECTIONBITMAP 0x00200000L
#endif

// ---------------------------------------------------------------- Media Engine (mfmediaengine.h у mingw немає)
struct NRect { float left, top, right, bottom; };

struct IMEngineNotify : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE EventNotify(DWORD ev, DWORD_PTR p1, DWORD p2) = 0;
};

struct IMEngine : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE GetError(IUnknown** e) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetErrorCode(int e) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetSourceElements(IUnknown* e) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetSource(BSTR url) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSource(BSTR* url) = 0;
    virtual USHORT STDMETHODCALLTYPE GetNetworkState() = 0;
    virtual int STDMETHODCALLTYPE GetPreload() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPreload(int p) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetBuffered(IUnknown** r) = 0;
    virtual HRESULT STDMETHODCALLTYPE Load() = 0;
    virtual HRESULT STDMETHODCALLTYPE CanPlayType(BSTR t, int* a) = 0;
    virtual USHORT STDMETHODCALLTYPE GetReadyState() = 0;
    virtual BOOL STDMETHODCALLTYPE IsSeeking() = 0;
    virtual double STDMETHODCALLTYPE CurTime() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCurrentTime(double t) = 0;
    virtual double STDMETHODCALLTYPE GetStartTime() = 0;
    virtual double STDMETHODCALLTYPE GetDuration() = 0;
    virtual BOOL STDMETHODCALLTYPE IsPaused() = 0;
    virtual double STDMETHODCALLTYPE GetDefaultPlaybackRate() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDefaultPlaybackRate(double r) = 0;
    virtual double STDMETHODCALLTYPE GetPlaybackRate() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPlaybackRate(double r) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPlayed(IUnknown** r) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetSeekable(IUnknown** r) = 0;
    virtual BOOL STDMETHODCALLTYPE IsEnded() = 0;
    virtual BOOL STDMETHODCALLTYPE GetAutoPlay() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetAutoPlay(BOOL a) = 0;
    virtual BOOL STDMETHODCALLTYPE GetLoop() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetLoop(BOOL l) = 0;
    virtual HRESULT STDMETHODCALLTYPE Play() = 0;
    virtual HRESULT STDMETHODCALLTYPE Pause() = 0;
    virtual BOOL STDMETHODCALLTYPE GetMuted() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetMuted(BOOL m) = 0;
    virtual double STDMETHODCALLTYPE GetVolume() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetVolume(double v) = 0;
    virtual BOOL STDMETHODCALLTYPE HasVideo() = 0;
    virtual BOOL STDMETHODCALLTYPE HasAudio() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetNativeVideoSize(DWORD* cx, DWORD* cy) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetVideoAspectRatio(DWORD* cx, DWORD* cy) = 0;
    virtual HRESULT STDMETHODCALLTYPE Shutdown() = 0;
    virtual HRESULT STDMETHODCALLTYPE TransferVideoFrame(IUnknown* surface, const NRect* src, const RECT* dst, const MFARGB* border) = 0;
    virtual HRESULT STDMETHODCALLTYPE OnVideoStreamTick(LONGLONG* pts) = 0;
};

struct IMEngineFactory : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE CreateInstance(DWORD flags, IMFAttributes* attr, IMEngine** engine) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateTimeRange(IUnknown** r) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateError(IUnknown** e) = 0;
};

static const GUID kCLSID_MEFactory  = { 0xb44392da, 0x499b, 0x446b, { 0xa4, 0xcb, 0x00, 0x5f, 0xea, 0xd0, 0xe6, 0xd5 } };
static const GUID kIID_MEFactory    = { 0x4d645ace, 0x26aa, 0x4688, { 0x9b, 0xe1, 0xdf, 0x35, 0x16, 0x99, 0x0b, 0x93 } };
static const GUID kIID_MENotify     = { 0xfee7c112, 0xe776, 0x42b5, { 0x9b, 0xbf, 0x00, 0x48, 0x52, 0x4e, 0x2b, 0xd5 } };
static const GUID kME_CALLBACK      = { 0xc60381b8, 0x83a4, 0x41f8, { 0xa3, 0xd0, 0xde, 0x05, 0x07, 0x68, 0x49, 0xa9 } };
static const GUID kME_DXGI_MANAGER  = { 0x065702da, 0x1094, 0x486d, { 0x86, 0x17, 0xee, 0x7c, 0xc4, 0xee, 0x46, 0x48 } };
static const GUID kME_OUTPUT_FORMAT = { 0x5066893c, 0x8cf9, 0x42bc, { 0x8b, 0x8a, 0x47, 0x22, 0x12, 0xe5, 0x27, 0x26 } };

typedef HRESULT (WINAPI* PFN_MFStartup)(ULONG, DWORD);
typedef HRESULT (WINAPI* PFN_MFShutdown)();
typedef HRESULT (WINAPI* PFN_MFCreateAttributes)(IMFAttributes**, UINT32);
typedef HRESULT (WINAPI* PFN_MFCreateDXGIDeviceManager)(UINT*, IMFDXGIDeviceManager**);
static PFN_MFStartup pMFStartup;
static PFN_MFShutdown pMFShutdown;
static PFN_MFCreateAttributes pMFCreateAttributes;
static PFN_MFCreateDXGIDeviceManager pMFCreateDXGIDeviceManager;

#define WM_ME_EVENT  (WM_APP + 1)
#define WM_IMG_READY (WM_APP + 2)

template <class T> static void Rel(T*& p) { if (p) { p->Release(); p = NULL; } }

// ---------------------------------------------------------------- журнал і стан
static HINSTANCE gInst;      // модуль цієї DLL: до нього прив'язані класи вікон
static wchar_t gLog[1024], gStatusFile[1024];
static HWND gIpc, gOwner;

static const char* U8(const wchar_t* s)
{
    static char buf[4][2048];
    static int n;
    char* b = buf[n++ & 3];
    b[0] = 0;
    if (s) WideCharToMultiByte(CP_UTF8, 0, s, -1, b, 2047, NULL, NULL);
    return b;
}

static void Log(const char* fmt, ...)
{
    if (!gLog[0]) return;
    char msg[1600];
    va_list a;
    va_start(a, fmt);
    vsnprintf(msg, sizeof msg, fmt, a);
    va_end(a);
    SYSTEMTIME t;
    GetLocalTime(&t);
    char line[1800];
    int n = snprintf(line, sizeof line, "%04d-%02d-%02d %02d:%02d:%02d.%03d  [шпалери] %s\r\n",
                     t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, msg);
    if (n <= 0) return;
    if (n > (int)sizeof line - 1) n = sizeof line - 1;
    // NeoBar пише в цей самий файл; якщо він саме зайнятий - кілька коротких спроб
    for (int i = 0; i < 8; i++)
    {
        HANDLE h = CreateFileW(gLog, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_ALWAYS, 0, NULL);
        if (h != INVALID_HANDLE_VALUE)
        {
            DWORD w;
            WriteFile(h, line, (DWORD)n, &w, NULL);
            CloseHandle(h);
            return;
        }
        DWORD e = GetLastError();
        if (e == ERROR_ACCESS_DENIED && i == 0)
        {
            // тека NeoBar недоступна для запису зі звичайними правами (наприклад, Program Files) - пишемо в TEMP
            wchar_t tmp[MAX_PATH];
            if (GetTempPathW(MAX_PATH, tmp)) { _snwprintf(gLog, 1023, L"%lsNeoBarWall.log", tmp); gLog[1023] = 0; }
            continue;
        }
        Sleep(4);
    }
}

static char sState[32] = "starting", sLayer[96] = "", sRender[32] = "", sContent[64] = "", sErr[512] = "";
static int sWindows;
static int sPaused;

static void WriteStatus()
{
    if (!gStatusFile[0]) return;
    char b[1200];
    int n = snprintf(b, sizeof b, "state=%s\nlayer=%s\nwindows=%d\nrender=%s\ncontent=%s\nerror=%s\npid=%lu\npaused=%d\n",
                     sState, sLayer, sWindows, sRender, sContent, sErr, GetCurrentProcessId(), sPaused);
    HANDLE h = CreateFileW(gStatusFile, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, CREATE_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD w;
    WriteFile(h, b, (DWORD)n, &w, NULL);
    CloseHandle(h);
}

static void SetErr(const char* fmt, ...)
{
    va_list a;
    va_start(a, fmt);
    vsnprintf(sErr, sizeof sErr, fmt, a);
    va_end(a);
    Log("помилка: %s", sErr);
    WriteStatus();
}

// ---------------------------------------------------------------- налаштування з командного рядка
static int gAttachUsed;
static bool optAll = true;

// ---------------------------------------------------------------- Direct3D / Direct2D / WIC
static ID3D11Device* dev;
static ID3D11DeviceContext* ctx;
static IDXGIFactory2* dxf;
static IDXGIOutput* vout;
static ID2D1Factory1* d2f;
static ID2D1Device* d2d;
static ID2D1DeviceContext* dc;
static IWICImagingFactory* wic;
static bool deviceLost;

// ---------------------------------------------------------------- збій
// Необроблений виняток (свій чи в системних бібліотеках - Media Foundation, драйвері після скидання відеокарти):
// записати, де саме, у журнал і стан - і одразу завершитись. Без цього Windows показує вікно «Application Error»,
// а процес висить, доки його не закриють, і NeoBar не може запустити рушій знову.
static volatile LONG gCrashed;
static LONG WINAPI CrashFilter(EXCEPTION_POINTERS* ep)
{
    if (InterlockedExchange(&gCrashed, 1)) TerminateProcess(GetCurrentProcess(), 4);
    void* at = ep && ep->ExceptionRecord ? ep->ExceptionRecord->ExceptionAddress : NULL;
    DWORD code = ep && ep->ExceptionRecord ? ep->ExceptionRecord->ExceptionCode : 0;
    HMODULE mod = NULL;
    wchar_t mn[MAX_PATH] = L"?";
    if (at && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)at, &mod) && mod)
        GetModuleFileNameW(mod, mn, MAX_PATH);
    unsigned long long off = mod ? (unsigned long long)((BYTE*)at - (BYTE*)mod) : (unsigned long long)(ULONG_PTR)at;
    snprintf(sErr, sizeof sErr, "збій 0x%08lx у %s+0x%llx", (unsigned long)code, U8(PathFindFileNameW(mn)), off);
    Log("рушій впав: %s (потік %lu) - процес завершено, NeoBar запустить рушій знову", sErr, GetCurrentThreadId());
    strcpy(sState, "crashed");
    WriteStatus();
    TerminateProcess(GetCurrentProcess(), 4);
    return EXCEPTION_EXECUTE_HANDLER;
}

// відеокарту скинуто (TDR, оновлення драйвера): далі пристрій лише повертає помилки, а Media Foundation може впасти
static bool DeviceGone()
{
    if (!deviceLost && dev && dev->GetDeviceRemovedReason() != S_OK)
    {
        deviceLost = true;
        Log("відеокарта недоступна (0x%08lx)", (unsigned long)dev->GetDeviceRemovedReason());
    }
    return deviceLost;
}


static bool InitD3D()
{
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0, D3D_FEATURE_LEVEL_9_3 };
    D3D_DRIVER_TYPE types[] = { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP };
    HRESULT hr = E_FAIL;
    for (int t = 0; t < 2 && FAILED(hr); t++)
        for (int v = 0; v < 2 && FAILED(hr); v++)
        {
            UINT f = D3D11_CREATE_DEVICE_BGRA_SUPPORT | (v == 0 ? D3D11_CREATE_DEVICE_VIDEO_SUPPORT : 0);
            hr = D3D11CreateDevice(NULL, types[t], NULL, f, fl, 5, D3D11_SDK_VERSION, &dev, NULL, &ctx);
            if (hr == E_INVALIDARG) hr = D3D11CreateDevice(NULL, types[t], NULL, f, fl + 1, 4, D3D11_SDK_VERSION, &dev, NULL, &ctx);
            if (SUCCEEDED(hr)) Log("Direct3D 11: %s%s, рівень 0x%x", t == 0 ? "відеокарта" : "програмний (WARP)", v == 0 ? ", з відеодекодуванням" : ", без відеодекодування", dev->GetFeatureLevel());
        }
    if (FAILED(hr)) { SetErr("Direct3D 11 недоступний (0x%08lx)", (unsigned long)hr); return false; }

    ID3D10Multithread* mt = NULL;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D10Multithread), (void**)&mt))) { mt->SetMultithreadProtected(TRUE); mt->Release(); }

    IDXGIDevice* xd = NULL;
    IDXGIAdapter* ad = NULL;
    dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&xd);
    if (xd) xd->GetAdapter(&ad);
    if (ad)
    {
        ad->GetParent(__uuidof(IDXGIFactory2), (void**)&dxf);
        DXGI_ADAPTER_DESC d;
        if (SUCCEEDED(ad->GetDesc(&d))) Log("адаптер: %s", U8(d.Description));
        if (FAILED(ad->EnumOutputs(0, &vout))) vout = NULL;
        ad->Release();
    }
    if (!dxf) { if (xd) xd->Release(); SetErr("DXGI 1.2 недоступний"); return false; }

    D2D1_FACTORY_OPTIONS o;
    o.debugLevel = D2D1_DEBUG_LEVEL_NONE;
    hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, __uuidof(ID2D1Factory1), &o, (void**)&d2f);
    if (SUCCEEDED(hr)) hr = d2f->CreateDevice(xd, &d2d);
    if (SUCCEEDED(hr)) hr = d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
    xd->Release();
    if (FAILED(hr)) { SetErr("Direct2D 1.1 недоступний (0x%08lx)", (unsigned long)hr); return false; }
    hr = CoCreateInstance(CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER, __uuidof(IWICImagingFactory), (void**)&wic);
    if (FAILED(hr)) { SetErr("WIC недоступний (0x%08lx)", (unsigned long)hr); return false; }
    return true;
}

// ---------------------------------------------------------------- шар робочого столу
struct Layer { HWND progman, parent, defview, worker; bool raised; int kind; };
static Layer L;

static const char* ClassOf(HWND h)
{
    static char b[4][128];
    static int n;
    char* r = b[n++ & 3];
    wchar_t w[128] = L"";
    if (h) GetClassNameW(h, w, 127);
    strcpy(r, U8(w));
    return r;
}

static HWND gIconsTop;
static BOOL CALLBACK FindIconsTop(HWND top, LPARAM)
{
    if (FindWindowExW(top, NULL, L"SHELLDLL_DefView", NULL)) { gIconsTop = top; return FALSE; }
    return TRUE;
}

// want: 1 класичний WorkerW, 2 дочірнє Progman під іконками, 3 у WorkerW усередині Progman.
// Повертає false, якщо такого шару на цьому комп'ютері немає (без підміни іншим - підбір робить AttachAll).
static bool gRaisedOs;      // робочий стіл «піднятий» (Windows 11 24H2+)
static bool FindLayer(int want, bool quiet)
{
    memset(&L, 0, sizeof L);
    HWND progman = FindWindowW(L"Progman", NULL);
    if (!progman) { SetErr("не знайдено вікно робочого столу Progman (Провідник не запущено?)"); return false; }
    L.progman = progman;
    DWORD_PTR r;
    // просимо Провідник створити шар WorkerW під іконками (так роблять усі програми живих шпалер)
    SendMessageTimeoutW(progman, 0x052C, 0xD, 1, SMTO_NORMAL, 1000, &r);
    HWND dvInProgman = FindWindowExW(progman, NULL, L"SHELLDLL_DefView", NULL);
    HWND wInProgman = FindWindowExW(progman, NULL, L"WorkerW", NULL);
    LONG ex = GetWindowLongW(progman, GWL_EXSTYLE);
    bool raised = dvInProgman != NULL || (ex & WS_EX_NOREDIRECTIONBITMAP) != 0;
    gRaisedOs = raised;
    gIconsTop = NULL;
    EnumWindows(FindIconsTop, 0);
    HWND topWorker = gIconsTop ? FindWindowExW(NULL, gIconsTop, L"WorkerW", NULL) : NULL;
    if (!quiet)
        Log("Progman %p (exstyle 0x%08lx), іконки в Progman: %s, WorkerW у Progman: %p, вікно з іконками: %p (%s), наступний WorkerW: %p",
            progman, (unsigned long)ex, dvInProgman ? "так" : "ні", wInProgman, gIconsTop, ClassOf(gIconsTop), topWorker);
    if (want == 1 && !topWorker) { if (!quiet) Log("класичний WorkerW не знайдено"); return false; }
    if (want == 3 && !wInProgman) { if (!quiet) Log("WorkerW усередині Progman не знайдено"); return false; }
    L.kind = want;
    L.defview = dvInProgman ? dvInProgman : FindWindowExW(gIconsTop, NULL, L"SHELLDLL_DefView", NULL);
    if (want == 1) { L.parent = topWorker; L.worker = topWorker; L.raised = false; snprintf(sLayer, sizeof sLayer, "classic"); }
    else if (want == 3) { L.parent = wInProgman; L.worker = wInProgman; L.raised = false; snprintf(sLayer, sizeof sLayer, "progman-workerw"); }
    else { L.parent = progman; L.worker = wInProgman; L.raised = true; snprintf(sLayer, sizeof sLayer, raised ? "raised" : "progman"); }
    return true;
}

// ---------------------------------------------------------------- вікна шпалер (по одному на монітор)
struct Host
{
    HWND hwnd;
    RECT mon;
    int w, h;
    IDXGISwapChain1* sc;
    ID3D11Texture2D* tex;
    ID3D11Texture2D* staging;
    ID2D1Bitmap1* target;
    ID2D1Bitmap1* cur;
    ID2D1Bitmap1* prev;
    D2D1_RECT_F curR, prevR;
    bool dirty;
    HDC mdc;            // сумісне виведення (GDI): пам'ять кадру, яку вікно перемальовує саме
    HBITMAP dib;
    void* bits;
};
static Host H[16];
static int nH;
static const wchar_t* kHostClass = L"NeoBarWallHost";

static LRESULT CALLBACK HostProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m)
    {
    case WM_ERASEBKGND: return 1;
    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        BeginPaint(h, &ps);
        for (int i = 0; i < nH; i++)
            if (H[i].hwnd == h)
            {
                if (H[i].mdc) BitBlt(ps.hdc, 0, 0, H[i].w, H[i].h, H[i].mdc, 0, 0, SRCCOPY);
                H[i].dirty = true;
            }
        EndPaint(h, &ps);
        return 0;
    }
    case WM_NCHITTEST: return HTTRANSPARENT;
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    }
    return DefWindowProcW(h, m, w, l);
}

static const char* kRenderNames[] = { "", "flip", "blt", "gdi" };

static bool MakeTarget(Host& h, int kind)
{
    HRESULT hr;
    if (kind == 1 || kind == 2)
    {
        DXGI_SWAP_CHAIN_DESC1 d;
        memset(&d, 0, sizeof d);
        d.Width = h.w; d.Height = h.h;
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        d.SampleDesc.Count = 1;
        d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        d.BufferCount = kind == 1 ? 2 : 1;
        d.Scaling = DXGI_SCALING_STRETCH;
        d.SwapEffect = kind == 1 ? DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL : DXGI_SWAP_EFFECT_DISCARD;
        d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        hr = dxf->CreateSwapChainForHwnd(dev, h.hwnd, &d, NULL, NULL, &h.sc);
        if (FAILED(hr)) { Log("ланцюжок кадрів %s не створився: 0x%08lx", kRenderNames[kind], (unsigned long)hr); h.sc = NULL; return false; }
        dxf->MakeWindowAssociation(h.hwnd, DXGI_MWA_NO_WINDOW_CHANGES | DXGI_MWA_NO_ALT_ENTER);
        hr = h.sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&h.tex);
        if (FAILED(hr)) { Rel(h.sc); return false; }
    }
    else
    {
        D3D11_TEXTURE2D_DESC t;
        memset(&t, 0, sizeof t);
        t.Width = h.w; t.Height = h.h; t.MipLevels = 1; t.ArraySize = 1;
        t.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        t.SampleDesc.Count = 1;
        t.Usage = D3D11_USAGE_DEFAULT;
        t.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        hr = dev->CreateTexture2D(&t, NULL, &h.tex);
        if (FAILED(hr)) return false;
        t.Usage = D3D11_USAGE_STAGING;
        t.BindFlags = 0;
        t.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = dev->CreateTexture2D(&t, NULL, &h.staging);
        if (FAILED(hr)) { Rel(h.tex); return false; }
        // кадр для GDI: DIB-секція в пам'яті, з якої вікно малюється і в WM_PAINT, і одразу після кожного кадру
        BITMAPINFO bi;
        memset(&bi, 0, sizeof bi);
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = h.w;
        bi.bmiHeader.biHeight = -h.h;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        HDC scr = GetDC(NULL);
        h.dib = CreateDIBSection(scr, &bi, DIB_RGB_COLORS, &h.bits, NULL, 0);
        h.mdc = CreateCompatibleDC(scr);
        ReleaseDC(NULL, scr);
        if (!h.dib || !h.mdc || !h.bits)
        {
            Log("  DIB-секцію для GDI не створено, код %lu", (unsigned long)GetLastError());
            if (h.mdc) DeleteDC(h.mdc);
            if (h.dib) DeleteObject(h.dib);
            h.mdc = NULL; h.dib = NULL; h.bits = NULL;
            Rel(h.tex); Rel(h.staging);
            return false;
        }
        SelectObject(h.mdc, h.dib);
    }
    IDXGISurface* s = NULL;
    h.tex->QueryInterface(__uuidof(IDXGISurface), (void**)&s);
    D2D1_BITMAP_PROPERTIES1 bp;
    memset(&bp, 0, sizeof bp);
    bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_IGNORE;
    bp.dpiX = bp.dpiY = 96.f;
    bp.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
    hr = s ? dc->CreateBitmapFromDxgiSurface(s, &bp, &h.target) : E_NOINTERFACE;
    Rel(s);
    if (FAILED(hr)) { Log("ціль Direct2D не створилась: 0x%08lx", (unsigned long)hr); Rel(h.tex); Rel(h.staging); Rel(h.sc); return false; }
    return true;
}

static void PresentHost(Host& h)
{
    if (h.sc)
    {
        HRESULT hr = h.sc->Present(0, 0);
        if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET) deviceLost = true;
        return;
    }
    if (!h.staging || !h.bits) return;
    ctx->CopyResource(h.staging, h.tex);
    D3D11_MAPPED_SUBRESOURCE m;
    if (FAILED(ctx->Map(h.staging, 0, D3D11_MAP_READ, 0, &m))) return;
    for (int y = 0; y < h.h; y++)
        memcpy((BYTE*)h.bits + (size_t)y * h.w * 4, (BYTE*)m.pData + (size_t)y * m.RowPitch, (size_t)h.w * 4);
    ctx->Unmap(h.staging, 0);
    HDC hdc = GetDC(h.hwnd);
    if (hdc) { BitBlt(hdc, 0, 0, h.w, h.h, h.mdc, 0, 0, SRCCOPY); ReleaseDC(h.hwnd, hdc); }
}

static void FreeHost(Host& h)
{
    Rel(h.cur); Rel(h.prev); Rel(h.target); Rel(h.staging); Rel(h.tex); Rel(h.sc);
    if (h.mdc) DeleteDC(h.mdc);
    if (h.dib) DeleteObject(h.dib);
    if (h.hwnd && IsWindow(h.hwnd)) DestroyWindow(h.hwnd);
    memset(&h, 0, sizeof h);
}

static void DestroyHosts()
{
    for (int i = 0; i < nH; i++) FreeHost(H[i]);
    nH = 0;
    sWindows = 0;
}

static RECT gMons[16];
static int gMonCount;
static BOOL CALLBACK MonProc(HMONITOR m, HDC, LPRECT, LPARAM)
{
    MONITORINFO mi;
    mi.cbSize = sizeof mi;
    if (!GetMonitorInfoW(m, &mi) || gMonCount >= 16) return TRUE;
    if (!optAll && !(mi.dwFlags & MONITORINFOF_PRIMARY)) return TRUE;
    gMons[gMonCount++] = mi.rcMonitor;
    return TRUE;
}

static void DrawAll(bool force);

static void Pump(int ms)
{
    ULONGLONG end = GetTickCount64() + ms;
    for (;;)
    {
        MSG m;
        while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
        if (GetTickCount64() >= end) break;
        Sleep(10);
    }
}

// ---------------------------------------------------------------- побудова вікон і самоперевірка
// Параметри поточної побудови: як створювати вікно і чи робити його шаруватим.
static int gHow;     // 0 одразу дочірнє (WS_CHILD + батько), 1 «як у Lively»: звичайне вікно -> стиль дочірнього -> SetParent
static int gLay;     // 1 - WS_EX_LAYERED + SetLayeredWindowAttributes(255)

typedef HRESULT (WINAPI* PFN_DwmGetWindowAttribute)(HWND, DWORD, PVOID, DWORD);

static int Cloaked(HWND h)
{
    static PFN_DwmGetWindowAttribute f;
    static bool tried;
    if (!tried) { tried = true; HMODULE m = LoadLibraryW(L"dwmapi.dll"); if (m) f = (PFN_DwmGetWindowAttribute)GetProcAddress(m, "DwmGetWindowAttribute"); }
    DWORD v = 0;
    if (!f || FAILED(f(h, 14 /* DWMWA_CLOAKED */, &v, sizeof v))) return -1;
    return (int)v;
}

static void PlaceHost(Host& h)
{
    POINT p = { h.mon.left, h.mon.top };
    ScreenToClient(L.parent, &p);
    if (L.raised && L.defview && GetParent(L.defview) == L.parent)
        SetWindowPos(h.hwnd, L.defview, p.x, p.y, h.w, h.h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    else
        SetWindowPos(h.hwnd, NULL, p.x, p.y, h.w, h.h, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
    if (L.raised && L.worker && IsWindow(L.worker) && GetWindow(L.worker, GW_HWNDNEXT) != NULL)
        SetWindowPos(L.worker, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

static HWND CreateHostWindow(Host& h, int i)
{
    HINSTANCE hi = gInst;
    POINT p = { h.mon.left, h.mon.top };
    ScreenToClient(L.parent, &p);
    HWND w;
    SetLastError(0);
    if (gHow == 0)
    {
        DWORD exs = WS_EX_NOACTIVATE | (gLay ? WS_EX_LAYERED : 0);
        w = CreateWindowExW(exs, kHostClass, L"NeoBarWall", WS_CHILD | WS_CLIPSIBLINGS, p.x, p.y, h.w, h.h, L.parent, NULL, hi, NULL);
        if (!w)
        {
            DWORD e = GetLastError();
            Log("  монітор %d: вікно не створилось, код %lu%s", i, (unsigned long)e, e == 87 && gLay ? " (шароване дочірнє вікно потребує маніфесту сумісності з Windows 8+)" : "");
            return NULL;
        }
        if (gLay && !SetLayeredWindowAttributes(w, 0, 255, LWA_ALPHA)) Log("  SetLayeredWindowAttributes: код %lu", (unsigned long)GetLastError());
        return w;
    }
    // як у Lively Wallpaper: звичайне вікно, потім стиль дочірнього і шаруватість ДО SetParent, потім розташування під іконками
    w = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, kHostClass, L"NeoBarWall", WS_POPUP, h.mon.left, h.mon.top, h.w, h.h, NULL, NULL, hi, NULL);
    if (!w) { Log("  монітор %d: вікно не створилось, код %lu", i, (unsigned long)GetLastError()); return NULL; }
    if (L.kind == 2)
    {
        LONG_PTR st = GetWindowLongPtrW(w, GWL_STYLE);
        SetWindowLongPtrW(w, GWL_STYLE, st | WS_CHILD);
    }
    if (gLay)
    {
        LONG_PTR ex = GetWindowLongPtrW(w, GWL_EXSTYLE);
        if (!(ex & WS_EX_LAYERED)) SetWindowLongPtrW(w, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        if (!SetLayeredWindowAttributes(w, 0, 255, LWA_ALPHA)) Log("  SetLayeredWindowAttributes: код %lu", (unsigned long)GetLastError());
    }
    SetLastError(0);
    HWND old = SetParent(w, L.parent);
    if (!old) Log("  SetParent не вдалось, код %lu", (unsigned long)GetLastError());
    if (L.kind == 2 && L.defview)
        SetWindowPos(w, L.defview, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    return w;
}

// Побудувати вікна шпалер за одним сполученням «куди вбудувати» + «як створити» + «шаруватість» + «як виводити».
static bool BuildHosts(int attach, int how, int lay, int render, bool quiet)
{
    DestroyHosts();
    if (!FindLayer(attach, quiet)) return false;
    gHow = how;
    gLay = (attach == 2 || lay) ? lay : 0;
    if (attach == 1) gLay = 0;
    gMonCount = 0;
    EnumDisplayMonitors(NULL, NULL, MonProc, 0);
    for (int i = 0; i < gMonCount; i++)
    {
        Host& h = H[nH];
        memset(&h, 0, sizeof h);
        h.mon = gMons[i];
        h.w = h.mon.right - h.mon.left;
        h.h = h.mon.bottom - h.mon.top;
        h.hwnd = CreateHostWindow(h, i);
        if (!h.hwnd) continue;
        if (!MakeTarget(h, render)) { Log("  монітор %d: виведення «%s» не підготувалось", i, kRenderNames[render]); DestroyWindow(h.hwnd); h.hwnd = NULL; continue; }
        nH++;
        PlaceHost(h);
        h.dirty = true;
        if (!quiet)
        {
            LONG_PTR st = GetWindowLongPtrW(h.hwnd, GWL_STYLE), ex = GetWindowLongPtrW(h.hwnd, GWL_EXSTYLE);
            RECT r;
            GetWindowRect(h.hwnd, &r);
            Log("  монітор %d: %dx%d, вікно %p у %s %p, стиль %08lx розш. %08lx, екранні координати (%ld,%ld)-(%ld,%ld), видиме: %s, приховане DWM: %d",
                i, h.w, h.h, h.hwnd, ClassOf(L.parent), L.parent, (unsigned long)st, (unsigned long)ex,
                (long)r.left, (long)r.top, (long)r.right, (long)r.bottom, IsWindowVisible(h.hwnd) ? "так" : "ні", Cloaked(h.hwnd));
        }
    }
    return nH > 0;
}

// Самоперевірка: заливаємо вікно кольором, якого на екрані немає, і зчитуємо піксель із екрана.
// Так видно, чи кадр справді дійшов до екрана (а не лише «вікно створено»).
static void ScreenRGB(int x, int y, int* r, int* g, int* b)
{
    *r = *g = *b = -1;
    HDC scr = GetDC(NULL);
    if (!scr) return;
    HDC mem = CreateCompatibleDC(scr);
    HBITMAP bmp = CreateCompatibleBitmap(scr, 1, 1);
    HGDIOBJ old = SelectObject(mem, bmp);
    if (BitBlt(mem, 0, 0, 1, 1, scr, x, y, SRCCOPY | CAPTUREBLT))
    {
        COLORREF c = GetPixel(mem, 0, 0);
        if (c != CLR_INVALID) { *r = GetRValue(c); *g = GetGValue(c); *b = GetBValue(c); }
    }
    SelectObject(mem, old);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(NULL, scr);
}

static int SampleScreen(const RECT& mon, int R, int G, int B)
{
    int hit = 0;
    int W = mon.right - mon.left, Hh = mon.bottom - mon.top;
    for (int iy = 0; iy < 3; iy++)
        for (int ix = 0; ix < 3; ix++)
        {
            int r, g, b;
            ScreenRGB(mon.left + W * (2 * ix + 1) / 6, mon.top + Hh * (2 * iy + 1) / 6, &r, &g, &b);
            if (r < 0) continue;
            if (abs(r - R) <= 3 && abs(g - G) <= 3 && abs(b - B) <= 3) hit++;
        }
    return hit;
}

static void DrawTest(Host& h, int R, int G, int B)
{
    if (!h.target) return;
    dc->SetTarget(h.target);
    dc->BeginDraw();
    D2D1_COLOR_F c = { R / 255.f, G / 255.f, B / 255.f, 1.f };
    dc->Clear(&c);
    HRESULT hr = dc->EndDraw();
    dc->SetTarget(NULL);
    if (FAILED(hr)) Log("  EndDraw: 0x%08lx", (unsigned long)hr);
    PresentHost(h);
}

// повертає найкращу кількість із 9 точок монітора, де видно тестовий колір
static int SelfTest(int* hostsOk, int* seenR, int* seenG, int* seenB)
{
    int R = 11, G = 19, B = 29;
    int best = 0, okHosts = 0;
    for (int i = 0; i < nH; i++)
    {
        int cx = H[i].mon.left + H[i].w / 2, cy = H[i].mon.top + H[i].h / 2;
        int br, bg, bb;
        ScreenRGB(cx, cy, &br, &bg, &bb);
        if (abs(br - R) <= 6 && abs(bg - G) <= 6 && abs(bb - B) <= 6) { R = 29; G = 11; B = 19; }   // раптом шпалери вже такого кольору
        for (int rep = 0; rep < 2; rep++) { DrawTest(H[i], R, G, B); Pump(rep == 0 ? 180 : 160); }
        int hit = SampleScreen(H[i].mon, R, G, B);
        int sr, sg, sb;
        ScreenRGB(cx, cy, &sr, &sg, &sb);
        if (seenR) { *seenR = sr; *seenG = sg; *seenB = sb; }
        Log("  самоперевірка, монітор %d: тестовий колір видно у %d з 9 точок (в центрі було %d,%d,%d, стало %d,%d,%d)", i, hit, br, bg, bb, sr, sg, sb);
        if (hit >= 3) okHosts++;
        if (hit > best) best = hit;
    }
    if (hostsOk) *hostsOk = okHosts;
    return best;
}

// Контроль самого знімка екрана: звичайне вікно зверху, залите яскравим кольором. Якщо його не видно - самоперевірці вірити не можна.
static LRESULT CALLBACK CtlProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_ERASEBKGND)
    {
        RECT r;
        GetClientRect(h, &r);
        HBRUSH b = CreateSolidBrush(RGB(11, 19, 29));      // майже чорний, як і тестовий колір самоперевірки: без яскравого спалаху
        FillRect((HDC)w, &r, b);
        DeleteObject(b);
        return 1;
    }
    return DefWindowProcW(h, m, w, l);
}

static int SamplerControl()
{
    static bool reg;
    if (!reg)
    {
        WNDCLASSW wc;
        memset(&wc, 0, sizeof wc);
        wc.lpfnWndProc = CtlProc;
        wc.hInstance = gInst;
        wc.lpszClassName = L"NeoBarWallCtl";
        RegisterClassW(&wc);
        reg = true;
    }
    RECT mon = gMonCount > 0 ? gMons[0] : RECT{ 0, 0, 800, 600 };
    HWND w = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"NeoBarWallCtl", L"", WS_POPUP,
                             mon.left, mon.top, mon.right - mon.left, mon.bottom - mon.top, NULL, NULL, gInst, NULL);
    if (!w) return -1;
    ShowWindow(w, SW_SHOWNOACTIVATE);
    UpdateWindow(w);
    Pump(300);
    int hit = SampleScreen(mon, 11, 19, 29);
    DestroyWindow(w);
    Pump(100);
    return hit;
}

struct Combo { int attach, how, lay, render; };

static const char* AttachName(int a) { return a == 1 ? "класичний WorkerW" : a == 2 ? "Progman (шароване вікно під іконками)" : "WorkerW усередині Progman"; }

static wchar_t gPrefFile[1024];
static bool LoadPref(Combo* c)
{
    wchar_t tmp[900];
    if (!GetTempPathW(900, tmp)) return false;
    swprintf(gPrefFile, 1024, L"%lsNeoBarWall2.pref", tmp);
    FILE* f = _wfopen(gPrefFile, L"r");
    if (!f) return false;
    int a = 0, h = 0, l = 0, r = 0, build = 0;
    int n = fscanf(f, "%d %d %d %d %d", &a, &h, &l, &r, &build);
    fclose(f);
    OSVERSIONINFOW vi;
    memset(&vi, 0, sizeof vi);
    vi.dwOSVersionInfoSize = sizeof vi;
    typedef LONG (WINAPI* PFN_Rtl)(OSVERSIONINFOW*);
    PFN_Rtl rgv = (PFN_Rtl)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion");
    if (rgv) rgv(&vi);
    if (n != 5 || (DWORD)build != vi.dwBuildNumber || a < 1 || a > 3 || h < 0 || h > 1 || l < 0 || l > 1 || r < 1 || r > 3) return false;
    c->attach = a; c->how = h; c->lay = l; c->render = r;
    return true;
}

static void SavePref(const Combo& c)
{
    if (!gPrefFile[0]) return;
    OSVERSIONINFOW vi;
    memset(&vi, 0, sizeof vi);
    vi.dwOSVersionInfoSize = sizeof vi;
    typedef LONG (WINAPI* PFN_Rtl)(OSVERSIONINFOW*);
    PFN_Rtl rgv = (PFN_Rtl)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion");
    if (rgv) rgv(&vi);
    FILE* f = _wfopen(gPrefFile, L"w");
    if (!f) return;
    fprintf(f, "%d %d %d %d %lu\n", c.attach, c.how, c.lay, c.render, (unsigned long)vi.dwBuildNumber);
    fclose(f);
}

static bool gPrefFirst;      // перше сполучення вже підтверджене самоперевіркою на цьому комп'ютері й цій збірці Windows
static int Candidates(Combo* out, int cap)
{
    int n = 0;
    bool raisedOs = false;
    {
        HWND pm = FindWindowW(L"Progman", NULL);
        if (pm) raisedOs = FindWindowExW(pm, NULL, L"SHELLDLL_DefView", NULL) != NULL || (GetWindowLongW(pm, GWL_EXSTYLE) & WS_EX_NOREDIRECTIONBITMAP) != 0;
    }
    // способи вбудування: {куди, як створювати}
    int at[8][2], na = 0;
    if (raisedOs) { at[na][0] = 2; at[na][1] = 0; na++; at[na][0] = 2; at[na][1] = 1; na++; at[na][0] = 3; at[na][1] = 0; na++; at[na][0] = 3; at[na][1] = 1; na++; }
    else { at[na][0] = 1; at[na][1] = 1; na++; at[na][0] = 1; at[na][1] = 0; na++; at[na][0] = 2; at[na][1] = 1; na++; }
    // виведення: flip, blt, сумісне (GDI)
    int rd[3] = { 1, 2, 3 }, nr = 3;
    // раніше підтверджене на цьому комп'ютері сполучення пробуємо першим
    Combo pref;
    gPrefFirst = false;
    if (LoadPref(&pref)) { out[n++] = pref; gPrefFirst = true; }
    for (int a = 0; a < na; a++)
        for (int r = 0; r < nr; r++)
        {
            Combo c = { at[a][0], at[a][1], at[a][0] == 1 ? 0 : 1, rd[r] };
            bool dup = false;
            for (int k = 0; k < n; k++) if (out[k].attach == c.attach && out[k].how == c.how && out[k].lay == c.lay && out[k].render == c.render) dup = true;
            if (!dup && n < cap) out[n++] = c;
        }
    return n;
}

static const char* HowName(int h) { return h ? "створення як у Lively" : "одразу дочірнє"; }

static bool AttachAllImpl()
{
    DestroyHosts();
    Combo cs[40];
    int n = Candidates(cs, 40);
    int chosen = -1;
    int firstBuilt = -1;
    for (int i = 0; i < n && chosen < 0; i++)
    {
        Log("спроба %d з %d: %s, %s, виведення %s", i + 1, n, AttachName(cs[i].attach), HowName(cs[i].how), kRenderNames[cs[i].render]);
        if (!BuildHosts(cs[i].attach, cs[i].how, cs[i].lay, cs[i].render, false)) { DestroyHosts(); continue; }
        if (firstBuilt < 0) firstBuilt = i;
        // сполучення, що вже підтвердилось на цьому комп'ютері, повторно не перевіряємо: самоперевірка
        // на мить заливає екран тестовим кольором, і при кожному застосуванні шпалер це було б видно
        if (i == 0 && gPrefFirst) { Log("  це сполучення вже підтверджене раніше - самоперевірку пропускаю"); chosen = i; break; }
        int okHosts = 0;
        SelfTest(&okHosts, NULL, NULL, NULL);
        if (okHosts > 0) { chosen = i; break; }
        Log("  кадр на екрані не видно - пробую інше сполучення");
        DestroyHosts();
    }
    bool samplerBad = false;
    if (chosen < 0 && firstBuilt >= 0)
    {
        // жодне сполучення не підтвердилось: перевіряємо, чи сам знімок екрана взагалі працює
        int c = SamplerControl();
        Log("контроль знімка екрана (звичайне вікно зверху): %d з 9 точок", c);
        if (c >= 0 && c < 3) { samplerBad = true; Log("знімок екрана не бачить навіть звичайного вікна - самоперевірці вірити не можна, лишаю перший спосіб без підтвердження"); }
    }
    // знімок екрана не працює - лишаємо перший спосіб, що створився
    if (chosen < 0 && samplerBad && firstBuilt >= 0) BuildHosts(cs[firstBuilt].attach, cs[firstBuilt].how, cs[firstBuilt].lay, cs[firstBuilt].render, true);
    if (chosen < 0 && !(samplerBad && nH > 0))
    {
        DestroyHosts();
        strcpy(sState, "error");
        sRender[0] = 0;
        SetErr("жоден зі способів вбудування не показав кадр на екрані (увімкніть журнал панелі в «Діагностика» і перегляньте рядки [шпалери])");
        return false;
    }
    int k = chosen >= 0 ? chosen : firstBuilt;
    sWindows = nH;
    snprintf(sRender, sizeof sRender, "%s", kRenderNames[cs[k].render]);
    gAttachUsed = cs[k].attach;
    Log("%s: %s, %s, виведення %s, вікон: %d", chosen >= 0 ? "обрано" : "лишено (самоперевірка не підтвердила)", AttachName(cs[k].attach), HowName(cs[k].how), kRenderNames[cs[k].render], nH);
    if (chosen >= 0) SavePref(cs[k]);
    strcpy(sState, chosen >= 0 ? "ok" : "unverified");
    sErr[0] = 0;
    WriteStatus();
    DrawAll(true);
    return true;
}

// Команди від NeoBar, що прийшли, поки йде вбудування (самоперевірка качає повідомлення і малює тестовий колір):
// виконати їх одразу означало б зіпсувати самоперевірку і малювати по вікнах, що саме будуються. Відкладаємо до кінця.
static int gAttaching;
static wchar_t* gPend[16];
static int nPend;
static void Command(wchar_t* s);

static void RunPending()
{
    while (nPend > 0)
    {
        wchar_t* c = gPend[0];
        for (int i = 1; i < nPend; i++) gPend[i - 1] = gPend[i];
        nPend--;
        Log("виконую відкладену команду");
        Command(c);
        free(c);
    }
}

static bool AttachAll()
{
    gAttaching++;
    bool r = AttachAllImpl();
    gAttaching--;
    if (gAttaching == 0) RunPending();
    return r;
}

// ---------------------------------------------------------------- вміст
enum { C_NONE, C_IMAGE, C_GIF, C_VIDEO };
static int content;
static wchar_t curPath[2048];
static int curFit;
static bool paused;

// перехід між зображеннями
static bool animating;
static ULONGLONG animT0;
static int animMs = 1200, animKind;
static float animK = 1.f;

static float Ease(float x) { return x * x * (3.f - 2.f * x); }

static D2D1_RECT_F RF(float l, float t, float r, float b) { D2D1_RECT_F x; x.left = l; x.top = t; x.right = r; x.bottom = b; return x; }

static D2D1_RECT_F FitRect(float iw, float ih, float W, float Hh, int fit)
{
    if (iw <= 0 || ih <= 0 || fit == 2) return RF(0, 0, W, Hh);
    if (fit == 3) return RF((W - iw) / 2, (Hh - ih) / 2, (W + iw) / 2, (Hh + ih) / 2);
    float k = fit == 0 ? (W / iw > Hh / ih ? W / iw : Hh / ih) : (W / iw < Hh / ih ? W / iw : Hh / ih);
    float w = iw * k, h = ih * k;
    return RF((W - w) / 2, (Hh - h) / 2, (W + w) / 2, (Hh + h) / 2);
}

// ---- GIF: складання кадрів (зміщення, прозорість, способи очищення)
static IWICBitmapDecoder* gDec;
static UINT gCount, gIdx, gW, gH, gPrevDisp;
static RECT gPrevR;
static BYTE* gCanvas;
static BYTE* gSaved;
static ID2D1Bitmap1* gBmp;
static ULONGLONG gNext;

static UINT MetaU(IWICMetadataQueryReader* q, const wchar_t* name, UINT def)
{
    PROPVARIANT v;
    PropVariantInit(&v);
    UINT r = def;
    if (q && SUCCEEDED(q->GetMetadataByName(name, &v)))
    {
        if (v.vt == VT_UI2) r = v.uiVal;
        else if (v.vt == VT_UI1) r = v.bVal;
        else if (v.vt == VT_UI4) r = v.ulVal;
    }
    PropVariantClear(&v);
    return r;
}

static void GifClose()
{
    Rel(gBmp); Rel(gDec);
    free(gCanvas); gCanvas = NULL;
    free(gSaved); gSaved = NULL;
    gCount = gIdx = 0;
}

static UINT GifStep()
{
    if (!gDec || !gCount) return 1000;
    if (gIdx == 0) { memset(gCanvas, 0, (size_t)gW * gH * 4); gPrevDisp = 0; }
    else if (gPrevDisp == 2)
    {
        for (LONG y = gPrevR.top; y < gPrevR.bottom; y++)
            if (y >= 0 && y < (LONG)gH)
            {
                LONG x0 = gPrevR.left < 0 ? 0 : gPrevR.left, x1 = gPrevR.right > (LONG)gW ? (LONG)gW : gPrevR.right;
                if (x1 > x0) memset(gCanvas + ((size_t)y * gW + x0) * 4, 0, (size_t)(x1 - x0) * 4);
            }
    }
    else if (gPrevDisp == 3 && gSaved) memcpy(gCanvas, gSaved, (size_t)gW * gH * 4);

    UINT delay = 100;
    IWICBitmapFrameDecode* fr = NULL;
    if (SUCCEEDED(gDec->GetFrame(gIdx, &fr)))
    {
        IWICMetadataQueryReader* q = NULL;
        fr->GetMetadataQueryReader(&q);
        UINT left = MetaU(q, L"/imgdesc/Left", 0), top = MetaU(q, L"/imgdesc/Top", 0);
        UINT d = MetaU(q, L"/grctlext/Delay", 10), disp = MetaU(q, L"/grctlext/Disposal", 0);
        Rel(q);
        delay = d < 2 ? 100 : d * 10;
        if (disp == 3)
        {
            if (!gSaved) gSaved = (BYTE*)malloc((size_t)gW * gH * 4);
            if (gSaved) memcpy(gSaved, gCanvas, (size_t)gW * gH * 4);
        }
        IWICFormatConverter* cv = NULL;
        UINT fw = 0, fh = 0;
        if (SUCCEEDED(wic->CreateFormatConverter(&cv)) &&
            SUCCEEDED(cv->Initialize(fr, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom)) &&
            SUCCEEDED(cv->GetSize(&fw, &fh)) && fw && fh)
        {
            BYTE* buf = (BYTE*)malloc((size_t)fw * fh * 4);
            if (buf && SUCCEEDED(cv->CopyPixels(NULL, fw * 4, fw * fh * 4, buf)))
            {
                for (UINT y = 0; y < fh; y++)
                {
                    UINT cy = top + y;
                    if (cy >= gH) break;
                    const UINT* s = (const UINT*)(buf + (size_t)y * fw * 4);
                    UINT* t = (UINT*)(gCanvas + ((size_t)cy * gW) * 4);
                    for (UINT x = 0; x < fw; x++)
                    {
                        UINT cx = left + x;
                        if (cx >= gW) break;
                        if (s[x] >> 24) t[cx] = s[x];
                    }
                }
            }
            free(buf);
        }
        Rel(cv);
        fr->Release();
        gPrevDisp = disp;
        gPrevR.left = left; gPrevR.top = top; gPrevR.right = left + fw; gPrevR.bottom = top + fh;
    }
    if (gBmp) gBmp->CopyFromMemory(NULL, gCanvas, gW * 4);
    gIdx = (gIdx + 1) % gCount;
    return delay;
}

static bool GifOpen(const wchar_t* path)
{
    GifClose();
    HRESULT hr = wic->CreateDecoderFromFilename(path, NULL, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &gDec);
    if (FAILED(hr)) { SetErr("GIF не відкрився (0x%08lx)", (unsigned long)hr); return false; }
    gDec->GetFrameCount(&gCount);
    IWICMetadataQueryReader* q = NULL;
    gDec->GetMetadataQueryReader(&q);
    gW = MetaU(q, L"/logscrdesc/Width", 0);
    gH = MetaU(q, L"/logscrdesc/Height", 0);
    Rel(q);
    if (!gW || !gH)
    {
        IWICBitmapFrameDecode* f0 = NULL;
        if (SUCCEEDED(gDec->GetFrame(0, &f0))) { f0->GetSize(&gW, &gH); f0->Release(); }
    }
    if (!gCount || !gW || !gH || gW > 8192 || gH > 8192) { SetErr("GIF порожній або завеликий"); GifClose(); return false; }
    gCanvas = (BYTE*)calloc((size_t)gW * gH, 4);
    D2D1_BITMAP_PROPERTIES1 bp;
    memset(&bp, 0, sizeof bp);
    bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
    bp.dpiX = bp.dpiY = 96.f;
    D2D1_SIZE_U sz = { gW, gH };
    hr = gCanvas ? dc->CreateBitmap(sz, gCanvas, gW * 4, &bp, &gBmp) : E_OUTOFMEMORY;
    if (FAILED(hr)) { SetErr("GIF: не вдалося створити текстуру (0x%08lx)", (unsigned long)hr); GifClose(); return false; }
    gIdx = 0;
    gNext = GetTickCount64() + GifStep();
    Log("GIF %ux%u, кадрів: %u", gW, gH, gCount);
    return true;
}

// ---- відео (Media Foundation Media Engine)
static IMEngine* me;
static IMFDXGIDeviceManager* dman;
static UINT dtoken;
static bool meReady, mfOk, mfTried;
static DWORD vidW, vidH;
static volatile LONG meErrCode;
static ID3D11Texture2D* vidTex;      // кадр відео в рідному розмірі: розміщення (заповнити, вписати...) робить Direct2D, а не драйвер
static ID2D1Bitmap1* vidBmp;
static UINT vidTexW, vidTexH;
static bool gVidUrlTried;

struct Notify : public IMEngineNotify
{
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** p)
    {
        if (riid == IID_IUnknown || riid == kIID_MENotify) { *p = this; return S_OK; }
        *p = NULL;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() { return 2; }
    ULONG STDMETHODCALLTYPE Release() { return 1; }
    HRESULT STDMETHODCALLTYPE EventNotify(DWORD ev, DWORD_PTR p1, DWORD p2)
    {
        if (ev == 5) InterlockedExchange(&meErrCode, (LONG)p1);
        PostMessageW(gIpc, WM_ME_EVENT, ev, (LPARAM)p2);
        return S_OK;
    }
};
static Notify gNotify;

static bool MFInit()
{
    if (mfTried) return mfOk;
    mfTried = true;
    HMODULE m = LoadLibraryW(L"mfplat.dll");
    if (!m) { SetErr("немає Media Foundation (mfplat.dll) - для Windows N потрібен Media Feature Pack"); return false; }
    pMFStartup = (PFN_MFStartup)GetProcAddress(m, "MFStartup");
    pMFShutdown = (PFN_MFShutdown)GetProcAddress(m, "MFShutdown");
    pMFCreateAttributes = (PFN_MFCreateAttributes)GetProcAddress(m, "MFCreateAttributes");
    pMFCreateDXGIDeviceManager = (PFN_MFCreateDXGIDeviceManager)GetProcAddress(m, "MFCreateDXGIDeviceManager");
    if (!pMFStartup || !pMFCreateAttributes || !pMFCreateDXGIDeviceManager) { SetErr("Media Foundation застаріла"); return false; }
    HRESULT hr = pMFStartup(0x00020070, 1);
    if (FAILED(hr)) { SetErr("Media Foundation не запустилась (0x%08lx)", (unsigned long)hr); return false; }
    mfOk = true;
    return true;
}

static void VideoClose()
{
    if (me) { me->Shutdown(); Rel(me); }
    Rel(vidBmp); Rel(vidTex); vidTexW = vidTexH = 0;
    meReady = false;
    vidW = vidH = 0;
}

static bool VideoOpen(const wchar_t* path, bool mute)
{
    VideoClose();
    if (!MFInit()) return false;
    HRESULT hr = S_OK;
    if (!dman)
    {
        hr = pMFCreateDXGIDeviceManager(&dtoken, &dman);
        if (SUCCEEDED(hr)) hr = dman->ResetDevice(dev, dtoken);
        if (FAILED(hr)) { Rel(dman); SetErr("відео: DXGI-менеджер (0x%08lx)", (unsigned long)hr); return false; }
    }
    IMFAttributes* a = NULL;
    hr = pMFCreateAttributes(&a, 3);
    if (FAILED(hr)) { SetErr("відео: атрибути (0x%08lx)", (unsigned long)hr); return false; }
    a->SetUnknown(kME_DXGI_MANAGER, dman);
    a->SetUnknown(kME_CALLBACK, &gNotify);
    a->SetUINT32(kME_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);
    IMEngineFactory* f = NULL;
    hr = CoCreateInstance(kCLSID_MEFactory, NULL, CLSCTX_ALL, kIID_MEFactory, (void**)&f);
    if (SUCCEEDED(hr)) hr = f->CreateInstance(mute ? 4u : 0u, a, &me);     // 4 = MF_MEDIA_ENGINE_FORCEMUTE: без звуку аудіопристрій не відкривається
    Rel(f);
    Rel(a);
    if (FAILED(hr) || !me) { me = NULL; SetErr("відео: Media Engine не створився (0x%08lx)", (unsigned long)hr); return false; }
    me->SetLoop(TRUE);
    me->SetAutoPlay(TRUE);
    me->SetMuted(mute ? TRUE : FALSE);
    BSTR b = SysAllocString(path);
    hr = me->SetSource(b);
    SysFreeString(b);
    if (FAILED(hr)) { SetErr("відео: файл не прийнято (0x%08lx)", (unsigned long)hr); VideoClose(); return false; }
    gVidUrlTried = false;
    Log("відео: відкриваю %s%s", U8(path), mute ? " (без звуку)" : "");
    return true;
}

// проміжна текстура з кадром відео в рідному розмірі. Масштабування й обрізання при TransferVideoFrame
// деякі драйвери ігнорують, тому кадр переноситься цілим, а розміщує його Direct2D (як зображення й GIF)
static bool EnsureVidTex()
{
    if (!vidW || !vidH) return false;
    if (vidTex && vidBmp && vidTexW == vidW && vidTexH == vidH) return true;
    Rel(vidBmp); Rel(vidTex);
    D3D11_TEXTURE2D_DESC t;
    memset(&t, 0, sizeof t);
    t.Width = vidW; t.Height = vidH; t.MipLevels = 1; t.ArraySize = 1;
    t.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    t.SampleDesc.Count = 1;
    t.Usage = D3D11_USAGE_DEFAULT;
    t.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(dev->CreateTexture2D(&t, NULL, &vidTex))) { Log("відео: проміжну текстуру не створено"); return false; }
    IDXGISurface* s = NULL;
    vidTex->QueryInterface(__uuidof(IDXGISurface), (void**)&s);
    D2D1_BITMAP_PROPERTIES1 bp;
    memset(&bp, 0, sizeof bp);
    bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_IGNORE;
    bp.dpiX = bp.dpiY = 96.f;
    HRESULT hr = s ? dc->CreateBitmapFromDxgiSurface(s, &bp, &vidBmp) : E_NOINTERFACE;
    Rel(s);
    if (FAILED(hr)) { Log("відео: кадр для Direct2D не створено 0x%08lx", (unsigned long)hr); Rel(vidTex); return false; }
    vidTexW = vidW; vidTexH = vidH;
    return true;
}

// ---- зображення: декодування у фоновому потоці (великі фото не гальмують робочий стіл)
struct ImgJob
{
    LONG seq;
    wchar_t path[2048];
    int fit, n;
    int W[16], Hh[16];
    IWICBitmap* out[16];
    D2D1_RECT_F rect[16];
    bool animate;
    HRESULT hr;
};
static volatile LONG imgSeq;
static int pendingTrans = -1;

static DWORD WINAPI ImgThread(void* p)
{
    ImgJob* j = (ImgJob*)p;
    CoInitializeEx(NULL, COINIT_MULTITHREADED);
    IWICImagingFactory* f = NULL;
    IWICBitmapDecoder* dec = NULL;
    IWICBitmapFrameDecode* fr = NULL;
    IWICBitmapSource* src = NULL;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER, __uuidof(IWICImagingFactory), (void**)&f);
    if (SUCCEEDED(hr)) hr = f->CreateDecoderFromFilename(j->path, NULL, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &dec);
    if (SUCCEEDED(hr)) hr = dec->GetFrame(0, &fr);
    if (SUCCEEDED(hr))
    {
        src = fr; src->AddRef();
        IWICMetadataQueryReader* q = NULL;
        UINT orient = 1;
        if (SUCCEEDED(fr->GetMetadataQueryReader(&q))) { orient = MetaU(q, L"/app1/ifd/{ushort=274}", 1); q->Release(); }
        WICBitmapTransformOptions o = WICBitmapTransformRotate0;
        switch (orient)
        {
        case 2: o = WICBitmapTransformFlipHorizontal; break;
        case 3: o = WICBitmapTransformRotate180; break;
        case 4: o = WICBitmapTransformFlipVertical; break;
        case 5: case 6: o = WICBitmapTransformRotate90; break;
        case 7: case 8: o = WICBitmapTransformRotate270; break;
        }
        if (o != WICBitmapTransformRotate0)
        {
            IWICBitmapFlipRotator* r = NULL;
            if (SUCCEEDED(f->CreateBitmapFlipRotator(&r)) && SUCCEEDED(r->Initialize(src, o))) { src->Release(); src = r; }
            else Rel(r);
        }
    }
    UINT iw = 0, ih = 0;
    if (SUCCEEDED(hr)) hr = src->GetSize(&iw, &ih);
    for (int k = 0; k < j->n && SUCCEEDED(hr); k++)
    {
        int W = j->W[k], Hh = j->Hh[k];
        WICRect clip = { 0, 0, (INT)iw, (INT)ih };
        UINT ow = iw, oh = ih;
        float dx = 0, dy = 0;
        if (j->fit == 0)
        {
            double s = (double)W / iw > (double)Hh / ih ? (double)W / iw : (double)Hh / ih;
            clip.Width = (INT)(W / s + .5); clip.Height = (INT)(Hh / s + .5);
            if (clip.Width > (INT)iw) clip.Width = iw;
            if (clip.Height > (INT)ih) clip.Height = ih;
            if (clip.Width < 1) clip.Width = 1;
            if (clip.Height < 1) clip.Height = 1;
            clip.X = (iw - clip.Width) / 2; clip.Y = (ih - clip.Height) / 2;
            ow = W; oh = Hh;
        }
        else if (j->fit == 1)
        {
            double s = (double)W / iw < (double)Hh / ih ? (double)W / iw : (double)Hh / ih;
            ow = (UINT)(iw * s + .5); oh = (UINT)(ih * s + .5);
            if (ow < 1) ow = 1;
            if (oh < 1) oh = 1;
            dx = (W - (int)ow) / 2.f; dy = (Hh - (int)oh) / 2.f;
        }
        else if (j->fit == 2) { ow = W; oh = Hh; }
        else
        {
            clip.Width = (INT)iw < W ? (INT)iw : W; clip.Height = (INT)ih < Hh ? (INT)ih : Hh;
            clip.X = (iw - clip.Width) / 2; clip.Y = (ih - clip.Height) / 2;
            ow = clip.Width; oh = clip.Height;
            dx = (W - (int)ow) / 2.f; dy = (Hh - (int)oh) / 2.f;
        }
        IWICBitmapSource* s = src;
        s->AddRef();
        if (clip.X || clip.Y || clip.Width != (INT)iw || clip.Height != (INT)ih)
        {
            IWICBitmapClipper* c = NULL;
            if (SUCCEEDED(f->CreateBitmapClipper(&c)) && SUCCEEDED(c->Initialize(s, &clip))) { s->Release(); s = c; }
            else Rel(c);
        }
        if (ow != (UINT)clip.Width || oh != (UINT)clip.Height)
        {
            IWICBitmapScaler* sc = NULL;
            if (SUCCEEDED(f->CreateBitmapScaler(&sc)))
            {
                if (SUCCEEDED(sc->Initialize(s, ow, oh, (WICBitmapInterpolationMode)4)) ||     // HighQualityCubic (Windows 10+)
                    SUCCEEDED(sc->Initialize(s, ow, oh, WICBitmapInterpolationModeFant))) { s->Release(); s = sc; }
                else Rel(sc);
            }
        }
        IWICFormatConverter* cv = NULL;
        hr = f->CreateFormatConverter(&cv);
        if (SUCCEEDED(hr)) hr = cv->Initialize(s, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr)) hr = f->CreateBitmapFromSource(cv, WICBitmapCacheOnLoad, &j->out[k]);
        Rel(cv);
        s->Release();
        j->rect[k] = RF(dx, dy, dx + ow, dy + oh);
    }
    Rel(src); Rel(fr); Rel(dec); Rel(f);
    j->hr = hr;
    if (!PostMessageW(gIpc, WM_IMG_READY, 0, (LPARAM)j))
    {
        for (int k = 0; k < j->n; k++) Rel(j->out[k]);
        delete j;
    }
    CoUninitialize();
    return 0;
}

static void ImageLoad(const wchar_t* path, int fit, bool animate)
{
    ImgJob* j = new ImgJob();
    memset(j, 0, sizeof *j);
    j->seq = InterlockedIncrement(&imgSeq);
    wcsncpy(j->path, path, 2047);
    j->fit = fit;
    j->n = nH;
    j->animate = animate;
    for (int k = 0; k < nH; k++) { j->W[k] = H[k].w; j->Hh[k] = H[k].h; }
    HANDLE t = CreateThread(NULL, 0, ImgThread, j, 0, NULL);
    if (t) CloseHandle(t); else { delete j; SetErr("не вдалося запустити потік декодування"); }
}

static void ImageReady(ImgJob* j)
{
    if (j->seq == imgSeq && content == C_IMAGE && j->n == nH)
    {
        if (FAILED(j->hr)) SetErr("зображення не відкрилось: %s (0x%08lx)", U8(j->path), (unsigned long)j->hr);
        else
        {
            bool any = false;
            D2D1_BITMAP_PROPERTIES1 bp;
            memset(&bp, 0, sizeof bp);
            bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
            bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
            bp.dpiX = bp.dpiY = 96.f;
            for (int k = 0; k < nH; k++)
            {
                ID2D1Bitmap1* b = NULL;
                HRESULT hb = j->out[k] ? dc->CreateBitmapFromWicBitmap(j->out[k], &bp, &b) : E_POINTER;
                if (FAILED(hb) || !b) { Log("зображення: монітор %d - текстуру не створено, 0x%08lx", k, (unsigned long)hb); continue; }
                Rel(H[k].prev);
                if (j->animate && H[k].cur) { H[k].prev = H[k].cur; H[k].prevR = H[k].curR; any = true; }
                else Rel(H[k].cur);
                H[k].cur = b;
                H[k].curR = j->rect[k];
                H[k].dirty = true;
            }
            if (any && pendingTrans >= 0)
            {
                animKind = pendingTrans == 4 ? (int)(GetTickCount64() % 4) : pendingTrans;
                animT0 = GetTickCount64();
                animating = true;
                animK = 0.f;
            }
            else { animating = false; for (int k = 0; k < nH; k++) Rel(H[k].prev); }
            snprintf(sContent, sizeof sContent, "image");
            sErr[0] = 0;
            WriteStatus();
            Log("зображення: %s (текстур: %d з %d, перехід %s)", U8(j->path), (int)(H[0].cur != NULL) + (nH > 1 ? (int)(H[1].cur != NULL) : 0), nH, any ? "так" : "ні");
        }
    }
    for (int k = 0; k < j->n; k++) Rel(j->out[k]);
    delete j;
}

// ---------------------------------------------------------------- малювання
static ID2D1SolidColorBrush* blackBrush;

static void DrawImage(Host& h)
{
    if (!h.cur) return;
    float W = (float)h.w, Hh = (float)h.h;
    if (!animating || !h.prev)
    {
        dc->DrawBitmap(h.cur, &h.curR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        return;
    }
    float k = Ease(animK);
    D2D1_MATRIX_3X2_F id = D2D1::Matrix3x2F::Identity();
    switch (animKind)
    {
    case 1:     // зсув
    {
        float off = W * k;
        dc->SetTransform(D2D1::Matrix3x2F::Translation(-off, 0));
        dc->DrawBitmap(h.prev, &h.prevR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        dc->SetTransform(D2D1::Matrix3x2F::Translation(W - off, 0));
        dc->DrawBitmap(h.cur, &h.curR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        dc->SetTransform(id);
        break;
    }
    case 2:     // наближення: нове зображення «опускається» з легкого збільшення і проявляється
    {
        dc->DrawBitmap(h.prev, &h.prevR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        float z = 1.08f - 0.08f * k;
        dc->SetTransform(D2D1::Matrix3x2F::Scale(z, z, D2D1::Point2F(W / 2, Hh / 2)));
        dc->DrawBitmap(h.cur, &h.curR, k, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        dc->SetTransform(id);
        break;
    }
    case 3:     // шторки
    {
        dc->DrawBitmap(h.prev, &h.prevR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        int n = 12;
        float sw = W / n;
        for (int i = 0; i < n; i++)
        {
            dc->PushAxisAlignedClip(RF(i * sw, 0, i * sw + sw * k + 1, Hh), D2D1_ANTIALIAS_MODE_ALIASED);
            dc->DrawBitmap(h.cur, &h.curR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
            dc->PopAxisAlignedClip();
        }
        break;
    }
    default:    // згасання
        dc->DrawBitmap(h.prev, &h.prevR, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        dc->DrawBitmap(h.cur, &h.curR, k, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
        break;
    }
}

static void DrawHost(Host& h)
{
    if (!h.target) return;
    bool vidOk = false;
    if (content == C_VIDEO && me && meReady && vidW && EnsureVidTex())
    {
        RECT full = { 0, 0, (LONG)vidW, (LONG)vidH };
        MFARGB bk0 = { 0, 0, 0, 255 };
        vidOk = SUCCEEDED(me->TransferVideoFrame(vidTex, NULL, &full, &bk0));
        if (!vidOk && DeviceGone()) return;
    }
    dc->SetTarget(h.target);
    dc->BeginDraw();
    dc->SetTransform(D2D1::Matrix3x2F::Identity());
    D2D1_COLOR_F black = { 0.f, 0.f, 0.f, 1.f };
    dc->Clear(&black);
    if (content == C_IMAGE) DrawImage(h);
    else if (content == C_GIF && gBmp)
    {
        D2D1_RECT_F r = FitRect((float)gW, (float)gH, (float)h.w, (float)h.h, curFit);
        dc->DrawBitmap(gBmp, &r, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
    }
    else if (vidOk)
    {
        // заповнити - більше за екран (зайве обріжеться), вписати - з полями, розтягнути, по центру
        D2D1_RECT_F r = FitRect((float)vidW, (float)vidH, (float)h.w, (float)h.h, curFit);
        dc->DrawBitmap(vidBmp, &r, 1.f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, NULL);
    }
    HRESULT hr = dc->EndDraw();
    dc->SetTarget(NULL);
    if (hr == (HRESULT)D2DERR_RECREATE_TARGET) deviceLost = true;
    PresentHost(h);
    h.dirty = false;
}

static void DrawAll(bool force)
{
    for (int i = 0; i < nH; i++) if (force || H[i].dirty) DrawHost(H[i]);
}

// ---------------------------------------------------------------- команди від NeoBar
static void SetContent(int c)
{
    if (c != C_VIDEO) VideoClose();
    if (c != C_GIF) GifClose();
    if (c != C_IMAGE) { animating = false; for (int i = 0; i < nH; i++) { Rel(H[i].cur); Rel(H[i].prev); } }
    content = c;
}

static void Command(wchar_t* s)
{
    wchar_t* f[8] = { 0 };
    int n = 0;
    f[n++] = s;
    for (wchar_t* p = s; *p && n < 8; p++) if (*p == L'\t') { *p = 0; f[n++] = p + 1; }
    const wchar_t* c = f[0];
    if (!wcscmp(c, L"quit")) { PostQuitMessage(0); return; }
    if (!wcscmp(c, L"refresh")) { Log("оновлення за запитом"); AttachAll(); if (content == C_IMAGE && curPath[0]) { pendingTrans = -1; ImageLoad(curPath, curFit, false); } return; }
    if (!wcscmp(c, L"pause") && n >= 2)
    {
        bool p = f[1][0] == L'1';
        if (p == paused) return;
        paused = p;
        sPaused = p ? 1 : 0;
        WriteStatus();
        if (me) { if (p) me->Pause(); else me->Play(); }
        if (!p) { if (content == C_GIF) gNext = GetTickCount64(); DrawAll(true); }
        Log(p ? "пауза" : "продовження");
        return;
    }
    if (!wcscmp(c, L"image") && n >= 2)
    {
        bool same = content == C_IMAGE;
        SetContent(C_IMAGE);
        wcsncpy(curPath, f[1], 2047);
        curFit = n >= 3 ? _wtoi(f[2]) : 0;
        pendingTrans = n >= 4 ? _wtoi(f[3]) : -1;
        animMs = n >= 5 ? _wtoi(f[4]) : 1200;
        if (animMs < 100) animMs = 100;
        ImageLoad(curPath, curFit, same && pendingTrans >= 0);
        return;
    }
    if (!wcscmp(c, L"video") && n >= 2)
    {
        wcsncpy(curPath, f[1], 2047);
        curFit = n >= 3 ? _wtoi(f[2]) : 0;
        bool mute = n < 4 || f[3][0] != L'0';
        const wchar_t* ext = wcsrchr(curPath, L'.');
        if (ext && !_wcsicmp(ext, L".gif"))
        {
            SetContent(C_GIF);
            if (GifOpen(curPath)) { snprintf(sContent, sizeof sContent, "gif"); sErr[0] = 0; WriteStatus(); }
        }
        else
        {
            SetContent(C_VIDEO);
            if (VideoOpen(curPath, mute)) { snprintf(sContent, sizeof sContent, "video"); sErr[0] = 0; WriteStatus(); }
        }
        DrawAll(true);
        return;
    }
}

static const char* MeEventName(DWORD e)
{
    switch (e)
    {
    case 5: return "помилка";
    case 10: return "метадані";
    case 11: return "дані";
    case 13: return "грає";
    case 14: return "можна грати";
    case 1009: return "перший кадр";
    }
    return NULL;
}

static UINT msgTaskbar;

static LRESULT CALLBACK IpcProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m)
    {
    case WM_COPYDATA:
    {
        COPYDATASTRUCT* cd = (COPYDATASTRUCT*)l;
        if (!cd || cd->dwData != 0x5342 || !cd->lpData || cd->cbData < 2 || cd->cbData > 16384) return FALSE;
        int nch = cd->cbData / 2;
        wchar_t* b = (wchar_t*)malloc((nch + 1) * sizeof(wchar_t));
        if (!b) return FALSE;
        memcpy(b, cd->lpData, nch * sizeof(wchar_t));
        b[nch] = 0;
        if (gAttaching && wcsncmp(b, L"quit", 4) != 0)
        {
            if (nPend < 16) { gPend[nPend++] = b; Log("команда прийшла під час вбудування - відкладено"); return TRUE; }
            free(b);
            return FALSE;
        }
        Command(b);
        free(b);
        return TRUE;
    }
    case WM_ME_EVENT:
    {
        DWORD e = (DWORD)w;
        const char* nm = MeEventName(e);
        if (nm) Log("відео: подія «%s»", nm);
        if (!me) return 0;
        if (e == 10 || e == 11 || e == 14 || e == 1009 || e == 13)
        {
            DWORD cx = 0, cy = 0;
            if (SUCCEEDED(me->GetNativeVideoSize(&cx, &cy)) && cx && cy)
            {
                if (cx != vidW || cy != vidH) Log("відео: %lux%lu", cx, cy);
                vidW = cx; vidH = cy;
                meReady = true;
            }
            if (paused) { me->Pause(); DrawAll(true); }     // на паузі все одно показати перший кадр, а не чорний екран
        }
        else if (e == 5)
        {
            static const char* en[] = { "?", "перервано", "мережа", "декодування", "формат/кодек не підтримується", "захищений вміст" };
            Log("відео: помилка рушія, код %ld (%s), 0x%08lx", (long)meErrCode, (meErrCode >= 0 && meErrCode <= 5) ? en[meErrCode] : "?", (unsigned long)l);
            if (!gVidUrlTried && curPath[0])
            {
                gVidUrlTried = true;
                wchar_t url[2200];
                DWORD un = 2200;
                if (SUCCEEDED(UrlCreateFromPathW(curPath, url, &un, 0)))
                {
                    BSTR bs = SysAllocString(url);
                    HRESULT hr2 = me->SetSource(bs);
                    SysFreeString(bs);
                    Log("відео: повторна спроба як адреса %s -> 0x%08lx", U8(url), (unsigned long)hr2);
                    if (SUCCEEDED(hr2)) return 0;
                }
            }
            SetErr("відео не відтворюється: код %ld, 0x%08lx (кодек не підтримується або файл пошкоджено)", (long)meErrCode, (unsigned long)l);
            meReady = false;
        }
        return 0;
    }
    case WM_IMG_READY:
        ImageReady((ImgJob*)l);
        return 0;
    case WM_TIMER:
        if ((w == 1 || w == 2) && gAttaching) return 0;
        if (w == 1)
        {
            // власник (NeoBar) закрився - виходимо
            if (gOwner && !IsWindow(gOwner)) { Log("NeoBar закрито - рушій завершується"); PostQuitMessage(0); return 0; }
            if (DeviceGone()) return 0;
            static int idle;
            bool broken = nH == 0 || !IsWindow(L.parent);
            for (int i = 0; i < nH && !broken; i++) if (!IsWindow(H[i].hwnd)) broken = true;
            if (broken)
            {
                // Провідник перезапустився або вбудовування не вдалося - пробуємо знову (не частіше ніж раз на ~6 с)
                if (++idle >= 3) { idle = 0; Log("робочий стіл змінився - вбудовую заново"); if (AttachAll() && content == C_IMAGE && curPath[0]) { pendingTrans = -1; ImageLoad(curPath, curFit, false); } }
                return 0;
            }
            idle = 0;
            if (L.raised)
            {
                HWND wk = FindWindowExW(L.parent, NULL, L"WorkerW", NULL);
                if (wk) L.worker = wk;
                HWND dv = FindWindowExW(L.parent, NULL, L"SHELLDLL_DefView", NULL);
                if (dv) L.defview = dv;
                for (int i = 0; i < nH; i++)
                {
                    HWND above = GetWindow(H[i].hwnd, GW_HWNDPREV);
                    bool okZ = above == L.defview;
                    for (int k = 0; k < nH && !okZ; k++) if (H[k].hwnd == above) okZ = true;
                    if (!okZ || (L.worker && GetWindow(L.worker, GW_HWNDNEXT) != NULL)) { PlaceHost(H[i]); H[i].dirty = true; }
                }
            }
        }
        else if (w == 2)
        {
            KillTimer(h, 2);
            Log("змінилися монітори або Провідник - вбудовую заново");
            if (AttachAll() && content == C_IMAGE && curPath[0]) { pendingTrans = -1; ImageLoad(curPath, curFit, false); }
        }
        return 0;
    case WM_DISPLAYCHANGE:
        SetTimer(h, 2, 1500, NULL);
        return 0;
    case WM_CLOSE:
        PostQuitMessage(0);
        return 0;
    }
    if (m == msgTaskbar && msgTaskbar) { SetTimer(h, 2, 1500, NULL); return 0; }
    return DefWindowProcW(h, m, w, l);
}


// ---------------------------------------------------------------- запуск
static void Arg(int argc, wchar_t** argv, const wchar_t* name, wchar_t* out, int cap)
{
    for (int i = 1; i + 1 < argc; i++)
        if (!wcscmp(argv[i], name)) { wcsncpy(out, argv[i + 1], cap - 1); out[cap - 1] = 0; return; }
}

static int ArgI(int argc, wchar_t** argv, const wchar_t* name, int def)
{
    wchar_t b[64] = L"";
    Arg(argc, argv, name, b, 64);
    return b[0] ? _wtoi(b) : def;
}

static int EngineMain(HINSTANCE inst, int argc, wchar_t** argv)
{
    wchar_t ownerS[64] = L"";
    if (argv)
    {
        Arg(argc, argv, L"--log", gLog, 1024);
        Arg(argc, argv, L"--status", gStatusFile, 1024);
        Arg(argc, argv, L"--owner", ownerS, 64);
        optAll = ArgI(argc, argv, L"--all", 1) != 0;
    }
    gOwner = (HWND)(INT_PTR)_wtoi64(ownerS);

    // попередній екземпляр (наприклад, після збою NeoBar) - закрити
    HWND old = FindWindowW(L"NeoBarWallIpc", NULL);
    if (old)
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(old, &pid);
        PostMessageW(old, WM_CLOSE, 0, 0);
        HANDLE ph = OpenProcess(SYNCHRONIZE, FALSE, pid);
        if (ph) { WaitForSingleObject(ph, 2000); CloseHandle(ph); }
    }

    // справжні пікселі моніторів (як у Провідника)
    // процес-хазяїн (rundll32) може мати свою DPI-обізнаність - для вікон рушія вона задається на рівні потоку
    typedef HANDLE (WINAPI* PFN_ThreadDpi)(HANDLE);
    PFN_ThreadDpi setThr = (PFN_ThreadDpi)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetThreadDpiAwarenessContext");
    bool thrDpi = setThr && setThr((HANDLE)(INT_PTR)-4) != NULL;
    typedef BOOL (WINAPI* PFN_DpiCtx)(HANDLE);
    PFN_DpiCtx setCtx = (PFN_DpiCtx)GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext");
    if (!thrDpi && (!setCtx || !setCtx((HANDLE)(INT_PTR)-4))) SetProcessDPIAware();

    CoInitializeEx(NULL, COINIT_MULTITHREADED);

    BOOL elev = FALSE;
    HANDLE tok;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok))
    {
        TOKEN_ELEVATION te;
        DWORD sz;
        if (GetTokenInformation(tok, TokenElevation, &te, sizeof te, &sz)) elev = te.TokenIsElevated;
        CloseHandle(tok);
    }
    Log("рушій запущено (%d-біт, %s), усі монітори: %s",
        (int)(sizeof(void*) * 8), elev ? "від адміністратора" : "звичайні права", optAll ? "так" : "ні");

    {
        // від маніфесту процесу-хазяїна залежить, чи вдасться створити дочірнє шароване вікно (потрібна сумісність з Windows 8+)
        OSVERSIONINFOEXW need;
        memset(&need, 0, sizeof need);
        need.dwOSVersionInfoSize = sizeof need;
        need.dwMajorVersion = 10;
        DWORDLONG mask = VerSetConditionMask(0, VER_MAJORVERSION, VER_GREATER_EQUAL);
        BOOL okMan = VerifyVersionInfoW(&need, VER_MAJORVERSION, mask);
        wchar_t host[MAX_PATH] = L"";
        GetModuleFileNameW(NULL, host, MAX_PATH);
        Log("процес-хазяїн: %s; маніфест сумісності з Windows 10 застосовано: %s; DPI потоку: %s", U8(PathFindFileNameW(host)), okMan ? "так" : "НІ (шароване дочірнє вікно може не вийти)", thrDpi ? "за монітором" : "не змінено");
    }

    WNDCLASSEXW wc;
    memset(&wc, 0, sizeof wc);
    wc.cbSize = sizeof wc;
    wc.lpfnWndProc = HostProc;
    wc.hInstance = inst;
    wc.lpszClassName = kHostClass;
    wc.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
    RegisterClassExW(&wc);
    wc.lpfnWndProc = IpcProc;
    wc.lpszClassName = L"NeoBarWallIpc";
    RegisterClassExW(&wc);
    gIpc = CreateWindowExW(WS_EX_TOOLWINDOW, L"NeoBarWallIpc", L"NeoBarWall", WS_POPUP, 0, 0, 0, 0, NULL, NULL, inst, NULL);
    msgTaskbar = RegisterWindowMessageW(L"TaskbarCreated");
    if (msgTaskbar)
    {
        typedef BOOL (WINAPI* PFN_Filter)(HWND, UINT, DWORD, void*);
        PFN_Filter flt = (PFN_Filter)GetProcAddress(GetModuleHandleW(L"user32.dll"), "ChangeWindowMessageFilterEx");
        if (flt) { flt(gIpc, msgTaskbar, 1, NULL); flt(gIpc, WM_COPYDATA, 1, NULL); }
    }

    if (!InitD3D())
    {
        strcpy(sState, "error");
        WriteStatus();
        // вікно для команд лишається: NeoBar побачить помилку у файлі стану
    }
    else AttachAll();
    SetTimer(gIpc, 1, 2000, NULL);

    MSG msg;
    bool quit = false;
    while (!quit)
    {
        bool video = content == C_VIDEO && me && meReady && !paused;
        bool gif = content == C_GIF && gBmp && gCount > 1 && !paused;
        bool vsync = animating || video;
        if (vsync)
        {
            if (!vout || FAILED(vout->WaitForVBlank())) { Sleep(15); DeviceGone(); }
        }
        else
        {
            DWORD wait = INFINITE;
            if (gif)
            {
                ULONGLONG now = GetTickCount64();
                wait = gNext > now ? (DWORD)(gNext - now) : 0;
            }
            bool dirty = false;
            for (int i = 0; i < nH; i++) if (H[i].dirty) dirty = true;
            if (dirty) wait = 0;
            MsgWaitForMultipleObjects(0, NULL, FALSE, wait, QS_ALLINPUT);
        }
        while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) { quit = true; break; }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        if (quit || deviceLost) break;
        if (!dc) continue;

        bool drawAll = false;
        if (animating)
        {
            float t = (float)(GetTickCount64() - animT0) / (float)animMs;
            if (t >= 1.f)
            {
                t = 1.f;
                animating = false;
                for (int i = 0; i < nH; i++) Rel(H[i].prev);
            }
            animK = t;
            drawAll = true;
        }
        if (content == C_VIDEO && me && meReady && !paused)
        {
            LONGLONG pts;
            if (me->OnVideoStreamTick(&pts) == S_OK) drawAll = true;
        }
        if (content == C_GIF && gBmp && gCount > 1 && !paused && GetTickCount64() >= gNext)
        {
            gNext = GetTickCount64() + GifStep();
            drawAll = true;
        }
        DrawAll(drawAll);
    }

    if (deviceLost)
    {
        // об'єкти відео й Direct3D після скидання відеокарти не звільняємо: Media Foundation у такому стані падає
        // (саме так з'являлось вікно «NeoBarWall: Application Error»). Процес завершується - Windows прибере все сама.
        Log("відеокарту скинуто (драйвер оновився або збій) - рушій перезапуститься");
        strcpy(sState, "lost");
        WriteStatus();
        TerminateProcess(GetCurrentProcess(), 3);
    }
    else { strcpy(sState, "stopped"); WriteStatus(); }
    SetContent(C_NONE);
    DestroyHosts();
    Rel(dman);
    Rel(blackBrush);
    Rel(vidBmp); Rel(vidTex);
    Rel(dc); Rel(d2d); Rel(d2f); Rel(vout); Rel(dxf); Rel(wic);
    if (ctx) { ctx->ClearState(); ctx->Flush(); }
    Rel(ctx); Rel(dev);
    if (mfOk && pMFShutdown) pMFShutdown();
    CoUninitialize();
    Log("рушій зупинено");
    return deviceLost ? 3 : 0;
}

// ---------------------------------------------------------------- вхід DLL (rundll32 "NeoBarWall64.dll",RunW <аргументи>)
struct EngineArgs { int argc; wchar_t** argv; int ret; };

static DWORD WINAPI EngineThread(LPVOID p)
{
    EngineArgs* a = (EngineArgs*)p;
    a->ret = EngineMain(gInst, a->argc, a->argv);
    return 0;
}

extern "C" BOOL WINAPI DllMain(HINSTANCE h, DWORD why, LPVOID)
{
    if (why == DLL_PROCESS_ATTACH) { gInst = h; DisableThreadLibraryCalls(h); }
    return TRUE;
}

// сигнатура rundll32; суфікс W - рядок аргументів приходить у UTF-16
extern "C" __declspec(dllexport) void CALLBACK RunW(HWND, HINSTANCE, LPWSTR cmd, int)
{
    const wchar_t* tail = cmd ? cmd : L"";
    size_t n = wcslen(tail);
    wchar_t* full = (wchar_t*)malloc((n + 16) * sizeof(wchar_t));
    if (!full) return;
    wcscpy(full, L"NeoBarWall ");
    wcscat(full, tail);
    EngineArgs a = { 0, NULL, 0 };
    a.argv = CommandLineToArgvW(full, &a.argc);
    if (!a.argv) { free(full); return; }
    // без вікон Windows про помилки: при збої процес має завершитись, а не чекати на кнопку «OK»
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    SetUnhandledExceptionFilter(CrashFilter);
    // окремий потік: свій COM-апартамент (MTA) і своя черга повідомлень, незалежно від того, що робив rundll32
    HANDLE th = CreateThread(NULL, 0, EngineThread, &a, 0, NULL);
    if (th) { WaitForSingleObject(th, INFINITE); CloseHandle(th); }
    ExitProcess((UINT)a.ret);
}
