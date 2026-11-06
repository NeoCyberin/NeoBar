﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Вікно налаштувань: «Джерела даних» - звідки NeoBar бере погоду, свята, карту, місце й точний час
// і чи вони зараз доступні (перевірка - NetSources.cs)
sealed partial class SettingsForm
{
    SCard srcCard;

    void BuildSources(SPage left)
    {
        srcCard = left.Card(L.T("Джерела даних"));
        // служба розташування Windows - не мережа, але теж джерело місця
        SRow lr = srcCard.Row(L.T("Служба розташування Windows"), "");
        lr.DescFn = delegate
        {
            if (!c.LocAllow) return L.T("не використовується (визначення розташування вимкнено)");
            string why = SysCheck.LocationOff();
            return why.Length == 0 ? L.T("увімкнена - місто й часовий пояс визначаються точно") : why + L.T(" Місце визначається за IP.");
        };
        foreach (NetSource s0 in NetSources.All)
        {
            NetSource s = s0;
            SRow r = srcCard.Row(s.Title, "");
            r.DescFn = delegate { return s.Line; };
        }
        Label note = Note();
        note.Text = L.T("Усе завантажене зберігається в кеші (розділ «Дані»); без інтернету показується збережене. Сервер часу потрібен для точності годинника й синхронізації часу.");
        srcCard.WithNote(srcCard.Row(L.T("Перевірка з'єднання"), "", Bt(L.T("Перевірити"), "g:refresh", 150, delegate { CheckSources(); })), note);
        srcCard.Rows[srcCard.Rows.Count - 1].DescFn = delegate
        {
            if (NetSources.Busy) return L.T("Перевіряю…");
            if (NetSources.LastAt == DateTime.MinValue) return L.T("Ще не перевірялось");
            return L.T("Перевірено о ") + NetSources.LastAt.ToString("HH:mm:ss");
        };
        // перша перевірка - коли розділ відкрили (якщо свіжого результату ще немає)
        left.VisibleChanged += delegate { if (left.Visible && !NetSources.Fresh) CheckSources(); };
    }

    void CheckSources()
    {
        if (!NetSources.CheckAll(delegate { SourcesChanged(); })) return;
        SourcesChanged();
    }

    // текст стану може зайняти інший рядок - перерахувати висоту картки
    void SourcesChanged()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                if (srcCard != null && !srcCard.IsDisposed) { srcCard.Queue(); srcCard.Invalidate(); }
            }));
        }
        catch { }
    }
}
