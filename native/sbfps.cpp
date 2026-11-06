// ======================================================================
//  NeoBarFps - обмежувач і лічильник кадрів для ігрового оверлея NeoBar.
//  NeoBar впроваджує цю бібліотеку в гру, яку користувач сам позначив.
//  Перехоплюється виведення кадру:
//    DirectX 10/11/12 - IDXGISwapChain::Present / Present1
//    DirectX 9        - IDirect3DDevice9::Present / PresentEx, IDirect3DSwapChain9::Present
//    OpenGL           - wglSwapBuffers
//  Ліміт і стан береться зі спільної пам'яті "Local\NeoBarFps_<pid>", туди ж
//  записується час кожного кадру (для FPS, 1% low і графіка в оверлеї).
//  Бібліотека лишається в грі до її закриття: зняти перехоплення під час кадру
//  небезпечно, тому вимкнення - це просто enabled = 0.
//
//  Окрім того (якщо NeoBar увімкнув borderless для цієї гри) бібліотека підміняє
//  «справжній» повноекранний режим на вікно без рамки на весь монітор:
//    DirectX 10/11/12 - IDXGISwapChain::SetFullscreenState / ResizeTarget / GetFullscreenState / GetDesc,
//                       IDXGIFactory::CreateSwapChain, IDXGIFactory2::CreateSwapChainForHwnd
//    DirectX 9        - IDirect3D9::CreateDevice, IDirect3D9Ex::CreateDeviceEx, Reset, ResetEx
//  Гра вважає, що вона у повноекранному режимі, а система бачить звичайне вікно, тому
//  оверлеї, Alt+Tab і запис екрана працюють. Саме вікно змінює окремий потік (див. BdrWorker):
//  змінювати стиль чужого вікна з потоку рендеру можна лише без очікування, інакше можливе зависання.
// ======================================================================
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <d3d9.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <ddraw.h>
#include "MinHook.h"
#include <stdarg.h>

// ======================================================================
//  Журнал бібліотеки (neobar-dll.log поруч із бібліотекою): події навколо створення пристрою, Reset, збої Present і винятки.
//  Потрібен для розслідування вильотів у іграх; обмежений за розміром (400 рядків на процес).
// ======================================================================
static HMODULE g_self = nullptr;
// Журнал і перехоплювач винятків вмикаються лише коли в налаштуваннях NeoBar увімкнено журнал «Бібліотека в грі»
// (позначка logs\dll.on поруч із бібліотекою); інакше бібліотека нічого не пише на диск і не втручається в обробку винятків гри.
static wchar_t g_logPath[MAX_PATH] = L"";
static volatile LONG g_logN = 0;
static void DllLog(const char* fmt, ...)
{
    if (!g_logPath[0] || InterlockedIncrement(&g_logN) > 400) return;
    char buf[700];
    SYSTEMTIME st; GetLocalTime(&st);
    int n = wsprintfA(buf, "%02d:%02d:%02d.%03d [pid %lu t%lu] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId());
    va_list ap; va_start(ap, fmt);
    n += wvsprintfA(buf + n, fmt, ap);
    va_end(ap);
    if (n > 690) n = 690;
    buf[n++] = '\r'; buf[n++] = '\n';
    HANDLE f = CreateFileW(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD w = 0; WriteFile(f, buf, (DWORD)n, &w, nullptr);
    CloseHandle(f);
}

// Модуль графічного API, з яким працюємо (створюємо об'єкти, ставимо перехоплення): закріплюємо його до кінця процесу.
// Звичайний GetModuleHandle не тримає посилання: гра може саме тоді вивантажити бібліотеку (FreeLibrary після пробного
// завантаження) - і наш потік звертається до вже неіснуючої пам'яті. Так падав SWAT 4 на ноутбуці (d3d9.dll вивантажилась
// між Direct3DCreate9 і Release). Закріплений модуль лишається в пам'яті, а перехоплення в ньому - дійсними.
static HMODULE PinMod(const wchar_t* name)
{
    HMODULE m = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, name, &m)) return nullptr;
    return m;
}

static void ModOf(void* addr, char* out, int cap, DWORD* rva)
{
    out[0] = 0; *rva = 0;
    HMODULE m = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)addr, &m) && m)
    {
        char path[MAX_PATH];
        if (GetModuleFileNameA(m, path, MAX_PATH))
        {
            const char* b = path; for (const char* c = path; *c; c++) if (*c == '\\' || *c == '/') b = c + 1;
            lstrcpynA(out, b, cap);
        }
        *rva = (DWORD)((char*)addr - (char*)m);
    }
}

// винятки, що зазвичай закінчуються вильотом: де саме (модуль + зміщення) і хто викликав
static bool InSelf(DWORD_PTR a)
{
    static DWORD_PTR lo = 0, hi = 0;
    if (!lo && g_self)
    {
        IMAGE_DOS_HEADER* dh = (IMAGE_DOS_HEADER*)g_self;
        IMAGE_NT_HEADERS* nth = (IMAGE_NT_HEADERS*)((BYTE*)g_self + dh->e_lfanew);
        lo = (DWORD_PTR)g_self; hi = lo + nth->OptionalHeader.SizeOfImage;
    }
    return a >= lo && a < hi;
}

// Журнал винятків: гра (особливо JVM) постійно ловить власні помилки доступу, тож пишемо лише ті, де в ланцюжку є наш код, і завжди - фатальні (fast fail, пошкодження купи)
static LONG CALLBACK SbVeh(PEXCEPTION_POINTERS ep)
{
    if (!ep || !ep->ExceptionRecord) return EXCEPTION_CONTINUE_SEARCH;
    DWORD c = ep->ExceptionRecord->ExceptionCode;
    if (c != 0xC0000005 && c != 0xC000001D && c != 0xC0000094 && c != 0xC00000FD && c != 0xC0000409 && c != 0xC0000374 && c != 0xC0000096)
        return EXCEPTION_CONTINUE_SEARCH;
    PCONTEXT cx = ep->ContextRecord;
#ifdef _WIN64
    DWORD_PTR* sp = (DWORD_PTR*)cx->Rsp;
#else
    DWORD_PTR* sp = (DWORD_PTR*)cx->Esp;
#endif
    bool ours = InSelf((DWORD_PTR)ep->ExceptionRecord->ExceptionAddress) || c == 0xC0000409 || c == 0xC0000374;
    for (int i = 0; i < 64 && !ours; i++)
    {
        DWORD_PTR v = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), sp + i, &v, sizeof(v), nullptr)) break;
        if (InSelf(v)) ours = true;
    }
    if (!ours) return EXCEPTION_CONTINUE_SEARCH;
    static volatile LONG cnt = 0;
    if (InterlockedIncrement(&cnt) > 12) return EXCEPTION_CONTINUE_SEARCH;
    char mod[64]; DWORD rva;
    ModOf(ep->ExceptionRecord->ExceptionAddress, mod, sizeof(mod), &rva);
    DllLog("ВИНЯТОК 0x%08lX за адресою %s+0x%lX (код доступу %lu, адреса даних 0x%08lX)", c, mod[0] ? mod : "?", rva,
           (DWORD)ep->ExceptionRecord->ExceptionInformation[0], (DWORD)ep->ExceptionRecord->ExceptionInformation[1]);
    int shown = 0;
    for (int i = 0; i < 96 && shown < 10; i++)
    {
        DWORD_PTR v = 0;
        if (!ReadProcessMemory(GetCurrentProcess(), sp + i, &v, sizeof(v), nullptr)) break;
        char m2[64]; DWORD r2;
        ModOf((void*)v, m2, sizeof(m2), &r2);
        if (m2[0] && r2 > 0x1000) { DllLog("  стек[%d]: %s+0x%lX", i, m2, r2); shown++; }
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// ---------------- спільна пам'ять (розкладка збігається з Limiter.cs) ----------------
#define SB_MAGIC   0x31464253u   // 'SBF1'
#define SB_VERSION 1
#define SB_RING    1024
#define SB_SIZE    8192

enum { HK_DXGI = 1, HK_DXGI1 = 2, HK_D3D9 = 4, HK_D3D9EX = 8, HK_GL = 16, HK_DDRAW = 32, HK_D3D8 = 64, HK_VK = 128, HK_KMT = 256 };
enum { API_NONE = 0, API_DXGI = 1, API_D3D9 = 2, API_GL = 3, API_DDRAW = 4, API_D3D8 = 5, API_VK = 6 };

// Direct3D 8 - в окремій одиниці компіляції (sbd8.cpp)
extern "C" void* SbD8PresentAddr(HWND w, long* hr);
extern "C" void* SbD8CreateAddr();
extern "C" void* SbD8PresentOf(void* dev);
extern "C" int SbD8Size(void* dev, int* W, int* H);
extern "C" int SbD8Draw(void* dev, const uint32_t* bits, int w, int h, uint32_t gen, int x, int y);
extern "C" void SbD8Free();

#pragma pack(push, 4)
struct SbShared
{
    uint32_t magic;               // 0
    uint32_t version;             // 4
    volatile LONG enabled;        // 8   NeoBar -> DLL: обмежувати чи ні
    volatile LONG targetMilli;    // 12  NeoBar -> DLL: ліміт * 1000
    volatile LONG hooks;          // 16  DLL: які перехоплення стоять (HK_*)
    volatile LONG api;            // 20  DLL: через що йдуть кадри (API_*)
    volatile LONG status;         // 24  DLL: 1 - ініціалізація, 2 - готово, <0 - помилка
    volatile LONG frames;         // 28  DLL: усього кадрів (індекс кільця)
    volatile LONG limiting;       // 32  DLL: 1 - останній кадр чекав на ліміт
    volatile LONG d3dver;         // 36  DLL: 9, 10, 11, 12 (за завантаженими модулями)
    int64_t qpcFreq;              // 40
    volatile int64_t lastQpc;     // 48  час останнього кадру
    float ft[SB_RING];            // 56  інтервали між кадрами, мс
    volatile LONG borderless;     // 4152 NeoBar -> DLL: 1 - підміняти повноекран на вікно без рамки
    volatile LONG bdrState;       // 4156 DLL: 0 - нічого, 1 - перехоплення стоять, 2 - режим підмінено, 3 - не вдалося підмінити
    volatile LONG bdrCount;       // 4160 DLL: скільки разів підмінено
    volatile LONG bdrApi;         // 4164 DLL: 1 - DXGI, 2 - Direct3D 9 (через що підмінено востаннє)
    volatile LONG bdrTarget;      // 4168 DLL: куди розтягуємо вікно: ширина << 16 | висота
    volatile LONG bdrWinSize;     // 4172 DLL: яким вікно гри було востаннє, коли дивились
    volatile LONG bdrFixes;       // 4176 DLL: скільки разів гра повертала вікно назад і ми його виправляли
    volatile LONG osdOn;          // 4180 NeoBar -> DLL: 1 - малювати показники в кадрі (DirectDraw)
    volatile LONG osdCorner;      // 4184 NeoBar -> DLL: 0 ↖, 1 ↗, 2 ↙, 3 ↘
    volatile LONG osdScale;       // 4188 NeoBar -> DLL: масштаб тексту, %
    volatile LONG osdDrawn;       // 4192 DLL: скільки разів намальовано
    volatile LONG ddKind;         // 4196 DLL: чим рахуємо кадри DirectDraw: 1 - Flip, 2 - Blt, 3 - Unlock
    wchar_t osdText[256];         // 4200 NeoBar -> DLL: рядки через \n
    volatile LONG osdErr;         // 4712 DLL: 0 - добре; 1 - формат буфера кадру не підтримується; 2 - не Direct3D 11; 3 - згладжування MSAA; інше - код HRESULT
    volatile LONG osdApi;         // 4716 DLL: через що намальовано: 1 - Direct3D 11, 2 - Direct3D 9, 4 - DirectDraw
    volatile LONG osdFrameW;      // 4720 DLL: ширина кадру, у який малюємо
    volatile LONG osdFrameH;      // 4724 DLL: висота кадру
    volatile LONG osdImg;         // 4728 DLL: 1 - показується картинка, яку намалював NeoBar
    volatile LONG osdExcl;        // 4732 DLL: 1 - гра в справжньому повноекрані (DXGI / Direct3D 9), 0 - у вікні або вікні без рамки
    volatile LONG dxgiVer;        // 4736 DLL: який пристрій у ланцюжка DXGI: 10, 11 або 12
    volatile LONG ddFail;         // 4740 DLL: DirectDraw - скільки разів GetDC первинної поверхні не вдався
    volatile LONG ddHr;           // 4744 DLL: DirectDraw - код останньої помилки GetDC
    volatile LONG ddCalls;        // 4748 DLL: DirectDraw - скільки разів показано кадр первинної поверхні
    volatile LONG ddBox;          // 4752 DLL: DirectDraw - розмір області малювання: ширина << 16 | висота
    volatile LONG vkFrames;       // 4756 DLL: скільки кадрів Vulkan перехоплено
    volatile LONG dxFrames;       // 4760 DLL: скільки кадрів DXGI враховано
    volatile LONG osdFmt;         // 4764 DLL: формат буфера кадру (DXGI_FORMAT), у який пробували малювати
    volatile LONG osdMsaa;        // 4768 DLL: кількість семплів згладжування буфера кадру
    volatile LONG d8Hr;           // 4772 DLL: код помилки, коли DirectX 8 не вдалося перехопити
    volatile LONG createApi;      // 4776 DLL: API, який гра обрала під час створення пристрою / ланцюжка (2 dx9, 5 dx8, 6 vulkan, 10-12 DXGI)
    volatile LONG scEffect;       // 4780 DLL: SwapEffect ланцюжка, створеного грою (DXGI)
    volatile LONG scFlags;        // 4784 DLL: Flags ланцюжка
    volatile LONG scFmt;          // 4788 DLL: формат буфера ланцюжка
    volatile LONG scWindowed;     // 4792 DLL: 1 - гра просила віконний ланцюжок, 0 - повноекранний
    volatile LONG scBufs;         // 4796 DLL: кількість буферів ланцюжка
};
#pragma pack(pop)
static_assert(offsetof(SbShared, scBufs) == 4796, "layout");
static_assert(offsetof(SbShared, qpcFreq) == 40, "layout");
static_assert(offsetof(SbShared, ft) == 56, "layout");
static_assert(offsetof(SbShared, borderless) == 4152, "layout");
static_assert(offsetof(SbShared, bdrFixes) == 4176, "layout");
static_assert(offsetof(SbShared, osdText) == 4200, "layout");
static_assert(offsetof(SbShared, osdApi) == 4716, "layout");
static_assert(offsetof(SbShared, osdImg) == 4728, "layout");
static_assert(offsetof(SbShared, osdExcl) == 4732, "layout");
static_assert(offsetof(SbShared, dxgiVer) == 4736, "layout");
static_assert(offsetof(SbShared, ddBox) == 4752, "layout");
static_assert(offsetof(SbShared, dxFrames) == 4760, "layout");
static_assert(offsetof(SbShared, d8Hr) == 4772, "layout");
static_assert(sizeof(SbShared) <= SB_SIZE, "size");

static SbShared* sh = nullptr;
static HANDLE shMap = nullptr;
static LARGE_INTEGER freq;
static DWORD tlsDepth = TLS_OUT_OF_INDEXES, tlsTimer = TLS_OUT_OF_INDEXES;
static CRITICAL_SECTION cs;
static bool highRes = true;

static inline int64_t Qpc() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }

static void OpenShared()
{
    wchar_t name[64];
    wsprintfW(name, L"Local\\NeoBarFps_%lu", GetCurrentProcessId());
    // Пам'ять створює NeoBar (до впровадження або до першого кадру шару Vulkan); без неї бібліотека нічого не робить -
    // зокрема в Vulkan-програмах, куди її завантажив шар, але NeoBar про них не знає.
    shMap = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (!shMap) return;
    sh = (SbShared*)MapViewOfFile(shMap, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, SB_SIZE);
    if (!sh) { CloseHandle(shMap); shMap = nullptr; return; }
    if (sh->magic != SB_MAGIC)
    {
        sh->version = SB_VERSION;
        sh->enabled = 0;
        sh->targetMilli = 0;
        sh->magic = SB_MAGIC;
    }
    sh->qpcFreq = freq.QuadPart;
}

// ---------------- точне очікування ----------------
// Високоточний таймер Windows (10 1803+) спить до ~0.5 мс до мети, решту - коротке активне очікування.
static void WaitUntil(int64_t target)
{
    HANDLE tm = (HANDLE)TlsGetValue(tlsTimer);
    if (!tm)
    {
        tm = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /*CREATE_WAITABLE_TIMER_HIGH_RESOLUTION*/, TIMER_ALL_ACCESS);
        if (!tm)
        {
            highRes = false;
            tm = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
        }
        TlsSetValue(tlsTimer, tm);
    }
    const int64_t marginUs = highRes ? 700 : 2000;
    for (int guard = 0; guard < 200000000; guard++)
    {
        int64_t now = Qpc();
        int64_t rem = target - now;
        if (rem <= 0) break;
        int64_t remUs = rem * 1000000 / freq.QuadPart;
        if (remUs > 1000000) remUs = 1000000;             // не довше секунди (захист від дивних значень)
        if (tm && remUs > marginUs)
        {
            LARGE_INTEGER due;
            due.QuadPart = -(LONGLONG)((remUs - marginUs) * 10);   // відносний час, одиниці по 100 нс
            if (SetWaitableTimer(tm, &due, 0, nullptr, nullptr, FALSE))
                WaitForSingleObject(tm, 1100);
            else
                Sleep(1);
        }
        else
            YieldProcessor();
    }
}

// ---------------- облік кадру ----------------
static void* mainChain = nullptr;    // основний ланцюжок виведення (у грі їх може бути кілька)
static int64_t mainLast = 0, lastFrame = 0, nextDeadline = 0;
static volatile LONG layerActive = 0;      // 1 - бібліотеку підключив шар Vulkan: кадри Vulkan рахує він, а не внутрішні перехоплення
static volatile LONG initRunning = 0;      // 1 - триває Init (він сам відкриває спільну пам'ять)
static volatile DWORD vkLastTick = 0;     // коли востаннє бачили кадр Vulkan (драйвер Vulkan може показувати кадри через DXGI всередині - їх не враховуємо)
// чи викликано Present з драйвера Vulkan (він показує кадри Vulkan через DXGI всередині)
static HMODULE crLastMod = nullptr; static bool crLastVk = false;
static bool CallerIsVk(void* ra)
{
    HMODULE m = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)ra, &m) || !m) return false;
    if (m == crLastMod) return crLastVk;
    wchar_t p[MAX_PATH] = L"";
    GetModuleFileNameW(m, p, MAX_PATH);
    for (wchar_t* c = p; *c; c++) if (*c >= L'A' && *c <= L'Z') *c += 32;
    const wchar_t* f = wcsrchr(p, L'\\');
    f = f ? f + 1 : p;
    bool vk = wcsstr(f, L"vulkan") || wcsstr(f, L"vlk") || wcsstr(f, L"nvoglv") || wcsstr(f, L"igvk") || wcsstr(f, L"vk_");
    crLastMod = m; crLastVk = vk;
    return vk;
}
static inline bool VkRecent() { DWORD t = vkLastTick; return t && GetTickCount() - t < 2000; }

static void OnFrame(void* chain, int api)
{
    if (!sh) return;
    int64_t now = Qpc();
    EnterCriticalSection(&cs);
    if (chain != mainChain)
    {
        // інший ланцюжок стає основним, лише якщо основний мовчить понад пів секунди
        if (mainChain != nullptr && now - mainLast < freq.QuadPart / 2) { LeaveCriticalSection(&cs); return; }
        mainChain = chain;
        lastFrame = 0;
        nextDeadline = 0;
    }
    mainLast = now;
    LeaveCriticalSection(&cs);

    // обмеження
    LONG en = sh->enabled, tmilli = sh->targetMilli;
    bool waited = false;
    if (en && tmilli >= 1000 && tmilli <= 1000000)
    {
        int64_t period = freq.QuadPart * 1000 / tmilli;
        if (nextDeadline == 0 || now - nextDeadline > period) nextDeadline = now;      // відстали більше ніж на кадр - без «доганяння»
        else if (now < nextDeadline) { WaitUntil(nextDeadline); now = Qpc(); waited = true; }
        nextDeadline += period;
    }
    else nextDeadline = 0;

    // запис інтервалу
    EnterCriticalSection(&cs);
    if (lastFrame != 0)
    {
        double ms = (double)(now - lastFrame) * 1000.0 / (double)freq.QuadPart;
        if (ms > 0 && ms < 5000)
        {
            LONG n = sh->frames;
            sh->ft[(uint32_t)n % SB_RING] = (float)ms;
            MemoryBarrier();
            InterlockedExchange(&sh->frames, n + 1);
        }
    }
    lastFrame = now;
    sh->lastQpc = now;
    sh->api = api;
    sh->limiting = waited ? 1 : 0;
    LeaveCriticalSection(&cs);
}

// захист від повторного входу (Present1 може викликати Present усередині)
struct Outer
{
    bool on;
    Outer()
    {
        LONG_PTR d = (LONG_PTR)TlsGetValue(tlsDepth);
        on = d == 0;
        TlsSetValue(tlsDepth, (void*)(d + 1));
    }
    ~Outer()
    {
        LONG_PTR d = (LONG_PTR)TlsGetValue(tlsDepth);
        TlsSetValue(tlsDepth, (void*)(d > 0 ? d - 1 : 0));
    }
};

// ======================================================================
//  Повноекран -> вікно без рамки
// ======================================================================
static CRITICAL_SECTION bcs;
static HANDLE bdrEvent = nullptr;
static volatile LONG bdrWorkerStarted = 0;

static inline bool BdrOn() { return sh && sh->borderless != 0; }

// вікна, які треба зробити «безрамковими» (їх обробляє окремий потік)
struct BdrWin
{
    HWND h;
    RECT rc;              // куди розтягнути (монітор)
    bool want;            // true - тримати без рамки, false - повернути як було
    bool dirty;           // є нове завдання
    bool applied;         // зміни вже внесено
    bool haveOrig;
    DWORD style, ex;      // стиль до змін
    RECT orig;            // розташування до змін
    int fixes;            // скільки разів довелося виправляти за грою
    LONG gen;             // номер завдання: щоб потік не затер новіше
};
static BdrWin bwin[8];

// підклас вікна: гра (особливо старі рушії) сама ставить своє вікно на місце, і опитування раз на пів секунди
// не встигає - тому розмір і стиль виправляємо в самому повідомленні, до того як вони набудуть чинності
struct BdrSub { HWND h; WNDPROC old; };
static BdrSub bsub[8];

// ланцюжки DXGI, які гра вважає повноекранними (а насправді це вікно)
struct BdrChain { void* sc; HWND hwnd; bool faked; };
static BdrChain bch[16];
static unsigned bchNext = 0;
static void* bdrLastChecked = nullptr;
static bool d9Faked = false;
static HWND d9Hwnd = nullptr;

static BdrChain* ChainFor(void* sc, bool create)      // викликати під bcs
{
    for (int i = 0; i < 16; i++) if (bch[i].sc == sc) return &bch[i];
    if (!create) return nullptr;
    BdrChain* c = &bch[bchNext++ % 16];
    c->sc = sc; c->hwnd = nullptr; c->faked = false;
    return c;
}

static bool BdrFaked(void* sc)
{
    if (!sh) return false;
    EnterCriticalSection(&bcs);
    BdrChain* c = ChainFor(sc, false);
    bool f = c && c->faked;
    LeaveCriticalSection(&bcs);
    return f;
}

static bool MonitorRectOf(HMONITOR m, HWND w, RECT* rc)
{
    if (!m && w) m = MonitorFromWindow(w, MONITOR_DEFAULTTONEAREST);
    if (!m) { POINT pt = { 0, 0 }; m = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY); }
    MONITORINFO mi = {};
    mi.cbSize = sizeof(mi);
    if (!m || !GetMonitorInfoW(m, &mi)) return false;
    *rc = mi.rcMonitor;
    return true;
}

static bool RectForOutput(IDXGIOutput* out, HWND w, RECT* rc)
{
    HMONITOR m = nullptr;
    if (out) { DXGI_OUTPUT_DESC od; if (SUCCEEDED(out->GetDesc(&od))) m = od.Monitor; }
    return MonitorRectOf(m, w, rc);
}

static LRESULT CALLBACK BdrWndProc(HWND h, UINT m, WPARAM wp, LPARAM lp)
{
    WNDPROC old = nullptr;
    bool want = false;
    RECT rc = {};
    EnterCriticalSection(&bcs);
    for (int i = 0; i < 8; i++) if (bsub[i].h == h) { old = bsub[i].old; break; }
    for (int i = 0; i < 8; i++) if (bwin[i].h == h) { want = bwin[i].want; rc = bwin[i].rc; break; }
    LeaveCriticalSection(&bcs);
    if (!old) return DefWindowProcW(h, m, wp, lp);
    if (want && BdrOn())
    {
        if (m == WM_WINDOWPOSCHANGING && lp)
        {
            WINDOWPOS* p = (WINDOWPOS*)lp;
            bool away = (p->flags & SWP_HIDEWINDOW) || p->x <= -30000 || p->y <= -30000 || IsIconic(h);
            if (!away)
            {
                if (!(p->flags & SWP_NOMOVE)) { p->x = rc.left; p->y = rc.top; }
                if (!(p->flags & SWP_NOSIZE)) { p->cx = rc.right - rc.left; p->cy = rc.bottom - rc.top; }
            }
        }
        else if (m == WM_STYLECHANGING && wp == (WPARAM)GWL_STYLE && lp)
        {
            STYLESTRUCT* st = (STYLESTRUCT*)lp;
            st->styleNew &= ~(DWORD)(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME);
            st->styleNew |= WS_POPUP;
        }
    }
    return CallWindowProcW(old, h, m, wp, lp);
}

static void BdrSubclass(HWND h)
{
    WNDPROC cur = (WNDPROC)GetWindowLongPtrW(h, GWLP_WNDPROC);
    if (!cur || cur == BdrWndProc) return;
    EnterCriticalSection(&bcs);
    BdrSub* e = nullptr;
    for (int i = 0; i < 8; i++) if (bsub[i].h == h) { e = &bsub[i]; break; }
    if (!e) for (int i = 0; i < 8; i++) if (!bsub[i].h || !IsWindow(bsub[i].h)) { e = &bsub[i]; break; }
    if (e) { e->h = h; e->old = cur; }
    LeaveCriticalSection(&bcs);
    if (e) SetWindowLongPtrW(h, GWLP_WNDPROC, (LONG_PTR)BdrWndProc);
}

static void BdrApply(BdrWin& c)
{
    BdrSubclass(c.h);
    if (!c.haveOrig)
    {
        c.style = (DWORD)GetWindowLongPtrW(c.h, GWL_STYLE);
        c.ex = (DWORD)GetWindowLongPtrW(c.h, GWL_EXSTYLE);
        GetWindowRect(c.h, &c.orig);
        c.haveOrig = true;
    }
    DWORD st = (DWORD)GetWindowLongPtrW(c.h, GWL_STYLE), ex = (DWORD)GetWindowLongPtrW(c.h, GWL_EXSTYLE);
    st &= ~(DWORD)(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME | WS_MAXIMIZE);
    st |= WS_POPUP | WS_VISIBLE;
    ex &= ~(DWORD)(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
    SetWindowLongPtrW(c.h, GWL_STYLE, (LONG_PTR)(LONG)st);
    SetWindowLongPtrW(c.h, GWL_EXSTYLE, (LONG_PTR)(LONG)ex);
    // ASYNCWINDOWPOS: не чекати на потік вікна (він може чекати на потік рендеру)
    SetWindowPos(c.h, HWND_TOP, c.rc.left, c.rc.top, c.rc.right - c.rc.left, c.rc.bottom - c.rc.top,
                 SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS);
    c.applied = true;
}

static void BdrRestore(BdrWin& c)
{
    if (c.haveOrig)
    {
        SetWindowLongPtrW(c.h, GWL_STYLE, (LONG_PTR)(LONG)c.style);
        SetWindowLongPtrW(c.h, GWL_EXSTYLE, (LONG_PTR)(LONG)c.ex);
        SetWindowPos(c.h, nullptr, c.orig.left, c.orig.top, c.orig.right - c.orig.left, c.orig.bottom - c.orig.top,
                     SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }
    c.applied = false;
    c.haveOrig = false;
}

// гра іноді сама повертає рамку або розмір: перевіряємо раз на пів секунди
static bool BdrDrifted(const BdrWin& c)
{
    DWORD st = (DWORD)GetWindowLongPtrW(c.h, GWL_STYLE);
    if (st & (WS_CAPTION | WS_THICKFRAME)) return true;
    RECT r;
    if (!GetWindowRect(c.h, &r)) return false;
    return r.left != c.rc.left || r.top != c.rc.top || r.right != c.rc.right || r.bottom != c.rc.bottom;
}

static DWORD WINAPI BdrWorker(LPVOID)
{
    for (;;)
    {
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            BdrWin c;
            EnterCriticalSection(&bcs);
            c = bwin[i];
            LeaveCriticalSection(&bcs);
            if (!c.h) continue;
            if (!IsWindow(c.h))
            {
                EnterCriticalSection(&bcs);
                if (bwin[i].h == c.h) memset(&bwin[i], 0, sizeof(BdrWin));
                LeaveCriticalSection(&bcs);
                continue;
            }
            bool changed = false;
            if (c.want)
            {
                any = true;
                if (c.dirty) { BdrApply(c); changed = true; }
                else if (c.applied && c.fixes < 500 && !IsIconic(c.h) && BdrDrifted(c)) { BdrApply(c); c.fixes++; changed = true; InterlockedIncrement(&sh->bdrFixes); }
                RECT wr;
                if (c.applied && GetWindowRect(c.h, &wr)) sh->bdrWinSize = ((wr.right - wr.left) << 16) | ((wr.bottom - wr.top) & 0xFFFF);
            }
            else if (c.dirty)
            {
                if (c.applied) BdrRestore(c);
                changed = true;
            }
            if (!changed) continue;
            EnterCriticalSection(&bcs);
            if (bwin[i].h == c.h)
            {
                bwin[i].haveOrig = c.haveOrig; bwin[i].style = c.style; bwin[i].ex = c.ex; bwin[i].orig = c.orig;
                bwin[i].applied = c.applied; bwin[i].fixes = c.fixes;
                if (bwin[i].gen == c.gen) bwin[i].dirty = false;
                if (!bwin[i].want && !bwin[i].dirty) memset(&bwin[i], 0, sizeof(BdrWin));
            }
            LeaveCriticalSection(&bcs);
        }
        WaitForSingleObject(bdrEvent, any ? 500 : 3000);
    }
}

static void BdrRequest(HWND h, const RECT* rc, bool want)
{
    if (!h || !sh) return;
    if (!bdrEvent) return;
    if (InterlockedCompareExchange(&bdrWorkerStarted, 1, 0) == 0)
    {
        HANDLE t = CreateThread(nullptr, 0, BdrWorker, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    }
    EnterCriticalSection(&bcs);
    BdrWin* w = nullptr;
    for (int i = 0; i < 8; i++) if (bwin[i].h == h) { w = &bwin[i]; break; }
    if (!w) for (int i = 0; i < 8; i++) if (!bwin[i].h) { w = &bwin[i]; break; }
    if (!w) w = &bwin[0];
    if (w->h != h) { memset(w, 0, sizeof(BdrWin)); w->h = h; }
    if (want && rc) { w->rc = *rc; w->fixes = 0; sh->bdrTarget = ((rc->right - rc->left) << 16) | ((rc->bottom - rc->top) & 0xFFFF); }
    w->want = want;
    w->dirty = true;
    w->gen++;
    LeaveCriticalSection(&bcs);
    SetEvent(bdrEvent);
}

static void BdrMarked(LONG api)
{
    sh->bdrApi = api;
    sh->bdrState = 2;
    InterlockedIncrement(&sh->bdrCount);
}

// ---------- DXGI ----------
typedef HRESULT (STDMETHODCALLTYPE* SetFS_t)(IDXGISwapChain*, BOOL, IDXGIOutput*);
typedef HRESULT (STDMETHODCALLTYPE* GetFS_t)(IDXGISwapChain*, BOOL*, IDXGIOutput**);
typedef HRESULT (STDMETHODCALLTYPE* GetDesc_t)(IDXGISwapChain*, DXGI_SWAP_CHAIN_DESC*);
typedef HRESULT (STDMETHODCALLTYPE* ResizeTarget_t)(IDXGISwapChain*, const DXGI_MODE_DESC*);
typedef HRESULT (STDMETHODCALLTYPE* CreateSC_t)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
typedef HRESULT (STDMETHODCALLTYPE* CreateSCHwnd_t)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
                                                    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
static SetFS_t oSetFS = nullptr;
static GetFS_t oGetFS = nullptr;
static GetDesc_t oGetDesc = nullptr;
static ResizeTarget_t oResizeTarget = nullptr;
static CreateSC_t oCreateSC = nullptr;
static CreateSCHwnd_t oCreateSCHwnd = nullptr;

static void MarkChainFaked(void* sc, HWND w)
{
    EnterCriticalSection(&bcs);
    BdrChain* c = ChainFor(sc, true);
    c->faked = true;
    c->hwnd = w;
    LeaveCriticalSection(&bcs);
}

static HRESULT STDMETHODCALLTYPE hkSetFS(IDXGISwapChain* sc, BOOL fs, IDXGIOutput* target)
{
    if (!BdrOn()) return oSetFS(sc, fs, target);
    EnterCriticalSection(&bcs);
    BdrChain* c = ChainFor(sc, true);
    bool faked = c->faked;
    HWND fw = c->hwnd;
    LeaveCriticalSection(&bcs);
    if (fs)
    {
        if (faked) return S_OK;
        DXGI_SWAP_CHAIN_DESC d = {};
        if (FAILED(oGetDesc(sc, &d)) || !d.OutputWindow) return oSetFS(sc, fs, target);
        BOOL cur = FALSE;
        if (SUCCEEDED(oGetFS(sc, &cur, nullptr)) && cur) oSetFS(sc, FALSE, nullptr);     // уже справжній повноекран - виходимо з нього
        RECT rc;
        if (!RectForOutput(target, d.OutputWindow, &rc)) return oSetFS(sc, fs, target);
        MarkChainFaked(sc, d.OutputWindow);
        BdrRequest(d.OutputWindow, &rc, true);
        BdrMarked(1);
        return S_OK;
    }
    if (faked)
    {
        EnterCriticalSection(&bcs);
        BdrChain* c2 = ChainFor(sc, false);
        if (c2) c2->faked = false;
        LeaveCriticalSection(&bcs);
        BdrRequest(fw, nullptr, false);
        return S_OK;
    }
    return oSetFS(sc, fs, target);
}

static HRESULT STDMETHODCALLTYPE hkGetFS(IDXGISwapChain* sc, BOOL* pfs, IDXGIOutput** pout)
{
    HRESULT hr = oGetFS(sc, pfs, pout);
    if (BdrFaked(sc))
    {
        if (pfs) *pfs = TRUE;
        if (pout && !*pout) sc->GetContainingOutput(pout);
        return S_OK;
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE hkGetDesc(IDXGISwapChain* sc, DXGI_SWAP_CHAIN_DESC* d)
{
    HRESULT hr = oGetDesc(sc, d);
    if (SUCCEEDED(hr) && d && BdrFaked(sc)) d->Windowed = FALSE;
    return hr;
}

// гра просить змінити режим екрана: вікно вже на весь монітор, тож це не потрібно
static HRESULT STDMETHODCALLTYPE hkResizeTarget(IDXGISwapChain* sc, const DXGI_MODE_DESC* m)
{
    if (BdrFaked(sc)) return S_OK;
    return oResizeTarget(sc, m);
}

// ланцюжок, створений до нашого підключення, уже може бути у справжньому повноекрані:
// виходимо з нього тут, у потоці вікна, і далі діємо як при підміні
static void BdrLate(IDXGISwapChain* sc)
{
    if (sc == bdrLastChecked || !BdrOn() || !oGetFS || !oSetFS || !oGetDesc) return;
    bdrLastChecked = sc;
    if (BdrFaked(sc)) return;
    BOOL fs = FALSE;
    IDXGIOutput* out = nullptr;
    if (FAILED(oGetFS(sc, &fs, &out)) || !fs) { if (out) out->Release(); return; }
    DXGI_SWAP_CHAIN_DESC d = {};
    HWND w = SUCCEEDED(oGetDesc(sc, &d)) ? d.OutputWindow : nullptr;
    RECT rc;
    bool ok = w && GetWindowThreadProcessId(w, nullptr) == GetCurrentThreadId() && RectForOutput(out, w, &rc);
    if (out) out->Release();
    if (!ok) { sh->bdrState = 3; return; }      // кадри йдуть не з потоку вікна: вихід із повноекрану звідси ризикований
    if (FAILED(oSetFS(sc, FALSE, nullptr))) { sh->bdrState = 3; return; }
    MarkChainFaked(sc, w);
    BdrRequest(w, &rc, true);
    BdrMarked(1);
}

static void Dx12NoteQueue(void* sc, IUnknown* dev);
static void DxgiHookSc(IDXGISwapChain* sc);
static void DxgiNoteCreate(IUnknown* dev, UINT effect, UINT flags, UINT fmt, BOOL windowed, UINT bufs, void* ra);
static void D9HookReal(IDirect3DDevice9* dev);

// ланцюжок створюється одразу у повноекранному режимі - створюємо віконний
static HRESULT STDMETHODCALLTYPE hkCreateSC(IDXGIFactory* f, IUnknown* dev, DXGI_SWAP_CHAIN_DESC* d, IDXGISwapChain** out)
{
    if (BdrOn() && d && out && !d->Windowed && d->OutputWindow)
    {
        DXGI_SWAP_CHAIN_DESC m = *d;
        m.Windowed = TRUE;
        HRESULT hr = oCreateSC(f, dev, &m, out);
        if (SUCCEEDED(hr) && *out)
        {
            DxgiNoteCreate(dev, d->SwapEffect, d->Flags, d->BufferDesc.Format, FALSE, d->BufferCount, __builtin_return_address(0));
            DxgiHookSc(*out);
            RECT rc;
            if (MonitorRectOf(nullptr, d->OutputWindow, &rc))
            {
                MarkChainFaked(*out, d->OutputWindow);
                BdrRequest(d->OutputWindow, &rc, true);
                BdrMarked(1);
            }
            Dx12NoteQueue(*out, dev);
            return hr;
        }
    }
    HRESULT hr0 = oCreateSC(f, dev, d, out);
    if (SUCCEEDED(hr0) && out && *out)
    {
        if (d) DxgiNoteCreate(dev, d->SwapEffect, d->Flags, d->BufferDesc.Format, d->Windowed, d->BufferCount, __builtin_return_address(0));
        DxgiHookSc(*out);
        Dx12NoteQueue(*out, dev);
    }
    return hr0;
}

static HRESULT STDMETHODCALLTYPE hkCreateSCHwnd(IDXGIFactory2* f, IUnknown* dev, HWND w, const DXGI_SWAP_CHAIN_DESC1* d,
                                                const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fd, IDXGIOutput* ro, IDXGISwapChain1** out)
{
    if (BdrOn() && fd && out && !fd->Windowed && w)
    {
        DXGI_SWAP_CHAIN_FULLSCREEN_DESC m = *fd;
        m.Windowed = TRUE;
        HRESULT hr = oCreateSCHwnd(f, dev, w, d, &m, ro, out);
        if (SUCCEEDED(hr) && *out)
        {
            if (d) DxgiNoteCreate(dev, d->SwapEffect, d->Flags, d->Format, FALSE, d->BufferCount, __builtin_return_address(0));
            DxgiHookSc((IDXGISwapChain*)*out);
            RECT rc;
            if (RectForOutput(ro, w, &rc))
            {
                MarkChainFaked(*out, w);
                BdrRequest(w, &rc, true);
                BdrMarked(1);
            }
            Dx12NoteQueue(*out, dev);
            return hr;
        }
    }
    HRESULT hr1 = oCreateSCHwnd(f, dev, w, d, fd, ro, out);
    if (SUCCEEDED(hr1) && out && *out)
    {
        if (d) DxgiNoteCreate(dev, d->SwapEffect, d->Flags, d->Format, fd ? fd->Windowed : TRUE, d->BufferCount, __builtin_return_address(0));
        DxgiHookSc((IDXGISwapChain*)*out);
        Dx12NoteQueue(*out, dev);
    }
    return hr1;
}

// ---------- Direct3D 9 ----------
typedef HRESULT (STDMETHODCALLTYPE* D9Create_t)(IDirect3D9*, UINT, D3DDEVTYPE, HWND, DWORD, D3DPRESENT_PARAMETERS*, IDirect3DDevice9**);
typedef HRESULT (STDMETHODCALLTYPE* D9CreateEx_t)(IDirect3D9Ex*, UINT, D3DDEVTYPE, HWND, DWORD, D3DPRESENT_PARAMETERS*, D3DDISPLAYMODEEX*, IDirect3DDevice9Ex**);
typedef HRESULT (STDMETHODCALLTYPE* D9Reset_t)(IDirect3DDevice9*, D3DPRESENT_PARAMETERS*);
typedef HRESULT (STDMETHODCALLTYPE* D9ResetEx_t)(IDirect3DDevice9Ex*, D3DPRESENT_PARAMETERS*, D3DDISPLAYMODEEX*);
static D9Create_t oD9Create = nullptr, oD9CreateB = nullptr;
static D9CreateEx_t oD9CreateEx = nullptr;
static D9Reset_t oD9Reset = nullptr, oD9ResetB = nullptr;
static D9ResetEx_t oD9ResetEx = nullptr;

// віконні параметри замість повноекранних; false - нічого міняти не треба
static bool D9Windowed(const D3DPRESENT_PARAMETERS* pp, D3DPRESENT_PARAMETERS* m, HWND w, RECT* rc)
{
    if (!BdrOn() || !pp || pp->Windowed || !w) return false;
    if (!MonitorRectOf(nullptr, w, rc)) return false;
    *m = *pp;
    m->Windowed = TRUE;
    m->FullScreen_RefreshRateInHz = 0;
    if (!m->BackBufferWidth) m->BackBufferWidth = rc->right - rc->left;
    if (!m->BackBufferHeight) m->BackBufferHeight = rc->bottom - rc->top;
    return true;
}

static void D9Done(D3DPRESENT_PARAMETERS* pp, const D3DPRESENT_PARAMETERS& m, HWND w, const RECT& rc)
{
    // гра бачить значення, які повернув Direct3D, але режим лишається таким, як вона просила
    pp->BackBufferWidth = m.BackBufferWidth; pp->BackBufferHeight = m.BackBufferHeight;
    pp->BackBufferFormat = m.BackBufferFormat; pp->BackBufferCount = m.BackBufferCount;
    d9Faked = true;
    d9Hwnd = w;
    BdrRequest(w, &rc, true);
    BdrMarked(2);
}

static void D9Leave()
{
    if (d9Faked && d9Hwnd) BdrRequest(d9Hwnd, nullptr, false);
    d9Faked = false;
}

static void OsdD9Release();
static HRESULT STDMETHODCALLTYPE hkD9CreateDev(IDirect3D9* d3d, UINT a, D3DDEVTYPE t, HWND fw, DWORD fl, D3DPRESENT_PARAMETERS* pp, IDirect3DDevice9** ret, D9Create_t orig)
{
    OsdD9Release();      // ресурси попереднього пристрою тримають його живим: без цього гра, що перестворює пристрій (напр. GTA SA), його не отримає
    D3DPRESENT_PARAMETERS m; RECT rc;
    HWND w = pp && pp->hDeviceWindow ? pp->hDeviceWindow : fw;
    if (pp) DllLog("CreateDevice: вікно=%d %dx%d fmt=%d буферів=%d msaa=%d гц=%d прапори=0x%lX", (int)pp->Windowed, (int)pp->BackBufferWidth, (int)pp->BackBufferHeight, (int)pp->BackBufferFormat, (int)pp->BackBufferCount, (int)pp->MultiSampleType, (int)pp->FullScreen_RefreshRateInHz, (DWORD)fl);
    if (ret && D9Windowed(pp, &m, w, &rc))
    {
        HRESULT hr = orig(d3d, a, t, fw, fl, &m, ret);
        DllLog("CreateDevice (підмінено на вікно без рамки): hr=0x%08lX", (DWORD)hr);
        if (SUCCEEDED(hr)) { if (*ret) D9HookReal(*ret); D9Done(pp, m, w, rc); return hr; }
    }
    HRESULT hr2 = orig(d3d, a, t, fw, fl, pp, ret);
    DllLog("CreateDevice: hr=0x%08lX", (DWORD)hr2);
    if (SUCCEEDED(hr2) && ret && *ret) D9HookReal(*ret);      // пристрій гри: перехоплюємо його методи, тестовий пристрій не потрібен
    return hr2;
}
static HRESULT STDMETHODCALLTYPE hkD9Create(IDirect3D9* d3d, UINT a, D3DDEVTYPE t, HWND fw, DWORD fl, D3DPRESENT_PARAMETERS* pp, IDirect3DDevice9** ret)
{ return hkD9CreateDev(d3d, a, t, fw, fl, pp, ret, oD9Create); }
static HRESULT STDMETHODCALLTYPE hkD9CreateB(IDirect3D9* d3d, UINT a, D3DDEVTYPE t, HWND fw, DWORD fl, D3DPRESENT_PARAMETERS* pp, IDirect3DDevice9** ret)
{ return hkD9CreateDev(d3d, a, t, fw, fl, pp, ret, oD9CreateB); }

static HRESULT STDMETHODCALLTYPE hkD9CreateEx(IDirect3D9Ex* d3d, UINT a, D3DDEVTYPE t, HWND fw, DWORD fl, D3DPRESENT_PARAMETERS* pp, D3DDISPLAYMODEEX* fm, IDirect3DDevice9Ex** ret)
{
    OsdD9Release();
    D3DPRESENT_PARAMETERS m; RECT rc;
    HWND w = pp && pp->hDeviceWindow ? pp->hDeviceWindow : fw;
    if (ret && D9Windowed(pp, &m, w, &rc))
    {
        HRESULT hr = oD9CreateEx(d3d, a, t, fw, fl, &m, nullptr, ret);
        if (SUCCEEDED(hr)) { if (*ret) D9HookReal(*ret); D9Done(pp, m, w, rc); return hr; }
    }
    HRESULT hr2 = oD9CreateEx(d3d, a, t, fw, fl, pp, fm, ret);
    if (SUCCEEDED(hr2) && ret && *ret) D9HookReal(*ret);
    return hr2;
}

static HWND D9WindowOf(IDirect3DDevice9* dev, const D3DPRESENT_PARAMETERS* pp)
{
    if (pp && pp->hDeviceWindow) return pp->hDeviceWindow;
    D3DDEVICE_CREATION_PARAMETERS cp = {};
    if (SUCCEEDED(dev->GetCreationParameters(&cp))) return cp.hFocusWindow;
    return nullptr;
}

static void OsdD9Release();
static HRESULT STDMETHODCALLTYPE hkD9ResetDev(IDirect3DDevice9* dev, D3DPRESENT_PARAMETERS* pp, D9Reset_t orig)
{
    OsdD9Release();      // ресурси в DEFAULT-пулі треба відпустити до Reset
    D3DPRESENT_PARAMETERS m; RECT rc;
    HWND w = D9WindowOf(dev, pp);
    if (pp) DllLog("Reset: вікно=%d %dx%d fmt=%d буферів=%d msaa=%d гц=%d", (int)pp->Windowed, (int)pp->BackBufferWidth, (int)pp->BackBufferHeight, (int)pp->BackBufferFormat, (int)pp->BackBufferCount, (int)pp->MultiSampleType, (int)pp->FullScreen_RefreshRateInHz);
    if (D9Windowed(pp, &m, w, &rc))
    {
        HRESULT hr = orig(dev, &m);
        DllLog("Reset (підмінено на вікно без рамки): hr=0x%08lX", (DWORD)hr);
        if (SUCCEEDED(hr)) { D9Done(pp, m, w, rc); return hr; }
    }
    HRESULT hr = orig(dev, pp);
    DllLog("Reset: hr=0x%08lX", (DWORD)hr);
    if (SUCCEEDED(hr) && pp && pp->Windowed) D9Leave();
    return hr;
}
static HRESULT STDMETHODCALLTYPE hkD9Reset(IDirect3DDevice9* d, D3DPRESENT_PARAMETERS* pp) { return hkD9ResetDev(d, pp, oD9Reset); }
static HRESULT STDMETHODCALLTYPE hkD9ResetB(IDirect3DDevice9* d, D3DPRESENT_PARAMETERS* pp) { return hkD9ResetDev(d, pp, oD9ResetB); }

static HRESULT STDMETHODCALLTYPE hkD9ResetEx(IDirect3DDevice9Ex* dev, D3DPRESENT_PARAMETERS* pp, D3DDISPLAYMODEEX* fm)
{
    OsdD9Release();
    D3DPRESENT_PARAMETERS m; RECT rc;
    HWND w = D9WindowOf(dev, pp);
    if (D9Windowed(pp, &m, w, &rc))
    {
        HRESULT hr = oD9ResetEx(dev, &m, nullptr);
        if (SUCCEEDED(hr)) { D9Done(pp, m, w, rc); return hr; }
    }
    HRESULT hr = oD9ResetEx(dev, pp, fm);
    if (SUCCEEDED(hr) && pp && pp->Windowed) D9Leave();
    return hr;
}

// ======================================================================
//  Показники прямо в кадрі гри: Direct3D 9, 11, 12 (через DXGI), OpenGL і DirectDraw
//  Текст малюється в NeoBar у звичайну картинку з попередньо помноженою прозорістю (BGRA). Тут вона змішується з кадром
//  (ONE, 1-SRC_ALPHA) власним малюнком: чотирикутник із текстурою. Стан гри зберігається й повертається.
//  Де змішування неможливе (DirectDraw, запасні шляхи) картинка «приплюскується» на темний фон.
// ======================================================================
static CRITICAL_SECTION ocs;
static HDC osdDc = nullptr;
static HBITMAP osdBm = nullptr;
static uint32_t* osdBits = nullptr;     // BGRA, зверху вниз
static int osdW = 0, osdH = 0;
static uint32_t osdGen = 0;               // збільшується при кожній новій картинці

static void OsdInstall(HBITMAP bm, uint32_t* bits, int w, int h)
{
    if (osdBm) DeleteObject(osdBm);
    osdBm = bm; osdBits = bits; osdW = w; osdH = h;
    osdGen++;
}

// ---- готова картинка від NeoBar: той самий малюнок, що й у вікні оверлею ----
// Спільна пам'ять Local\NeoBarOsd_<pid>: 64 байти заголовка (int32): 0 'SBIM', 1 seqA, 2 ширина, 3 висота, 4 кут, 5 відступ x, 6 відступ y,
// 7 показувати (1/0), 8 seqB, 9 мітка часу (GetTickCount); далі піксели BGRA зверху вниз.
// seqA пишеться перед пікселями, seqB - після: збіг означає, що картинку не змінювали під час копіювання.
#define IM_MAGIC 0x4D494253u
#define IM_HDR 64
#define IM_MAX (8 * 1024 * 1024)
static HANDLE imMap = nullptr;
static const BYTE* imView = nullptr;
static DWORD imTried = 0;
static LONG imSeq = 0;
static bool imHave = false, imEver = false;      // imEver: картинка від NeoBar вже була
static int imShow = 1, imCorner = 0, imMx = 8, imMy = 8;

// 0 - картинки (ще) немає, 1 - є, 2 - NeoBar просить нічого не малювати
static int OsdImage()
{
    if (!imView)
    {
        DWORD now = GetTickCount();
        if (imTried && now - imTried < 250) return 0;
        imTried = now ? now : 1;
        wchar_t name[64];
        wsprintfW(name, L"Local\\NeoBarOsd_%lu", GetCurrentProcessId());
        imMap = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
        if (!imMap) return 0;
        imView = (const BYTE*)MapViewOfFile(imMap, FILE_MAP_READ, 0, 0, 0);
        if (!imView) { CloseHandle(imMap); imMap = nullptr; return 0; }
    }
    const volatile LONG* hd = (const volatile LONG*)imView;
    if ((uint32_t)hd[0] != IM_MAGIC) return 0;
    LONG sb = hd[8];
    if (sb == 0) return 0;
    if (GetTickCount() - (DWORD)hd[9] > 5000) { imHave = false; return imEver ? 2 : 0; }       // NeoBar давно нічого не передавав: стару картинку не показуємо
    if (!hd[7]) { imShow = 0; return 2; }
    if (sb != imSeq || !imHave)
    {
        int w = hd[2], h = hd[3];
        if (w <= 0 || h <= 0 || (size_t)w * h * 4 > IM_MAX) return imHave ? 1 : 0;
        if (!osdDc) return imHave ? 1 : 0;
        BITMAPINFO bi = {};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -h;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        uint32_t* bits = nullptr;
        HBITMAP bm = CreateDIBSection(osdDc, &bi, DIB_RGB_COLORS, (void**)&bits, nullptr, 0);
        if (!bm || !bits) return imHave ? 1 : 0;
        memcpy(bits, imView + IM_HDR, (size_t)w * h * 4);
        if (hd[1] != sb || hd[2] != w || hd[3] != h) { DeleteObject(bm); return imHave ? 1 : 0; }   // NeoBar саме писав: лишаємо попередню
        OsdInstall(bm, bits, w, h);
        imSeq = sb; imHave = true; imEver = true;
        imShow = 1; imCorner = hd[4] & 3; imMx = hd[5]; imMy = hd[6];
        if (imMx < 0 || imMx > 512) imMx = 8;
        if (imMy < 0 || imMy > 512) imMy = 8;
    }
    return 1;
}

static bool OsdBuild()
{
    if (!osdDc) osdDc = CreateCompatibleDC(nullptr);
    if (!osdDc) return false;
    int im = OsdImage();
    sh->osdImg = im == 1 ? 1 : 0;
    if (im == 2) return false;
    if (im == 1) return true;
    return false;                  // картинки від NeoBar ще немає: нічого не малюємо (розмір кадру вже записано)
}

static void OsdPos(int W, int H, int w, int h, int* x, int* y)
{
    int mx = 8, my = 8, c = sh->osdCorner;
    if (imHave && sh->osdImg) { mx = imMx; my = imMy; c = imCorner; }
    sh->osdFrameW = W; sh->osdFrameH = H;
    *x = (c == 1 || c == 3) ? W - mx - w : mx;
    *y = (c == 2 || c == 3) ? H - my - h : my;
    if (*x < 0) *x = 0;
    if (*y < 0) *y = 0;
}

static inline uint32_t To565(uint32_t p) { return ((p >> 19) & 0x1F) << 11 | ((p >> 10) & 0x3F) << 5 | ((p >> 3) & 0x1F); }

// ---------- спільне ----------
// Картинка від NeoBar має попередньо помножену прозорість (BGRA). Шляхи, що не вміють змішувати (запасні), беруть її «приплюснутою»
// на темний фон: osdFlat() заповнює flBm / flBits.
static HBITMAP flBm = nullptr;
static uint32_t* flBits = nullptr;
static uint32_t flGen = 0;
static int flW = 0, flH = 0;

static bool OsdFlat()
{
    if (flBits && flGen == osdGen && flW == osdW && flH == osdH) return true;
    if (!osdDc || !osdBits || osdW <= 0 || osdH <= 0) return false;
    if (!flBits || flW != osdW || flH != osdH)
    {
        BITMAPINFO bi = {};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = osdW;
        bi.bmiHeader.biHeight = -osdH;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        uint32_t* bits = nullptr;
        HBITMAP bm = CreateDIBSection(osdDc, &bi, DIB_RGB_COLORS, (void**)&bits, nullptr, 0);
        if (!bm || !bits) return false;
        if (flBm) DeleteObject(flBm);
        flBm = bm; flBits = bits; flW = osdW; flH = osdH;
    }
    size_t n = (size_t)osdW * osdH;
    for (size_t i = 0; i < n; i++)
    {
        uint32_t p = osdBits[i], ia = 255 - (p >> 24);
        uint32_t r = ((p >> 16) & 0xFF) + (0x14 * ia + 127) / 255, g = ((p >> 8) & 0xFF) + (0x16 * ia + 127) / 255, b = (p & 0xFF) + (0x20 * ia + 127) / 255;
        if (r > 255) r = 255;
        if (g > 255) g = 255;
        if (b > 255) b = 255;
        flBits[i] = 0xFF000000u | (r << 16) | (g << 8) | b;
    }
    flGen = osdGen;
    return true;
}

#include "shaders.h"

// Ресурси відпускаються, якщо гра кілька секунд не показувала кадрів (пристрій мусить мати змогу повністю звільнитися).
static DWORD d9Use = 0, d11Use = 0, d12Use = 0, d10Use = 0, d8Use = 0;
static volatile LONG wdStarted = 0;
static void OsdWatchStart();

// ---------- Direct3D 9 ----------
// Основний шлях: чотирикутник із текстурою й змішуванням (прозорість), стан гри зберігаємо блоком стану й повертаємо.
// Запасний (якщо не вдалося): копіювання пікселів на буфер кадру, без прозорості.
// Ресурси тримаємо між кадрами (інакше щокадру створювати текстуру на відеокарті), але відпускаємо при Reset і при зміні пристрою.
struct D9Res { IDirect3DDevice9* dev; IDirect3DTexture9* tex; IDirect3DVertexBuffer9* vb; int w, h; uint32_t gen; };
static D9Res d9r = {};

static void D9ResFree()      // під ocs
{
    if (d9r.vb) d9r.vb->Release();
    if (d9r.tex) d9r.tex->Release();
    d9r = D9Res();
}

static void OsdD9Release()
{
    EnterCriticalSection(&ocs);
    D9ResFree();
    LeaveCriticalSection(&ocs);
}

static bool D9Blend(IDirect3DDevice9* dev, IDirect3DSurface9* bb, int W, int H, int x, int y)
{
    // пристрій без власного посилання: ресурси й так тримають його, а зайве посилання не дає грі його знищити
    if (d9r.dev != dev) { D9ResFree(); d9r.dev = dev; }
    else if (d9r.tex)
    {
        IDirect3DDevice9* td = nullptr;
        if (SUCCEEDED(d9r.tex->GetDevice(&td)) && td) { bool same = td == dev; td->Release(); if (!same) { D9ResFree(); d9r.dev = dev; } }
    }
    if (!d9r.tex || d9r.w != osdW || d9r.h != osdH || d9r.gen != osdGen)
    {
        if (!d9r.tex || d9r.w != osdW || d9r.h != osdH)
        {
            if (d9r.tex) { d9r.tex->Release(); d9r.tex = nullptr; }
            if (FAILED(dev->CreateTexture(osdW, osdH, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &d9r.tex, nullptr)) || !d9r.tex) { d9r.tex = nullptr; return false; }
            d9r.w = osdW; d9r.h = osdH;
        }
        IDirect3DTexture9* sys = nullptr;
        if (FAILED(dev->CreateTexture(osdW, osdH, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &sys, nullptr)) || !sys) return false;
        bool ok = false;
        D3DLOCKED_RECT lr;
        if (SUCCEEDED(sys->LockRect(0, &lr, nullptr, 0)))
        {
            for (int r = 0; r < osdH; r++) memcpy((BYTE*)lr.pBits + (size_t)r * lr.Pitch, osdBits + (size_t)r * osdW, (size_t)osdW * 4);
            sys->UnlockRect(0);
            ok = SUCCEEDED(dev->UpdateTexture(sys, d9r.tex));
        }
        sys->Release();
        if (!ok) return false;
        d9r.gen = osdGen;
    }
    struct Vtx { float x, y, z, rhw, u, v; };
    if (!d9r.vb && (FAILED(dev->CreateVertexBuffer(sizeof(Vtx) * 4, D3DUSAGE_DYNAMIC | D3DUSAGE_WRITEONLY, D3DFVF_XYZRHW | D3DFVF_TEX1, D3DPOOL_DEFAULT, &d9r.vb, nullptr)) || !d9r.vb)) { d9r.vb = nullptr; return false; }
    IDirect3DStateBlock9* sb = nullptr;
    if (FAILED(dev->CreateStateBlock(D3DSBT_ALL, &sb)) || !sb) return false;
    IDirect3DSurface9 *rt = nullptr, *ds = nullptr;
    dev->GetRenderTarget(0, &rt);
    dev->GetDepthStencilSurface(&ds);
    bool rtSet = rt != bb;
    if (rtSet) dev->SetRenderTarget(0, bb);
    dev->SetDepthStencilSurface(nullptr);
    bool began = SUCCEEDED(dev->BeginScene());
    D3DVIEWPORT9 vp = { 0, 0, (DWORD)W, (DWORD)H, 0.0f, 1.0f };
    dev->SetViewport(&vp);
    dev->SetVertexShader(nullptr);
    dev->SetPixelShader(nullptr);
    dev->SetFVF(D3DFVF_XYZRHW | D3DFVF_TEX1);
    dev->SetTexture(0, d9r.tex);
    dev->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
    dev->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
    dev->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
    dev->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
    dev->SetTextureStageState(0, D3DTSS_TEXCOORDINDEX, 0);
    dev->SetTextureStageState(0, D3DTSS_TEXTURETRANSFORMFLAGS, D3DTTFF_DISABLE);
    dev->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE);
    dev->SetTextureStageState(1, D3DTSS_ALPHAOP, D3DTOP_DISABLE);
    dev->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_POINT);
    dev->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_POINT);
    dev->SetSamplerState(0, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
    dev->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP);
    dev->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP);
    dev->SetSamplerState(0, D3DSAMP_SRGBTEXTURE, FALSE);
    dev->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
    dev->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE, FALSE);
    dev->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);
    dev->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
    dev->SetRenderState(D3DRS_BLENDOP, D3DBLENDOP_ADD);
    dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);
    dev->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
    dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
    dev->SetRenderState(D3DRS_LIGHTING, FALSE);
    dev->SetRenderState(D3DRS_FOGENABLE, FALSE);
    dev->SetRenderState(D3DRS_STENCILENABLE, FALSE);
    dev->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);
    dev->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);
    dev->SetRenderState(D3DRS_COLORWRITEENABLE, 0xF);
    dev->SetRenderState(D3DRS_SRGBWRITEENABLE, FALSE);
    dev->SetRenderState(D3DRS_FILLMODE, D3DFILL_SOLID);
    dev->SetRenderState(D3DRS_CLIPPLANEENABLE, 0);
    float x0 = (float)x - 0.5f, y0 = (float)y - 0.5f, x1 = (float)(x + osdW) - 0.5f, y1 = (float)(y + osdH) - 0.5f;
    Vtx q[4] = { { x0, y0, 0.0f, 1.0f, 0.0f, 0.0f }, { x1, y0, 0.0f, 1.0f, 1.0f, 0.0f }, { x0, y1, 0.0f, 1.0f, 0.0f, 1.0f }, { x1, y1, 0.0f, 1.0f, 1.0f, 1.0f } };
    HRESULT hr = D3DERR_INVALIDCALL;
    void* vp2 = nullptr;
    if (SUCCEEDED(d9r.vb->Lock(0, sizeof(q), &vp2, D3DLOCK_DISCARD)) && vp2)
    {
        memcpy(vp2, q, sizeof(q));
        d9r.vb->Unlock();
        dev->SetStreamSource(0, d9r.vb, 0, sizeof(Vtx));
        hr = dev->DrawPrimitive(D3DPT_TRIANGLESTRIP, 0, 2);
    }
    if (began) dev->EndScene();
    sb->Apply();
    sb->Release();
    if (ds) { dev->SetDepthStencilSurface(ds); ds->Release(); }
    else dev->SetDepthStencilSurface(nullptr);
    if (rtSet && rt) dev->SetRenderTarget(0, rt);
    if (rt) rt->Release();
    return SUCCEEDED(hr);
}

static void OsdDraw9(IDirect3DDevice9* dev)
{
    if (!dev) return;
    if (dev->TestCooperativeLevel() != D3D_OK) return;      // пристрій втрачено (згорнули гру, зміна режиму): не чіпаємо
    EnterCriticalSection(&ocs);
    d9Use = GetTickCount(); OsdWatchStart();
    IDirect3DSurface9* bb = nullptr;
    if (SUCCEEDED(dev->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &bb)) && bb)
    {
        D3DSURFACE_DESC d = {};
        bb->GetDesc(&d);
        sh->osdFrameW = (LONG)d.Width; sh->osdFrameH = (LONG)d.Height;
        int bpp = (d.Format == D3DFMT_A8R8G8B8 || d.Format == D3DFMT_X8R8G8B8) ? 4 : (d.Format == D3DFMT_R5G6B5 ? 2 : 0);
        if (!OsdBuild()) {}
        else if (osdW > (int)d.Width || osdH > (int)d.Height) sh->osdErr = 4;
        else
        {
            int w = osdW, h = osdH, x, y;
            OsdPos(d.Width, d.Height, w, h, &x, &y);
            if (D9Blend(dev, bb, (int)d.Width, (int)d.Height, x, y))
            {
                sh->osdErr = 0; sh->osdApi = 2;
                InterlockedIncrement(&sh->osdDrawn);
            }
            else if (!bpp) sh->osdErr = 1;
            else if (d.MultiSampleType != D3DMULTISAMPLE_NONE) sh->osdErr = 3;
            else if (OsdFlat())
            {
                // запасний шлях: копіювання пікселів без прозорості
                IDirect3DSurface9* sys = nullptr;
                if (SUCCEEDED(dev->CreateOffscreenPlainSurface(w, h, d.Format, D3DPOOL_SYSTEMMEM, &sys, nullptr)) && sys)
                {
                    D3DLOCKED_RECT lr;
                    if (SUCCEEDED(sys->LockRect(&lr, nullptr, 0)))
                    {
                        for (int r = 0; r < h; r++)
                        {
                            const uint32_t* src = flBits + (size_t)r * flW;
                            if (bpp == 4) memcpy((BYTE*)lr.pBits + (size_t)r * lr.Pitch, src, (size_t)w * 4);
                            else { uint16_t* dst = (uint16_t*)((BYTE*)lr.pBits + (size_t)r * lr.Pitch); for (int c = 0; c < w; c++) dst[c] = (uint16_t)To565(src[c]); }
                        }
                        sys->UnlockRect();
                        POINT pt = { x, y };
                        RECT sr = { 0, 0, w, h };
                        HRESULT hr = dev->UpdateSurface(sys, &sr, bb, &pt);
                        if (FAILED(hr))
                        {
                            IDirect3DSurface9* gpu = nullptr;
                            if (SUCCEEDED(dev->CreateOffscreenPlainSurface(w, h, d.Format, D3DPOOL_DEFAULT, &gpu, nullptr)) && gpu)
                            {
                                if (SUCCEEDED(dev->UpdateSurface(sys, &sr, gpu, nullptr)))
                                {
                                    RECT dr = { x, y, x + w, y + h };
                                    hr = dev->StretchRect(gpu, &sr, bb, &dr, D3DTEXF_NONE);
                                }
                                gpu->Release();
                            }
                        }
                        sh->osdErr = SUCCEEDED(hr) ? 0 : (LONG)hr;
                        if (SUCCEEDED(hr)) { sh->osdApi = 2; InterlockedIncrement(&sh->osdDrawn); }
                    }
                    sys->Release();
                }
            }
        }
    }
    if (bb) bb->Release();
    LeaveCriticalSection(&ocs);
}

// ---------- Direct3D 11 (через DXGI) ----------
// Основний шлях: повноекранний трикутник-смуга з вершинного шейдера (без буферів), піксельний шейдер читає текстуру; змішування з прозорістю.
// Стан контексту гри зберігаємо й повертаємо. Запасний шлях: CopySubresourceRegion (без прозорості).
struct D11Res
{
    ID3D11Device* dev;
    ID3D11VertexShader* vs; ID3D11PixelShader* ps;
    ID3D11BlendState* bs; ID3D11RasterizerState* rs; ID3D11DepthStencilState* ds; ID3D11SamplerState* ss;
    ID3D11Texture2D* tex; ID3D11ShaderResourceView* srv; ID3D11ShaderResourceView* srvS;
    int w, h; uint32_t gen; bool failed;
};
static D11Res d11r = {};

#define SAFE_REL(p) do { if (p) { (p)->Release(); (p) = nullptr; } } while (0)

static void D11ResFree()     // під ocs
{
    SAFE_REL(d11r.srv); SAFE_REL(d11r.srvS); SAFE_REL(d11r.tex);
    SAFE_REL(d11r.vs); SAFE_REL(d11r.ps); SAFE_REL(d11r.bs); SAFE_REL(d11r.rs); SAFE_REL(d11r.ds); SAFE_REL(d11r.ss);
    SAFE_REL(d11r.dev);
    d11r = D11Res();
}

static bool D11ResInit(ID3D11Device* dev)
{
    D11ResFree();
    d11r.dev = dev; dev->AddRef();
    if (FAILED(dev->CreateVertexShader(g_vs40, sizeof(g_vs40), nullptr, &d11r.vs))) return false;
    if (FAILED(dev->CreatePixelShader(g_ps40, sizeof(g_ps40), nullptr, &d11r.ps))) return false;
    D3D11_BLEND_DESC bd = {};
    bd.RenderTarget[0].BlendEnable = TRUE;
    bd.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
    bd.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (FAILED(dev->CreateBlendState(&bd, &d11r.bs))) return false;
    D3D11_RASTERIZER_DESC rd = {};
    rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
    if (FAILED(dev->CreateRasterizerState(&rd, &d11r.rs))) return false;
    D3D11_DEPTH_STENCIL_DESC dd = {};
    dd.DepthEnable = FALSE; dd.StencilEnable = FALSE; dd.DepthFunc = D3D11_COMPARISON_ALWAYS;
    if (FAILED(dev->CreateDepthStencilState(&dd, &d11r.ds))) return false;
    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(dev->CreateSamplerState(&sd, &d11r.ss))) return false;
    return true;
}

// Формат вигляду (RTV) для буфера кадру: безтипові формати - як UNORM; sRGB лишається, лише позначається srgb
// (спільне для D3D10, D3D11 і D3D12)
static DXGI_FORMAT RtvFormat(DXGI_FORMAT fmt, bool& srgb)
{
    srgb = false;
    switch (fmt)
    {
        case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: srgb = true; return fmt;
        default: return fmt;
    }
}

static bool D11Blend(ID3D11Device* dev, ID3D11Texture2D* bb, DXGI_FORMAT fmt, int W, int H, int x, int y)
{
    if (d11r.dev != dev) { if (!D11ResInit(dev)) d11r.failed = true; }
    if (d11r.failed) return false;
    ID3D11DeviceContext* ctx = nullptr;
    dev->GetImmediateContext(&ctx);
    if (!ctx) return false;
    bool ok = false;
    // текстура: нова при зміні розміру, інакше лише оновлюємо вміст
    if (!d11r.tex || d11r.w != osdW || d11r.h != osdH)
    {
        SAFE_REL(d11r.srv); SAFE_REL(d11r.srvS); SAFE_REL(d11r.tex);
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = osdW; td.Height = osdH; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA sd = {};
        sd.pSysMem = osdBits; sd.SysMemPitch = (UINT)osdW * 4;
        D3D11_SHADER_RESOURCE_VIEW_DESC vd = {};
        vd.ViewDimension = D3D_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipLevels = 1;
        if (SUCCEEDED(dev->CreateTexture2D(&td, &sd, &d11r.tex)) && d11r.tex)
        {
            vd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            HRESULT h1 = dev->CreateShaderResourceView(d11r.tex, &vd, &d11r.srv);
            vd.Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
            HRESULT h2 = dev->CreateShaderResourceView(d11r.tex, &vd, &d11r.srvS);
            if (FAILED(h1) || FAILED(h2)) { SAFE_REL(d11r.srv); SAFE_REL(d11r.srvS); SAFE_REL(d11r.tex); }
        }
        d11r.w = osdW; d11r.h = osdH; d11r.gen = osdGen;
        if (!d11r.tex) { d11r.failed = true; ctx->Release(); return false; }
    }
    else if (d11r.gen != osdGen)
    {
        ctx->UpdateSubresource(d11r.tex, 0, nullptr, osdBits, (UINT)osdW * 4, 0);
        d11r.gen = osdGen;
    }
    // вигляд буфера кадру
    bool srgb; DXGI_FORMAT rf = RtvFormat(fmt, srgb);
    D3D11_RENDER_TARGET_VIEW_DESC rv = {};
    rv.Format = rf;
    rv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    ID3D11RenderTargetView* rtv = nullptr;
    if (SUCCEEDED(dev->CreateRenderTargetView(bb, &rv, &rtv)) && rtv)
    {
        // ---- зберігаємо стан контексту гри ----
        ID3D11RenderTargetView* oRtv[8] = {}; ID3D11DepthStencilView* oDsv = nullptr;
        ctx->OMGetRenderTargets(8, oRtv, &oDsv);
        ID3D11BlendState* oBs = nullptr; FLOAT oBf[4] = {}; UINT oMask = 0;
        ctx->OMGetBlendState(&oBs, oBf, &oMask);
        ID3D11DepthStencilState* oDs = nullptr; UINT oRef = 0;
        ctx->OMGetDepthStencilState(&oDs, &oRef);
        ID3D11RasterizerState* oRs = nullptr;
        ctx->RSGetState(&oRs);
        UINT nvp = 0;
        ctx->RSGetViewports(&nvp, nullptr);
        if (nvp > 16) nvp = 16;
        D3D11_VIEWPORT oVp[16] = {};
        if (nvp) ctx->RSGetViewports(&nvp, oVp);
        ID3D11InputLayout* oIl = nullptr; D3D11_PRIMITIVE_TOPOLOGY oTop = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
        ctx->IAGetInputLayout(&oIl);
        ctx->IAGetPrimitiveTopology(&oTop);
        ID3D11VertexShader* oVs = nullptr; ID3D11PixelShader* oPs = nullptr; ID3D11GeometryShader* oGs = nullptr;
        ID3D11HullShader* oHs = nullptr; ID3D11DomainShader* oDms = nullptr;
        ctx->VSGetShader(&oVs, nullptr, nullptr); ctx->PSGetShader(&oPs, nullptr, nullptr); ctx->GSGetShader(&oGs, nullptr, nullptr);
        ctx->HSGetShader(&oHs, nullptr, nullptr); ctx->DSGetShader(&oDms, nullptr, nullptr);
        ID3D11ShaderResourceView* oSrv = nullptr; ID3D11SamplerState* oSs = nullptr;
        ctx->PSGetShaderResources(0, 1, &oSrv);
        ctx->PSGetSamplers(0, 1, &oSs);
        // ---- наш малюнок ----
        D3D11_VIEWPORT vp = { (FLOAT)x, (FLOAT)y, (FLOAT)osdW, (FLOAT)osdH, 0.0f, 1.0f };
        FLOAT bf[4] = { 0, 0, 0, 0 };
        ID3D11ShaderResourceView* srv = srgb ? d11r.srvS : d11r.srv;
        ctx->IASetInputLayout(nullptr);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        ctx->VSSetShader(d11r.vs, nullptr, 0);
        ctx->GSSetShader(nullptr, nullptr, 0);
        ctx->HSSetShader(nullptr, nullptr, 0);
        ctx->DSSetShader(nullptr, nullptr, 0);
        ctx->PSSetShader(d11r.ps, nullptr, 0);
        ctx->PSSetShaderResources(0, 1, &srv);
        ctx->PSSetSamplers(0, 1, &d11r.ss);
        ctx->OMSetBlendState(d11r.bs, bf, 0xFFFFFFFFu);
        ctx->OMSetDepthStencilState(d11r.ds, 0);
        ctx->RSSetState(d11r.rs);
        ctx->RSSetViewports(1, &vp);
        ctx->OMSetRenderTargets(1, &rtv, nullptr);
        ctx->Draw(4, 0);
        // ---- повертаємо ----
        ctx->OMSetRenderTargets(8, oRtv, oDsv);
        ctx->OMSetBlendState(oBs, oBf, oMask);
        ctx->OMSetDepthStencilState(oDs, oRef);
        ctx->RSSetState(oRs);
        ctx->RSSetViewports(nvp, nvp ? oVp : nullptr);
        ctx->IASetInputLayout(oIl);
        ctx->IASetPrimitiveTopology(oTop);
        ctx->VSSetShader(oVs, nullptr, 0); ctx->PSSetShader(oPs, nullptr, 0); ctx->GSSetShader(oGs, nullptr, 0);
        ctx->HSSetShader(oHs, nullptr, 0); ctx->DSSetShader(oDms, nullptr, 0);
        ctx->PSSetShaderResources(0, 1, &oSrv);
        ctx->PSSetSamplers(0, 1, &oSs);
        for (int i = 0; i < 8; i++) SAFE_REL(oRtv[i]);
        SAFE_REL(oDsv); SAFE_REL(oBs); SAFE_REL(oDs); SAFE_REL(oRs); SAFE_REL(oIl);
        SAFE_REL(oVs); SAFE_REL(oPs); SAFE_REL(oGs); SAFE_REL(oHs); SAFE_REL(oDms); SAFE_REL(oSrv); SAFE_REL(oSs);
        rtv->Release();
        ok = true;
    }
    ctx->Release();
    return ok;
}

static void OsdDraw11(IDXGISwapChain* sc, ID3D11Device* dev)
{
    EnterCriticalSection(&ocs);
    d11Use = GetTickCount(); OsdWatchStart();
    ID3D11Texture2D* bb = nullptr;
    if (SUCCEEDED(sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) && bb)
    {
        D3D11_TEXTURE2D_DESC bd = {};
        bb->GetDesc(&bd);
        sh->osdFrameW = (LONG)bd.Width; sh->osdFrameH = (LONG)bd.Height;
        bool bgra = bd.Format == DXGI_FORMAT_B8G8R8A8_UNORM || bd.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
                    bd.Format == DXGI_FORMAT_B8G8R8X8_UNORM || bd.Format == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB || bd.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS;
        bool rgba = bd.Format == DXGI_FORMAT_R8G8B8A8_UNORM || bd.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || bd.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS;
        // 10 біт (R10G10B10A2) і 16-бітні плаваючі формати теж змішуються звичайним шейдером; для них немає запасного копіювання
        bool wide = bd.Format == DXGI_FORMAT_R10G10B10A2_UNORM || bd.Format == DXGI_FORMAT_R10G10B10A2_TYPELESS || bd.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        sh->osdFmt = (LONG)bd.Format; sh->osdMsaa = (LONG)bd.SampleDesc.Count;
        if (!OsdBuild()) {}
        else if (!bgra && !rgba && !wide) sh->osdErr = 1;
        else if (bd.SampleDesc.Count != 1) sh->osdErr = 3;
        else if (osdW > (int)bd.Width || osdH > (int)bd.Height) sh->osdErr = 4;
        else
        {
            int w = osdW, h = osdH, x, y;
            OsdPos(bd.Width, bd.Height, w, h, &x, &y);
            bool bindable = (bd.BindFlags & D3D11_BIND_RENDER_TARGET) != 0;
            sh->osdErr = bindable ? 42 : 41;        // якщо жоден шлях не спрацює, помилка лишиться (раніше невдача була мовчазною)
            if (bindable && D11Blend(dev, bb, bd.Format, (int)bd.Width, (int)bd.Height, x, y))
            {
                sh->osdErr = 0; sh->osdApi = 1;
                InterlockedIncrement(&sh->osdDrawn);
            }
            else if ((bgra || rgba) && OsdFlat())
            {
                // запасний шлях: копіювання пікселів без прозорості
                uint32_t* tmp = (uint32_t*)HeapAlloc(GetProcessHeap(), 0, (size_t)w * h * 4);
                if (tmp)
                {
                    for (int r = 0; r < h; r++)
                    {
                        const uint32_t* src = flBits + (size_t)r * flW;
                        uint32_t* dst = tmp + (size_t)r * w;
                        if (bgra) memcpy(dst, src, (size_t)w * 4);
                        else for (int c = 0; c < w; c++) { uint32_t p = src[c]; dst[c] = (p & 0xFF00FF00u) | ((p & 0xFF) << 16) | ((p >> 16) & 0xFF); }
                    }
                    D3D11_TEXTURE2D_DESC td = {};
                    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
                    td.Format = bd.Format;
                    td.SampleDesc.Count = 1;
                    td.Usage = D3D11_USAGE_DEFAULT;
                    D3D11_SUBRESOURCE_DATA sd = {};
                    sd.pSysMem = tmp;
                    sd.SysMemPitch = (UINT)w * 4;
                    ID3D11Texture2D* tex = nullptr;
                    HRESULT hr = dev->CreateTexture2D(&td, &sd, &tex);
                    if (SUCCEEDED(hr) && tex)
                    {
                        ID3D11DeviceContext* ctx = nullptr;
                        dev->GetImmediateContext(&ctx);
                        if (ctx)
                        {
                            D3D11_BOX box = { 0, 0, 0, (UINT)w, (UINT)h, 1 };
                            ctx->CopySubresourceRegion(bb, 0, x, y, 0, tex, 0, &box);
                            ctx->Release();
                            sh->osdErr = 0; sh->osdApi = 1;
                            InterlockedIncrement(&sh->osdDrawn);
                        }
                        tex->Release();
                    }
                    else sh->osdErr = (LONG)hr;
                    HeapFree(GetProcessHeap(), 0, tmp);
                }
            }
        }
    }
    if (bb) bb->Release();
    LeaveCriticalSection(&ocs);
}

// ---------- Direct3D 10 (через DXGI) ----------
// Те саме, що й для Direct3D 11, але без контексту: пристрій сам виконує команди. Шейдери ті самі (4.0).
struct D10Res
{
    ID3D10Device* dev;
    ID3D10VertexShader* vs; ID3D10PixelShader* ps;
    ID3D10BlendState* bs; ID3D10RasterizerState* rs; ID3D10DepthStencilState* ds; ID3D10SamplerState* ss;
    ID3D10Texture2D* tex; ID3D10ShaderResourceView* srv; ID3D10ShaderResourceView* srvS;
    int w, h; uint32_t gen; bool failed;
};
static D10Res d10r = {};


static void D10ResFree()     // під ocs
{
    SAFE_REL(d10r.srv); SAFE_REL(d10r.srvS); SAFE_REL(d10r.tex);
    SAFE_REL(d10r.vs); SAFE_REL(d10r.ps); SAFE_REL(d10r.bs); SAFE_REL(d10r.rs); SAFE_REL(d10r.ds); SAFE_REL(d10r.ss);
    SAFE_REL(d10r.dev);
    d10r = D10Res();
}

static bool D10ResInit(ID3D10Device* dev)
{
    D10ResFree();
    d10r.dev = dev; dev->AddRef();
    if (FAILED(dev->CreateVertexShader(g_vs40, sizeof(g_vs40), &d10r.vs))) return false;
    if (FAILED(dev->CreatePixelShader(g_ps40, sizeof(g_ps40), &d10r.ps))) return false;
    D3D10_BLEND_DESC bd = {};
    bd.BlendEnable[0] = TRUE;
    bd.SrcBlend = D3D10_BLEND_ONE;
    bd.DestBlend = D3D10_BLEND_INV_SRC_ALPHA;
    bd.BlendOp = D3D10_BLEND_OP_ADD;
    bd.SrcBlendAlpha = D3D10_BLEND_ONE;
    bd.DestBlendAlpha = D3D10_BLEND_INV_SRC_ALPHA;
    bd.BlendOpAlpha = D3D10_BLEND_OP_ADD;
    bd.RenderTargetWriteMask[0] = D3D10_COLOR_WRITE_ENABLE_ALL;
    if (FAILED(dev->CreateBlendState(&bd, &d10r.bs))) return false;
    D3D10_RASTERIZER_DESC rd = {};
    rd.FillMode = D3D10_FILL_SOLID; rd.CullMode = D3D10_CULL_NONE; rd.DepthClipEnable = TRUE;
    if (FAILED(dev->CreateRasterizerState(&rd, &d10r.rs))) return false;
    D3D10_DEPTH_STENCIL_DESC dd = {};
    dd.DepthEnable = FALSE; dd.StencilEnable = FALSE; dd.DepthFunc = D3D10_COMPARISON_ALWAYS;
    if (FAILED(dev->CreateDepthStencilState(&dd, &d10r.ds))) return false;
    D3D10_SAMPLER_DESC sd = {};
    sd.Filter = D3D10_FILTER_MIN_MAG_MIP_POINT;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D10_TEXTURE_ADDRESS_CLAMP;
    sd.ComparisonFunc = D3D10_COMPARISON_NEVER;
    sd.MaxLOD = D3D10_FLOAT32_MAX;
    if (FAILED(dev->CreateSamplerState(&sd, &d10r.ss))) return false;
    return true;
}

static bool D10Blend(ID3D10Device* dev, ID3D10Texture2D* bb, DXGI_FORMAT fmt, int x, int y)
{
    if (d10r.dev != dev) { if (!D10ResInit(dev)) d10r.failed = true; }
    if (d10r.failed) return false;
    if (!d10r.tex || d10r.w != osdW || d10r.h != osdH)
    {
        SAFE_REL(d10r.srv); SAFE_REL(d10r.srvS); SAFE_REL(d10r.tex);
        D3D10_TEXTURE2D_DESC td = {};
        td.Width = osdW; td.Height = osdH; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS;
        td.SampleDesc.Count = 1;
        td.Usage = D3D10_USAGE_DEFAULT;
        td.BindFlags = D3D10_BIND_SHADER_RESOURCE;
        D3D10_SUBRESOURCE_DATA sd = {};
        sd.pSysMem = osdBits; sd.SysMemPitch = (UINT)osdW * 4;
        D3D10_SHADER_RESOURCE_VIEW_DESC vd = {};
        vd.ViewDimension = D3D10_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipLevels = 1;
        if (SUCCEEDED(dev->CreateTexture2D(&td, &sd, &d10r.tex)) && d10r.tex)
        {
            vd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            HRESULT h1 = dev->CreateShaderResourceView(d10r.tex, &vd, &d10r.srv);
            vd.Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
            HRESULT h2 = dev->CreateShaderResourceView(d10r.tex, &vd, &d10r.srvS);
            if (FAILED(h1) || FAILED(h2)) { SAFE_REL(d10r.srv); SAFE_REL(d10r.srvS); SAFE_REL(d10r.tex); }
        }
        d10r.w = osdW; d10r.h = osdH; d10r.gen = osdGen;
        if (!d10r.tex) { d10r.failed = true; return false; }
    }
    else if (d10r.gen != osdGen)
    {
        dev->UpdateSubresource(d10r.tex, 0, nullptr, osdBits, (UINT)osdW * 4, 0);
        d10r.gen = osdGen;
    }
    bool srgb; DXGI_FORMAT rf = RtvFormat(fmt, srgb);
    D3D10_RENDER_TARGET_VIEW_DESC rv = {};
    rv.Format = rf;
    rv.ViewDimension = D3D10_RTV_DIMENSION_TEXTURE2D;
    ID3D10RenderTargetView* rtv = nullptr;
    if (FAILED(dev->CreateRenderTargetView(bb, &rv, &rtv)) || !rtv) return false;
    // ---- зберігаємо стан пристрою гри ----
    ID3D10RenderTargetView* oRtv[8] = {}; ID3D10DepthStencilView* oDsv = nullptr;
    dev->OMGetRenderTargets(8, oRtv, &oDsv);
    ID3D10BlendState* oBs = nullptr; FLOAT oBf[4] = {}; UINT oMask = 0;
    dev->OMGetBlendState(&oBs, oBf, &oMask);
    ID3D10DepthStencilState* oDs = nullptr; UINT oRef = 0;
    dev->OMGetDepthStencilState(&oDs, &oRef);
    ID3D10RasterizerState* oRs = nullptr;
    dev->RSGetState(&oRs);
    UINT nvp = 0;
    dev->RSGetViewports(&nvp, nullptr);
    if (nvp > 16) nvp = 16;
    D3D10_VIEWPORT oVp[16] = {};
    if (nvp) dev->RSGetViewports(&nvp, oVp);
    ID3D10InputLayout* oIl = nullptr; D3D10_PRIMITIVE_TOPOLOGY oTop = D3D10_PRIMITIVE_TOPOLOGY_UNDEFINED;
    dev->IAGetInputLayout(&oIl);
    dev->IAGetPrimitiveTopology(&oTop);
    ID3D10VertexShader* oVs = nullptr; ID3D10PixelShader* oPs = nullptr; ID3D10GeometryShader* oGs = nullptr;
    dev->VSGetShader(&oVs); dev->PSGetShader(&oPs); dev->GSGetShader(&oGs);
    ID3D10ShaderResourceView* oSrv = nullptr; ID3D10SamplerState* oSs = nullptr;
    dev->PSGetShaderResources(0, 1, &oSrv);
    dev->PSGetSamplers(0, 1, &oSs);
    // ---- наш малюнок ----
    D3D10_VIEWPORT vp = { x, y, (UINT)osdW, (UINT)osdH, 0.0f, 1.0f };
    FLOAT bf[4] = { 0, 0, 0, 0 };
    ID3D10ShaderResourceView* srv = srgb ? d10r.srvS : d10r.srv;
    dev->IASetInputLayout(nullptr);
    dev->IASetPrimitiveTopology(D3D10_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    dev->VSSetShader(d10r.vs);
    dev->GSSetShader(nullptr);
    dev->PSSetShader(d10r.ps);
    dev->PSSetShaderResources(0, 1, &srv);
    dev->PSSetSamplers(0, 1, &d10r.ss);
    dev->OMSetBlendState(d10r.bs, bf, 0xFFFFFFFFu);
    dev->OMSetDepthStencilState(d10r.ds, 0);
    dev->RSSetState(d10r.rs);
    dev->RSSetViewports(1, &vp);
    dev->OMSetRenderTargets(1, &rtv, nullptr);
    dev->Draw(4, 0);
    // ---- повертаємо ----
    dev->OMSetRenderTargets(8, oRtv, oDsv);
    dev->OMSetBlendState(oBs, oBf, oMask);
    dev->OMSetDepthStencilState(oDs, oRef);
    dev->RSSetState(oRs);
    dev->RSSetViewports(nvp, nvp ? oVp : nullptr);
    dev->IASetInputLayout(oIl);
    dev->IASetPrimitiveTopology(oTop);
    dev->VSSetShader(oVs); dev->PSSetShader(oPs); dev->GSSetShader(oGs);
    dev->PSSetShaderResources(0, 1, &oSrv);
    dev->PSSetSamplers(0, 1, &oSs);
    for (int i = 0; i < 8; i++) SAFE_REL(oRtv[i]);
    SAFE_REL(oDsv); SAFE_REL(oBs); SAFE_REL(oDs); SAFE_REL(oRs); SAFE_REL(oIl);
    SAFE_REL(oVs); SAFE_REL(oPs); SAFE_REL(oGs); SAFE_REL(oSrv); SAFE_REL(oSs);
    rtv->Release();
    return true;
}

static void OsdDraw10(IDXGISwapChain* sc, ID3D10Device* dev)
{
    EnterCriticalSection(&ocs);
    d10Use = GetTickCount(); OsdWatchStart();
    ID3D10Texture2D* bb = nullptr;
    if (SUCCEEDED(sc->GetBuffer(0, __uuidof(ID3D10Texture2D), (void**)&bb)) && bb)
    {
        D3D10_TEXTURE2D_DESC bd = {};
        bb->GetDesc(&bd);
        sh->osdFrameW = (LONG)bd.Width; sh->osdFrameH = (LONG)bd.Height;
        bool bgra = bd.Format == DXGI_FORMAT_B8G8R8A8_UNORM || bd.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
                    bd.Format == DXGI_FORMAT_B8G8R8X8_UNORM || bd.Format == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB || bd.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS;
        bool rgba = bd.Format == DXGI_FORMAT_R8G8B8A8_UNORM || bd.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || bd.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS;
        // 10 біт (R10G10B10A2) і 16-бітні плаваючі формати теж змішуються звичайним шейдером; для них немає запасного копіювання
        bool wide = bd.Format == DXGI_FORMAT_R10G10B10A2_UNORM || bd.Format == DXGI_FORMAT_R10G10B10A2_TYPELESS || bd.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        sh->osdFmt = (LONG)bd.Format; sh->osdMsaa = (LONG)bd.SampleDesc.Count;
        if (!OsdBuild()) {}
        else if (!bgra && !rgba && !wide) sh->osdErr = 1;
        else if (bd.SampleDesc.Count != 1) sh->osdErr = 3;
        else if (osdW > (int)bd.Width || osdH > (int)bd.Height) sh->osdErr = 4;
        else if (!(bd.BindFlags & D3D10_BIND_RENDER_TARGET)) sh->osdErr = 41;
        else
        {
            int x, y;
            OsdPos(bd.Width, bd.Height, osdW, osdH, &x, &y);
            if (D10Blend(dev, bb, bd.Format, x, y))
            {
                sh->osdErr = 0; sh->osdApi = 6;
                InterlockedIncrement(&sh->osdDrawn);
            }
            else sh->osdErr = 42;
        }
    }
    if (bb) bb->Release();
    LeaveCriticalSection(&ocs);
}

// Direct3D 8 (через sbd8.cpp)
static void OsdDraw8(void* dev)
{
    EnterCriticalSection(&ocs);
    d8Use = GetTickCount(); OsdWatchStart();
    int W = 0, H = 0;
    if (SbD8Size(dev, &W, &H) == 0)
    {
        sh->osdFrameW = W; sh->osdFrameH = H;
        if (!OsdBuild()) {}
        else if (osdW > W || osdH > H) sh->osdErr = 4;
        else
        {
            int x, y;
            OsdPos(W, H, osdW, osdH, &x, &y);
            int r = SbD8Draw(dev, osdBits, osdW, osdH, osdGen, x, y);
            if (r == 0) { sh->osdErr = 0; sh->osdApi = 7; InterlockedIncrement(&sh->osdDrawn); }
            else sh->osdErr = r;
        }
    }
    LeaveCriticalSection(&ocs);
}

// ---------- Direct3D 12 ----------
// Черга команд гри: ExecuteCommandLists перехоплено (остання DIRECT-черга), а черги ланцюжків, створених після впровадження, відомі точно.
// Малюємо власним списком команд на тій самій черзі, відразу перед Present: буфер кадру переходить PRESENT -> RENDER_TARGET -> PRESENT.
// Кілька (три) наборів ресурсів по колу, кожен із власним розподілювачем, текстурою й купою дескрипторів, бо GPU може ще читати попередній.
typedef void (STDMETHODCALLTYPE* ExecCL_t)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
static ExecCL_t oExecCL = nullptr;
static ID3D12CommandQueue* volatile d12Last = nullptr;
static volatile DWORD d12LastT = 0;
struct D12ScQ { void* sc; ID3D12CommandQueue* q; };
static D12ScQ d12scq[4];
static unsigned d12scqNext = 0;
static bool d12Hooked = false;
static int d12Tries = 0;

static void STDMETHODCALLTYPE hkExecCL(ID3D12CommandQueue* q, UINT n, ID3D12CommandList* const* l)
{
    d12LastT = GetTickCount();
    if (q != d12Last && n && l && l[0] && l[0]->GetType() == D3D12_COMMAND_LIST_TYPE_DIRECT)
    {
        q->AddRef();
        ID3D12CommandQueue* old = (ID3D12CommandQueue*)InterlockedExchangePointer((PVOID volatile*)&d12Last, q);
        if (old) old->Release();
    }
    oExecCL(q, n, l);
}

static void Dx12NoteQueue(void* sc, IUnknown* dev)
{
    if (!dev || !sc) return;
    ID3D12CommandQueue* q = nullptr;
    if (FAILED(dev->QueryInterface(__uuidof(ID3D12CommandQueue), (void**)&q)) || !q) return;
    EnterCriticalSection(&ocs);
    D12ScQ* e = &d12scq[d12scqNext++ % 4];
    if (e->q) e->q->Release();
    e->sc = sc; e->q = q;
    LeaveCriticalSection(&ocs);
}

struct D12Slot
{
    ID3D12CommandAllocator* alloc;
    ID3D12Resource* upload; UINT64 upSize;
    ID3D12Resource* tex; int tw, th; uint32_t tgen; D3D12_RESOURCE_STATES texState;
    ID3D12DescriptorHeap* srvHeap;
    UINT64 fenceVal;
};
struct D12Pso { DXGI_FORMAT fmt; ID3D12PipelineState* pso; };
struct D12Res
{
    ID3D12Device* dev;
    ID3D12RootSignature* rs;
    D12Pso pso[4];
    ID3D12DescriptorHeap* rtvHeap;
    ID3D12GraphicsCommandList* cl;
    ID3D12Fence* fence; HANDLE ev; UINT64 fenceNext;
    D12Slot slot[3]; unsigned cur;
    UINT rtvInc, srvInc;
    bool failed;
};
static D12Res d12r = {};

static void D12ResFree()     // під ocs
{
    if (d12r.fence && d12r.ev)
    {
        // GPU мав закінчити роботу з нашими ресурсами
        for (int i = 0; i < 3; i++)
            if (d12r.slot[i].fenceVal && d12r.fence->GetCompletedValue() < d12r.slot[i].fenceVal)
            {
                if (SUCCEEDED(d12r.fence->SetEventOnCompletion(d12r.slot[i].fenceVal, d12r.ev))) WaitForSingleObject(d12r.ev, 500);
            }
    }
    for (int i = 0; i < 3; i++)
    {
        D12Slot& s = d12r.slot[i];
        SAFE_REL(s.srvHeap); SAFE_REL(s.tex); SAFE_REL(s.upload); SAFE_REL(s.alloc);
    }
    for (int i = 0; i < 4; i++) SAFE_REL(d12r.pso[i].pso);
    SAFE_REL(d12r.cl); SAFE_REL(d12r.rtvHeap); SAFE_REL(d12r.rs); SAFE_REL(d12r.fence);
    if (d12r.ev) CloseHandle(d12r.ev);
    SAFE_REL(d12r.dev);
    d12r = D12Res();
}

static bool D12ResInit(ID3D12Device* dev)
{
    D12ResFree();
    d12r.dev = dev; dev->AddRef();
    HMODULE m = PinMod(L"d3d12.dll");
    typedef HRESULT (WINAPI* Ser_t)(const D3D12_ROOT_SIGNATURE_DESC*, D3D_ROOT_SIGNATURE_VERSION, ID3DBlob**, ID3DBlob**);
    Ser_t ser = m ? (Ser_t)(void*)GetProcAddress(m, "D3D12SerializeRootSignature") : nullptr;
    if (!ser) return false;
    D3D12_DESCRIPTOR_RANGE range = {};
    range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; range.NumDescriptors = 1; range.BaseShaderRegister = 0; range.RegisterSpace = 0;
    range.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;
    D3D12_ROOT_PARAMETER rp = {};
    rp.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    rp.DescriptorTable.NumDescriptorRanges = 1; rp.DescriptorTable.pDescriptorRanges = &range;
    rp.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    D3D12_STATIC_SAMPLER_DESC ss = {};
    ss.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
    ss.AddressU = ss.AddressV = ss.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    ss.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
    ss.BorderColor = D3D12_STATIC_BORDER_COLOR_TRANSPARENT_BLACK;
    ss.MaxLOD = D3D12_FLOAT32_MAX;
    ss.ShaderRegister = 0; ss.RegisterSpace = 0;
    ss.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
    D3D12_ROOT_SIGNATURE_DESC rsd = {};
    rsd.NumParameters = 1; rsd.pParameters = &rp; rsd.NumStaticSamplers = 1; rsd.pStaticSamplers = &ss;
    rsd.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;
    ID3DBlob *blob = nullptr, *err = nullptr;
    HRESULT hr = ser(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err);
    if (err) err->Release();
    if (FAILED(hr) || !blob) return false;
    hr = dev->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), __uuidof(ID3D12RootSignature), (void**)&d12r.rs);
    blob->Release();
    if (FAILED(hr)) return false;
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; hd.NumDescriptors = 3; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
    if (FAILED(dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), (void**)&d12r.rtvHeap))) return false;
    d12r.rtvInc = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    d12r.srvInc = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    for (int i = 0; i < 3; i++)
        if (FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator), (void**)&d12r.slot[i].alloc))) return false;
    if (FAILED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, d12r.slot[0].alloc, nullptr, __uuidof(ID3D12GraphicsCommandList), (void**)&d12r.cl))) return false;
    d12r.cl->Close();
    if (FAILED(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), (void**)&d12r.fence))) return false;
    d12r.ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    return d12r.ev != nullptr;
}

static ID3D12PipelineState* D12PsoFor(DXGI_FORMAT rf)
{
    for (int i = 0; i < 4; i++) if (d12r.pso[i].pso && d12r.pso[i].fmt == rf) return d12r.pso[i].pso;
    D3D12_GRAPHICS_PIPELINE_STATE_DESC gp = {};
    gp.pRootSignature = d12r.rs;
    gp.VS.pShaderBytecode = g_vs50; gp.VS.BytecodeLength = sizeof(g_vs50);
    gp.PS.pShaderBytecode = g_ps50; gp.PS.BytecodeLength = sizeof(g_ps50);
    D3D12_RENDER_TARGET_BLEND_DESC& b = gp.BlendState.RenderTarget[0];
    b.BlendEnable = TRUE;
    b.SrcBlend = D3D12_BLEND_ONE; b.DestBlend = D3D12_BLEND_INV_SRC_ALPHA; b.BlendOp = D3D12_BLEND_OP_ADD;
    b.SrcBlendAlpha = D3D12_BLEND_ONE; b.DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA; b.BlendOpAlpha = D3D12_BLEND_OP_ADD;
    b.LogicOp = D3D12_LOGIC_OP_NOOP;
    b.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
    gp.SampleMask = 0xFFFFFFFFu;
    gp.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
    gp.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
    gp.RasterizerState.DepthClipEnable = TRUE;
    gp.DepthStencilState.DepthEnable = FALSE;
    gp.DepthStencilState.StencilEnable = FALSE;
    gp.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    gp.NumRenderTargets = 1;
    gp.RTVFormats[0] = rf;
    gp.SampleDesc.Count = 1;
    ID3D12PipelineState* p = nullptr;
    if (FAILED(d12r.dev->CreateGraphicsPipelineState(&gp, __uuidof(ID3D12PipelineState), (void**)&p)) || !p) return nullptr;
    for (int i = 0; i < 4; i++) if (!d12r.pso[i].pso) { d12r.pso[i].fmt = rf; d12r.pso[i].pso = p; return p; }
    SAFE_REL(d12r.pso[0].pso);              // таблиця заповнена (дивна гра зі зміною формату): заміняємо першу
    d12r.pso[0].fmt = rf; d12r.pso[0].pso = p;
    return p;
}

static void D12Barrier(ID3D12GraphicsCommandList* cl, ID3D12Resource* r, D3D12_RESOURCE_STATES a, D3D12_RESOURCE_STATES b)
{
    D3D12_RESOURCE_BARRIER rb = {};
    rb.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    rb.Transition.pResource = r;
    rb.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    rb.Transition.StateBefore = a; rb.Transition.StateAfter = b;
    cl->ResourceBarrier(1, &rb);
}

// 0 - намальовано; інакше код помилки для osdErr
static int D12Blend(IDXGISwapChain* sc, ID3D12Device* dev, int W, int H, DXGI_FORMAT fmt, int x, int y)
{
    // черга: точна для ланцюжка або остання, на якій гра виконувала команди
    ID3D12CommandQueue* q = nullptr;
    // ланцюжок DXGI 12 сам віддає свою чергу
    if (FAILED(sc->GetDevice(__uuidof(ID3D12CommandQueue), (void**)&q))) q = nullptr;
    if (!q) for (int i = 0; i < 4; i++) if (d12scq[i].sc == (void*)sc && d12scq[i].q) { q = d12scq[i].q; q->AddRef(); break; }
    if (!q) { q = d12Last; if (q) q->AddRef(); }
    if (!q) return 32;
    int res = 0;
    do
    {
        ID3D12Device* qd = nullptr;
        if (SUCCEEDED(q->GetDevice(__uuidof(ID3D12Device), (void**)&qd)) && qd) { bool same = qd == dev; qd->Release(); if (!same) { res = 33; break; } }
        if (d12r.dev != dev) { if (!D12ResInit(dev)) d12r.failed = true; }
        if (d12r.failed) { res = 31; break; }
        bool srgb; DXGI_FORMAT rf = RtvFormat(fmt, srgb);
        IDXGISwapChain3* sc3 = nullptr;
        ID3D12Resource* bb = nullptr;
        if (FAILED(sc->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&sc3)) || !sc3) { res = 31; break; }
        UINT bi = sc3->GetCurrentBackBufferIndex();
        sc3->Release();
        if (FAILED(sc->GetBuffer(bi, __uuidof(ID3D12Resource), (void**)&bb)) || !bb) { res = 31; break; }
        D12Slot& s = d12r.slot[d12r.cur % 3];
        unsigned slotNo = d12r.cur % 3;
        d12r.cur++;
        // слот вільний, лише коли GPU закінчив із попереднім використанням
        if (s.fenceVal && d12r.fence->GetCompletedValue() < s.fenceVal)
        {
            if (FAILED(d12r.fence->SetEventOnCompletion(s.fenceVal, d12r.ev)) || WaitForSingleObject(d12r.ev, 2000) != WAIT_OBJECT_0) { bb->Release(); res = 31; break; }
        }
        ID3D12PipelineState* pso = D12PsoFor(rf);
        if (!pso) { bb->Release(); res = 31; break; }
        if (FAILED(s.alloc->Reset()) || FAILED(d12r.cl->Reset(s.alloc, nullptr))) { bb->Release(); res = 31; break; }
        ID3D12GraphicsCommandList* cl = d12r.cl;
        bool ok = true;
        // текстура слота: створюємо / оновлюємо вміст
        if (!s.tex || s.tw != osdW || s.th != osdH)
        {
            SAFE_REL(s.tex); SAFE_REL(s.upload); SAFE_REL(s.srvHeap);
            D3D12_HEAP_PROPERTIES hp = {};
            hp.Type = D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC td = {};
            td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; td.Width = osdW; td.Height = osdH; td.DepthOrArraySize = 1; td.MipLevels = 1;
            td.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS; td.SampleDesc.Count = 1; td.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN; td.Flags = D3D12_RESOURCE_FLAG_NONE;
            if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &td, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, __uuidof(ID3D12Resource), (void**)&s.tex))) ok = false;
            else
            {
                s.tw = osdW; s.th = osdH; s.tgen = 0; s.texState = D3D12_RESOURCE_STATE_COPY_DEST;
                D3D12_DESCRIPTOR_HEAP_DESC hd = {};
                hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = 2; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
                if (FAILED(dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), (void**)&s.srvHeap))) ok = false;
                else
                {
                    D3D12_SHADER_RESOURCE_VIEW_DESC vd = {};
                    vd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                    vd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
                    vd.Texture2D.MipLevels = 1;
                    D3D12_CPU_DESCRIPTOR_HANDLE h = s.srvHeap->GetCPUDescriptorHandleForHeapStart();
                    vd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    dev->CreateShaderResourceView(s.tex, &vd, h);
                    h.ptr += d12r.srvInc;
                    vd.Format = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
                    dev->CreateShaderResourceView(s.tex, &vd, h);
                }
            }
        }
        if (ok && s.tgen != osdGen)
        {
            UINT pitch = ((UINT)osdW * 4 + 255) & ~255u;
            UINT64 need = (UINT64)pitch * osdH;
            if (!s.upload || s.upSize < need)
            {
                SAFE_REL(s.upload);
                D3D12_HEAP_PROPERTIES hp = {};
                hp.Type = D3D12_HEAP_TYPE_UPLOAD;
                D3D12_RESOURCE_DESC bd = {};
                bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; bd.Width = need; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1;
                bd.Format = DXGI_FORMAT_UNKNOWN; bd.SampleDesc.Count = 1; bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
                if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, __uuidof(ID3D12Resource), (void**)&s.upload))) ok = false;
                else s.upSize = need;
            }
            if (ok)
            {
                void* p = nullptr;
                D3D12_RANGE none = { 0, 0 };
                if (FAILED(s.upload->Map(0, &none, &p)) || !p) ok = false;
                else
                {
                    for (int r = 0; r < osdH; r++) memcpy((BYTE*)p + (size_t)r * pitch, osdBits + (size_t)r * osdW, (size_t)osdW * 4);
                    s.upload->Unmap(0, nullptr);
                    if (s.texState != D3D12_RESOURCE_STATE_COPY_DEST) { D12Barrier(cl, s.tex, s.texState, D3D12_RESOURCE_STATE_COPY_DEST); s.texState = D3D12_RESOURCE_STATE_COPY_DEST; }
                    D3D12_TEXTURE_COPY_LOCATION dl = {}, sl = {};
                    dl.pResource = s.tex; dl.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dl.SubresourceIndex = 0;
                    sl.pResource = s.upload; sl.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                    sl.PlacedFootprint.Offset = 0;
                    sl.PlacedFootprint.Footprint.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    sl.PlacedFootprint.Footprint.Width = osdW; sl.PlacedFootprint.Footprint.Height = osdH; sl.PlacedFootprint.Footprint.Depth = 1;
                    sl.PlacedFootprint.Footprint.RowPitch = pitch;
                    cl->CopyTextureRegion(&dl, 0, 0, 0, &sl, nullptr);
                    D12Barrier(cl, s.tex, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
                    s.texState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
                    s.tgen = osdGen;
                }
            }
        }
        if (ok && s.texState != D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE) ok = false;
        if (ok)
        {
            D3D12_CPU_DESCRIPTOR_HANDLE rtvH = d12r.rtvHeap->GetCPUDescriptorHandleForHeapStart();
            rtvH.ptr += (SIZE_T)d12r.rtvInc * slotNo;
            D3D12_RENDER_TARGET_VIEW_DESC rv = {};
            rv.Format = rf; rv.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
            dev->CreateRenderTargetView(bb, &rv, rtvH);
            D3D12_GPU_DESCRIPTOR_HANDLE gh = s.srvHeap->GetGPUDescriptorHandleForHeapStart();
            if (srgb) gh.ptr += d12r.srvInc;
            D12Barrier(cl, bb, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
            ID3D12DescriptorHeap* heaps[1] = { s.srvHeap };
            cl->SetGraphicsRootSignature(d12r.rs);
            cl->SetDescriptorHeaps(1, heaps);
            cl->SetPipelineState(pso);
            cl->SetGraphicsRootDescriptorTable(0, gh);
            D3D12_VIEWPORT vp = { (FLOAT)x, (FLOAT)y, (FLOAT)osdW, (FLOAT)osdH, 0.0f, 1.0f };
            D3D12_RECT sr = { x, y, x + osdW, y + osdH };
            cl->RSSetViewports(1, &vp);
            cl->RSSetScissorRects(1, &sr);
            cl->OMSetRenderTargets(1, &rtvH, FALSE, nullptr);
            cl->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            cl->DrawInstanced(4, 1, 0, 0);
            D12Barrier(cl, bb, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
        }
        HRESULT hc = cl->Close();
        if (ok && SUCCEEDED(hc))
        {
            ID3D12CommandList* lists[1] = { cl };
            q->ExecuteCommandLists(1, lists);
            s.fenceVal = ++d12r.fenceNext;
            if (FAILED(q->Signal(d12r.fence, s.fenceVal))) s.fenceVal = 0;
        }
        else res = 31;
        bb->Release();
    } while (false);
    q->Release();
    return res;
}

static void OsdDraw12(IDXGISwapChain* sc, ID3D12Device* dev)
{
    DXGI_SWAP_CHAIN_DESC sd = {};
    if (FAILED(sc->GetDesc(&sd))) return;
    int W = (int)sd.BufferDesc.Width, H = (int)sd.BufferDesc.Height;
    sh->osdFrameW = W; sh->osdFrameH = H;
    EnterCriticalSection(&ocs);
    d12Use = GetTickCount(); OsdWatchStart();
    if (OsdBuild())
    {
        DXGI_FORMAT f = sd.BufferDesc.Format;
        bool fmtOk = f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_TYPELESS ||
                     f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_R8G8B8A8_TYPELESS;
        if (!fmtOk) sh->osdErr = 1;
        else if (osdW > W || osdH > H) sh->osdErr = 4;
        else
        {
            int x, y;
            OsdPos(W, H, osdW, osdH, &x, &y);
            int r = D12Blend(sc, dev, W, H, f, x, y);
            sh->osdErr = r;
            if (r == 0) { sh->osdApi = 5; InterlockedIncrement(&sh->osdDrawn); }
        }
    }
    LeaveCriticalSection(&ocs);
}

static DWORD WINAPI OsdWatch(LPVOID)
{
    for (;;)
    {
        Sleep(1000);
        DWORD n = GetTickCount();
        EnterCriticalSection(&ocs);
        // Direct3D 9 і 8: пристрій без прапора MULTITHREADED не терпить звільнення ресурсів з іншого потоку (на екранах завантаження
        // гра кілька секунд не показує кадрів - саме тоді це спричиняло вильот GTA SA). Ресурси лишаються до Reset / нового пристрою гри.
        if (d11r.dev && n - d11Use > 3000) D11ResFree();
        if (d12r.dev && n - d12Use > 3000) D12ResFree();
        if (d10r.dev && n - d10Use > 3000) D10ResFree();
        if (n - d12Use > 3000)
            for (int i = 0; i < 4; i++) if (d12scq[i].q) { d12scq[i].q->Release(); d12scq[i].q = nullptr; d12scq[i].sc = nullptr; }
        if (d12Last && n - d12LastT > 3000)
        {
            ID3D12CommandQueue* old = (ID3D12CommandQueue*)InterlockedExchangePointer((PVOID volatile*)&d12Last, nullptr);
            if (old) old->Release();
        }
        LeaveCriticalSection(&ocs);
    }
    return 0;
}
static void OsdWatchStart()
{
    if (InterlockedExchange(&wdStarted, 1) != 0) return;
    HANDLE t = CreateThread(nullptr, 0, OsdWatch, nullptr, 0, nullptr);
    if (t) CloseHandle(t);
}

// DXGI: за пристроєм ланцюжка вибираємо Direct3D 11 або 12
static void OsdDrawDxgi(IDXGISwapChain* sc)
{
    ID3D11Device* d11 = nullptr;
    if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D11Device), (void**)&d11)) && d11) { OsdDraw11(sc, d11); d11->Release(); return; }
    ID3D12Device* d12 = nullptr;
    if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D12Device), (void**)&d12)) && d12) { OsdDraw12(sc, d12); d12->Release(); return; }
    ID3D10Device* d10 = nullptr;
    if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D10Device), (void**)&d10)) && d10) { OsdDraw10(sc, d10); d10->Release(); return; }
    sh->osdErr = 2;          // невідомий пристрій
}

// ---------- OpenGL ----------
// Усі функції беремо під час роботи з opengl32.dll гри (бібліотеку не лінкуємо, щоб не підвантажувати OpenGL у кожну гру).
// Сучасні контексти (3.0+): власна програма з шейдерами, VAO без атрибутів, текстура; стан гри зберігаємо й повертаємо.
// Старі контексти (до 3.0): glDrawPixels між glPushAttrib / glPopAttrib.
typedef unsigned int GLenum_; typedef unsigned int GLuint_; typedef int GLint_; typedef int GLsizei_;
typedef unsigned char GLboolean_; typedef float GLfloat_; typedef char GLchar_;
#define GLF(ret, name, args) typedef ret (WINAPI* name##_t) args; static name##_t name = nullptr;
GLF(const unsigned char*, glGetString, (GLenum_))
GLF(void, glGetIntegerv, (GLenum_, GLint_*))
GLF(void, glGetBooleanv, (GLenum_, GLboolean_*))
GLF(void, glEnable, (GLenum_))
GLF(void, glDisable, (GLenum_))
GLF(GLboolean_, glIsEnabled, (GLenum_))
GLF(void, glBindTexture, (GLenum_, GLuint_))
GLF(void, glGenTextures, (GLsizei_, GLuint_*))
GLF(void, glTexImage2D, (GLenum_, GLint_, GLint_, GLsizei_, GLsizei_, GLint_, GLenum_, GLenum_, const void*))
GLF(void, glTexParameteri, (GLenum_, GLenum_, GLint_))
GLF(void, glViewport, (GLint_, GLint_, GLsizei_, GLsizei_))
GLF(void, glDrawArrays, (GLenum_, GLint_, GLsizei_))
GLF(void, glPixelStorei, (GLenum_, GLint_))
GLF(void, glColorMask, (GLboolean_, GLboolean_, GLboolean_, GLboolean_))
GLF(void, glPolygonMode, (GLenum_, GLenum_))
GLF(void, glDrawBuffer, (GLenum_))
GLF(void, glPushAttrib, (unsigned int))
GLF(void, glPopAttrib, (void))
GLF(void, glPushClientAttrib, (unsigned int))
GLF(void, glPopClientAttrib, (void))
GLF(void, glMatrixMode, (GLenum_))
GLF(void, glPushMatrix, (void))
GLF(void, glPopMatrix, (void))
GLF(void, glLoadIdentity, (void))
GLF(void, glOrtho, (double, double, double, double, double, double))
GLF(void, glRasterPos2i, (GLint_, GLint_))
GLF(void, glDrawPixels, (GLsizei_, GLsizei_, GLenum_, GLenum_, const void*))
GLF(void, glPixelZoom, (GLfloat_, GLfloat_))
GLF(void, glBlendFunc, (GLenum_, GLenum_))
GLF(HGLRC, myWglCurrent, (void))
typedef PROC (WINAPI* wglGetProcAddress_t)(LPCSTR);
static wglGetProcAddress_t wglGetProcAddress_ = nullptr;
// розширення
GLF(void, glActiveTexture, (GLenum_))
GLF(GLuint_, glCreateShader, (GLenum_))
GLF(void, glShaderSource, (GLuint_, GLsizei_, const GLchar_* const*, const GLint_*))
GLF(void, glCompileShader, (GLuint_))
GLF(void, glGetShaderiv, (GLuint_, GLenum_, GLint_*))
GLF(void, glDeleteShader, (GLuint_))
GLF(GLuint_, glCreateProgram, (void))
GLF(void, glAttachShader, (GLuint_, GLuint_))
GLF(void, glLinkProgram, (GLuint_))
GLF(void, glGetProgramiv, (GLuint_, GLenum_, GLint_*))
GLF(void, glUseProgram, (GLuint_))
GLF(GLint_, glGetUniformLocation, (GLuint_, const GLchar_*))
GLF(void, glUniform1i, (GLint_, GLint_))
GLF(void, glUniform4f, (GLint_, GLfloat_, GLfloat_, GLfloat_, GLfloat_))
GLF(void, glGenVertexArrays, (GLsizei_, GLuint_*))
GLF(void, glBindVertexArray, (GLuint_))
GLF(void, glBindFramebuffer, (GLenum_, GLuint_))
GLF(void, glBindBuffer, (GLenum_, GLuint_))
GLF(void, glBindSampler, (GLuint_, GLuint_))
GLF(void, glBlendFuncSeparate, (GLenum_, GLenum_, GLenum_, GLenum_))
GLF(void, glBlendEquationSeparate, (GLenum_, GLenum_))

static bool glLoaded = false, glLoadOk = false;
static void* GlExt(const char* n)
{
    if (!wglGetProcAddress_) return nullptr;
    PROC p = wglGetProcAddress_(n);
    uintptr_t v = (uintptr_t)p;
    return (v == 0 || v == 1 || v == 2 || v == 3 || v == (uintptr_t)-1) ? nullptr : (void*)p;
}
static bool GlLoad()
{
    if (glLoaded) return glLoadOk;
    HMODULE m = PinMod(L"opengl32.dll");
    if (!m) return false;
    glLoaded = true;
#define GLB(n) n = (n##_t)(void*)GetProcAddress(m, #n)
    GLB(glGetString); GLB(glGetIntegerv); GLB(glGetBooleanv); GLB(glEnable); GLB(glDisable); GLB(glIsEnabled); GLB(glBindTexture);
    GLB(glGenTextures); GLB(glTexImage2D); GLB(glTexParameteri); GLB(glViewport); GLB(glDrawArrays); GLB(glPixelStorei);
    GLB(glColorMask); GLB(glPolygonMode); GLB(glDrawBuffer); GLB(glPushAttrib); GLB(glPopAttrib); GLB(glPushClientAttrib);
    GLB(glPopClientAttrib); GLB(glMatrixMode); GLB(glPushMatrix); GLB(glPopMatrix); GLB(glLoadIdentity); GLB(glOrtho);
    GLB(glRasterPos2i); GLB(glDrawPixels); GLB(glPixelZoom); GLB(glBlendFunc);
    myWglCurrent = (myWglCurrent_t)(void*)GetProcAddress(m, "wglGetCurrentContext");
    wglGetProcAddress_ = (wglGetProcAddress_t)(void*)GetProcAddress(m, "wglGetProcAddress");
#undef GLB
    glLoadOk = glGetString && glGetIntegerv && glGetBooleanv && glEnable && glDisable && glIsEnabled && glBindTexture && glGenTextures &&
               glTexImage2D && glTexParameteri && glViewport && glBlendFunc && myWglCurrent && wglGetProcAddress_;
    return glLoadOk;
}
static bool GlLoadExt()
{
#define GLX(n) n = (n##_t)GlExt(#n)
    GLX(glActiveTexture); GLX(glCreateShader); GLX(glShaderSource); GLX(glCompileShader); GLX(glGetShaderiv); GLX(glDeleteShader);
    GLX(glCreateProgram); GLX(glAttachShader); GLX(glLinkProgram); GLX(glGetProgramiv); GLX(glUseProgram); GLX(glGetUniformLocation);
    GLX(glUniform1i); GLX(glUniform4f); GLX(glGenVertexArrays); GLX(glBindVertexArray); GLX(glBindFramebuffer); GLX(glBindBuffer);
    GLX(glBindSampler); GLX(glBlendFuncSeparate); GLX(glBlendEquationSeparate);
#undef GLX
    return glActiveTexture && glCreateShader && glShaderSource && glCompileShader && glGetShaderiv && glCreateProgram && glAttachShader &&
           glLinkProgram && glGetProgramiv && glUseProgram && glGetUniformLocation && glUniform1i && glUniform4f &&
           glGenVertexArrays && glBindVertexArray && glBindFramebuffer;
}

static HGLRC glCtx = nullptr;
static GLuint_ glProg = 0, glVao = 0, glTex = 0;
static GLint_ glURect = -1;
static bool glModern = false, glBroken = false;
static const void* glUploaded = nullptr;
static int glUpW = 0, glUpH = 0;
static uint32_t glUpGen = 0;

static GLuint_ GlCompile(GLenum_ type, const char* src)
{
    GLuint_ s = glCreateShader(type);
    if (!s) return 0;
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint_ ok = 0;
    glGetShaderiv(s, 0x8B81 /*COMPILE_STATUS*/, &ok);
    if (!ok) { glDeleteShader(s); return 0; }
    return s;
}

static bool GlModernInit(int major, int minor)
{
    const char* ver = (major > 3 || (major == 3 && minor >= 2)) ? "#version 150\n" : "#version 130\n";
    char vs[512], fs[512];
    wsprintfA(vs, "%suniform vec4 uRect;\nout vec2 vUv;\nvoid main(){ vec2 p = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1)); vUv = vec2(p.x, 1.0 - p.y); gl_Position = vec4(mix(uRect.xy, uRect.zw, p), 0.0, 1.0); }\n", ver);
    wsprintfA(fs, "%suniform sampler2D uTex;\nin vec2 vUv;\nout vec4 o;\nvoid main(){ o = texture(uTex, vUv); }\n", ver);
    GLuint_ v = GlCompile(0x8B31, vs), f = GlCompile(0x8B30, fs);
    if (!v || !f) { if (v) glDeleteShader(v); if (f) glDeleteShader(f); return false; }
    glProg = glCreateProgram();
    glAttachShader(glProg, v);
    glAttachShader(glProg, f);
    glLinkProgram(glProg);
    glDeleteShader(v);
    glDeleteShader(f);
    GLint_ ok = 0;
    glGetProgramiv(glProg, 0x8B82 /*LINK_STATUS*/, &ok);
    if (!ok) return false;
    glURect = glGetUniformLocation(glProg, "uRect");
    GLint_ uTex = glGetUniformLocation(glProg, "uTex");
    GLint_ prevProg = 0;
    glGetIntegerv(0x8B8D, &prevProg);
    glUseProgram(glProg);
    if (uTex >= 0) glUniform1i(uTex, 0);
    glUseProgram((GLuint_)prevProg);
    glGenVertexArrays(1, &glVao);
    glGenTextures(1, &glTex);
    return glVao != 0 && glTex != 0 && glURect >= 0;
}

static void OsdDrawGl(HDC dc)
{
    if (!GlLoad()) return;
    HGLRC ctx = myWglCurrent();
    if (!ctx) return;
    HWND wnd = WindowFromDC(dc);
    RECT rc;
    if (!wnd || !GetClientRect(wnd, &rc)) return;
    int W = rc.right, H = rc.bottom;
    if (W < 64 || H < 64) return;
    sh->osdFrameW = W; sh->osdFrameH = H;
    EnterCriticalSection(&ocs);
    if (ctx != glCtx)
    {
        // новий контекст: що було створено в попередньому, залишаємо (видалити можна лише в тому контексті)
        glCtx = ctx; glProg = glVao = glTex = 0; glBroken = false; glModern = false; glUploaded = nullptr;
        const char* v = (const char*)glGetString(0x1F02 /*VERSION*/);
        int major = v && v[0] >= '0' && v[0] <= '9' ? v[0] - '0' : 0, minor = v && v[1] == '.' && v[2] >= '0' && v[2] <= '9' ? v[2] - '0' : 0;
        if (major >= 3)
        {
            if (!GlLoadExt() || !GlModernInit(major, minor)) { glBroken = true; sh->osdErr = 11; }
            else glModern = true;
        }
        else if (!(glPushAttrib && glPopAttrib && glPushClientAttrib && glPopClientAttrib && glMatrixMode && glPushMatrix && glPopMatrix &&
                   glLoadIdentity && glOrtho && glRasterPos2i && glDrawPixels && glPixelZoom)) { glBroken = true; sh->osdErr = 12; }
    }
    if (!glBroken && OsdBuild())
    {
        int w = osdW, h = osdH, x, y;
        OsdPos(W, H, w, h, &x, &y);
        if (w > W || h > H) sh->osdErr = 4;
        else if (glModern)
        {
            // ---- зберігаємо стан гри ----
            GLint_ prog = 0, vao = 0, abuf = 0, atex = 0, tex = 0, smp = 0, dfbo = 0, vp[4] = { 0, 0, 0, 0 }, pbo = 0, dbuf = 0, pm[2] = { 0x1B02, 0x1B02 };
            GLint_ ualign = 4, urow = 0, uskx = 0, usky = 0, bsr = 1, bdr = 0, bsa = 1, bda = 0, ber = 0x8006, bea = 0x8006;
            GLboolean_ cm[4] = { 1, 1, 1, 1 };
            glGetIntegerv(0x8B8D, &prog); glGetIntegerv(0x85B5, &vao); glGetIntegerv(0x8894, &abuf);
            glGetIntegerv(0x84E0, &atex);
            glActiveTexture(0x84C0);
            glGetIntegerv(0x8069, &tex);
            if (glBindSampler) glGetIntegerv(0x8919, &smp);
            glGetIntegerv(0x8CA6, &dfbo); glGetIntegerv(0x0BA2, vp); glGetIntegerv(0x88EC, &pbo); glGetIntegerv(0x0C01, &dbuf);
            glGetIntegerv(0x0B40, pm); glGetBooleanv(0x0C23, cm);
            glGetIntegerv(0x80C9, &bsr); glGetIntegerv(0x80C8, &bdr); glGetIntegerv(0x80CB, &bsa); glGetIntegerv(0x80CA, &bda);
            if (glBlendEquationSeparate) { glGetIntegerv(0x8009, &ber); glGetIntegerv(0x883D, &bea); }
            glGetIntegerv(0x0CF5, &ualign); glGetIntegerv(0x0CF2, &urow); glGetIntegerv(0x0CF4, &uskx); glGetIntegerv(0x0CF3, &usky);
            GLboolean_ eBlend = glIsEnabled(0x0BE2), eDepth = glIsEnabled(0x0B71), eCull = glIsEnabled(0x0B44), eScis = glIsEnabled(0x0C11),
                       eSten = glIsEnabled(0x0B90), eSrgb = glIsEnabled(0x8DB9);
            // ---- наш стан ----
            glBindFramebuffer(0x8CA9 /*DRAW_FRAMEBUFFER*/, 0);
            if (glDrawBuffer) glDrawBuffer(0x0405 /*BACK*/);
            glViewport(0, 0, W, H);
            glEnable(0x0BE2); glDisable(0x0B71); glDisable(0x0B44); glDisable(0x0C11); glDisable(0x0B90); glDisable(0x8DB9);
            if (glBlendEquationSeparate) glBlendEquationSeparate(0x8006, 0x8006);
            if (glBlendFuncSeparate) glBlendFuncSeparate(1 /*ONE*/, 0x0303 /*ONE_MINUS_SRC_ALPHA*/, 1, 0x0303); else glBlendFunc(1, 0x0303);
            glColorMask(1, 1, 1, 1);
            if (glPolygonMode) glPolygonMode(0x0408, 0x1B02);
            if (glBindBuffer) glBindBuffer(0x88EB /*PIXEL_UNPACK_BUFFER*/, 0);
            glPixelStorei(0x0CF5, 1); glPixelStorei(0x0CF2, 0); glPixelStorei(0x0CF4, 0); glPixelStorei(0x0CF3, 0);
            glBindTexture(0x0DE1, glTex);
            if (glUploaded != osdBits || glUpW != osdW || glUpH != osdH || glUpGen != osdGen)
            {
                glTexParameteri(0x0DE1, 0x2801, 0x2600); glTexParameteri(0x0DE1, 0x2800, 0x2600);
                glTexParameteri(0x0DE1, 0x2802, 0x812F); glTexParameteri(0x0DE1, 0x2803, 0x812F);
                glTexImage2D(0x0DE1, 0, 0x8058 /*RGBA8*/, osdW, osdH, 0, 0x80E1 /*BGRA*/, 0x1401, osdBits);
                glUploaded = osdBits; glUpW = osdW; glUpH = osdH; glUpGen = osdGen;
            }
            if (glBindSampler) glBindSampler(0, 0);
            glUseProgram(glProg);
            glUniform4f(glURect, -1.0f + 2.0f * x / W, 1.0f - 2.0f * (y + h) / H, -1.0f + 2.0f * (x + w) / W, 1.0f - 2.0f * y / H);
            glBindVertexArray(glVao);
            glDrawArrays(0x0005 /*TRIANGLE_STRIP*/, 0, 4);
            // ---- повертаємо стан гри ----
            glBindVertexArray((GLuint_)vao);
            glUseProgram((GLuint_)prog);
            if (glBindSampler) glBindSampler(0, (GLuint_)smp);
            glBindTexture(0x0DE1, (GLuint_)tex);
            glActiveTexture((GLenum_)atex);
            if (glBindBuffer) { glBindBuffer(0x8892, (GLuint_)abuf); glBindBuffer(0x88EB, (GLuint_)pbo); }
            glPixelStorei(0x0CF5, ualign); glPixelStorei(0x0CF2, urow); glPixelStorei(0x0CF4, uskx); glPixelStorei(0x0CF3, usky);
            if (glPolygonMode) glPolygonMode(0x0408, (GLenum_)pm[0]);
            glColorMask(cm[0], cm[1], cm[2], cm[3]);
            if (glBlendEquationSeparate) glBlendEquationSeparate((GLenum_)ber, (GLenum_)bea);
            if (glBlendFuncSeparate) glBlendFuncSeparate((GLenum_)bsr, (GLenum_)bdr, (GLenum_)bsa, (GLenum_)bda); else glBlendFunc((GLenum_)bsr, (GLenum_)bdr);
            if (eBlend) glEnable(0x0BE2); else glDisable(0x0BE2);
            if (eDepth) glEnable(0x0B71);
            if (eCull) glEnable(0x0B44);
            if (eScis) glEnable(0x0C11);
            if (eSten) glEnable(0x0B90);
            if (eSrgb) glEnable(0x8DB9);
            glViewport(vp[0], vp[1], vp[2], vp[3]);
            if (glDrawBuffer) glDrawBuffer((GLenum_)dbuf);
            glBindFramebuffer(0x8CA9, (GLuint_)dfbo);
            sh->osdErr = 0; sh->osdApi = 3;
            InterlockedIncrement(&sh->osdDrawn);
        }
        else
        {
            // старий контекст: фіксований конвеєр
            GLint_ prog = 0;
            PROC up = wglGetProcAddress_ ? wglGetProcAddress_("glUseProgram") : nullptr;
            typedef void (WINAPI* UseProg_t)(GLuint_);
            UseProg_t useProg = (UseProg_t)(void*)up;
            if ((uintptr_t)up < 4 || (uintptr_t)up == (uintptr_t)-1) useProg = nullptr;
            if (useProg) { glGetIntegerv(0x8B8D, &prog); useProg(0); }
            glPushAttrib(0x000FFFFF);
            glPushClientAttrib(0xFFFFFFFF);
            glMatrixMode(0x1701); glPushMatrix(); glLoadIdentity(); glOrtho(0, W, 0, H, -1, 1);
            glMatrixMode(0x1700); glPushMatrix(); glLoadIdentity();
            glDisable(0x0DE1); glDisable(0x0B50); glDisable(0x0B60); glDisable(0x0B71); glEnable(0x0BE2); glDisable(0x0B90);
            glBlendFunc(1, 0x0303);
            glDisable(0x0C11); glDisable(0x0BC0); glDisable(0x0B44);
            glViewport(0, 0, W, H);
            glPixelStorei(0x0CF5, 1); glPixelStorei(0x0CF2, osdW);
            glPixelZoom(1.0f, -1.0f);
            glRasterPos2i(x, H - y);
            glDrawPixels(w, h, 0x80E1, 0x1401, osdBits);
            glMatrixMode(0x1701); glPopMatrix();
            glMatrixMode(0x1700); glPopMatrix();
            glPopClientAttrib();
            glPopAttrib();
            if (useProg) useProg((GLuint_)prog);
            sh->osdErr = 0; sh->osdApi = 3;
            InterlockedIncrement(&sh->osdDrawn);
        }
    }
    LeaveCriticalSection(&ocs);
}

// ---------------- перехоплювачі ----------------
typedef HRESULT (STDMETHODCALLTYPE* Present_t)(IDXGISwapChain*, UINT, UINT);
typedef HRESULT (STDMETHODCALLTYPE* Present1_t)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
typedef HRESULT (STDMETHODCALLTYPE* D9Present_t)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
typedef HRESULT (STDMETHODCALLTYPE* D9PresentEx_t)(IDirect3DDevice9Ex*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
typedef HRESULT (STDMETHODCALLTYPE* D9ScPresent_t)(IDirect3DSwapChain9*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
typedef BOOL (WINAPI* SwapBuffers_t)(HDC);

static Present_t oPresent = nullptr;
static Present1_t oPresent1 = nullptr;
static D9Present_t oD9Present = nullptr, oD9PresentB = nullptr;
static D9PresentEx_t oD9PresentEx = nullptr;
static D9ScPresent_t oD9ScPresent = nullptr;
static SwapBuffers_t oSwap = nullptr, oSwapGdi = nullptr;

// Який пристрій у ланцюжка (11 чи 12) і чи справжній повноекран. Дивимось не щокадру: перший кадр і далі раз на 30.
static void NoteDxgi(IDXGISwapChain* sc)
{
    static IDXGISwapChain* lastSc = nullptr;
    static unsigned cnt = 0;
    if (sc == lastSc && (++cnt % 30) != 0) return;
    if (sc != lastSc)
    {
        lastSc = sc; cnt = 0;
        IUnknown* d = nullptr;
        LONG v = 10;
        if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D12Device), (void**)&d)) && d) { v = 12; d->Release(); }
        else if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D11Device), (void**)&d)) && d) { v = 11; d->Release(); }
        sh->dxgiVer = v;
    }
    BOOL fs = FALSE;
    IDXGIOutput* out = nullptr;
    if (oGetFS && SUCCEEDED(oGetFS(sc, &fs, &out))) sh->osdExcl = fs ? 1 : 0;
    if (out) out->Release();
}

static void NoteD9(IDirect3DSwapChain9* s9)
{
    static unsigned cnt = 0;
    if ((cnt++ % 30) != 0) return;
    D3DPRESENT_PARAMETERS pp = {};
    if (SUCCEEDED(s9->GetPresentParameters(&pp))) sh->osdExcl = pp.Windowed ? 0 : 1;
}
static void NoteD9Dev(IDirect3DDevice9* d)
{
    static unsigned cnt = 0;
    if ((cnt++ % 30) != 0) return;
    IDirect3DSwapChain9* s9 = nullptr;
    if (SUCCEEDED(d->GetSwapChain(0, &s9)) && s9)
    {
        D3DPRESENT_PARAMETERS pp = {};
        if (SUCCEEDED(s9->GetPresentParameters(&pp))) sh->osdExcl = pp.Windowed ? 0 : 1;
        s9->Release();
    }
}

static void VkInternalFrame(void* sc)
{
    if (layerActive) return;                    // кадри вже враховує шар Vulkan
    vkLastTick = GetTickCount() | 1;
    InterlockedIncrement(&sh->vkFrames);
    OnFrame(sc, API_VK);
}

static HRESULT STDMETHODCALLTYPE hkPresent(IDXGISwapChain* sc, UINT sync, UINT flags)
{
    Outer o;
    if (o.on && !(flags & DXGI_PRESENT_TEST) && CallerIsVk(__builtin_return_address(0))) { VkInternalFrame(sc); return oPresent(sc, sync, flags); }
    if (o.on && !(flags & DXGI_PRESENT_TEST) && !VkRecent()) { BdrLate(sc); OnFrame(sc, API_DXGI); InterlockedIncrement(&sh->dxFrames); NoteDxgi(sc); if (sh->osdOn) OsdDrawDxgi(sc); }
    return oPresent(sc, sync, flags);
}

static HRESULT STDMETHODCALLTYPE hkPresent1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p)
{
    Outer o;
    if (o.on && !(flags & DXGI_PRESENT_TEST) && CallerIsVk(__builtin_return_address(0))) { VkInternalFrame(sc); return oPresent1(sc, sync, flags, p); }
    if (o.on && !(flags & DXGI_PRESENT_TEST) && !VkRecent()) { BdrLate((IDXGISwapChain*)sc); OnFrame(sc, API_DXGI); InterlockedIncrement(&sh->dxFrames); NoteDxgi((IDXGISwapChain*)sc); if (sh->osdOn) OsdDrawDxgi((IDXGISwapChain*)sc); }
    return oPresent1(sc, sync, flags, p);
}

static void D9PresentFail(HRESULT hr)
{
    static volatile LONG n = 0;
    if (FAILED(hr) && hr != (HRESULT)0x8876086A /*WASSTILLDRAWING*/ && InterlockedIncrement(&n) <= 8) DllLog("Present повернув 0x%08lX", (DWORD)hr);
}

static HRESULT STDMETHODCALLTYPE hkD9Present(IDirect3DDevice9* d, const RECT* a, const RECT* b, HWND w, const RGNDATA* r)
{
    Outer o;
    if (o.on) { OnFrame(d, API_D3D9); NoteD9Dev(d); if (sh->osdOn) OsdDraw9(d); }
    HRESULT hr = oD9Present(d, a, b, w, r);
    D9PresentFail(hr);
    return hr;
}

static HRESULT STDMETHODCALLTYPE hkD9PresentB(IDirect3DDevice9* d, const RECT* a, const RECT* b, HWND w, const RGNDATA* r)
{
    Outer o;
    if (o.on) { OnFrame(d, API_D3D9); NoteD9Dev(d); if (sh->osdOn) OsdDraw9(d); }
    HRESULT hr = oD9PresentB(d, a, b, w, r);
    D9PresentFail(hr);
    return hr;
}

static HRESULT STDMETHODCALLTYPE hkD9PresentEx(IDirect3DDevice9Ex* d, const RECT* a, const RECT* b, HWND w, const RGNDATA* r, DWORD f)
{
    Outer o;
    if (o.on && !(f & D3DPRESENT_DONOTWAIT)) { OnFrame(d, API_D3D9); NoteD9Dev(d); if (sh->osdOn) OsdDraw9(d); }
    return oD9PresentEx(d, a, b, w, r, f);
}

static HRESULT STDMETHODCALLTYPE hkD9ScPresent(IDirect3DSwapChain9* s, const RECT* a, const RECT* b, HWND w, const RGNDATA* r, DWORD f)
{
    Outer o;
    if (o.on)
    {
        OnFrame(s, API_D3D9);
        NoteD9(s);
        if (sh->osdOn)
        {
            IDirect3DDevice9* dv = nullptr;
            if (SUCCEEDED(s->GetDevice(&dv)) && dv) { OsdDraw9(dv); dv->Release(); }
        }
    }
    return oD9ScPresent(s, a, b, w, r, f);
}

static BOOL WINAPI hkSwap(HDC dc)
{
    Outer o;
    if (o.on) { OnFrame(WindowFromDC(dc), API_GL); if (sh->osdOn) OsdDrawGl(dc); }
    return oSwap(dc);
}

// SwapBuffers з gdi32 (деякі ігри викликають її, а не wglSwapBuffers)
static BOOL WINAPI hkSwapGdi(HDC dc)
{
    Outer o;
    if (o.on) { OnFrame(WindowFromDC(dc), API_GL); if (sh->osdOn) OsdDrawGl(dc); }
    return oSwapGdi(dc);
}

static bool Hook(void* target, void* detour, void** orig)
{
    if (!target) return false;
    MH_STATUS s = MH_CreateHook(target, detour, orig);
    if (s != MH_OK) return false;
    return MH_EnableHook(target) == MH_OK;
}

// ---------------- допоміжне вікно для «пустушок» ----------------
static HWND DummyWindow()
{
    static HWND w = nullptr;
    if (w) return w;
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"NeoBarFpsDummy";
    RegisterClassExW(&wc);
    w = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
    return w;
}

static void** VT(void* obj) { return *(void***)obj; }

// ======================================================================
//  DirectDraw (старі 2D-ігри): підрахунок кадрів і показники прямо в кадрі гри
//  Кадром вважається показ первинної поверхні: Flip, Blt/BltFast у неї або Unlock після Lock.
//  Показники малюються через GDI (GetDC поверхні) одразу після показу кадру.
// ======================================================================
static const GUID G_DD7   = { 0x15e65ec0, 0x3b9c, 0x11d2, { 0xb9, 0x2f, 0x00, 0x60, 0x97, 0x97, 0xea, 0x5b } };
static const GUID G_DDS7  = { 0x06675a80, 0x3b9b, 0x11d2, { 0xb9, 0x2f, 0x00, 0x60, 0x97, 0x97, 0xea, 0x5b } };
static const GUID G_DDS4  = { 0x0b2b8630, 0xad35, 0x11d0, { 0x8e, 0xa6, 0x00, 0x60, 0x97, 0x97, 0xea, 0x5b } };
static const GUID G_DDS3  = { 0xda044e00, 0x69b2, 0x11d0, { 0xa1, 0xd5, 0x00, 0xaa, 0x00, 0xb8, 0xdf, 0xbb } };
static const GUID G_DDS2  = { 0x57805885, 0x6eec, 0x11cf, { 0x94, 0x41, 0xa8, 0x23, 0x03, 0xc1, 0x0e, 0x27 } };
static const GUID G_DDS1  = { 0x6c14db81, 0xa733, 0x11ce, { 0xa5, 0x21, 0x00, 0x20, 0xaf, 0x0b, 0xe5, 0x60 } };

typedef HRESULT (STDMETHODCALLTYPE* DdBlt_t)(void*, LPRECT, void*, LPRECT, DWORD, void*);
typedef HRESULT (STDMETHODCALLTYPE* DdBltFast_t)(void*, DWORD, DWORD, void*, LPRECT, DWORD);
typedef HRESULT (STDMETHODCALLTYPE* DdFlip_t)(void*, void*, DWORD);
typedef HRESULT (STDMETHODCALLTYPE* DdUnlock_t)(void*, void*);
typedef HRESULT (STDMETHODCALLTYPE* DdGetCaps_t)(void*, void*);
typedef HRESULT (STDMETHODCALLTYPE* DdGetDC_t)(void*, HDC*);
typedef HRESULT (STDMETHODCALLTYPE* DdRelDC_t)(void*, HDC);

static void* oDdBlt[5], *oDdBltFast[5], *oDdFlip[5], *oDdUnlock[5];
static bool ddHooked = false;
static void* const DD_CHAIN = (void*)(uintptr_t)0xDD;

// чи це первинна поверхня (кеш на кілька секунд: поверхні створюються й знищуються)
struct DdCache { void* s; bool prim; };
static DdCache ddc[8];
static unsigned ddcNext = 0;
static DWORD ddcAt = 0;
static DWORD ddSeen[4];            // коли востаннє бачили спосіб показу кадру (1 - Flip, 2 - Blt, 3 - Unlock)

static bool DdIsPrimary(void* s)
{
    DWORD now = GetTickCount();
    if (now - ddcAt > 3000) { memset(ddc, 0, sizeof(ddc)); ddcAt = now; }
    for (int i = 0; i < 8; i++) if (ddc[i].s == s) return ddc[i].prim;
    DWORD caps[4] = { 0, 0, 0, 0 };
    bool prim = false;
    if (SUCCEEDED(((DdGetCaps_t)VT(s)[14])(s, caps))) prim = (caps[0] & 0x200 /*DDSCAPS_PRIMARYSURFACE*/) != 0;
    ddc[ddcNext++ % 8].s = s;
    ddc[(ddcNext - 1) % 8].prim = prim;
    return prim;
}

// запасний лічильник FPS для підпису, якщо NeoBar не передав текст

static void OsdDraw(void* s)
{
    EnterCriticalSection(&ocs);
    sh->osdFrameW = GetSystemMetrics(SM_CXSCREEN); sh->osdFrameH = GetSystemMetrics(SM_CYSCREEN);     // точніший розмір буде з області малювання
    HDC dc = nullptr;
    HRESULT dhr = S_OK;
    HCURSOR cur0 = GetCursor();     // GetDC первинної поверхні в повноекрані може показати системний курсор: гра його ховала - ховаємо знову
    bool built = OsdBuild();
    if (built) dhr = ((DdGetDC_t)VT(s)[17])(s, &dc);
    if (built && (FAILED(dhr) || !dc))
    {
        // не вдалося отримати контекст поверхні: запам'ятовуємо код, щоб його видно було в налаштуваннях
        InterlockedIncrement(&sh->ddFail);
        sh->ddHr = (LONG)dhr;
        sh->osdErr = FAILED(dhr) ? (LONG)dhr : 5;
    }
    else if (built)
    {
        RECT box = {};
        GetClipBox(dc, &box);
        sh->ddBox = ((box.right - box.left) << 16) | ((box.bottom - box.top) & 0xFFFF);
        int x, y;
        OsdPos(box.right - box.left, box.bottom - box.top, osdW, osdH, &x, &y);
        // суцільний фон затирає попередній підпис: гра не перемальовує цю ділянку щокадру (тому прозорості тут нема)
        if (OsdFlat())
        {
            HGDIOBJ old = SelectObject(osdDc, flBm);
            BitBlt(dc, box.left + x, box.top + y, osdW, osdH, osdDc, 0, 0, SRCCOPY);
            SelectObject(osdDc, old);
        }
        ((DdRelDC_t)VT(s)[26])(s, dc);
        if (!cur0) SetCursor(nullptr);
        sh->osdErr = 0; sh->osdApi = 4;
        InterlockedIncrement(&sh->osdDrawn);
    }
    LeaveCriticalSection(&ocs);
}

// kind: 1 - Flip, 2 - Blt / BltFast, 3 - Unlock
static void DdFrame(void* s, int kind)
{
    if (!sh || !DdIsPrimary(s)) return;
    DWORD now = GetTickCount();
    ddSeen[kind] = now;
    // кілька способів показу в одній грі (наприклад, Blt і Unlock): рахуємо лише найточніший з тих, що йдуть
    for (int k = 1; k < kind; k++) if (now - ddSeen[k] < 400) return;
    sh->ddKind = kind;
    InterlockedIncrement(&sh->ddCalls);
    OnFrame(DD_CHAIN, API_DDRAW);
    if (sh->osdOn) OsdDraw(s);
}

template <int V> static HRESULT STDMETHODCALLTYPE hkDdBlt(void* s, LPRECT d, void* src, LPRECT sr, DWORD f, void* fx)
{
    Outer o;
    HRESULT hr = ((DdBlt_t)oDdBlt[V])(s, d, src, sr, f, fx);
    if (o.on && SUCCEEDED(hr)) DdFrame(s, 2);
    return hr;
}
template <int V> static HRESULT STDMETHODCALLTYPE hkDdBltFast(void* s, DWORD x, DWORD y, void* src, LPRECT sr, DWORD f)
{
    Outer o;
    HRESULT hr = ((DdBltFast_t)oDdBltFast[V])(s, x, y, src, sr, f);
    if (o.on && SUCCEEDED(hr)) DdFrame(s, 2);
    return hr;
}
template <int V> static HRESULT STDMETHODCALLTYPE hkDdFlip(void* s, void* over, DWORD f)
{
    Outer o;
    HRESULT hr = ((DdFlip_t)oDdFlip[V])(s, over, f);
    if (o.on && SUCCEEDED(hr)) DdFrame(s, 1);
    return hr;
}
template <int V> static HRESULT STDMETHODCALLTYPE hkDdUnlock(void* s, void* r)
{
    Outer o;
    HRESULT hr = ((DdUnlock_t)oDdUnlock[V])(s, r);
    if (o.on && SUCCEEDED(hr)) DdFrame(s, 3);
    return hr;
}

template <int V> static bool DdHookVersion(void* surf)
{
    void** vt = VT(surf);
    bool any = false;
    any = Hook(vt[5], (void*)hkDdBlt<V>, &oDdBlt[V]) || any;
    any = Hook(vt[7], (void*)hkDdBltFast<V>, &oDdBltFast[V]) || any;
    any = Hook(vt[11], (void*)hkDdFlip<V>, &oDdFlip[V]) || any;
    any = Hook(vt[32], (void*)hkDdUnlock<V>, &oDdUnlock[V]) || any;
    return any;
}

static void TryDdraw()
{
    if (ddHooked || sh->hooks & HK_DDRAW) return;
    HMODULE dd = PinMod(L"ddraw.dll");
    if (!dd) return;
    // ddraw.dll нерідко лише залежність Direct3D-гри (GTA SA та інші): тимчасовий DirectDraw створюємо, лише коли жодного іншого API не видно
    if (sh->hooks & (HK_D3D9 | HK_D3D9EX | HK_D3D8 | HK_DXGI | HK_DXGI1 | HK_GL | HK_VK)) return;
    if (GetModuleHandleW(L"d3d9.dll") || GetModuleHandleW(L"d3d11.dll") || GetModuleHandleW(L"d3d10core.dll") || GetModuleHandleW(L"d3d12.dll")) return;
    { static int ddTries = 0; if (++ddTries < 10) return; }      // дати грі час завантажити свою графіку
    // обгортка cnc-ddraw (експортує DDIsWindowed): це не справжній DirectDraw, гра йде через Direct3D 9 / OpenGL - їх перехоплюють свої шляхи,
    // а тимчасовий об'єкт DirectDraw у ній створювати не можна (він чіпає спільний стан обгортки)
    if (GetProcAddress(dd, "DDIsWindowed")) { ddHooked = true; return; }
    // ddraw.dll підтягує й Direct3D 8: тоді тимчасовий об'єкт DirectDraw створювати не можна (він чіпає стан гри)
    if (GetModuleHandleW(L"d3d8.dll")) return;
    typedef HRESULT (WINAPI* CreateEx_t)(GUID*, void**, const GUID&, IUnknown*);
    CreateEx_t create = (CreateEx_t)(void*)GetProcAddress(dd, "DirectDrawCreateEx");
    HWND w = DummyWindow();
    if (!create || !w) return;
    IDirectDraw7* d7 = nullptr;
    if (FAILED(create(nullptr, (void**)&d7, G_DD7, nullptr)) || !d7) return;
    LONG h = 0;
    {
        // Тимчасовий об'єкт не повинен чіпати стан гри (рівень співпраці DirectDraw спільний на процес; у повноекранній грі
        // його зміна з'являлась як зайвий курсор Windows). Спершу пробуємо взагалі без SetCooperativeLevel, потім з NULL-вікном.
        DDSURFACEDESC2 sd = {};
        sd.dwSize = sizeof(sd);
        sd.dwFlags = DDSD_CAPS | DDSD_WIDTH | DDSD_HEIGHT;
        sd.dwWidth = 16;
        sd.dwHeight = 16;
        sd.ddsCaps.dwCaps = DDSCAPS_OFFSCREENPLAIN | DDSCAPS_SYSTEMMEMORY;
        IDirectDrawSurface7* s7 = nullptr;
        if (FAILED(d7->CreateSurface(&sd, &s7, nullptr)) || !s7)
        {
            s7 = nullptr;
            if (SUCCEEDED(d7->SetCooperativeLevel(nullptr, DDSCL_NORMAL | DDSCL_NOWINDOWCHANGES)) ||
                SUCCEEDED(d7->SetCooperativeLevel(w, DDSCL_NORMAL)))
                if (FAILED(d7->CreateSurface(&sd, &s7, nullptr))) s7 = nullptr;
        }
        if (s7)
        {
            // кожна версія інтерфейсу поверхні має власну таблицю методів
            bool a7 = DdHookVersion<4>(s7);
            void* q = nullptr;
            bool a4 = false, a3 = false, a2 = false, a1 = false;
            if (SUCCEEDED(s7->QueryInterface(G_DDS4, &q)) && q) { a4 = DdHookVersion<3>(q); ((IUnknown*)q)->Release(); }
            q = nullptr;
            if (SUCCEEDED(s7->QueryInterface(G_DDS3, &q)) && q) { a3 = DdHookVersion<2>(q); ((IUnknown*)q)->Release(); }
            q = nullptr;
            if (SUCCEEDED(s7->QueryInterface(G_DDS2, &q)) && q) { a2 = DdHookVersion<1>(q); ((IUnknown*)q)->Release(); }
            q = nullptr;
            if (SUCCEEDED(s7->QueryInterface(G_DDS1, &q)) && q) { a1 = DdHookVersion<0>(q); ((IUnknown*)q)->Release(); }
            if (a7 || a4 || a3 || a2 || a1) h |= HK_DDRAW;
            s7->Release();
        }
    }
    d7->Release();
    if (h) { ddHooked = true; InterlockedOr(&sh->hooks, h); }
}

// DXGI: адреси Present (8) і Present1 (22) з тимчасового ланцюжка Direct3D 11
static CRITICAL_SECTION hcs;           // перехоплення з потоку гри (створення пристрою) і з потоку Init не мають перетинатися

// записати, що гра обрала при створенні ланцюжка (API, режим буферів)
static void DxgiNoteCreate(IUnknown* dev, UINT effect, UINT flags, UINT fmt, BOOL windowed, UINT bufs, void* ra)
{
    if (!sh) return;
    sh->scEffect = (LONG)effect; sh->scFlags = (LONG)flags; sh->scFmt = (LONG)fmt; sh->scWindowed = windowed ? 1 : 0; sh->scBufs = (LONG)bufs;
    if (CallerIsVk(ra)) return;            // драйвер Vulkan створює свій ланцюжок DXGI всередині - API гри не змінюємо
    if (!dev) return;
    IUnknown* q = nullptr;
    LONG v = 10;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D12CommandQueue), (void**)&q)) && q) { v = 12; q->Release(); }
    else if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D11Device), (void**)&q)) && q) { v = 11; q->Release(); }
    sh->createApi = v;
    sh->dxgiVer = v;
}

// методи ланцюжка (один раз): Present, Present1 і стан повноекрану
static bool dxScHooked = false;
static void DxgiHookSc(IDXGISwapChain* sc)
{
    if (!sc || !sh) return;
    EnterCriticalSection(&hcs);
    if (dxScHooked) { LeaveCriticalSection(&hcs); return; }
    dxScHooked = true;
    void* pPresent = VT(sc)[8];
    void* pPresent1 = nullptr;
    IDXGISwapChain1* sc1 = nullptr;
    if (SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&sc1)) && sc1)
    {
        pPresent1 = VT(sc1)[22];
        sc1->Release();
    }
    void* pSetFS = VT(sc)[10];
    void* pGetFS = VT(sc)[11];
    void* pGetDesc = VT(sc)[12];
    void* pResizeTarget = VT(sc)[14];
    LONG h = 0;
    if (Hook(pPresent, (void*)hkPresent, (void**)&oPresent)) h |= HK_DXGI;
    if (pPresent1 && pPresent1 != pPresent && Hook(pPresent1, (void*)hkPresent1, (void**)&oPresent1)) h |= HK_DXGI1;
    InterlockedOr(&sh->hooks, h);
    // порядок важливий: оригінали GetFS/GetDesc потрібні іншим перехоплювачам, тож ставимо їх першими
    bool b = Hook(pGetFS, (void*)hkGetFS, (void**)&oGetFS);
    b = Hook(pGetDesc, (void*)hkGetDesc, (void**)&oGetDesc) && b;
    b = Hook(pSetFS, (void*)hkSetFS, (void**)&oSetFS) && b;
    Hook(pResizeTarget, (void*)hkResizeTarget, (void**)&oResizeTarget);
    if (b && sh->bdrState < 2) sh->bdrState = 1;
    LeaveCriticalSection(&hcs);
}

// DXGI: спершу стежимо за створенням ланцюжка гри (фабрика без пристрою) - так робить і Steam; тестовий пристрій - запасний шлях через кілька секунд
static bool dxFactoryDone = false, dxFactoryHooked = false;
static int dxTries = 0;
// OpenGL-хук ставимо щойно завантажено opengl32.dll (його тягнуть і драйвери в іграх на DirectX), тож сам по собі він не означає, що гра на OpenGL: гра на OpenGL - коли через нього йдуть кадри
static bool GlGame() { return (sh->hooks & HK_GL) && sh->api == API_GL && sh->frames > 0; }

static void TryDxgi()
{
    if (sh->hooks & (HK_DXGI | HK_DXGI1)) return;
    HMODULE dx = PinMod(L"dxgi.dll");
    if (!dx) return;
    if (!dxFactoryDone)
    {
        dxFactoryDone = true;
        typedef HRESULT (WINAPI* Fac_t)(REFIID, void**);
        Fac_t cf = (Fac_t)(void*)GetProcAddress(dx, "CreateDXGIFactory1");
        IDXGIFactory* fac = nullptr;
        if (cf && SUCCEEDED(cf(__uuidof(IDXGIFactory), (void**)&fac)) && fac)
        {
            bool c1 = Hook(VT(fac)[10], (void*)hkCreateSC, (void**)&oCreateSC);
            IDXGIFactory2* fac2 = nullptr;
            if (SUCCEEDED(fac->QueryInterface(__uuidof(IDXGIFactory2), (void**)&fac2)) && fac2)
            {
                Hook(VT(fac2)[15], (void*)hkCreateSCHwnd, (void**)&oCreateSCHwnd);
                fac2->Release();
            }
            fac->Release();
            dxFactoryHooked = c1;
        }
    }
    if (dxFactoryHooked && ++dxTries <= 5) return;
    // запасний шлях (ланцюжок створено ще до нас): потрібні лише ігри, що вже завантажили DirectX 10-12
    // (у грі на Direct3D 9 / 8 цей тимчасовий ланцюжок зайвий: d3d11 там підтягують оверлеї Steam чи драйвера)
    if ((sh->hooks & (HK_D3D9 | HK_D3D9EX | HK_D3D8 | HK_KMT)) || GlGame()) return;
    if (!GetModuleHandleW(L"d3d11.dll") && !GetModuleHandleW(L"d3d12.dll") && !GetModuleHandleW(L"d3d10core.dll")) return;
    HMODULE d11 = LoadLibraryW(L"d3d11.dll");
    if (!d11) return;
    typedef HRESULT (WINAPI* Create_t)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT, UINT,
                                       const DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
    Create_t create = (Create_t)(void*)GetProcAddress(d11, "D3D11CreateDeviceAndSwapChain");
    HWND w = DummyWindow();
    if (!create || !w) return;
    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 1;
    sd.BufferDesc.Width = 64;
    sd.BufferDesc.Height = 64;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = w;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    HRESULT hr = create(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, fl, 3, D3D11_SDK_VERSION, &sd, &sc, &dev, nullptr, nullptr);
    if (FAILED(hr)) hr = create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, fl, 3, D3D11_SDK_VERSION, &sd, &sc, &dev, nullptr, nullptr);
    if (FAILED(hr) || !sc) { if (dev) dev->Release(); return; }
    void* pCreateSC = nullptr;
    void* pCreateSCHwnd = nullptr;
    IDXGIFactory* fac = nullptr;
    if (SUCCEEDED(sc->GetParent(__uuidof(IDXGIFactory), (void**)&fac)) && fac)
    {
        pCreateSC = VT(fac)[10];
        IDXGIFactory2* fac2 = nullptr;
        if (SUCCEEDED(fac->QueryInterface(__uuidof(IDXGIFactory2), (void**)&fac2)) && fac2)
        {
            pCreateSCHwnd = VT(fac2)[15];
            fac2->Release();
        }
        fac->Release();
    }
    DxgiHookSc(sc);
    sc->Release();
    dev->Release();
    if (!dxFactoryHooked)
    {
        if (pCreateSC) Hook(pCreateSC, (void*)hkCreateSC, (void**)&oCreateSC);
        if (pCreateSCHwnd) Hook(pCreateSCHwnd, (void*)hkCreateSCHwnd, (void**)&oCreateSCHwnd);
    }
}

// Direct3D 9: тимчасовий пристрій (звичайний і Ex)
// методи пристрою, який створила гра (Present, Reset): викликається з обгорток створення пристрою
// перехоплення методів за таблицею методів пристрою (без викликів методів самого пристрою): Present (17) і Reset (16); для Ex ще PresentEx (121) і ResetEx (132)
static LONG D9HookVt(void** vt, bool isEx)
{
    LONG h = 0;
    if (!isEx)
    {
        if (!oD9PresentB && Hook(vt[17], (void*)hkD9PresentB, (void**)&oD9PresentB)) h |= HK_D3D9;
        if (!oD9ResetB) Hook(vt[16], (void*)hkD9ResetB, (void**)&oD9ResetB);
    }
    else
    {
        if (!oD9Present && Hook(vt[17], (void*)hkD9Present, (void**)&oD9Present)) h |= HK_D3D9;
        if (!oD9Reset) Hook(vt[16], (void*)hkD9Reset, (void**)&oD9Reset);
        if (!oD9PresentEx && Hook(vt[121], (void*)hkD9PresentEx, (void**)&oD9PresentEx)) h |= HK_D3D9EX;
        if (!oD9ResetEx) Hook(vt[132], (void*)hkD9ResetEx, (void**)&oD9ResetEx);
    }
    return h;
}

static void D9HookReal(IDirect3DDevice9* dev)
{
    if (!sh || !dev) return;
    sh->createApi = 2;
    EnterCriticalSection(&hcs);
    void** vt = VT(dev);
    IDirect3DDevice9Ex* ex = nullptr;
    bool isEx = SUCCEEDED(dev->QueryInterface(__uuidof(IDirect3DDevice9Ex), (void**)&ex)) && ex;
    if (ex) ex->Release();
    LONG h = D9HookVt(vt, isEx);
    if (isEx)
    {
        IDirect3DSwapChain9* sc = nullptr;
        if (!oD9ScPresent && SUCCEEDED(dev->GetSwapChain(0, &sc)) && sc)
        {
            if (Hook(VT(sc)[3], (void*)hkD9ScPresent, (void**)&oD9ScPresent)) h |= HK_D3D9;
            sc->Release();
        }
    }
    if (h) InterlockedOr(&sh->hooks, h);
    DllLog("D9HookReal: пристрій=%08lX isEx=%d нові хуки=0x%lX", (DWORD)(DWORD_PTR)dev, (int)isEx, (DWORD)h);
    LeaveCriticalSection(&hcs);
}

static bool HookKmt(int api);
// у процесі вже є чужий оверлей (Steam, RTSS, Discord, NVIDIA), який сам перехоплює Direct3D 9: наші пробні пристрої з ним конфліктують
// (у GTA SA з оверлеєм Steam це закінчувалось вильотом гри), тому там пробний пристрій не створюємо
static bool OtherOverlayLoaded()
{
    static const wchar_t* n[] = { L"gameoverlayrenderer.dll", L"gameoverlayrenderer64.dll", L"rtsshooks.dll", L"rtsshooks64.dll", L"discordhook.dll", L"discordhook64.dll", L"nvspcap.dll", L"nvspcap64.dll", L"owclient.dll" };
    for (size_t i = 0; i < sizeof(n) / sizeof(n[0]); i++) if (GetModuleHandleW(n[i])) return true;
    return false;
}

// Пошук пристрою Direct3D 9, який гра створила до нас, у пам'яті процесу - без жодного нового пристрою (з оверлеєм Steam новий пристрій вів до вильоту).
// Об'єкт COM починається з вказівника на таблицю методів; у d3d9.dll таблиця пристрою - 119+ вказівників на код модуля, і лише пристрій відповідає на QueryInterface(IDirect3DDevice9).
// Таблиця методів пристрою Direct3D 9 - рівно 119 записів (Ex - 133). У системних dll .rdata зливають із кодом, тому таблиця лежить усередині секції коду, а межу таблиці
// видно за вказівником RTTI, що передує наступній таблиці (структура, яка починається з нуля), тож довжину таблиці рахуємо до першого запису не на код.
static bool IsCodeEntry(DWORD_PTR e, DWORD_PTR lo, DWORD_PTR hi)
{
    if (e < lo || e + 4 > hi) return false;
    DWORD first = *(const DWORD*)e;              // усередині образу модуля: сторінки доступні
    return first != (sizeof(void*) == 4 ? 0u : 1u);
}
static int D9VtLen(DWORD_PTR v, DWORD_PTR lo, DWORD_PTR hi, DWORD_PTR tlo, DWORD_PTR thi)
{
    int n = 0;
    while (n < 200)
    {
        DWORD_PTR p = v + (DWORD_PTR)n * sizeof(void*);
        if (p + sizeof(void*) > hi) break;
        DWORD_PTR e = *(const DWORD_PTR*)p;
        if (e < tlo || e >= thi || !IsCodeEntry(e, lo, hi)) break;
        n++;
    }
    return n;
}

#ifndef _WIN64
// число N з першої команди ret N у методі (за ланцюжком jmp); -1 - метод не в d3d9.dll (чужий перехоплювач) або ret N не знайдено поблизу
static int D9RetImm(DWORD_PTR f, DWORD_PTR lo, DWORD_PTR hi)
{
    for (int hop = 0; hop < 3; hop++)
    {
        if (f < lo || f + 8 > hi) return -1;
        const BYTE* b = (const BYTE*)f;
        if (b[0] == 0xE9) { f = f + 5 + *(const INT32*)(b + 1); continue; }
        if (b[0] == 0xFF && b[1] == 0x25)
        {
            // jmp [адреса]: комірка може бути поза модулем - читаємо безпечно, без ризику помилки доступу в грі
            DWORD_PTR cell = *(const DWORD_PTR*)(b + 2), to = 0;
            SIZE_T got = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), (LPCVOID)cell, &to, sizeof(to), &got) || got != sizeof(to)) return -1;
            f = to; continue;
        }
        break;
    }
    if (f < lo || f + 8 > hi) return -1;
    DWORD_PTR lim = f + 3000 < hi ? f + 3000 : hi - 3;
    for (DWORD_PTR q = f; q < lim; q++)
    {
        const BYTE* b = (const BYTE*)q;
        if (b[0] == 0xC2 && b[2] == 0x00 && (b[1] & 3) == 0) return b[1];
    }
    return -1;
}
#endif

// повертає таблицю методів пристрою гри (isEx - таблиця Ex), знайдену за об'єктом у пам'яті процесу; пристрій не викликаємо й нового не створюємо
static void** FindD9Vtable(HMODULE d9, bool* isEx)
{
    BYTE* base = (BYTE*)d9;
    IMAGE_DOS_HEADER* dh = (IMAGE_DOS_HEADER*)base;
    if (dh->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    IMAGE_NT_HEADERS* nth = (IMAGE_NT_HEADERS*)(base + dh->e_lfanew);
    DWORD_PTR lo = (DWORD_PTR)base, hi = lo + nth->OptionalHeader.SizeOfImage, tlo = 0, thi = 0;
    IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nth);
    for (int i = 0; i < nth->FileHeader.NumberOfSections; i++)
    {
        if (!(sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
        DWORD_PTR s0 = lo + sec[i].VirtualAddress, e0 = s0 + (sec[i].Misc.VirtualSize > sec[i].SizeOfRawData ? sec[i].Misc.VirtualSize : sec[i].SizeOfRawData);
        if (!tlo || s0 < tlo) tlo = s0;
        if (e0 > thi) thi = e0;
    }
    if (!tlo) return nullptr;
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    BYTE* p = (BYTE*)si.lpMinimumApplicationAddress;
    BYTE* end = (BYTE*)si.lpMaximumApplicationAddress;
    const SIZE_T CH = 1 << 20;
    BYTE* buf = (BYTE*)VirtualAlloc(nullptr, CH, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    const int HS = 1 << 15;
    DWORD_PTR* seenTab = (DWORD_PTR*)VirtualAlloc(nullptr, HS * sizeof(DWORD_PTR), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!buf || !seenTab) { if (buf) VirtualFree(buf, 0, MEM_RELEASE); if (seenTab) VirtualFree(seenTab, 0, MEM_RELEASE); return nullptr; }
    int nTables = 0, nEval = 0, nBest = 0;
    const int MAXC = 512;
    DWORD_PTR* bestV = (DWORD_PTR*)VirtualAlloc(nullptr, MAXC * sizeof(DWORD_PTR), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!bestV) { VirtualFree(buf, 0, MEM_RELEASE); VirtualFree(seenTab, 0, MEM_RELEASE); return nullptr; }
    void** found = nullptr;
    HANDLE self = GetCurrentProcess();
    DWORD tStart = GetTickCount();
    while (p < end && !found)
    {
        if (GetTickCount() - tStart > 3000) { DllLog("пошук пристрою D3D9: перервано за часом (3 с)"); break; }
        MEMORY_BASIC_INFORMATION mi;
        if (!VirtualQuery(p, &mi, sizeof(mi))) break;
        BYTE* rb = (BYTE*)mi.BaseAddress;
        BYTE* next = rb + mi.RegionSize;
        if (mi.State == MEM_COMMIT && mi.Type == MEM_PRIVATE && (mi.Protect & PAGE_READWRITE) && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) && rb != buf && rb != (BYTE*)seenTab)
        {
            for (SIZE_T off = 0; off < mi.RegionSize && !found; off += CH)
            {
                SIZE_T want = mi.RegionSize - off < CH ? mi.RegionSize - off : CH, got = 0;
                if (!ReadProcessMemory(self, rb + off, buf, want, &got) || got < sizeof(DWORD_PTR)) continue;
                for (SIZE_T i = 0; i + sizeof(DWORD_PTR) <= got; i += sizeof(DWORD_PTR))
                {
                    DWORD_PTR v = *(DWORD_PTR*)(buf + i);
                    if (v < tlo || v >= thi || (v & (sizeof(void*) - 1))) continue;
                    unsigned h = (unsigned)((v >> 2) * 2654435761u) & (HS - 1);
                    bool seen = false;
                    for (int k = 0; k < 64; k++)
                    {
                        DWORD_PTR t = seenTab[(h + k) & (HS - 1)];
                        if (t == v) { seen = true; break; }
                        if (t == 0) { seenTab[(h + k) & (HS - 1)] = v; nTables++; break; }
                    }
                    if (seen) continue;
                    nEval++;
                    int n = D9VtLen(v, lo, hi, tlo, thi);
                    if (n >= 8 && nBest < MAXC) bestV[nBest++] = v;
                }
            }
        }
        if (next <= p) break;
        p = next;
    }
    VirtualFree(buf, 0, MEM_RELEASE);
    VirtualFree(seenTab, 0, MEM_RELEASE);
    // у d3d9.dll таблиці методів лежать упритул (RTTI немає), тому довжину таблиці дає відстань до початку наступної, яку теж утримує якийсь об'єкт.
    // Пристрій - рівно 119 записів (Ex - 133): шукаємо таблицю, від початку якої до наступного початку саме стільки.
    for (int i = 1; i < nBest; i++)
        for (int j = i; j > 0 && bestV[j] < bestV[j - 1]; j--) { DWORD_PTR t = bestV[j]; bestV[j] = bestV[j - 1]; bestV[j - 1] = t; }
#ifdef _WIN64
    // x64: параметри не чистяться командою ret, тож пізнаємо таблицю лише за розміром (119 записів інтерфейсу, Ex - 133, плюс кілька службових)
    int nCand = 0;
    for (int i = 0; i + 1 < nBest; i++)
    {
        DWORD_PTR gap = (bestV[i + 1] - bestV[i]) / sizeof(void*);
        bool ex = gap >= 133 && gap <= 138, plain = gap >= 119 && gap <= 124;
        if (!ex && !plain) continue;
        DllLog("  кандидат пристрою %p: до наступної %lu записів", (void*)bestV[i], (unsigned long)gap);
        nCand++;
        if (nCand == 1) { *isEx = ex; found = (void**)bestV[i]; }
    }
    if (nCand != 1) found = nullptr;
#else
    // x86: усі методи IDirect3DDevice9 - stdcall із this у стеку, тож кожен закінчується на ret N, де N відомий з сигнатури (QueryInterface - 0Ch, Present - 14h тощо).
    // Межу таблиці за відстанню до наступної не беремо: сусідні таблиці, яких у пам'яті немає, зливаються з нею. Таблицю пристрою пізнаємо за збігом N у перших 26 методах.
    static const BYTE want[26] = { 0x0C,0x04,0x04,0x04,0x04,0x04,0x08,0x08,0x0C,0x08,0x10,0x10,0x08,0x0C,0x0C,0x04,0x08,0x14,0x14,0x0C,0x08,0x10,0x0C,0x24,0x28,0x20 };
    int bestScore = -1, bestIdx = -1, second = -1;
    for (int i = 0; i < nBest; i++)
    {
        void** vt = (void**)bestV[i];
        int sc = 0, miss = 0;
        for (int k = 0; k < 26; k++)
        {
            int r = D9RetImm((DWORD_PTR)vt[k], lo, hi);
            if (r < 0) continue;                         // чужий перехоплювач або нічого не знайдено - не рахуємо
            if (r == want[k]) sc++; else miss++;
        }
        if (sc >= 10) DllLog("  таблиця %p: збігів ret %d, розбіжностей %d", (void*)vt, sc, miss);
        if (sc > bestScore) { second = bestScore; bestScore = sc; bestIdx = i; }
        else if (sc > second) second = sc;
    }
    if (bestIdx >= 0 && bestScore >= 14 && bestScore - second >= 4)
    {
        void** vt = (void**)bestV[bestIdx];
        // Ex: PresentEx (121) закінчується на ret 18h, ResetEx (132) - на ret 0Ch
        *isEx = D9RetImm((DWORD_PTR)vt[121], lo, hi) == 0x18 && D9RetImm((DWORD_PTR)vt[132], lo, hi) == 0x0C;
        found = vt;
        DllLog("  таблиця пристрою %p: збігів %d з 26 (наступна за збігами - %d), Ex=%d", (void*)vt, bestScore, second, (int)*isEx);
    }
    else DllLog("  таблицю пристрою не визначено: найкращий збіг %d з 26, наступний %d", bestScore, second);
#endif
    DllLog("пошук пристрою D3D9: вказівників у код d3d9 %d, розібрано %d, початків таблиць (8+ записів) %d%s", nTables, nEval, nBest, found ? ", таблицю пристрою знайдено" : "");
    VirtualFree(bestV, 0, MEM_RELEASE);
    return found;
}

// Direct3D 9: спершу стежимо за створенням пристрою гри (об'єкт без пристрою) - так робить і Steam; тестовий пристрій - запасний шлях через кілька секунд
static bool d9FactoryDone = false;
static int d9Tries = 0;
static void TryD3D9()
{
    if (sh->hooks & (HK_D3D9 | HK_D3D9EX)) return;
    HMODULE d9 = PinMod(L"d3d9.dll");
    if (!d9) return;
    if (!d9FactoryDone)
    {
        d9FactoryDone = true;
        bool b = false;
        typedef IDirect3D9* (WINAPI* CreateP_t)(UINT);
        CreateP_t cp = (CreateP_t)(void*)GetProcAddress(d9, "Direct3DCreate9");
        if (cp)
        {
            IDirect3D9* d3d = cp(D3D_SDK_VERSION);
            if (d3d) { b = Hook(VT(d3d)[16], (void*)hkD9CreateB, (void**)&oD9CreateB) || b; d3d->Release(); }
        }
        typedef HRESULT (WINAPI* CreateX_t)(UINT, IDirect3D9Ex**);
        CreateX_t cx = (CreateX_t)(void*)GetProcAddress(d9, "Direct3DCreate9Ex");
        if (cx)
        {
            IDirect3D9Ex* d3d = nullptr;
            if (SUCCEEDED(cx(D3D_SDK_VERSION, &d3d)) && d3d)
            {
                b = Hook(VT(d3d)[16], (void*)hkD9Create, (void**)&oD9Create) || b;
                b = Hook(VT(d3d)[20], (void*)hkD9CreateEx, (void**)&oD9CreateEx) || b;
                d3d->Release();
            }
        }
        if (b && sh->bdrState < 2) sh->bdrState = 1;
    }
    if (++d9Tries <= 5) return;
    // d3d9.dll у грі на DirectX 11/12 підвантажений випадково: шукати там пристрій D3D9 (довге сканування пам'яті) і чіпати ядро не треба
    if (GetModuleHandleW(L"d3d12.dll") || ((sh->hooks & (HK_DXGI | HK_DXGI1)) && sh->dxFrames > 0)) return;
    if (OtherOverlayLoaded())
    {
        // пристрій гри створено до нас, а в процесі вже є чужий оверлей: пробного пристрою не створюємо, а шукаємо пристрій гри в пам'яті
        static int scans = 0;
        static bool noted = false;
        if (!noted) { noted = true; DllLog("Direct3D 9: пристрій гри не перехоплено при створенні, у процесі чужий оверлей - пробний пристрій не створюю, шукаю пристрій гри в пам'яті"); }
        if (scans < 3)
        {
            scans++;
            DWORD t0 = GetTickCount();
            bool vex = false;
            void** gvt = FindD9Vtable(d9, &vex);
            DllLog("Direct3D 9: пошук пристрою гри в пам'яті (спроба %d): %s, %lu мс", scans, gvt ? (vex ? "знайдено таблицю Ex" : "знайдено таблицю") : "не знайдено", (unsigned long)(GetTickCount() - t0));
            if (gvt)
            {
                EnterCriticalSection(&hcs);
                LONG hh = D9HookVt(gvt, vex);
                if (hh) InterlockedOr(&sh->hooks, hh);
                sh->createApi = 2;
                DllLog("D9HookVt: таблиця=%p isEx=%d нові хуки=0x%lX", (void*)gvt, (int)vex, (unsigned long)hh);
                LeaveCriticalSection(&hcs);
                if (sh->hooks & (HK_D3D9 | HK_D3D9EX)) { sh->d8Hr = (LONG)0x7D800021; return; }
            }
            if (scans < 3) return;
        }
        if (HookKmt(API_D3D9)) sh->d8Hr = (LONG)0x7D800020;
        return;
    }
    HWND w = DummyWindow();
    if (!w) return;
    DllLog("Direct3D 9: пробний пристрій (пристрій гри не перехоплено при створенні)");
    D3DPRESENT_PARAMETERS pp = {};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.BackBufferWidth = 64;
    pp.BackBufferHeight = 64;
    pp.hDeviceWindow = w;
    DWORD flags = D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_DISABLE_DRIVER_MANAGEMENT;
    LONG h = 0;

    typedef HRESULT (WINAPI* CreateEx_t)(UINT, IDirect3D9Ex**);
    CreateEx_t createEx = (CreateEx_t)(void*)GetProcAddress(d9, "Direct3DCreate9Ex");
    if (createEx)
    {
        IDirect3D9Ex* d3d = nullptr;
        if (SUCCEEDED(createEx(D3D_SDK_VERSION, &d3d)) && d3d)
        {
            IDirect3DDevice9Ex* dev = nullptr;
            if (SUCCEEDED(d3d->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, w, flags, &pp, nullptr, &dev)) && dev)
            {
                void* pPresent = VT(dev)[17];
                void* pPresentEx = VT(dev)[121];
                void* pReset = VT(dev)[16];
                void* pResetEx = VT(dev)[132];
                void* pCreateDev = VT(d3d)[16];
                void* pCreateDevEx = VT(d3d)[20];
                IDirect3DSwapChain9* sc = nullptr;
                void* pScPresent = nullptr;
                if (SUCCEEDED(dev->GetSwapChain(0, &sc)) && sc) { pScPresent = VT(sc)[3]; sc->Release(); }
                dev->Release();
                // «без рамки»: параметри пристрою задаються під час створення й скидання
                bool b = Hook(pCreateDev, (void*)hkD9Create, (void**)&oD9Create);
                b = Hook(pCreateDevEx, (void*)hkD9CreateEx, (void**)&oD9CreateEx) || b;
                b = Hook(pReset, (void*)hkD9Reset, (void**)&oD9Reset) || b;
                b = Hook(pResetEx, (void*)hkD9ResetEx, (void**)&oD9ResetEx) || b;
                if (b && sh->bdrState < 2) sh->bdrState = 1;
                if (Hook(pPresent, (void*)hkD9Present, (void**)&oD9Present)) h |= HK_D3D9;
                if (Hook(pPresentEx, (void*)hkD9PresentEx, (void**)&oD9PresentEx)) h |= HK_D3D9EX;
                if (pScPresent && Hook(pScPresent, (void*)hkD9ScPresent, (void**)&oD9ScPresent)) h |= HK_D3D9;
            }
            d3d->Release();
        }
    }
    // звичайний пристрій може мати іншу таблицю методів
    typedef IDirect3D9* (WINAPI* Create_t)(UINT);
    Create_t create = (Create_t)(void*)GetProcAddress(d9, "Direct3DCreate9");
    if (create)
    {
        IDirect3D9* d3d = create(D3D_SDK_VERSION);
        if (d3d)
        {
            IDirect3DDevice9* dev = nullptr;
            if (SUCCEEDED(d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, w, flags, &pp, &dev)) && dev)
            {
                void* pPresent = VT(dev)[17];
                void* pReset = VT(dev)[16];
                void* pCreateDev = VT(d3d)[16];
                dev->Release();
                if (pPresent && Hook(pPresent, (void*)hkD9PresentB, (void**)&oD9PresentB)) h |= HK_D3D9;
                bool b = Hook(pCreateDev, (void*)hkD9CreateB, (void**)&oD9CreateB);
                b = Hook(pReset, (void*)hkD9ResetB, (void**)&oD9ResetB) || b;
                if (b && sh->bdrState < 2) sh->bdrState = 1;
            }
            d3d->Release();
        }
    }
    InterlockedOr(&sh->hooks, h);
}

// OpenGL: wglSwapBuffers (через неї йде і SwapBuffers з gdi32)
static void TryGl()
{
    if (sh->hooks & HK_GL) return;
    HMODULE gl = PinMod(L"opengl32.dll");
    if (!gl) return;
    void* p = (void*)GetProcAddress(gl, "wglSwapBuffers");
    LONG h = 0;
    if (Hook(p, (void*)hkSwap, (void**)&oSwap)) h |= HK_GL;
    HMODULE g32 = GetModuleHandleW(L"gdi32.dll");
    void* p2 = g32 ? (void*)GetProcAddress(g32, "SwapBuffers") : nullptr;
    if (p2 && Hook(p2, (void*)hkSwapGdi, (void**)&oSwapGdi)) h |= HK_GL;
    if (h) InterlockedOr(&sh->hooks, h);
}

// Direct3D 12: ExecuteCommandLists черги (через тимчасовий пристрій) - щоб знати чергу гри для власних команд
static void TryD3D12()
{
    if (d12Hooked || d12Tries >= 8) return;
    HMODULE m = PinMod(L"d3d12.dll");
    if (!m) return;
    if ((sh->hooks & (HK_D3D9 | HK_D3D9EX | HK_D3D8 | HK_KMT)) || GlGame()) return;
    d12Tries++;
    typedef HRESULT (WINAPI* Create_t)(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
    Create_t create = (Create_t)(void*)GetProcAddress(m, "D3D12CreateDevice");
    if (!create) { d12Tries = 8; return; }
    ID3D12Device* dev = nullptr;
    if (FAILED(create(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), (void**)&dev)) || !dev) return;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ID3D12CommandQueue* q = nullptr;
    if (SUCCEEDED(dev->CreateCommandQueue(&qd, __uuidof(ID3D12CommandQueue), (void**)&q)) && q)
    {
        void* p = VT(q)[10];
        if (Hook(p, (void*)hkExecCL, (void**)&oExecCL)) d12Hooked = true;
        q->Release();
    }
    dev->Release();
}

// ======================================================================
//  Vulkan: підміна справжнього повноекрану вікном без рамки.
//  Справжній повноекран у Vulkan на Windows роблять двома способами: розширення VK_EXT_full_screen_exclusive
//  (vkAcquireFullScreenExclusiveModeEXT) і зміною режиму екрана (ChangeDisplaySettings). Перше забороняємо й
//  вдаємо, що режим отримано; друге пропускаємо й замість нього розтягуємо вікно гри на монітор.
//  Це не показ у кадрі й не обмеження кадрів - лише підміна, щоб окреме вікно оверлею було видно поверх гри.
// ======================================================================
#define VKAPI WINAPI
typedef void* (VKAPI* VkGetProc_t)(void*, const char*);
typedef int (VKAPI* VkCreateSwapchain_t)(void*, const void*, const void*, void*);
typedef int (VKAPI* VkCreateSurface_t)(void*, const void*, const void*, void*);
typedef int (VKAPI* VkAcquireFS_t)(void*, void*);
typedef int (VKAPI* VkQueuePresent_t)(void*, const void*);
static VkQueuePresent_t oVkQpExp = nullptr, oVkQpGpa = nullptr;
static void* vkQpExport = nullptr;
struct VkPresentInfo { int sType; const void* pNext; unsigned waitCount; const void* pWait; unsigned scCount; const void* const* pSc; };
static VkGetProc_t oVkGIPAexp = nullptr, oVkGDPAexp = nullptr;
static VkCreateSwapchain_t oVkSwExp = nullptr, oVkSwGpa = nullptr;
static VkCreateSurface_t oVkSfExp = nullptr, oVkSfGpa = nullptr;
static void* vkSwExport = nullptr; static void* vkSfExport = nullptr;
static volatile HWND vkHwnd = nullptr;
static volatile LONG vkSeen = 0;

struct VkHdr { int sType; const void* pNext; };
struct VkWin32SurfCI { int sType; const void* pNext; unsigned flags; HINSTANCE hinst; HWND hwnd; };
struct VkFseInfo { int sType; const void* pNext; int mode; };

static bool VkBdr() { HWND h = vkHwnd; return BdrOn() && h && IsWindow(h); }

static int VkSwapchainBody(VkCreateSwapchain_t orig, void* dev, const void* ci, const void* al, void* out)
{
    if (sh) sh->createApi = 6;
    if (BdrOn() && ci)
    {
        // забороняємо грі програмно керований повноекран: тоді вона лишається у вікні, яке ми самі розтягнемо
        for (const VkHdr* h = (const VkHdr*)((const VkHdr*)ci)->pNext; h; h = (const VkHdr*)h->pNext)
        {
            if (h->sType == 1000255000 /*VK_STRUCTURE_TYPE_SURFACE_FULL_SCREEN_EXCLUSIVE_INFO_EXT*/)
            {
                VkFseInfo* f = (VkFseInfo*)h;
                if (f->mode != 2 /*DISALLOWED*/) { f->mode = 2; vkSeen = 1; if (sh->bdrState < 2) sh->bdrState = 1; }
            }
        }
    }
    return orig(dev, ci, al, out);
}
static int VKAPI hkVkSwExp(void* d, const void* ci, const void* al, void* out) { return VkSwapchainBody(oVkSwExp, d, ci, al, out); }
static int VKAPI hkVkSwGpa(void* d, const void* ci, const void* al, void* out) { return VkSwapchainBody(oVkSwGpa, d, ci, al, out); }

static int VkSurfaceBody(VkCreateSurface_t orig, void* inst, const void* ci, const void* al, void* out)
{
    int r = orig(inst, ci, al, out);
    if (r == 0 && ci) { vkHwnd = ((const VkWin32SurfCI*)ci)->hwnd; vkSeen = 1; }
    return r;
}
static int VKAPI hkVkSfExp(void* i, const void* ci, const void* al, void* out) { return VkSurfaceBody(oVkSfExp, i, ci, al, out); }
static int VKAPI hkVkSfGpa(void* i, const void* ci, const void* al, void* out) { return VkSurfaceBody(oVkSfGpa, i, ci, al, out); }

// кадр Vulkan: рахуємо й обмежуємо перед показом
static int VkPresentBody(VkQueuePresent_t orig, void* q, const void* pi)
{
    if (layerActive) return orig(q, pi);        // кадри вже враховує шар Vulkan
    Outer o;
    if (o.on && pi)
    {
        const VkPresentInfo* p = (const VkPresentInfo*)pi;
        void* chain = (p->scCount && p->pSc) ? (void*)p->pSc[0] : q;
        vkLastTick = GetTickCount() | 1;
        InterlockedIncrement(&sh->vkFrames);
        OnFrame(chain, API_VK);
    }
    return orig(q, pi);
}
static int VKAPI hkVkQpExp(void* q, const void* pi) { return VkPresentBody(oVkQpExp, q, pi); }
static int VKAPI hkVkQpGpa(void* q, const void* pi) { return VkPresentBody(oVkQpGpa, q, pi); }

// гра просить справжній повноекран: вдаємо, що отримали, а вікно розтягуємо на монітор
static int VKAPI hkVkAcquire(void*, void*)
{
    if (VkBdr())
    {
        RECT rc;
        HWND h = vkHwnd;
        if (MonitorRectOf(nullptr, h, &rc)) { BdrRequest(h, &rc, true); BdrMarked(3); }
    }
    return 0;
}
static int VKAPI hkVkRelease(void*, void*)
{
    if (VkBdr()) BdrRequest(vkHwnd, nullptr, false);
    return 0;
}

// ім'я функції, яку гра отримує через vkGet*ProcAddr: підставляємо свою
static void* VkMap(const char* n, void* r)
{
    if (!n || !r) return r;
    if (!strcmp(n, "vkCreateSwapchainKHR")) { if (r == vkSwExport) return r; oVkSwGpa = (VkCreateSwapchain_t)r; return (void*)hkVkSwGpa; }
    if (!strcmp(n, "vkCreateWin32SurfaceKHR")) { if (r == vkSfExport) return r; oVkSfGpa = (VkCreateSurface_t)r; return (void*)hkVkSfGpa; }
    if (!strcmp(n, "vkQueuePresentKHR")) { if (r == vkQpExport) return r; oVkQpGpa = (VkQueuePresent_t)r; return (void*)hkVkQpGpa; }
    if (!strcmp(n, "vkAcquireFullScreenExclusiveModeEXT")) return (void*)hkVkAcquire;
    if (!strcmp(n, "vkReleaseFullScreenExclusiveModeEXT")) return (void*)hkVkRelease;
    return r;
}
static void* VKAPI hkVkGIPA(void* inst, const char* n) { return VkMap(n, oVkGIPAexp(inst, n)); }
static void* VKAPI hkVkGDPA(void* dev, const char* n) { return VkMap(n, oVkGDPAexp(dev, n)); }

// зміна режиму екрана: замість неї вікно просто розтягується на монітор
typedef LONG (WINAPI* CdsExW_t)(LPCWSTR, DEVMODEW*, HWND, DWORD, LPVOID);
typedef LONG (WINAPI* CdsW_t)(DEVMODEW*, DWORD);
static CdsExW_t oCdsExW = nullptr;
static CdsW_t oCdsW = nullptr;
static bool CdsSwallow(DEVMODEW* dm, DWORD fl)
{
    if (!dm || (fl & CDS_TEST) || !VkBdr()) return false;
    if (!(dm->dmFields & (DM_PELSWIDTH | DM_PELSHEIGHT))) return false;
    RECT rc;
    HWND h = vkHwnd;
    if (!MonitorRectOf(nullptr, h, &rc)) return false;
    BdrRequest(h, &rc, true);
    BdrMarked(3);
    return true;
}
static LONG WINAPI hkCdsExW(LPCWSTR dev, DEVMODEW* dm, HWND w, DWORD fl, LPVOID p)
{
    if (CdsSwallow(dm, fl)) return DISP_CHANGE_SUCCESSFUL;
    return oCdsExW(dev, dm, w, fl, p);
}
static LONG WINAPI hkCdsW(DEVMODEW* dm, DWORD fl)
{
    if (CdsSwallow(dm, fl)) return DISP_CHANGE_SUCCESSFUL;
    return oCdsW(dm, fl);
}

// ---- шар Vulkan (sbvk.cpp) ----
// Спільну пам'ять NeoBar створює, коли помічає гру; шар може запуститися раніше - тоді пробуємо відкрити її щопівсекунди.
static bool LayerShared()
{
    if (sh) return true;
    if (initRunning) return false;
    static DWORD last = 0;
    DWORD now = GetTickCount();
    if (last && now - last < 500) return false;
    last = now ? now : 1;
    if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
    OpenShared();
    if (!sh) return false;
    sh->status = 2;
    InterlockedOr(&sh->hooks, HK_VK);
    if (sh->bdrState < 2) sh->bdrState = 1;
    return true;
}
extern "C" int SbLayerBdrOn() { return LayerShared() && BdrOn(); }
extern "C" void SbLayerAttach() { InterlockedExchange(&layerActive, 1); }
extern "C" void SbLayerFrame(void* chain)
{
    if (!LayerShared()) return;
    vkLastTick = GetTickCount() | 1;
    InterlockedIncrement(&sh->vkFrames);
    OnFrame(chain, API_VK);
}
extern "C" void SbLayerStripNote() { if (sh) { vkSeen = 1; if (sh->bdrState < 2) sh->bdrState = 1; } }
extern "C" void SbLayerSurface(HWND h) { vkHwnd = h; vkSeen = 1; }
extern "C" int SbLayerAcquire() { return hkVkAcquire(nullptr, nullptr); }
extern "C" int SbLayerRelease() { return hkVkRelease(nullptr, nullptr); }

static void TryVulkan()
{
    if (sh->hooks & HK_VK) return;
    HMODULE vk = PinMod(L"vulkan-1.dll");
    if (!vk) return;
    vkSwExport = (void*)GetProcAddress(vk, "vkCreateSwapchainKHR");
    vkSfExport = (void*)GetProcAddress(vk, "vkCreateWin32SurfaceKHR");
    vkQpExport = (void*)GetProcAddress(vk, "vkQueuePresentKHR");
    void* gi = (void*)GetProcAddress(vk, "vkGetInstanceProcAddr");
    void* gd = (void*)GetProcAddress(vk, "vkGetDeviceProcAddr");
    bool any = false;
    if (vkSwExport) any = Hook(vkSwExport, (void*)hkVkSwExp, (void**)&oVkSwExp) || any;
    if (vkSfExport) any = Hook(vkSfExport, (void*)hkVkSfExp, (void**)&oVkSfExp) || any;
    if (vkQpExport) any = Hook(vkQpExport, (void*)hkVkQpExp, (void**)&oVkQpExp) || any;
    if (gi) any = Hook(gi, (void*)hkVkGIPA, (void**)&oVkGIPAexp) || any;
    if (gd) any = Hook(gd, (void*)hkVkGDPA, (void**)&oVkGDPAexp) || any;
    HMODULE u32 = GetModuleHandleW(L"user32.dll");
    if (u32)
    {
        void* a = (void*)GetProcAddress(u32, "ChangeDisplaySettingsExW");
        void* b = (void*)GetProcAddress(u32, "ChangeDisplaySettingsW");
        if (a) Hook(a, (void*)hkCdsExW, (void**)&oCdsExW);
        if (b) Hook(b, (void*)hkCdsW, (void**)&oCdsW);
    }
    if (any)
    {
        if (sh->bdrState < 2) sh->bdrState = 1;
        InterlockedOr(&sh->hooks, HK_VK);
    }
}

// Direct3D 8: IDirect3DDevice8::Present (індекс 15)
typedef HRESULT (STDMETHODCALLTYPE* D8Present_t)(void*, const RECT*, const RECT*, HWND, const RGNDATA*);
static D8Present_t oD8Present = nullptr;
static HRESULT STDMETHODCALLTYPE hkD8Present(void* d, const RECT* a, const RECT* b, HWND w, const RGNDATA* r)
{
    Outer o;
    if (o.on) { OnFrame(d, API_D3D8); if (sh->osdOn) OsdDraw8(d); }
    return oD8Present(d, a, b, w, r);
}

// DirectX 8: вбудовані функції d3d8.dll інколи не піддаються вбудованому перехопленню (MinHook) - підміняємо ячейки таблиці методів.
// Таблиця спільна для всіх об'єктів класу, тож підміна діє й на пристрій гри.
static bool VtPatch(void** slot, void* detour, void** orig)
{
    if (!slot || *slot == detour) return false;
    DWORD old = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) return false;
    *orig = InterlockedExchangePointer(slot, detour);
    DWORD tmp;
    VirtualProtect(slot, sizeof(void*), old, &tmp);
    return true;
}

// пристрій DirectX 8, створений грою після нашого підключення: Present беремо з нього (нічого не створюємо самі)
typedef HRESULT (STDMETHODCALLTYPE* D8Create_t)(void*, UINT, int, HWND, DWORD, void*, void**);
static D8Create_t oD8Create = nullptr;
static bool d8PresentPatched = false;
static HRESULT STDMETHODCALLTYPE hkD8Create(void* self, UINT ad, int type, HWND w, DWORD fl, void* pp, void** dev)
{
    HRESULT hr = oD8Create(self, ad, type, w, fl, pp, dev);
    if (SUCCEEDED(hr) && sh) sh->createApi = 5;
    if (SUCCEEDED(hr) && dev && *dev && !d8PresentPatched)
    {
        void* slot = SbD8PresentOf(*dev);
        if (slot && VtPatch((void**)slot, (void*)hkD8Present, (void**)&oD8Present))
        {
            d8PresentPatched = true;
            sh->d8Hr = 0;
            InterlockedOr(&sh->hooks, HK_D3D8);
        }
        else sh->d8Hr = (LONG)0x7D800001;      // пристрій гри створено, а Present не підмінився
    }
    return hr;
}

// Запасний шлях для DirectX 8, коли пристрій гри вже існує (його ми знайти не можемо, а пробний пристрій при повноекрані не створюється):
// кадри рахуємо й обмежуємо на показі ядром (D3DKMTPresent). Показників у кадрі тут немає. Працює лише доки жоден хук DirectX 8 не встановлено.
typedef LONG (WINAPI* KmtPresent_t)(void*);
static KmtPresent_t oKmtPresent = nullptr;
static int kmtApi = API_D3D8;
static KmtPresent_t oKmtW = nullptr, oMpo1 = nullptr, oMpo2 = nullptr, oMpo3 = nullptr;
static volatile LONG kmtSeen[5];
static inline bool KmtOnly() { return !(sh->hooks & (HK_D3D8 | HK_D3D9 | HK_D3D9EX | HK_DXGI | HK_DXGI1 | HK_GL)); }
static void KmtNote(int i)
{
    static const char* nm[5] = { "D3DKMTPresent", "NtGdiDdDDIPresent", "PresentMultiPlaneOverlay", "PresentMultiPlaneOverlay2", "PresentMultiPlaneOverlay3" };
    if (InterlockedExchange(&kmtSeen[i], 1) == 0) DllLog("показ ядром: перший виклик %s", nm[i]);
}
static LONG WINAPI hkKmtPresent(void* p)
{
    Outer o;
    if (o.on && KmtOnly()) { KmtNote(0); OnFrame((void*)&oKmtPresent, kmtApi); }
    return oKmtPresent(p);
}
static LONG WINAPI hkKmtPresentW(void* p)
{
    Outer o;
    if (o.on && KmtOnly()) { KmtNote(1); OnFrame((void*)&oKmtW, kmtApi); }
    return oKmtW(p);
}
static LONG WINAPI hkMpo1(void* p)
{
    Outer o;
    if (o.on && KmtOnly()) { KmtNote(2); OnFrame((void*)&oMpo1, kmtApi); }
    return oMpo1(p);
}
static LONG WINAPI hkMpo2(void* p)
{
    Outer o;
    if (o.on && KmtOnly()) { KmtNote(3); OnFrame((void*)&oMpo2, kmtApi); }
    return oMpo2(p);
}
static LONG WINAPI hkMpo3(void* p)
{
    Outer o;
    if (o.on && KmtOnly()) { KmtNote(4); OnFrame((void*)&oMpo3, kmtApi); }
    return oMpo3(p);
}

// кадри на показі ядром (без пристрою гри): лише підрахунок і обмеження. Для Direct3D 9/8, коли пробний пристрій небезпечний.
// Гра може показувати кадр різними викликами ядра (D3DKMTPresent, а на сучасних системах і MultiPlaneOverlay), тому ставимо всі; основним стає той, що йде першим.
static bool HookKmt(int api)
{
    if (sh->hooks & HK_KMT) return true;
    HMODULE g = GetModuleHandleW(L"gdi32.dll"), w32 = GetModuleHandleW(L"win32u.dll");
    void* k = g ? (void*)GetProcAddress(g, "D3DKMTPresent") : nullptr;
    void* kw = w32 ? (void*)GetProcAddress(w32, "NtGdiDdDDIPresent") : nullptr;
    kmtApi = api;
    bool ok = false;
    if (k && Hook(k, (void*)hkKmtPresent, (void**)&oKmtPresent)) ok = true;
    if (kw && kw != k && Hook(kw, (void*)hkKmtPresentW, (void**)&oKmtW)) ok = true;
    if (w32)
    {
        void* m1 = (void*)GetProcAddress(w32, "NtGdiDdDDIPresentMultiPlaneOverlay");
        void* m2 = (void*)GetProcAddress(w32, "NtGdiDdDDIPresentMultiPlaneOverlay2");
        void* m3 = (void*)GetProcAddress(w32, "NtGdiDdDDIPresentMultiPlaneOverlay3");
        if (m1 && Hook(m1, (void*)hkMpo1, (void**)&oMpo1)) ok = true;
        if (m2 && Hook(m2, (void*)hkMpo2, (void**)&oMpo2)) ok = true;
        if (m3 && Hook(m3, (void*)hkMpo3, (void**)&oMpo3)) ok = true;
    }
    if (ok) { InterlockedOr(&sh->hooks, HK_KMT); return true; }
    return false;
}

static int d8Tries = 0;
static bool d8CreateHooked = false;
static void TryD3D8()
{
    if (sh->hooks & (HK_D3D8 | HK_KMT)) return;
    if (!GetModuleHandleW(L"d3d8.dll")) return;
    if (GetModuleHandleW(L"d3d9.dll")) return;        // справжній d3d8.dll d3d9 не вантажить: це обгортка DirectX 8 → 9, її перехоплює Direct3D 9
    // пристрій створить гра після нашого підключення - підміняємо CreateDevice (ячейку таблиці); пробних пристроїв не створюємо
    if (!d8CreateHooked)
    {
        void* c = SbD8CreateAddr();
        d8CreateHooked = c && VtPatch((void**)c, (void*)hkD8Create, (void**)&oD8Create);
        if (!d8CreateHooked) sh->d8Hr = (LONG)0x7D800002;
    }
    // пристрій гри не з'явився за кілька секунд - він створений ще до нас: переходимо на показ ядром
    if (++d8Tries < 4) return;
    if (HookKmt(API_D3D8)) sh->d8Hr = (LONG)0x7D800010;           // пристрій гри не перехоплено: лише підрахунок і обмеження кадрів на показі ядром
    else sh->d8Hr = (LONG)0x7D800011;
}

static void DetectD3d()
{
    LONG v = 0;
    if (GetModuleHandleW(L"ddraw.dll")) v = 7;       // DirectDraw (старі 2D-ігри)
    if (GetModuleHandleW(L"d3d8.dll")) v = 8;        // DirectX 8 (ddraw.dll тут лише залежність)
    if (GetModuleHandleW(L"d3d9.dll")) v = 9;
    if (GetModuleHandleW(L"d3d10.dll") || GetModuleHandleW(L"d3d10_1.dll")) v = 10;
    if (GetModuleHandleW(L"d3d11.dll")) v = 11;
    if (GetModuleHandleW(L"d3d12.dll")) v = 12;
    sh->d3dver = v;
}

static DWORD WINAPI InitBody();
static DWORD WINAPI Init(LPVOID)
{
    InterlockedExchange(&initRunning, 1);
    DWORD r = InitBody();
    InterlockedExchange(&initRunning, 0);
    return r;
}

static DWORD WINAPI InitBody()
{
    QueryPerformanceFrequency(&freq);
    OpenShared();
    if (!sh) return 1;
    sh->status = 1;
    if (MH_Initialize() != MH_OK) { sh->status = -1; return 1; }
    // гра могла ще не завантажити графіку - пробуємо до хвилини
    for (int i = 0; i < 60; i++)
    {
        DetectD3d();
        TryDxgi();
        TryD3D9();
        TryGl();
        TryD3D12();
        TryD3D8();
        TryVulkan();
        if (sh->hooks != 0) sh->status = 2;
        TryDdraw();
        if (sh->hooks != 0 && i >= 45 && (d12Hooked || d12Tries >= 8 || !GetModuleHandleW(L"d3d12.dll"))) break;
        Sleep(1000);
    }
    DetectD3d();
    sh->status = sh->hooks != 0 ? 2 : -2;
    if (DummyWindow()) DestroyWindow(DummyWindow());
    return 0;
}

extern "C" BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(inst);
        g_self = inst;
        {
            // журнал - лише якщо є позначка logs\dll.on у теці бібліотеки
            wchar_t base[MAX_PATH];
            DWORD n = GetModuleFileNameW(inst, base, MAX_PATH);
            while (n > 0 && base[n - 1] != L'\\' && base[n - 1] != L'/') n--;
            base[n] = 0;
            wchar_t mark[MAX_PATH];
            if (n > 0 && n + 24 < MAX_PATH)
            {
                lstrcpyW(mark, base); lstrcatW(mark, L"logs\\dll.on");
                if (GetFileAttributesW(mark) != INVALID_FILE_ATTRIBUTES)
                {
                    lstrcpyW(g_logPath, base); lstrcatW(g_logPath, L"logs\\dll.log");
                }
            }
        }
        if (g_logPath[0])
        {
            char exe[MAX_PATH]; exe[0] = 0; GetModuleFileNameA(nullptr, exe, MAX_PATH); DllLog("бібліотеку підключено до %s", exe);
            AddVectoredExceptionHandler(1, SbVeh);
        }
        InitializeCriticalSection(&cs);
        InitializeCriticalSection(&bcs);
        InitializeCriticalSection(&ocs);
        InitializeCriticalSection(&hcs);
        bdrEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        tlsDepth = TlsAlloc();
        tlsTimer = TlsAlloc();
        // посилання на себе: бібліотека не вивантажиться, навіть якщо хтось викличе FreeLibrary
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)(void*)&DllMain, &self);
        HANDLE t = CreateThread(nullptr, 0, Init, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    }
    else if (reason == DLL_PROCESS_DETACH) DllLog("процес завершується (DLL_PROCESS_DETACH)");
    return TRUE;
}
