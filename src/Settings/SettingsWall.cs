using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// ======================================================================
//  Налаштування: шпалери робочого столу (зображення, слайд-шоу з переходами, живі шпалери).
//  Файли обираються з галереї NeoBar: додані файли копіюються в окрему теку для кожного виду.
// ======================================================================
sealed partial class SettingsForm
{
    SCombo cbWMode, cbWFit, cbWInt, cbWTrans;
    CheckBox chWShuffle, chWSound, chWAll, chWCover, chWBat, chWGame;
    SSlider tkWDur;
    Label lWDur, lWStatus, lWGal;
    SCard wallGal, wallSrc, wallEco;
    WallGallery wallGallery;
    string wImage = "", wVideo = "";            // обрані в галереї зображення й відео
    string wallSig = "";                        // що було застосовано востаннє: без змін шпалери не перезапускаються

    int WMode { get { return cbWMode == null ? 0 : Math.Max(0, cbWMode.SelectedIndex); } }
    int WFx { get { return cbWTrans == null ? 1 : Math.Max(0, cbWTrans.SelectedIndex); } }

    // вид галереї для режиму: 1 зображення, 2 слайд-шоу, 3 живі
    static int GalKind(int mode) { return mode == 1 ? WallLib.Image : mode == 2 ? WallLib.Slideshow : mode == 3 ? WallLib.Live : -1; }

    Control BuildWall()
    {
        SPage p = new SPage();
        SCard k = p.Card(L.T("Шпалери робочого столу"));
        cbWMode = Cb(300, WallCfg.Modes);
        cbWMode.SelectedIndexChanged += delegate { WallQueue(); UpdateGallery(); };
        k.Row(L.T("Тип шпалер"), L.T("Окремо для кожного профілю. Шпалери NeoBar показуються поверх шпалер Windows і їх не змінюють: після виходу з NeoBar видно ваші звичайні шпалери."), cbWMode);
        k.Row(L.T("Шпалери Windows"), L.T("NeoBar нічого не показує поверх них і нічого не запускає у фоні")).When = delegate { return WMode == 0; };
        lWStatus = Note();
        k.NoteRow(lWStatus);

        // ---- галерея ----
        wallGal = p.Card(L.T("Галерея"));
        wallGal.When = delegate { return WMode >= 1; };
        SCard g = wallGal;
        wallGallery = new WallGallery();
        wallGallery.Height = SUi.S(300);
        wallGallery.SelectionChanged += delegate
        {
            if (wallGallery.Kind == WallLib.Image) wImage = wallGallery.Selected.Length > 0 ? Util.ToRelative(wallGallery.Selected) : "";
            else if (wallGallery.Kind == WallLib.Live) wVideo = wallGallery.Selected.Length > 0 ? Util.ToRelative(wallGallery.Selected) : "";
        };
        wallGallery.InfoChanged += delegate { GalNote(); };
        g.Full(wallGallery, SUi.S(300));
        lWGal = Note();
        g.NoteRow(lWGal);
        g.Row(L.T("Файли галереї"), L.T("Файли копіюються до NeoBar - оригінали потім можна видалити. Також їх можна просто перетягнути в галерею."),
            Bt(L.T("Додати…"), "g:plus", 120, delegate { AddToGallery(); }),
            Bt(L.T("Видалити"), "g:trash", 120, delegate { wallGallery.RemoveSelected(); }),
            Bt(L.T("Відкрити теку"), "g:folder", 150, delegate { wallGallery.OpenFolder(); }));
        g.Row(L.T("Додати всю теку"), L.T("Усі зображення з теки (без вкладених) копіюються до галереї слайд-шоу"),
            Bt(L.T("Тека…"), "g:folder", 120, delegate { AddFolderToGallery(); })).When = delegate { return WMode == 2; };

        // ---- показ ----
        wallSrc = p.Card(L.T("Показ"));
        wallSrc.When = delegate { return WMode >= 1; };
        SCard s = wallSrc;
        cbWFit = Cb(240, WallCfg.Fits);
        s.Row(L.T("Розміщення"), L.T("Як зображення чи відео заповнює екран"), cbWFit);

        cbWInt = Cb(200, WallCfg.IntervalNames);
        s.Row(L.T("Змінювати"), "", cbWInt).When = delegate { return WMode == 2; };
        chWShuffle = Tg();
        s.Row(L.T("Випадковий порядок"), L.T("Інакше - за назвою файлів"), chWShuffle).When = delegate { return WMode == 2; };
        cbWTrans = Cb(260, WallCfg.Transitions);
        cbWTrans.SelectedIndexChanged += delegate { WallQueue(); };
        s.Row(L.T("Перехід"), L.T("Як одне зображення змінюється іншим"), cbWTrans).When = delegate { return WMode == 2; };
        tkWDur = Tk(200, 3, 30);
        lWDur = Val(56);
        tkWDur.ValueChanged += delegate { lWDur.Text = (tkWDur.Value / 10.0).ToString("0.0") + L.T(" с"); };
        s.Row(L.T("Тривалість переходу"), "", tkWDur, lWDur).When = delegate { return WMode == 2 && WFx > 0; };
        s.Row(L.T("Наступне зображення"), L.T("Перейти одразу (коли слайд-шоу вже застосоване)"), Bt(L.T("Далі"), "g:refresh", 110, delegate { WallMgr.Next(true); lWStatus.Text = WallMgr.Status; })).When = delegate { return WMode == 2; };

        chWSound = Tg();
        s.Row(L.T("Звук відео"), "", chWSound).When = delegate { return WMode == 3; };

        // ---- монітори й економія ----
        wallEco = p.Card(L.T("Монітори й економія"));
        wallEco.When = delegate { return WMode >= 1; };
        SCard e = wallEco;
        chWAll = Tg();
        e.Row(L.T("На всіх моніторах"), L.T("Інакше - лише на основному"), chWAll);
        chWCover = Tg();
        e.Row(L.T("Пауза, коли робочий стіл закрито"), L.T("Гра, повноекранна програма чи розгорнуте вікно"), chWCover).When = delegate { return WMode >= 2; };
        chWGame = Tg();
        e.Row(L.T("Пауза під час гри"), L.T("Поки запущена гра з бібліотеки NeoBar - навіть у вікні чи на іншому моніторі. За пів хвилини паузи відеодекодер звільняється повністю"), chWGame).When = delegate { return WMode >= 2; };
        chWBat = Tg();
        bool bat = WallMgr.HasBattery;
        e.Row(L.T("Пауза від батареї"), bat ? L.T("Ноутбук без зарядки - живі шпалери й переходи зупиняються") : L.T("Недоступно: у цьому комп'ютері немає батареї"), chWBat).When = delegate { return WMode >= 2; };
        chWBat.Enabled = bat;

        return p;
    }

    void WallQueue()
    {
        if (wallGal != null) wallGal.Queue();
        if (wallSrc != null) wallSrc.Queue();
        if (wallEco != null) wallEco.Queue();
    }

    void UpdateGallery()
    {
        if (wallGallery == null) return;
        int kind = GalKind(WMode);
        if (kind < 0) return;
        wallGallery.SetKind(kind, kind == WallLib.Image ? wImage : kind == WallLib.Live ? wVideo : "");
        GalNote();
    }

    // підказка під галереєю: повідомлення про щойно зроблене або що робити
    void GalNote()
    {
        if (lWGal == null || wallGallery == null) return;
        string t = wallGallery.Info;
        if (t.Length == 0)
        {
            switch (wallGallery.Kind)
            {
                case WallLib.Image: t = L.T("Клацніть зображення, щоб обрати його, і натисніть «Застосувати»"); break;
                case WallLib.Slideshow: t = L.T("У слайд-шоу беруть участь усі зображення галереї"); break;
                case WallLib.Live: t = L.T("Клацніть відео чи GIF, щоб обрати, і натисніть «Застосувати». Відео грає по колу."); break;
            }
        }
        lWGal.Text = t;
        if (wallGal != null) wallGal.Queue();
    }

    void AddToGallery()
    {
        int kind = wallGallery.Kind;
        if (kind < 0) return;
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = WallLib.Filter(kind);
            d.Multiselect = true;
            d.Title = L.T("Додати до галереї");
            if (d.ShowDialog(this) == DialogResult.OK) wallGallery.AddFiles(d.FileNames);
        }
    }

    void AddFolderToGallery()
    {
        string dir;
        if (FolderPick.Ask(this, L.T("Теку із зображеннями для слайд-шоу"), null, out dir)) wallGallery.AddFiles(new string[] { dir });
    }

    void FillWall()
    {
        WallCfg.Load();
        bool old = loading; loading = true;
        wImage = WallCfg.Image;
        wVideo = WallCfg.Video;
        cbWMode.SelectedIndex = WallCfg.Mode;
        cbWFit.SelectedIndex = WallCfg.Fit;
        int ii = Array.IndexOf(WallCfg.Intervals, WallCfg.Interval);
        cbWInt.SelectedIndex = ii >= 0 ? ii : 2;
        chWShuffle.Checked = WallCfg.Shuffle;
        cbWTrans.SelectedIndex = WallCfg.Fx;
        tkWDur.Value = Math.Max(3, Math.Min(30, WallCfg.TransMs / 100));
        lWDur.Text = (tkWDur.Value / 10.0).ToString("0.0") + L.T(" с");
        chWSound.Checked = !WallCfg.Mute;
        chWAll.Checked = WallCfg.AllMonitors;
        chWCover.Checked = WallCfg.PauseCovered;
        chWGame.Checked = WallCfg.PauseGame;
        chWBat.Checked = WallMgr.HasBattery && WallCfg.PauseBattery;
        lWStatus.Text = WallMgr.Status;
        loading = old;
        UpdateGallery();
        WallQueue();
        wallSig = WallSignature();
    }

    // підпис усього, що впливає на шпалери: порівнюється із застосованим, щоб без змін шпалери не чіпати
    string WallSignature()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append(WMode).Append('|').Append(wImage).Append('|').Append(wVideo).Append('|')
          .Append(cbWFit.SelectedIndex).Append('|').Append(cbWInt.SelectedIndex).Append('|').Append(chWShuffle.Checked).Append('|')
          .Append(WFx).Append('|').Append(tkWDur.Value).Append('|').Append(chWSound.Checked).Append('|').Append(chWAll.Checked).Append('|')
          .Append(chWCover.Checked).Append('|').Append(chWBat.Checked).Append('|').Append(chWGame.Checked);
        if (WMode == 2)
        {
            // склад галереї слайд-шоу: додали чи видалили зображення - слайд-шоу треба перечитати
            try
            {
                string dir = WallLib.Dir(WallLib.Slideshow);
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir)) sb.Append('|').Append(Path.GetFileName(f)).Append(File.GetLastWriteTimeUtc(f).Ticks);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return sb.ToString();
    }

    // all: «Готово» (змінені шпалери з'являються обраним переходом); інакше - «Застосувати», і шпалери застосовуються лише з їхньої вкладки
    void ApplyWall(bool animate)
    {
        if (cbWMode == null) return;
        WallCfg.Mode = WMode;
        WallCfg.Image = wImage;
        WallCfg.Video = wVideo;
        WallCfg.Fit = Math.Max(0, cbWFit.SelectedIndex);
        WallCfg.Interval = WallCfg.Intervals[Math.Max(0, cbWInt.SelectedIndex)];
        WallCfg.Shuffle = chWShuffle.Checked;
        WallCfg.Fx = WFx;
        WallCfg.TransMs = tkWDur.Value * 100;
        WallCfg.Mute = !chWSound.Checked;
        WallCfg.AllMonitors = chWAll.Checked;
        WallCfg.PauseCovered = chWCover.Checked;
        WallCfg.PauseGame = chWGame.Checked;
        if (WallMgr.HasBattery) WallCfg.PauseBattery = chWBat.Checked;       // без батареї збережене значення не чіпаємо
        WallCfg.Save();
        string sig = WallSignature();
        if (sig != wallSig)
        {
            wallSig = sig;
            try { WallMgr.Apply(animate); } catch (Exception ex) { Util.ErrorLog(ex); }
        }
        lWStatus.Text = WallMgr.Status;
        // рушій пише стан за мить після запуску - оновити підпис ще раз
        Timer st = new Timer();
        st.Interval = 2500;
        st.Tick += delegate { st.Stop(); st.Dispose(); try { if (!lWStatus.IsDisposed) lWStatus.Text = WallMgr.Status; } catch (Exception skipEx) { SbLog.Skip(skipEx); } };
        st.Start();
        try { owner.Invalidate(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        WallQueue();
    }
}
