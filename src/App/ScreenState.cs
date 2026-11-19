using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Чи є людина біля екрана: монітор вимкнено (сон екрана) або комп'ютер заблоковано.
//  Поки нікого немає, NeoBar не малює й не опитує те, чого ніхто не побачить:
//  живі шпалери на паузі, датчики рідше (раз на 10 с - нагляд за перегрівом лишається),
//  віджети панелі, пінг і температури дисків стоять.
//  Після повернення шпалери оживають не одразу, а через кілька секунд - щоб не навантажувати відеокарту
//  саме тоді, коли драйвер вмикає монітор.
//  Сповіщення приходять від Windows (RegisterPowerSettingNotification, WTSRegisterSessionNotification) -
//  без таймерів і опитування.
// ======================================================================
static class ScreenState
{
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr h);
    [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr h, int flags);
    [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(IntPtr h);

    static Guid DisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");     // GUID_CONSOLE_DISPLAY_STATE
    public const int WakeDelayMs = 5000;

    static volatile bool displayOff, locked;
    static DateTime backAt = DateTime.MinValue;       // коли людина повернулась (UTC)
    static Wnd wnd;

    public static bool DisplayOff { get { return displayOff; } }
    public static bool Locked { get { return locked; } }
    // нікого немає: екран вимкнено або комп'ютер заблоковано
    public static bool Away { get { return displayOff || locked; } }
    // те саме плюс кілька секунд після повернення (для важкого: живих шпалер)
    public static bool AwayOrWaking { get { return Away || (DateTime.UtcNow - backAt).TotalMilliseconds < WakeDelayMs; } }
    // скільки секунд тому людина повернулась (-1 - ще не відходила)
    public static double SinceBack { get { return backAt == DateTime.MinValue ? -1 : (DateTime.UtcNow - backAt).TotalSeconds; } }

    // у потоці інтерфейсу, коли Away змінюється
    public static event Action Changed;

    public static void Start()
    {
        if (wnd != null) return;
        try { wnd = new Wnd(); }
        catch (Exception ex) { SbLog.Skip(ex); wnd = null; }
    }

    public static void Stop()
    {
        Wnd w = wnd;
        wnd = null;
        if (w != null) w.Close();
    }

    static void Set(bool off, bool lck)
    {
        bool was = Away;
        displayOff = off; locked = lck;
        bool now = Away;
        if (was == now) return;
        if (!now) backAt = DateTime.UtcNow;
        SbLog.W(SbLog.Panel, now
            ? "нікого немає (" + (off ? "екран вимкнено" : "комп'ютер заблоковано") + ") - шпалери, віджети й пінг на паузі, датчики раз на 10 с"
            : "людина повернулась - усе працює знову (шпалери - через " + WakeDelayMs / 1000 + " с)");
        Action a = Changed;
        if (a != null) try { a(); } catch (Exception ex) { Util.ErrorLog(ex); }
    }

    sealed class Wnd : NativeWindow
    {
        IntPtr power;
        bool session;

        public Wnd()
        {
            CreateHandle(new CreateParams());          // невидиме вікно лише для сповіщень Windows
            power = RegisterPowerSettingNotification(Handle, ref DisplayState, 0);   // DEVICE_NOTIFY_WINDOW_HANDLE; одразу надсилає поточний стан
            session = WTSRegisterSessionNotification(Handle, 0);                     // NOTIFY_FOR_THIS_SESSION
            if (power == IntPtr.Zero || !session)
                SbLog.W(SbLog.Panel, "стан екрана: сповіщення Windows недоступні (екран " + (power != IntPtr.Zero ? "так" : "ні") + ", блокування " + (session ? "так" : "ні") + ")");
        }

        public void Close()
        {
            try
            {
                if (power != IntPtr.Zero) UnregisterPowerSettingNotification(power);
                if (session) WTSUnRegisterSessionNotification(Handle);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            power = IntPtr.Zero; session = false;
            DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE: POWERBROADCAST_SETTING { GUID; DWORD DataLength; DWORD Data } - 0 вимкнено, 1 увімкнено, 2 притлумлено
                if (m.Msg == 0x218 && m.WParam.ToInt64() == 0x8013 && m.LParam != IntPtr.Zero)
                {
                    Guid g = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                    if (g == DisplayState && Marshal.ReadInt32(m.LParam, 16) >= 4)
                        Set(Marshal.ReadInt32(m.LParam, 20) == 0, locked);
                }
                // WM_WTSSESSION_CHANGE: WTS_SESSION_LOCK / WTS_SESSION_UNLOCK
                else if (m.Msg == 0x2B1)
                {
                    int c = m.WParam.ToInt32();
                    if (c == 7) Set(displayOff, true);
                    else if (c == 8) Set(displayOff, false);
                }
            }
            catch (Exception ex) { SbLog.Skip(ex); }
            base.WndProc(ref m);
        }
    }
}
