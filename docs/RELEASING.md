# Як опублікувати NeoBar і випускати версії

Покрокова інструкція для автора: GitHub, підпис, реліз, встановлення.

## 1. Що встановити (один раз)

| Що | Навіщо |
|---|---|
| [GitHub Desktop](https://desktop.github.com/) (або [Git for Windows](https://git-scm.com/download/win)) | завантажувати код на GitHub |
| [GitHub CLI](https://cli.github.com/) (`gh`), необов'язково | створювати релізи однією командою |
| [Inno Setup 6](https://jrsoftware.org/isdl.php) | збирати встановлювач |
| [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/) - лише компонент **Windows SDK Signing Tools for Desktop Apps** | `signtool.exe` для підпису |

На GitHub увімкніть двофакторний вхід (Settings → Password and authentication).

## 2. Перше завантаження коду

1. Перевірте, що в теці немає `sign-config.bat` з вашими даними (він і так ігнорується - `.gitignore`), а `dist` не потрібна.
2. На github.com: **New repository** → назва `NeoBar` → **Public** → **не** додавайте README, .gitignore і License (вони вже є) → Create.
3. GitHub Desktop: **File → Add local repository** → тека NeoBar → «create a repository» → Commit to main → **Publish repository**
   (зніміть «Keep this code private»).

   Або командами (у теці NeoBar):
   ```
   git init -b main
   git add .
   git commit -m "NeoBar 1.0.0"
   git remote add origin https://github.com/ЛОГІН/NeoBar.git
   git push -u origin main
   ```
4. Вкладка **Actions** у репозиторії: збірка має стати зеленою (exe і бібліотеки компілюються на серверах GitHub).

## 3. Налаштування репозиторію

- **About** (шестерня праворуч на головній сторінці): опис і теми - `windows`, `launcher`, `sidebar`, `game-overlay`, `fps-counter`.
- **Settings → Security → Private vulnerability reporting** → Enable (так повідомляють про вразливості не публічно - див. `SECURITY.md`).
- **Settings → Branches → Add rule** для `main` (необов'язково): «Require a pull request» і «Require status checks» (Build) -
  тоді ніщо не потрапить у `main` без вашого схвалення й успішної збірки.

## 4. Сертифікат для підпису

Для фізичної особи з відкритим проєктом підходить **Certum Open Source Code Signing** (Польща, видається поза США).
Спершу опублікуйте код (крок 2) - це сертифікат саме для розробників відкритого ПЗ.

- **У хмарі (SimplySign)**, ~€49: без картки; підписуєте через програму SimplySign Desktop, вхід - кодом із телефона.
- **З карткою й рідером**, ~€69: сертифікат на смарт-картці.

Ціни й наявність на сайті Certum міняються - перевірте перед замовленням. Буде перевірка особи за документами;
у сертифікаті стоятиме «Open Source Developer, ваше ім'я».

Після активації:
1. Запустіть SimplySign Desktop і увійдіть (або вставте картку) - сертифікат з'явиться в `certmgr.msc` → Особисті → Сертифікати.
2. Відкрийте сертифікат → Склад → **Відбиток** (Thumbprint), скопіюйте.
3. Скопіюйте `sign-config.example.bat` у `sign-config.bat` і впишіть відбиток у `SIGN_THUMBPRINT`.

### Варіант без купівлі: власний (самопідписаний) сертифікат

Запустіть **`make-cert.bat`** - він створить сертифікат на вашому комп'ютері, збереже відбиток у `sign-config.bat`,
і `release.bat` почне підписувати ним усе. Закритий ключ лишається в Windows і не експортується.

Чесно про обмеження: Windows не довіряє такому сертифікату **на жодному комп'ютері**, тож у користувачів
SmartScreen і далі скаже «невідомий видавець». Підпис при цьому має сенс: він захищає файл від підміни
(зміну одразу видно) і дає сталу назву видавця. Не просіть користувачів додавати ваш сертифікат у довірені.
Довіри публічно можна досягти лише сертифікатом від визнаного центру (Certum тощо).

## 5. Випуск версії

1. Змініть версію в двох місцях: `src/App/AssemblyInfo.cs` і `installer/NeoBar.iss` (`AppVersion`).
2. Увійдіть у SimplySign (або вставте картку) і запустіть **`release.bat`**. У `dist` з'являться:
   - `NeoBar-1.0.0-setup.exe` - встановлювач;
   - `NeoBar-1.0.0-portable.zip` - портативна версія;
   - `SHA256SUMS.txt` - контрольні суми.

   Підписуються exe, усі DLL, встановлювач і його програма видалення. Без `sign-config.bat` усе збирається без підпису.
3. Перевірте підпис: ПКМ на файлі → Властивості → **Цифрові підписи** (або `signtool verify /pa /v файл`).
4. Закомітьте зміну версії й зробіть push.
5. Реліз: на GitHub **Releases → Draft a new release** → тег `v1.0.0` → назва «NeoBar 1.0.0» → що нового →
   перетягніть три файли з `dist` → **Publish release**. Або однією командою:
   ```
   gh release create v1.0.0 dist\NeoBar-1.0.0-setup.exe dist\NeoBar-1.0.0-portable.zip dist\SHA256SUMS.txt --title "NeoBar 1.0.0" --notes "Що нового: …"
   ```

## 6. Як люди встановлюють

1. Сторінка **Releases** → `NeoBar-…-setup.exe`.
2. Поки програма нова, Windows SmartScreen може показати «Windows захистив ваш ПК»: **Докладніше → Усе одно запустити**.
   Підпис не прибирає це одразу - репутація набирається з кількістю завантажень (це стосується навіть дорогих EV-сертифікатів).
   Якщо Microsoft Defender помилково вважає файл загрозою, надішліть його на перевірку як розробник:
   <https://www.microsoft.com/wdsi/filesubmission>.
3. Встановлення без прав адміністратора - у `%LOCALAPPDATA%\Programs\NeoBar`, ярлик у меню «Пуск».
4. Оновлення - просто встановити нову версію поверх: профілі й налаштування лишаються.
5. Видалення - Параметри Windows → Програми; NeoBar прибере автозапуск, контекстне меню й інші свої сліди і спитає, чи видаляти профілі.

## 7. Як інші покращують NeoBar

1. Людина робить **Fork**, вносить зміни у своїй копії й відкриває **Pull request**.
2. GitHub Actions сам збирає зміни - видно, чи нічого не зламано; зібрану програму можна завантажити з вкладки Actions і перевірити.
3. Ви переглядаєте зміни (вкладка Files changed), за потреби просите виправити, і натискаєте **Merge**.
4. Переклад - це один файл `res/lang/<код>.lang` (див. `CONTRIBUTING.md`).

Офіційні підписані версії випускаєте лише ви (сертифікат на ваше ім'я), тож користувачі завжди можуть відрізнити оригінал.

## 8. Пізніше (необов'язково)

- **winget**: щоб ставити командою `winget install NeoBar`, додайте маніфест у репозиторій `microsoft/winget-pkgs`
  (є інструмент `wingetcreate`).
- **Безкоштовний підпис через GitHub Actions**: SignPath Foundation підписує збірки відкритих проєктів прямо на серверах GitHub,
  але видавцем у підписі буде SignPath, і потрібне їхнє схвалення проєкту.
