using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Гарячі клавіші: поле введення, перевірка зайнятості, глобальна реєстрація

// Поле-перехоплювач сполучення клавіш (для налаштувань)
sealed class HotkeyBox : System.Windows.Forms.TextBox
{
    public Keys Value = Keys.None;

    public HotkeyBox()
    {
        ReadOnly = true;
        Cursor = System.Windows.Forms.Cursors.Hand;
        BackColor = System.Drawing.Color.White;
        TabStop = true;
    }

    public void SetValue(Keys k)
    {
        Value = k;
        Text = OverlayCfg.KeyText(k);
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Back || key == Keys.Delete || key == Keys.Escape) { SetValue(Keys.None); return true; }
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.None || key == Keys.Tab) return key == Keys.Tab ? base.ProcessCmdKey(ref msg, keyData) : true;
        SetValue(keyData);
        return true;
    }
}

// Перевірка комбінації для гарячої клавіші: "" - можна, інакше причина, чому не можна
static class HotkeyCheck
{
    // Те, що Windows не дає перехопити (RegisterHotKey не повідомляє про зайнятість), і стандартні команди програм
    // відсіюються за списком (Microsoft: «Сполучення клавіш у Windows»); решта перевіряється реєстрацією.
    public static string Problem(Keys k, Keys ownNow)
    {
        Keys key = k & Keys.KeyCode;
        if (key == Keys.None) return "";
        bool ctrl = (k & Keys.Control) != 0, alt = (k & Keys.Alt) != 0, shift = (k & Keys.Shift) != 0;
        bool letter = key >= Keys.A && key <= Keys.Z, digit = (key >= Keys.D0 && key <= Keys.D9) || (key >= Keys.NumPad0 && key <= Keys.NumPad9);
        if (key == Keys.LWin || key == Keys.RWin) return L.T("клавіша Windows зарезервована системою.");
        if (!ctrl && !alt && !shift) return L.T("потрібен Ctrl, Alt або Shift.");
        if (shift && !ctrl && !alt && (letter || digit || key == Keys.Space || key == Keys.Delete || key == Keys.F10)) return L.T("заважатиме друкувати.");
        // системні комбінації Windows
        if ((alt && key == Keys.Tab) || (ctrl && alt && key == Keys.Tab) ||
            (alt && !ctrl && !shift && (key == Keys.F4 || key == Keys.Space || key == Keys.Enter || key == Keys.Escape)) ||
            (ctrl && !alt && key == Keys.Escape) || (ctrl && alt && (key == Keys.Delete || key == Keys.Decimal)) ||
            (ctrl && !alt && !shift && key == Keys.Space) || key == Keys.PrintScreen)
            return L.T("системна комбінація Windows.");
        // стандартні команди програм: панель перехоплювала б їх у всіх вікнах
        if (ctrl && !alt && !shift && (letter || digit || key == Keys.Tab || key == Keys.Insert || key == Keys.Delete || key == Keys.Home || key == Keys.End ||
            key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || (key >= Keys.F1 && key <= Keys.F12)))
            return L.T("стандартна команда програм.");
        if (ctrl && shift && !alt && (key == Keys.T || key == Keys.N || key == Keys.Z || key == Keys.V || key == Keys.C || key == Keys.P || key == Keys.Tab))
            return L.T("часта команда програм.");
        if (alt && !ctrl && !shift && (letter || digit)) return L.T("відкриває меню в програмах.");
        if (k == ownNow) return "";
        // зайнята іншою програмою чи самою Windows?
        using (HotkeyWindow probe = new HotkeyWindow())
            if (!probe.Register(0x5BFF, k)) return L.T("уже зайнята Windows або іншою програмою.");
        return "";
    }

}

// ----------------------------------------------------------------------
//  Гарячі клавіші: окреме невидиме вікно, що живе весь час роботи панелі
// ----------------------------------------------------------------------
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event Action<int> Pressed;
    readonly List<int> ids = new List<int>();
    public readonly Dictionary<int, bool> Ok = new Dictionary<int, bool>();

    public HotkeyWindow()
    {
        CreateParams cp = new CreateParams();
        cp.Caption = "NeoBar hotkeys";
        cp.Parent = new IntPtr(-3);      // HWND_MESSAGE: лише для повідомлень
        CreateHandle(cp);
    }

    public bool Register(int id, Keys k)
    {
        Win.UnregisterHotKey(Handle, id);
        ids.Remove(id);
        Ok.Remove(id);
        if ((k & Keys.KeyCode) == Keys.None) return true;
        uint mods = 0x4000;               // MOD_NOREPEAT
        if ((k & Keys.Alt) != 0) mods |= 1;
        if ((k & Keys.Control) != 0) mods |= 2;
        if ((k & Keys.Shift) != 0) mods |= 4;
        bool ok = false;
        try { ok = Win.RegisterHotKey(Handle, id, mods, (uint)(k & Keys.KeyCode)); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (ok) ids.Add(id);
        Ok[id] = ok;
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312)
        {
            Action<int> p = Pressed;
            if (p != null) try { p(m.WParam.ToInt32()); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in ids) try { Win.UnregisterHotKey(Handle, id); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        ids.Clear();
        DestroyHandle();
    }
}
