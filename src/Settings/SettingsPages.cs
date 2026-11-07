using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using System.Drawing.Text;

// Вікно налаштувань: побудова сторінок розділів (Елементи, Панель, Вигляд, Пошук, Місце й погода, Діагностика, Дані)
sealed partial class SettingsForm
{
    // ---------- «Елементи» ----------

    Control BuildItems()
    {
        SHost h = new SHost();

        // ліворуч - список із кнопками порядку й додавання
        SCard left = new SCard();
        h.Controls.Add(left);
        Label lt = new Label();
        lt.AutoSize = false; lt.Text = L.T("На панелі"); lt.Font = SUi.Head; lt.BackColor = Color.Transparent; lt.ForeColor = FormTheme.Fg; lt.TextAlign = ContentAlignment.MiddleLeft;
        left.Controls.Add(lt);
        lb = new SList();
        left.Controls.Add(lb);
        lb.ItemHeight = 30;
        lb.SelectedIndexChanged += delegate { if (!loading) LoadSel(); };
        InitListWidgets();
        bUp = Bt("", "ghost g:up", 34, delegate { MoveSel(-1); });
        bDown = Bt("", "ghost g:down", 34, delegate { MoveSel(1); });
        // «Видалити» - окремо й червоним, щоб не натиснути випадково
        bDel = Bt(L.T("Видалити"), "danger g:trash", 112, delegate { DeleteSel(); });
        left.Controls.Add(bUp); left.Controls.Add(bDown); left.Controls.Add(bDel);
        // програми, яких уже немає на диску: з'являється лише коли такі є
        bGone = Bt(L.T("Прибрати зниклі"), "ghost g:trash", 190, delegate { RemoveGone(); });
        bGone.Visible = false;
        left.Controls.Add(bGone);
        ToolTip tt = new ToolTip();
        tt.SetToolTip(bUp, L.T("Вище")); tt.SetToolTip(bDown, L.T("Нижче"));
        Label la = new Label();
        la.AutoSize = false; la.Text = L.T("Додати"); la.Font = SUi.Small; la.ForeColor = FormTheme.Sub; la.BackColor = Color.Transparent;
        left.Controls.Add(la);
        Button[] adds =
        {
            Bt(L.T("Програма"), "g:file", 100, delegate { AddFile(); }),
            Bt(L.T("Папка"), "g:folder", 100, delegate { AddFolder(); }),
            Bt(L.T("Сайт"), "g:globe", 100, delegate { AddSite(); }),
            Bt(L.T("Група"), "g:group", 100, delegate { AddEntry(MakeEntry(Kind.Header, L.T("Нова група"), "")); }),
            Bt(L.T("Ярлики…"), "g:shortcut", 100, delegate { AddDesktop(); }),
            Bt(L.T("Системні…"), "g:system", 100, delegate { AddSystem(); }),
            Bt(L.T("Роздільник"), "g:sep", 100, delegate { AddEntry(MakeEntry(Kind.Separator, "", "")); }),
        };
        tt.SetToolTip(adds[4], L.T("Ярлики з вашого робочого столу"));
        tt.SetToolTip(adds[5], L.T("Цей ПК, Кошик, особиста папка, Параметри Windows тощо"));
        foreach (Button b in adds) left.Controls.Add(b);
        left.Arrange = delegate (Rectangle r)
        {
            int p = S(14), bw = S(34), bh = S(32);
            lt.SetBounds(p + S(4), p, r.Width - p * 2 - S(200), bh);
            bDel.SetBounds(r.Width - p - bDel.Width, p, bDel.Width, bh);
            bDown.SetBounds(bDel.Left - S(6) - bw, p, bw, bh);
            bUp.SetBounds(bDown.Left - S(4) - bw, p, bw, bh);
            bGone.SetBounds(bUp.Left - S(10) - bGone.Width, p, bGone.Width, bh);
            int cols = 3, gap = S(6);
            int cw = (r.Width - p * 2 - gap * (cols - 1)) / cols;
            int rowsN = (adds.Length + cols - 1) / cols;
            int ay = r.Height - p - rowsN * bh - (rowsN - 1) * gap;
            la.SetBounds(p + S(4), ay - S(24), S(200), S(20));
            for (int i = 0; i < adds.Length; i++) adds[i].SetBounds(p + (i % cols) * (cw + gap), ay + (i / cols) * (bh + gap), cw, bh);
            lb.SetBounds(p - S(4), p + bh + S(10), r.Width - p * 2 + S(8), Math.Max(S(60), ay - S(34) - (p + bh + S(10))));
        };

        // праворуч - властивості вибраного й підказки
        SPage right = new SPage();
        h.Controls.Add(right);
        propCard = right.Card(L.T("Властивості"));
        SCard pc = propCard;
        SRow r0 = pc.Row(L.T("Нічого не вибрано"), L.T("Оберіть рядок у списку ліворуч або додайте новий елемент кнопками під списком."));
        r0.When = delegate { return pk == 0; };

        pic = new PictureBox();
        pic.Size = new Size(S(56), S(56));
        pic.SizeMode = PictureBoxSizeMode.CenterImage;
        SRow rp = pc.Row("", "", pic);
        rp.TitleFn = delegate { return tbName.Text.Trim().Length > 0 ? tbName.Text.Trim() : L.T("Без назви"); };
        rp.DescFn = delegate { string t = tbTarget.Text.Trim(); return t.Length > 0 ? t : L.T("Ціль не вказано"); };
        rp.When = delegate { return pk == 1; };

        tbName = Tb();
        tbName.TextChanged += delegate { OnNameChanged(); if (pk == 1) propCard.Invalidate(); };
        pc.Stacked(L.T("Назва"), "", tbName).When = delegate { return pk == 1 || pk == 2; };

        tbTarget = Tb();
        tbTarget.TextChanged += delegate { OnTargetChanged(); if (pk == 1) propCard.Invalidate(); };
        tbTarget.Leave += delegate
        {
            string n = Util.Collapse(tbTarget.Text);
            if (n != tbTarget.Text) tbTarget.Text = n;
        };
        bBrowseFile = Bt(L.T("Файл…"), "g:file", 104, delegate { BrowseTargetFile(); });
        bBrowseFolder = Bt(L.T("Папка…"), "g:folder", 108, delegate { BrowseTargetFolder(); });
        pc.Stacked(L.T("Ціль"), L.T("Файл, папка або посилання. Шлях скорочується автоматично (наприклад, до %USERPROFILE%)."), tbTarget, bBrowseFile, bBrowseFolder).When = delegate { return pk == 1; };

        cbIconMode = Cb(240, L.T("Авто (з файлу чи папки)"), L.T("Тематична"), L.T("Власний файл"));
        cbIconMode.SelectedIndexChanged += delegate { OnIconModeChanged(); };
        pc.Row(L.T("Іконка"), L.T("Звідки взяти іконку елемента"), cbIconMode).When = delegate { return pk == 1; };

        cbThemeIcon = Cb(240);
        foreach (string[] it in IconFactory.Catalog) cbThemeIcon.Items.Add(it[1]);
        cbThemeIcon.SelectedIndexChanged += delegate { OnThemeIconChanged(); };
        SRow rti = pc.Row(L.T("Тематична іконка"), L.T("Малюється у стилі поточної теми"), cbThemeIcon);
        rti.Indent = true;
        rti.When = delegate { return pk == 1 && cbIconMode.SelectedIndex == 1; };

        tbIconFile = Tb();
        tbIconFile.TextChanged += delegate { OnIconFileChanged(); };
        tbIconFile.Leave += delegate
        {
            string n = NormIcon(tbIconFile.Text);
            if (n != tbIconFile.Text) tbIconFile.Text = n;
        };
        bBrowseIcon = Bt(L.T("Огляд…"), "g:open", 108, delegate { BrowseIcon(); });
        bFavicon = Bt(L.T("З сайту"), "g:globe", 108, delegate { DoFavicon(); });
        SRow rif = pc.Stacked(L.T("Файл іконки"), L.T(".ico, .png, .jpg, .bmp, .exe або .dll; «З сайту» один раз завантажує іконку за адресою з поля «Ціль»"), tbIconFile, bBrowseIcon, bFavicon);
        rif.Indent = true;
        rif.When = delegate { return pk == 1 && cbIconMode.SelectedIndex == 2; };
        lStatus = Note();
        pc.NoteRow(lStatus).When = delegate { return pk == 1; };

        tbTags = Tb();
        tbTags.TextChanged += delegate { OnTagsChanged(); };
        pc.Stacked(L.T("Теги для пошуку"), L.T("Через кому, наприклад: ігри, steam"), tbTags).When = delegate { return pk == 1; };

        bColor = Bt("", "", 120, delegate { PickGroupColor(); });
        bColorReset = Bt(L.T("За темою"), "g:reset", 120, delegate { ResetGroupColor(); });
        pc.Row(L.T("Колір групи"), L.T("Колір заголовка й лінії групи на панелі"), bColor, bColorReset).When = delegate { return pk == 2; };
        cbGMode = Cb(300, L.T("Як у розділі «Вигляд»"), L.T("Розгорнута (усі елементи видно)"), L.T("Компактні плитки"));
        cbGMode.SelectedIndexChanged += delegate { OnGModeChanged(); };
        pc.Row(L.T("Вигляд групи"), L.T("Розгорнута показує елементи одразу; компактна - плитка 2×2 з мініатюрами, що відкривається по кліку"), cbGMode).When = delegate { return pk == 2; };

        SRow rw = pc.Row("", "");
        rw.TitleFn = delegate { return wTitle; };
        rw.DescFn = delegate { return wDesc; };
        rw.When = delegate { return pk == 3; };

        SCard help = right.Card(L.T("Як це працює"));
        help.Row(L.T("Групи"), L.T("Заголовок і всі елементи під ним до наступної групи, роздільника чи віджета."));
        help.Row(L.T("Віджети"), L.T("Завжди вгорі панелі. «−» у списку прибирає віджет, «+» додає; порядок між віджетами - кнопками ▲ ▼ або перетягуванням. Після віджетів роздільник ставиться сам."));
        help.Row(L.T("Швидке додавання"), L.T("Перетягніть ярлик, папку чи посилання просто на панель. «Системні» - Цей ПК, Кошик, особиста папка тощо; «Ярлики» - з вашого робочого столу."));

        h.Arrange = delegate (Rectangle r)
        {
            int lw = Math.Max(S(320), Math.Min(S(400), r.Width * 40 / 100));
            left.SetBounds(0, 0, lw, r.Height);
            left.Arrange(new Rectangle(0, 0, lw, r.Height));
            right.SetBounds(lw + S(16), 0, r.Width - lw - S(16), r.Height);
        };
        return h;
    }

    // ---------- «Панель» ----------

    Control BuildPanel()
    {
        SPage p = new SPage();
        SCard k = p.Card(L.T("Розташування"));
        cbSide = Cb(220, L.T("Ліворуч"), L.T("Праворуч"), L.T("Угорі"), L.T("Внизу"));
        cbSide.SelectedIndexChanged += delegate { SyncCompact(); };
        k.Row(L.T("Край екрана"), L.T("Біля якого краю стоїть панель"), cbSide);
        cbMonitor = Cb(320);
        int n = 1;
        foreach (Screen sc in Screen.AllScreens)
        {
            cbMonitor.Items.Add(L.T("Екран ") + n + " (" + sc.Bounds.Width + "×" + sc.Bounds.Height + ")" + (sc.Primary ? L.T(" - основний") : ""));
            monIds.Add(sc.Primary ? "" : sc.DeviceName);   // "" - завжди основний монітор
            n++;
        }
        k.Row(L.T("Монітор"), "", cbMonitor);
        nW = Nm(96, 56, 240);
        Bound(nW, L.T("Ширина панелі"));
        k.Row(L.T("Ширина панелі"), L.T("Для панелі ліворуч чи праворуч, від 56 до 240 px"), nW);

        k = p.Card(L.T("Поведінка"));
        cbMode = Cb(220, L.T("Закріплена"), L.T("Автосховок"));
        cbMode.SelectedIndexChanged += delegate { SyncCompact(); };
        k.Row(L.T("Режим"), L.T("Закріплена: вікна підлаштовуються під панель. Автосховок: панель лежить поверх вікон і з'являється, коли підвести мишу до краю екрана. Те саме перемикає пункт у вікні профілю."), cbMode);
        chCompact = Tg();
        SRow rc = k.Row(L.T("Компактна панель"), L.T("За вмістом, по центру, із заокругленням; лише для панелі вгорі чи внизу в режимі автосховку"), chCompact);
        rc.Indent = true;
        nGap = Nm(96, 0, 300);
        SRow rg = k.Row(L.T("Відступ від кутів екрана"), L.T("Для автосховку, px"), nGap);
        rg.Indent = true;
        chFs = Tg();
        k.Row(L.T("Ігри й повноекранні програми"), L.T("Ховати панель, вимикати пошук і призупиняти решту - працює лише ігровий оверлей"), chFs);

        k = p.Card(L.T("Температури в треї"));
        string[] tn = { L.T("Температура ЦП"), L.T("Температура ГП"), L.T("Температура ГП: hotspot") };
        string[] td = { L.T("Окремий значок біля годинника Windows із числом у градусах. Для температури процесора часто потрібні права адміністратора."),
                        L.T("Окремий значок біля годинника Windows із числом у градусах."),
                        L.T("Найгарячіша точка відеочіпа; не всі відеокарти її показують (тоді на значку буде риска).") };
        for (int i = 0; i < 3; i++)
        {
            int ti = i;
            chTray[i] = Tg();
            bTrayCol[i] = Bt(L.T("Колір"), "", 110, delegate { PickTrayColor(ti); });
            k.Row(tn[i], td[i], chTray[i], bTrayCol[i]);
        }

        k = p.Card(L.T("Запуск"));
        chAuto = Tg();
        k.Row(L.T("Автозапуск разом з Windows"), L.T("NeoBar заходить в останній профіль (якщо в нього є PIN - спитає його)"), chAuto);
        chAdmin = Tg();
        k.Row(L.T("Завжди від імені адміністратора"), L.T("Без запиту UAC під час запуску (Windows спитає дозвіл один раз). Потрібно для FPS в іграх, температури процесора й обслуговування дисків."), chAdmin);
        return p;
    }

    // ---------- «Вигляд» ----------

    Control BuildView()
    {
        SPage p = new SPage();
        SCard k = p.Card(L.T("Мова") + (L.Code == "en-US" ? "" : " · Language"));
        LangInfo cur = L.Find(L.Code) ?? L.All()[0];
        SBtn bLang = (SBtn)Bt(Flags.Title(cur), "", 240, null);
        bLang.Flag = cur;
        bLang.Click += delegate
        {
            Flags.Menu(bLang, new Point(0, bLang.Height), FormTheme.Bg, FormTheme.Fg, FormTheme.Acc, SUi.S(10), delegate (bool changed)
            {
                if (!changed) return;
                LangInfo nl = L.Find(L.Saved());
                if (nl != null) { bLang.Flag = nl; bLang.Text = Flags.Title(nl); bLang.Invalidate(); }
                if (SbDialog.Show(owner, this, L.T("Мова"), L.T("Нова мова з'явиться після перезапуску NeoBar. Перезапустити зараз?"), L.T("Перезапустити"), L.T("Пізніше")) == 0)
                    if (Prof.Restart()) owner.Quit();
            });
        };
        k.Row(L.T("Мова інтерфейсу"), L.T("Перекладів, яких бракує у вибраній мові, показуються англійською"), bLang);
        k.Row(L.T("Свій переклад"), L.T("Шаблон - файл з усіма текстами й англійськими підказками. Перекладіть його, збережіть у теці lang як код_мови.lang (наприклад nl.lang) - після перезапуску мова з'явиться в списку."),
            Bt(L.T("Створити шаблон"), "g:file", 190, delegate
            {
                try
                {
                    string f = L.WriteTemplate();
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f + "\"");
                }
                catch (Exception ex) { SbDialog.Show(owner, this, L.T("Шаблон"), ex.Message, "OK"); }
            }),
            Bt(L.T("Тека мов"), "g:folder", 140, delegate { OpenDir(L.Dir, true); }));

        k = p.Card(L.T("Тема"));
        cbTheme = Cb(240);
        foreach (Theme th in Themes.All) cbTheme.Items.Add(th.Title);
        cbTheme.SelectedIndexChanged += delegate { OnThemeChanged(); };
        k.Row(L.T("Тема оформлення"), L.T("Кольори, форма іконок і вигляд вікон NeoBar (зокрема цього)"), cbTheme);
        chBlur = Tg();
        chBlur.CheckedChanged += delegate { OnBlurChanged(); };
        k.Row(L.T("Скло"), L.T("Розмите тло зі шпалер робочого столу; лише для теми «Власна»"), chBlur);
        bCopy = Bt(L.T("Взяти поточну за основу"), "g:palette", 230, delegate { ToCustom(); UpdateSwatches(); });
        k.Row(L.T("Власна тема"), L.T("Готові теми змінити не можна - візьміть поточну за основу й налаштуйте кольори нижче"), bCopy);
        string[] names = { L.T("Фон"), L.T("Підсвітка"), L.T("Текст"), L.T("Акцент") };
        string[] descs = { L.T("Колір і прозорість фону панелі (0% - повністю прозорий)"), L.T("Тло елемента під мишею"), L.T("Підписи й заголовки"), L.T("Виділення, лінії груп, індикатори") };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            bCol[i] = Bt(names[i], "", 110, delegate { PickColor(idx); });
            tbA[i] = Tk(200, 0, 100);
            tbA[i].SmallChange = 5;
            tbA[i].LargeChange = 10;
            tbA[i].ValueChanged += delegate { OnAlphaChanged(idx); };
            lA[i] = Val(52);
            lA[i].Text = "100%";
            SRow rr = k.Row(names[i], descs[i], bCol[i], tbA[i], lA[i]);
            rr.Indent = true;
        }

        k = p.Card(L.T("Іконки й підписи"));
        nIcon = Nm(96, 24, 96);
        Bound(nIcon, L.T("Розмір іконок"));
        k.Row(L.T("Розмір іконок"), L.T("Від 24 до 96 px"), nIcon);
        cbLabelPos = Cb(220, L.T("Під іконкою"), L.T("Над іконкою"), L.T("Приховати"));
        k.Row(L.T("Підписи елементів"), "", cbLabelPos);
        cbGroups = Cb(220, L.T("Розгорнуті"), L.T("Компактні плитки"));
        k.Row(L.T("Групи"), L.T("Як показувати групи за замовчуванням; кожну можна змінити в розділі «Елементи»"), cbGroups);
        cbFx = Cb(220, L.T("Немає"), L.T("Збільшення"), L.T("Підйом"), L.T("Свічення"), L.T("Збільшення + свічення"));
        k.Row(L.T("Ефект при наведенні"), "", cbFx);
        chAnim = Tg();
        k.Row(L.T("Плавні анімації"), L.T("Висування панелі, поява й зникання"), chAnim);

        k = p.Card(L.T("Шрифт підписів"));
        cbFont = Cb(260);
        cbFont.Items.Add(L.T("(за темою)"));
        try
        {
            using (InstalledFontCollection ifc = new InstalledFontCollection())
                foreach (FontFamily ff in ifc.Families) cbFont.Items.Add(ff.Name);
        }
        catch { }
        k.Row(L.T("Шрифт"), "", cbFont);
        cbFontStyle = Cb(180, L.T("Звичайний"), L.T("Жирний"), L.T("Курсив"), L.T("Жирний курсив"));
        k.Row(L.T("Стиль"), "", cbFontStyle);
        nFont = Nm(96, 6, 20);
        nFont.DecimalPlaces = 1; nFont.Increment = 0.5m;
        k.Row(L.T("Розмір"), L.T("У пунктах, від 6 до 20"), nFont);
        return p;
    }

    // ---------- «Пошук» ----------

    Control BuildSearch()
    {
        SPage p = new SPage();
        SCard k = p.Card(L.T("Пошук"));
        chSearch = Tg();
        k.Row(L.T("Поле пошуку на панелі"), L.T("Угорі панелі, під профілем"), chSearch);
        chHot = Tg();
        hkSearch = new HotkeyBox();
        hkSearch.BorderStyle = BorderStyle.None;
        k.Want(hkSearch, S(190));
        lHkSearch = Note();
        k.WithNote(k.Row(L.T("Гаряча клавіша пошуку"), L.T("Відкриває пошук з будь-якої програми"), hkSearch, chHot), lHkSearch);
        chHot.CheckedChanged += delegate { hkSearch.Enabled = chHot.Checked; };
        hkSearch.TextChanged += delegate
        {
            if (loading) return;
            // своя комбінація - лише якщо не зайнята системою чи іншою програмою
            string why = HotkeyCheck.Problem(hkSearch.Value, owner.SearchKeyNow);
            if ((hkSearch.Value & Keys.KeyCode) == Keys.None) why = L.T("Порожньо - гаряча клавіша вимкнеться.");
            if (why.Length > 0 && (hkSearch.Value & Keys.KeyCode) != Keys.None)
            {
                lHkSearch.ForeColor = FormTheme.Bad;
                lHkSearch.Text = OverlayCfg.KeyText(hkSearch.Value) + " - " + why + L.T(" Спробуйте Alt+Shift+літеру або Ctrl+Alt+літеру.");
                loading = true; hkSearch.SetValue(searchKeyOk); loading = false;
                return;
            }
            searchKeyOk = hkSearch.Value;
            lHkSearch.ForeColor = FormTheme.Sub;
            lHkSearch.Text = why.Length > 0 ? why : L.T("Вільна - буде збережена після «Застосувати».");
        };
        chFiles = Tg();
        k.Row(L.T("Розширений пошук"), L.T("Вкладка «Комп'ютер» шукає всі файли й папки (індекс NTFS з правами адміністратора, інакше пошук Windows)"), chFiles);
        chSuggest = Tg();
        k.Row(L.T("Рекомендації"), L.T("Пропонувати програми й ігри, яких ще немає на панелі"), chSuggest);
        return p;
    }

    // ---------- «Місце й погода» ----------

    Control BuildPlace()
    {
        SHost h = new SHost();
        SPage left = new SPage();
        h.Controls.Add(left);

        SCard k = left.Card(L.T("Розташування"));
        // згода на визначення розташування: без неї немає автоматичного місця й часового поясу
        chLocAllow = Tg();
        chLocAllow.CheckedChanged += delegate { if (!loading) SyncLocAllow(); };
        k.Row(L.T("Визначати моє розташування"), L.T("Служба розташування Windows або IP-адреса. Без дозволу місто задається лише вручну, а автоматичний часовий пояс вимикається."), chLocAllow);
        rbAuto = new SRadio(); rbAuto.Text = L.T("Автоматично"); rbAuto.AutoSize = false; rbAuto.Size = new Size(S(126), S(26));
        rbManual = new SRadio(); rbManual.Text = L.T("Вручну"); rbManual.AutoSize = false; rbManual.Size = new Size(S(96), S(26));
        rbAuto.CheckedChanged += delegate { if (!loading) SyncLoc(); };
        rbManual.CheckedChanged += delegate { if (!loading) SyncLoc(); };
        k.Row(L.T("Спосіб"), L.T("Автоматично: спершу служба розташування Windows, інакше за IP (точність до міста). Вручну: пошук міста або клік на карті."), rbAuto, rbManual);
        bDetect = Bt(L.T("Визначити зараз"), "g:locate", 170, delegate { DetectNow(); });
        k.Row(L.T("Перевірити"), L.T("Одразу визначити місце й показати його на карті"), bDetect);

        cityCard = left.Card(L.T("Місто"));
        tbCity = Tb();
        lCity = Note();
        cityCard.WithNote(cityCard.Stacked(L.T("Пошук міста"), L.T("Введіть 3+ літери - з'являться підказки; Enter вибирає"), tbCity, Bt(L.T("Знайти"), "g:search", 110, delegate { LookupCity(); })), lCity);
        cityTimer = new System.Windows.Forms.Timer();
        cityTimer.Interval = 400;
        cityTimer.Tick += CityTick;
        tbCity.TextChanged += delegate { cityTimer.Stop(); cityTimer.Start(); };
        tbCity.KeyDown += CityKey;
        lbCitySug = new SList();
        lbCitySug.ItemHeight = 28;
        lbCitySug.BackColor = FormTheme.Input;
        lbCitySug.Click += delegate { PickCitySug(); };
        lbCitySug.KeyDown += CityKey;
        SRow rs = cityCard.Full(lbCitySug, S(160));
        rs.When = delegate { return citySugOpen; };

        BuildClock(left);
        BuildSources(left);

        // праворуч - карта на всю висоту
        SCard mc = new SCard();
        h.Controls.Add(mc);
        Label lm = new Label(); lm.AutoSize = false; lm.Text = L.T("Карта"); lm.Font = SUi.Head; lm.BackColor = Color.Transparent; lm.ForeColor = FormTheme.Fg;
        mc.Controls.Add(lm);
        map = new MapView();
        mc.Controls.Add(map);
        map.Picked += delegate { MapPicked(); };
        lMap = Note(); lMap.BackColor = Color.Transparent; lMap.Font = SUi.Base; lMap.ForeColor = FormTheme.Fg;
        mc.Controls.Add(lMap);
        mc.Arrange = delegate (Rectangle r)
        {
            int p = S(16);
            lm.SetBounds(p, p - S(2), r.Width - p * 2, S(24));
            int my = p + S(32);
            map.SetBounds(p, my, r.Width - p * 2, Math.Max(S(80), r.Height - my - p - S(40)));
            lMap.SetBounds(p, map.Bottom + S(6), r.Width - p * 2, S(34));
        };
        h.Arrange = delegate (Rectangle r)
        {
            int lw = Math.Max(S(380), Math.Min(S(470), r.Width * 45 / 100));
            left.SetBounds(0, 0, lw, r.Height);
            mc.SetBounds(lw + S(14), 0, r.Width - lw - S(14), r.Height);
            mc.Arrange(new Rectangle(0, 0, mc.Width, mc.Height));
        };
        return h;
    }

    void ShowCache()
    {
        long n = NetCache.Size();
        lCache.Text = L.T("Зараз у кеші: ") + (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" МБ");
    }

    void SyncLocAllow()
    {
        bool ok = chLocAllow.Checked;
        c.LocAllow = ok;
        rbAuto.Enabled = ok;
        bDetect.Enabled = ok;
        if (!ok && rbAuto.Checked) { loading = true; rbManual.Checked = true; loading = false; SyncLoc(); }
    }

    void SyncLoc()
    {
        bool man = rbManual.Checked;
        tbCity.Enabled = man;
        c.LocMode = man ? "manual" : "auto";
        if (!man) lMap.Text = L.T("Місце визначається автоматично; карта лише показує його.");
        else ShowMapText();
        cityCard.Invalidate();
    }

    void ShowMapText()
    {
        if (double.IsNaN(c.Lat)) { lMap.Text = L.T("Клацніть на карті, щоб вибрати місце."); return; }
        lMap.Text = (c.CityName.Length > 0 ? c.CityName + "  ·  " : "") + c.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + c.Lon.ToString("0.0000", CultureInfo.InvariantCulture) +
            (c.Country.Length > 0 ? "  ·  " + Holidays.CountryName(c.Country) : "");
    }

    void MapPicked()
    {
        double lat = map.MarkLat, lon = map.MarkLon;
        loading = true;
        rbManual.Checked = true;
        loading = false;
        SyncLoc();
        c.Lat = lat; c.Lon = lon;
        lMap.Text = L.T("Визначаю назву місця…");
        Util.Bg(delegate
        {
            string nm, cc;
            bool ok = Geo.Reverse(lat, lon, out nm, out cc);
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (c.Lat != lat || c.Lon != lon) return;
                    string name = ok && nm.Length > 0 ? nm : lat.ToString("0.00", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.00", CultureInfo.InvariantCulture);
                    c.City = name; c.CityName = name;
                    if (ok && cc.Length == 2) c.Country = cc.ToUpperInvariant();
                    cityLast = name;
                    tbCity.Text = name;
                    lCity.Text = L.T("Обрано на карті: ") + name;
                    ShowMapText();
                }));
            }
            catch { }
        });
    }


    void DetectNow()
    {
        lMap.Text = L.T("Визначаю місце…");
        Util.Bg(delegate
        {
            double lat = double.NaN, lon = double.NaN;
            string city = "", cc = "", tz = "", src = L.T("служба розташування Windows");
            bool ok = SysCheck.LocationOff().Length == 0 && Geo.WindowsLocation(out lat, out lon);
            string gsrc = "windows";
            if (!ok) { ok = Geo.IpLocation(out lat, out lon, out city, out cc, out tz); src = L.T("за IP-адресою - місто провайдера, може бути неточним"); gsrc = "ip"; }
            if (ok)
            {
                string nm, c2;
                if (Geo.Reverse(lat, lon, out nm, out c2)) { if (nm.Length > 0) city = nm; if (c2.Length > 0) cc = c2; }
            }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!ok) { lMap.Text = L.T("Не вдалося визначити місце (немає доступу до розташування й інтернету)."); return; }
                    Geo.Source = gsrc;
                    c.Lat = Math.Round(lat, 4); c.Lon = Math.Round(lon, 4);
                    if (city.Length > 0) { c.City = city; c.CityName = city; cityLast = city; tbCity.Text = city; }
                    if (cc.Length == 2) c.Country = cc.ToUpperInvariant();
                    map.SetMark(c.Lat, c.Lon, true);
                    ShowMapText();
                    lMap.Text += "  (" + src + ")";
                }));
            }
            catch { }
        });
    }

    // ---------- «Діагностика»: перевірка системи, вікно привітання, журнали ----------

    DiagList diag;
    bool diagBusy;
    readonly CheckBox[] logChk = new CheckBox[3];

    Control BuildDiag()
    {
        SHost h = new SHost();
        SCard dc = new SCard();
        h.Controls.Add(dc);
        Label lt = new Label(); lt.AutoSize = false; lt.Text = L.T("Перевірка системи"); lt.Font = SUi.Head; lt.BackColor = Color.Transparent; lt.ForeColor = FormTheme.Fg;
        Label ls = Note(); ls.Text = L.T("Що потрібно можливостям NeoBar, чи воно є на цьому комп'ютері і що зробити. Перевіряється, коли відкриваєте розділ."); ls.BackColor = Color.Transparent;
        Button br = Bt(L.T("Перевірити ще раз"), "g:refresh", 180, delegate { RunDiag(); });
        diag = new DiagList();
        foreach (Control x in new Control[] { lt, ls, br, diag }) dc.Controls.Add(x);
        dc.Arrange = delegate (Rectangle r)
        {
            int p = S(16);
            br.SetBounds(r.Width - p - br.Width, p, br.Width, S(32));
            lt.SetBounds(p, p - S(2), br.Left - p * 2, S(24));
            ls.SetBounds(p, p + S(22), br.Left - p * 2, S(34));
            diag.SetBounds(S(6), p + S(62), r.Width - S(12), r.Height - p - S(62) - S(6));
        };

        SPage right = new SPage();
        h.Controls.Add(right);
        SCard k = right.Card(L.T("Оновлення"));
        chUpd = Tg();
        k.Row(L.T("Перевіряти оновлення"), L.T("Під час запуску NeoBar дивиться на GitHub, чи вийшла новіша версія, і пропонує оновитися. Завантажує й встановлює лише після вашої згоди."), chUpd);
        Button bu = null;
        bu = Bt(L.T("Перевірити зараз"), "g:refresh", 170, delegate
        {
            bu.Enabled = false;
            Updater.CheckNow(delegate (string msg, string tag)
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        bu.Enabled = true;
                        if (tag != null) Updater.OfferFromSettings(owner, this, tag);
                        else SbDialog.Show(owner, this, L.T("Оновлення"), msg, L.T("Гаразд"));
                    }));
                }
                catch { }
            });
        });
        k.Row(L.T("Перевірити оновлення"), L.F("Поточна версія: {0}", Updater.CurrentText), bu);

        k = right.Card(L.T("Перший запуск"));
        chWelcome = Tg();
        k.Row(L.T("Вікно привітання"), L.T("Під час запуску, до появи панелі: перевірка системи й поради"), chWelcome);
        k.Row(L.T("Швидке налаштування"), L.T("Край екрана, режим, тема, віджети й пошук - в одному вікні (як під час першого запуску)"), Bt(L.T("Відкрити"), "g:view", 130, delegate { OpenQuick(); }));

        k = right.Card(L.T("Журнали для пошуку проблем"));
        for (int i = 0; i < 3; i++)
        {
            int gi = i;
            logChk[i] = Tg();
            logChk[i].Checked = SbLog.GroupOn(i);
            logChk[i].CheckedChanged += delegate { SbLog.SetGroup(gi, logChk[gi].Checked); };
            SRow lr = k.Row(SbLog.GroupTitles[i], "", logChk[i]);
            lr.DescFn = delegate { return SbLog.GroupDescs[gi] + "  ·  " + SizeText(SbLog.GroupSize(gi)); };
        }
        k.Row(L.T("Тека журналів"), L.T("Увімкнений журнал пише у файли до 2 МБ, вимкнений нічого не пише й не сповільнює програму"),
            Bt(L.T("Відкрити"), "g:open", 110, delegate { OpenDir(SbLog.Dir, true); }),
            Bt(L.T("Очистити"), "g:trash", 110, delegate { SbLog.Clear(); k.Invalidate(); }));
        SRow re = k.Row(L.T("Журнал помилок"), "",
            Bt("Відкрити", "g:open", 110, delegate { OpenErrorLog(); }),
            Bt(L.T("Очистити"), "g:trash", 110, delegate { try { File.Delete(ErrPath); } catch { } k.Invalidate(); RunDiag(); }));
        re.DescFn = delegate { long n = 0; try { if (File.Exists(ErrPath)) n = new FileInfo(ErrPath).Length; } catch { } return L.T("Записується завжди. Надішліть його разом з описом проблеми  ·  ") + SizeText(n); };

        h.Arrange = delegate (Rectangle r)
        {
            int rw = Math.Max(S(380), Math.Min(S(470), r.Width * 44 / 100));
            dc.SetBounds(0, 0, r.Width - rw - S(14), r.Height);
            dc.Arrange(new Rectangle(0, 0, dc.Width, dc.Height));
            right.SetBounds(r.Width - rw, 0, rw, r.Height);
        };
        return h;
    }

    static string ErrPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "neobar-error.log"); } }

    static string SizeText(long n)
    {
        if (n <= 0) return L.T("порожньо");
        if (n < 1024) return n + L.T(" Б");
        if (n < 1048576) return (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" КБ");
        return (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" МБ");
    }

    static void OpenDir(string d, bool create)
    {
        try { if (create) Directory.CreateDirectory(d); using (Process.Start("explorer.exe", "\"" + d + "\"")) { } } catch { }
    }

    void OpenErrorLog()
    {
        if (!File.Exists(ErrPath)) { SbDialog.Show(owner, this, L.T("Журнал помилок"), L.T("Помилок не записано - журнал порожній."), L.T("Гаразд")); return; }
        try { using (Process.Start("notepad.exe", "\"" + ErrPath + "\"")) { } } catch { }
    }

    void OpenQuick()
    {
        try
        {
            using (QuickSetup q = new QuickSetup(c, list, chAuto.Checked, cbGenOv == null || cbGenOv.Checked, false))
            {
                q.ShowDialog(this);
                if (!q.Done) return;
                q.ApplyTo(c, list);
                loading = true;
                int si = Array.IndexOf(sideIds, c.Side);
                cbSide.SelectedIndex = si >= 0 ? si : 1;
                cbMode.SelectedIndex = c.Mode == "autohide" ? 1 : 0;
                cbTheme.SelectedIndex = ThemeIndex(c.ThemeId);
                chSearch.Checked = c.ShowSearch;
                if (q.AutoKnown) chAuto.Checked = q.Auto;
                if (cbGenOv != null) cbGenOv.Checked = q.Overlay;
                // місце для погоди: елементи вкладки «Місце й погода» беруть вибір зі швидкого налаштування
                chLocAllow.Checked = c.LocAllow;
                rbAuto.Enabled = c.LocAllow;
                bDetect.Enabled = c.LocAllow;
                rbAuto.Checked = c.LocMode == "auto" && c.LocAllow;
                rbManual.Checked = !rbAuto.Checked;
                tbCity.Enabled = c.LocMode != "auto";
                loading = false;
                UpdateSwatches();
                SyncCompact();
                RefreshList(0);
                lData.Text = "";
                SbDialog.Show(owner, this, L.T("Швидке налаштування"), L.T("Вибране перенесено в налаштування. Натисніть «Застосувати» або «Готово», щоб панель змінилася."), L.T("Гаразд"));
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // перевірки у фоні (WMI може відповідати кілька секунд)
    void RunDiag()
    {
        if (diag == null || diagBusy) return;
        diagBusy = true;
        diag.Empty = L.T("Перевіряю…");
        diag.SetItems(null);
        Util.Bg(delegate
        {
            List<CheckItem> l = null;
            try { l = SysCheck.Collect(owner, this, true, delegate { RunDiag(); }); } catch { }
            try { BeginInvoke(new Action(delegate { diagBusy = false; if (!IsDisposed) diag.SetItems(l); })); } catch { diagBusy = false; }
        });
    }

    // ---------- «Дані» ----------

    Control BuildData()
    {
        SPage p = new SPage();
        SCard k = p.Card(L.T("Налаштування профілю «") + (Prof.Cur != null ? Prof.Cur.Name : "—") + L.Q("»"));
        k.Row(L.T("Експорт"), L.T("Файл .nbpack з елементами, групами, віджетами, темою й іконками цього профілю"), Bt(L.T("Експортувати…"), "g:export", 170, delegate { DoExport(); }));
        k.Row(L.T("Імпорт"), L.T("Замінює налаштування профілю; стара копія лишається як neobar.ini.bak"), Bt(L.T("Імпортувати…"), "g:import", 170, delegate { DoImport(); }));
        k.Row(L.T("Тека профілю"), Prof.Dir, Bt(L.T("Відкрити"), "g:open", 170, delegate { OpenDir(Prof.Dir, false); }));
        k.Row(L.T("Скинути до початкових"), L.T("Усі налаштування й елементи профілю - як після встановлення (копія - neobar.ini.bak)"), Bt(L.T("Скинути…"), "danger g:reset", 170, delegate { DoReset(); }));
        lData = Note();
        k.NoteRow(lData);

        k = p.Card(L.T("Кеш з інтернету"));
        lCache = Note();
        k.WithNote(k.Row(L.T("Кеш"), L.T("Погода, свята, тайли карти, іконки сайтів і місце за IP; без інтернету показується збережене"),
            Bt(L.T("Очистити"), "g:trash", 170, delegate { NetCache.Clear(); ShowCache(); })), lCache);
        return p;
    }
}
