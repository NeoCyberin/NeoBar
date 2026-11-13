// NeoBarDrop.exe - помічник перетягування файлів для NeoBar, запущеної від імені адміністратора.
// Windows (UIPI) не пускає перетягування зі звичайного Провідника у вікна з вищими правами. Цей крихітний
// процес працює зі звичайними правами: NeoBar ставить його невидиме вікно над своїм вікном, що приймає файли,
// помічник отримує WM_DROPFILES і передає шляхи панелі (WM_COPYDATA). Без .NET - мегабайт пам'яті замість десятків.
// Виклик: NeoBarDrop.exe <вікно-приймач NeoBar>. Сам закривається, коли NeoBar закрилась або 2 хв без роботи.
#include <windows.h>
#include <shellapi.h>
#include <stdlib.h>
#include <wchar.h>

static const UINT MsgShow = 0x8000 + 0x61, MsgHide = 0x8000 + 0x62;
static const ULONG_PTR Magic = 0x4E42444E;
static const UINT IdleMs = 120000;

static HWND target, zone;
static DWORD lastWork;

static void Off(HWND h) { ShowWindow(h, SW_HIDE); }

static void Send(HWND self, const wchar_t* text, size_t len)
{
    COPYDATASTRUCT cd;
    cd.dwData = Magic;
    cd.cbData = (DWORD)(len * sizeof(wchar_t));
    cd.lpData = (void*)text;
    DWORD_PTR res = 0;
    SendMessageTimeoutW(target, WM_COPYDATA, (WPARAM)self, (LPARAM)&cd, SMTO_ABORTIFHUNG, 5000, &res);
}

static void Drop(HWND self, HDROP hd)
{
    HWND z = zone;
    Off(self);
    UINT n = DragQueryFileW(hd, 0xFFFFFFFF, NULL, 0);
    if (n == 0 || z == NULL) { DragFinish(hd); return; }
    // «вікно\nфайл\nфайл…»
    size_t cap = 64;
    for (UINT i = 0; i < n; i++) cap += DragQueryFileW(hd, i, NULL, 0) + 1;
    wchar_t* buf = (wchar_t*)calloc(cap + 1, sizeof(wchar_t));
    if (!buf) { DragFinish(hd); return; }
    size_t pos = (size_t)swprintf(buf, 32, L"%lld", (long long)(LONG_PTR)z);
    for (UINT i = 0; i < n; i++)
    {
        buf[pos++] = L'\n';
        UINT l = DragQueryFileW(hd, i, buf + pos, (UINT)(cap - pos));
        pos += l;
    }
    DragFinish(hd);
    Send(self, buf, pos);
    free(buf);
}

static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == MsgShow)
    {
        RECT rc;
        HWND z = (HWND)w;
        lastWork = GetTickCount();
        if (!IsWindow(z) || !GetWindowRect(z, &rc)) return 0;
        zone = z;
        SetWindowPos(h, HWND_TOPMOST, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return 0;
    }
    if (m == MsgHide) { Off(h); return 0; }
    if (m == WM_DROPFILES) { lastWork = GetTickCount(); Drop(h, (HDROP)w); return 0; }
    if (m == WM_TIMER)
    {
        if (!IsWindow(target) || (!IsWindowVisible(h) && GetTickCount() - lastWork > IdleMs)) DestroyWindow(h);
        return 0;
    }
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, LPWSTR cmd, int)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);     // при збої - просто завершитись, без вікна Windows
    long long r = _wtoi64(cmd);
    target = (HWND)(LONG_PTR)r;
    if (r == 0 || !IsWindow(target)) return 1;
    wchar_t name[64];
    swprintf(name, 64, L"Local\\NeoBarDrop%lld", r);
    HANDLE mx = CreateMutexW(NULL, TRUE, name);
    if (!mx || GetLastError() == ERROR_ALREADY_EXISTS) return 0;
    SetProcessDPIAware();

    WNDCLASSW wc = {};
    wc.lpfnWndProc = Proc;
    wc.hInstance = inst;
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    wc.lpszClassName = L"NeoBarDrop";
    RegisterClassW(&wc);
    wchar_t title[64];
    swprintf(title, 64, L"NeoBarDrop %lld", r);         // за цим заголовком NeoBar знаходить помічника
    HWND h = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_ACCEPTFILES,
        wc.lpszClassName, title, WS_POPUP, -32000, -32000, 8, 8, NULL, NULL, inst, NULL);
    if (!h) return 1;
    SetLayeredWindowAttributes(h, 0, 1, LWA_ALPHA);     // невидиме, але приймає мишу й перетягування
    lastWork = GetTickCount();
    SetTimer(h, 1, 1000, NULL);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) DispatchMessageW(&msg);
    ReleaseMutex(mx);
    CloseHandle(mx);
    return 0;
}
