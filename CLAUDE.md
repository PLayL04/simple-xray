# CLAUDE.md

Руководство для Claude Code по работе с этим репозиторием.

## Проект

**SimpleXray** (до 0.1.1 — SimpleVpn; репозиторий https://github.com/PLayL04/simple-xray-client) — простой клиент для прокси на Xray (VLESS, в первую очередь VLESS + XHTTP + TLS; сервер — 3x-ui).
Цель: интерфейс простой, как у Amnezia VPN (одна кнопка «Подключить»), при гибкости v2rayN (пресеты маршрутизации, автообновление geo-баз, режим системного прокси или TUN).
Пользователи — нетехнические друзья администратора сервера.

Полная концепция, обоснование стека и дорожная карта: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). **Прочитай его перед любой существенной задачей** и обнови, если решение меняется.

Текущий статус: **M5 (удобство) готов** поверх M1 (подключение через системный прокси с откатом и восстановлением после сбоя), M2 (маршрутизация), M3 (автообновление geo-баз) и M4 (TUN).

M5:
- трей (цвет значка — состояние; меню: открыть, подключить, сервер, маршрутизация, выход), крестик прячет окно в трей (первый раз с подсказкой);
- завершение сеанса Windows (`ShutdownRequested` = WM_QUERYENDSESSION) отключает и восстанавливает прокси;
- автозапуск через `HKCU\...\Run` с `--autostart` (запуск без окна) и настройка «Подключаться при запуске»;
- QR: импорт из картинки и со снимка экрана, показ QR и копирование ссылки; раздел «Серверы» в настройках (переименование, удаление);
- флаги стран: эмодзи-флаг из имени, иначе страна по IP из локального `geoip.dat` (`ServerCountryService`); картинки флагов — `Assets/flags` (flagcdn.com, общественное достояние), потому что Windows не рисует эмодзи-флаги;
- журнал в настройках (последние 500 строк, предыдущий запуск, копирование);
- раздача — только portable-zip (`tools/make-zip.ps1`): в архиве файл `portable` и README.txt для друзей; если папка недоступна для записи, данные уходят в `%APPDATA%`.

M3: фоновая проверка по интервалу и кнопка «Обновить сейчас». Изменения определяются по `.sha256sum`. Перед установкой проверяются структура базы и наличие категорий пресетов, замена идёт с `.bak`, при отказе ядра файлы откатываются. Источник — runetfreedom или свои ссылки.

M2:
- три встроенных пресета: «Всё через прокси», «Всё, кроме RU» (по умолчанию) и «Только заблокированное»;
- свои пресеты: дублирование, редактор (форма и JSON), импорт и экспорт (файл, URL), обновление с источника;
- DNS: удалённый через прокси, прямой — системный;
- экран настроек, переключатель пресета на главном экране;
- смена сервера, пресета или настроек на лету: перезапускается только ядро, прокси остаётся.

M4 (механика проверена вживую с правами администратора; трафик через настоящий сервер в TUN ещё не проверялся): режим «VPN (TUN)» в настройках. Используется `tun` inbound Xray (`autoSystemRoutingTable`, `autoOutboundsInterface: auto`, WFP-блокировка утечек DNS), DNS с порта 53 уходит в outbound `dns`, системный прокси не трогается. Без прав `ConnectionManager` возвращает `ElevationRequired`, и главный экран предлагает перезапуск через UAC (`--connect --elevated-restart`) или режим прокси.

Следующий этап — M6 (per-app, подписки, статистика, Android).

## Стек

- C# / .NET 10 (SDK закреплён в `global.json`), **Avalonia 12**, MVVM на CommunityToolkit.Mvvm, DI на Microsoft.Extensions.DependencyInjection
- Версии пакетов — централизованно в `Directory.Packages.props` (в csproj версии не пишем)
- Ядро — **Xray-core** (единственное ядро; sing-box и mihomo не используем: в них нет полноценного XHTTP). Версия закреплена в `tools/xray-version.txt`
- TUN — встроенный `tun` inbound Xray + `wintun.dll` (входит в zip-архив Xray)
- JSON — System.Text.Json с source generators; QR — ZXing.Net
- Тесты — **xunit.v3 на Microsoft.Testing.Platform** (включено в `global.json`; пакеты `Microsoft.NET.Test.Sdk` / `xunit.runner.visualstudio` не нужны и с .NET 10 SDK ломают `dotnet test`)
- Платформы: Windows x64 (сейчас), Android (позже, отдельные View поверх общих ViewModel)

## Структура

```
src/SimpleXray.Core      net10.0, без UI и без Windows API, TreatWarningsAsErrors:
  Models/               ServerProfile, AppSettings (+ DnsSettings, ConnectionMode), SystemProxySettings, RuntimeState, ProfilesDocument
  Routing/              RoutingPreset/RoutingRule, BuiltInPresets (Presets/*.json — встроенные ресурсы), PresetRepository,
                        PresetValidator, PresetSerializer, PresetException + PresetError
  Net/                  HttpDownloader (строка или файл с прогрессом; напрямую, при сетевой ошибке — через запущенное ядро)
  Geo/                  GeoUpdater (+ GeoUpdateError, GeoStatus, geo-state.json), GeoUpdateScheduler, GeoDatFile,
                        GeoIpCountry + CountryFlags + ServerCountryService (флаг сервера)
  Qr/                   QrCodec (ZXing.Net: кодирование ссылки, поиск QR на картинке/снимке экрана), RawImage, QrMatrix
  Parsing/              VlessLinkParser (vless:// ⇄ ServerProfile), LinkParseException + LinkParseError
  Xray/                 XrayConfigBuilder — единственное место, где рождается JSON конфига Xray
  Storage/              AppPaths, ProfileRepository, SettingsStore (settings.json + state.json), GeoFiles,
                        JsonFile (атомарная запись), CoreJsonContext (source-gen)
  Connection/           ConnectionManager (+ IConnectionManager, ConnectionError/Code), LoopbackNetworkProbe
  Abstractions/         ICoreRunner, ISystemProxy, INetworkProbe, IElevation — реализуются платформой
src/SimpleXray.App       Avalonia-библиотека без Application: ViewModels (Main, Settings, GeoSettings, ServerList, LogViewer, PresetEditor, PresetItem),
                        Services (IClipboardService, IUiDispatcher, IFileService, IScreenCapture, IAutostart, IAppLifetime, StartupOptions),
                        Styles/Theme.axaml, Localization (Strings.resx = RU, Strings.en.resx = EN)
src/SimpleXray.Desktop   net10.0-windows WinExe (SimpleXray.exe): Program (DI, восстановление после сбоя, запуск GeoUpdateScheduler),
                        App.axaml (трей-приложение: окно создаётся по требованию, отключение при выходе и завершении сеанса),
                        TrayController, ServerFlags, Controls/QrCodeView, Assets (значки .ico, flags/*.png),
                        Views (MainView, SettingsView, PresetEditorView), Platform/: XrayProcessRunner + JobObject,
                        WinInetSystemProxy, WindowsElevation (runas), WindowsAutostart (Run), WindowsScreenCapture (GDI),
                        FileLoggerProvider, WindowsAppPaths,
                        SingleInstance (именованный mutex: второй запуск только активирует окно первого)
src/SimpleXray.Android   (позже — свой Application и свои View поверх SimpleXray.App)
tests/SimpleXray.Core.Tests  парсер, генератор (golden-файлы + все встроенные пресеты через настоящий `xray run -test`),
                            пресеты (валидация, хранилище), GeoUpdater на локальном HTTP-сервере (TestHttpServer),
                            ConnectionManager на фейках, хранилище
tests/SimpleXray.App.Tests   локализация (паритет RU/EN, тексты для всех кодов ошибок), MainViewModel, SettingsViewModel + редактор
tools/fetch-xray.ps1    скачивает xray.exe, wintun.dll (с проверкой .dgst) и geo-файлы runetfreedom (с проверкой sha256) в assets/
tools/make-zip.ps1      релизный архив publish/SimpleXray-<версия>-win-x64.zip (portable + README.txt из docs/README-friends.txt)
tools/fetch-flags.ps1   картинки флагов flagcdn.com → src/SimpleXray.Desktop/Assets/flags (закоммичены, запускать только для обновления)
tools/make-icons.ps1    рисует значки приложения и трея (закоммичены)
assets/                 результат fetch-xray (в .gitignore); Desktop копирует его в core/ и geo/ рядом с exe
```

Зависимости: `Core` ← `App` ← `Desktop`/`Android`. Не тяни платформенный код и Avalonia в `Core`.

## Команды

```bash
powershell -ExecutionPolicy Bypass -File tools/fetch-xray.ps1   # один раз после клонирования; -Force, -Proxy http://127.0.0.1:10809
dotnet build
dotnet test
dotnet run --project src/SimpleXray.Desktop                       # аргументы: --lang en|ru, --software-rendering
dotnet publish src/SimpleXray.Desktop -c Release -r win-x64 --self-contained -o publish/SimpleXray
powershell -ExecutionPolicy Bypass -File tools/make-zip.ps1     # publish + архив для друзей (-SkipPublish — только упаковать)
```

CI: `.github/workflows/build.yml` (windows-latest: fetch-xray → build → test → publish → make-zip → артефакт-zip).

## Подводные камни окружения

- `dotnet restore` / `dotnet tool install` иногда падают с «The SSL connection could not be established» к nuget.org, хотя curl работает. Это сетевой сбой — повтори команду.
- Первый запуск свежесобранного self-contained exe может «висеть» без окна 10+ секунд (сканирование антивирусом) — это не баг кода.
- Первый `Process.Start` свежего `xray.exe` тоже может занять ~20 с (антивирус). Поэтому `ConnectAsync` выполняет тяжёлую часть в `Task.Run` — не возвращай её в UI-поток.
- Живую проверку TUN проводи автономным скриптом с правами администратора. Он сам выполняет все шаги, всё убирает в `finally`, а отдельный процесс-сторож завершает xray и приложение по таймеру. Во время прогона сеть Claude Code может пропасть, поэтому интерактивно управлять TUN нельзя.
- Приложение живёт в трее: `ShutdownMode.OnExplicitShutdown`, окно создаётся в `App.ShowMainWindow`, `Closing` с `WindowCloseReason.WindowClosing` отменяется и прячет окно. Не возвращай `desktop.MainWindow` при старте с `--autostart`. Значок трея в Windows 11 обычно прячется под «^» (окно `TopLevelWindowForOverflowXamlIsland`), меню трея — окно Avalonia `TrayPopupRoot`.
- Эмодзи-флаги Windows не рисует (показывает буквы) — для флагов только картинки `ServerFlags`, а имя показывай через `ServerProfile.DisplayName` (без эмодзи).
- `ZipFile.CreateFromDirectory` в Windows PowerShell 5.1 пишет пути с `\` — в `make-zip.ps1` записи добавляются по одной с `/`.
- В UI-скриптах окно другого процесса PowerShell, запущенного с `-WindowStyle Hidden`, тоже скрыто (флаг STARTUPINFO); системный диалог выбора файла лежит в UIA внутри главного окна, а не на верхнем уровне.
- Если ПК заблокирован, захват экрана показывает экран блокировки. Для проверки UI используй `PrintWindow` (флаг 2) для снимка и UI Automation (`InvokePattern`, `ValuePattern`) для нажатий и ввода. Кнопки находятся по `AutomationProperties.Name`, статус и поле ссылки — по `AutomationId` (`StatusText`, `LinkInput`). Для ручных проверок запускай с файлом `portable` рядом с exe, чтобы не трогать `%APPDATA%`, и сохраняй настройки прокси (`reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"`) перед подключением.
- Скрипты PowerShell 5.1 без BOM читаются в ANSI — не пиши в них кириллицу (для UI-проверок запускай приложение с `--lang en`).
- **System.Text.Json source-gen и `init`:** для `init`-свойств генератор подставляет `default`, если поля нет в JSON, а не значение инициализатора. Поэтому модели, которые сериализуются (`Models/*`, `RoutingPreset`, `RoutingRule`), используют `{ get; set; }`. Не меняй на `init`, иначе старые файлы без новых полей прочитаются с `null` и `0`. Регрессионный тест — `Settings_OldFileWithoutNewFields_GetsDefaults`. Экземпляры не мутируй, изменяй через `with`: встроенные пресеты — общие статические объекты.
- Связанные со списками `ObservableCollection` обновляй через `CollectionSync.Sync`, а не `Clear()` + `Add()`. Если очистить список сразу после выбора пользователя в ComboBox, поле выбора в Avalonia остаётся пустым (регрессионные тесты `SwitchingServer_DoesNotRebuildList`, `SwitchingPreset_DoesNotRebuildList`).
- Списки отдают `ToString()` элементов в UI Automation. У record `ToString()` печатает все поля, поэтому `ServerProfile.ToString()` переопределён на имя: иначе UUID виден программам экранного доступа.
- Avalonia, компилируемые привязки: `IsVisible` и `DataContext` на одном элементе не сочетай — `IsVisible` будет искаться в новом `DataContext`. Оборачивай в `Panel` (см. MainView, SettingsView).
- Строки `.resx` раньше генерировались скриптом; теперь источник истины — сами `Strings.resx` и `Strings.en.resx`. Новые ключи добавляй в оба файла: тест паритета это проверяет.
- Старое имя `SimpleVpn` осталось только в миграции автозапуска (`WindowsAutostart.MigrateLegacyEntry`) и в истории — не «переименовывай» его там.
- Не оставляй в Bash-командах `cat` без входных данных (например `cat >> /dev/null;`): команда повиснет, ожидая stdin, и это выглядит как зависание тестов.
- xunit 4: атрибут отключения параллельности — `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]`; пропуск теста — `Assert.SkipUnless(...)`.
- В Git Bash heredoc с кириллицей и `…` внутри длинной составной команды может сломать разбор — большие файлы пиши инструментом Write.

## Ключевые принципы

1. **Простота на главном экране.** Главный экран: кнопка вкл/выкл, статус, выбор сервера, быстрый переключатель пресета маршрутизации. Всё остальное — в настройках. Иерархия настроек: *Способ подключения* (прокси / TUN) на верхнем уровне, *Маршрутизация* — внутри. Любая новая опция должна иметь разумное значение по умолчанию и не появляться на главном экране без веской причины.
2. **Никогда не оставлять пользователя без интернета.** Приложение работает в одном экземпляре на пользователя (`SingleInstance` в `Program.Main` — до логгера и `RecoverAfterCrash`): второй экземпляр сбросил бы прокси первого или запомнил бы его как «прежний». Системный прокси откатывается при отключении, при падении xray и при следующем старте после сбоя (флаг в `state.json`). Любая ошибка в `ConnectAsync` откатывает уже применённые шаги.
3. **Одно ядро — Xray.** Конфиг Xray генерируется только в `XrayConfigBuilder` из моделей (профиль + пресет + режим + настройки). Нельзя собирать JSON конфига строками в других местах.
4. **Версия Xray зафиксирована.** Не обновляй ядро «заодно»: XHTTP-параметры меняются между версиями и должны совпадать с сервером. После любого изменения генератора или версии ядра тест `Build_ProducesConfigAcceptedByXray` прогоняет конфиги через настоящий `xray run -test` и падает на любых «deprecated», кроме предупреждений о самих транспортах WS/gRPC. Пример: `allowInsecure` удалён в Xray 26 (замена — `pcs`/`vcn` = `pinnedPeerCertSha256`/`verifyPeerCertByName`), поэтому флаг из ссылки хранится только для экспорта.
5. **Пресеты — данные, не код.** Встроенные пресеты лежат в JSON-ресурсах (`Core/Routing/Presets/*.json`), пользовательские — в `%APPDATA%\SimpleXray\presets\<id>.json`. Встроенные неизменяемы, их можно только дублировать. Домены и IP внутри правила — ИЛИ (генератор разбивает правило), остальные поля — И. Новая категория geosite/geoip во встроенном пресете проверяется тестом с настоящим xray.
6. **Geo-обновление безопасно:** условный GET → временный файл → проверка sha256/валидности → атомарная замена с `.bak` → перезапуск ядра, если оно запущено, и откат при неудаче. Сначала прямая загрузка, затем через активный прокси.
7. **TUN требует прав администратора.** Приложение запускается как `asInvoker` и при необходимости перезапускает себя через `runas` с флагом `--connect`. Не ставь `requireAdministrator` в манифест.
8. **Секреты и приватность.** UUID/ключи из профилей не должны попадать в логи, исключения и диагностику. Access-лог Xray выключен (`"access": "none"`), иначе в наш лог (`logs\simplexray.log`) пишется история посещённых сайтов.
9. **Ошибки — коды, не строки.** Core сообщает `ConnectionErrorCode` / `LinkParseError`; UI превращает их в текст через ключи `Error_<Code>` / `LinkError_<Code>` (тест проверяет, что для каждого кода есть строка). Технические детали (вывод xray) — только в лог.
10. **Задел на будущее без реализации:** поле `process` в правилах (per-app), `IProfileSource` (подписки), отдельные View для Android. Не реализуй это до соответствующего этапа, но и не ломай.

## Соглашения

- Документация и общение — на русском. Идентификаторы, комментарии в коде и сообщения коммитов — на английском.
- Все строки UI — только через ресурсы локализации (`Strings.resx` — RU по умолчанию, `Strings.en.resx`). Никаких захардкоженных строк во View.
- Nullable reference types включены, предупреждения считаются ошибками в `Core`.
- Асинхронные операции ввода-вывода принимают `CancellationToken`.
- Для `Core` — тесты обязательны: парсер ссылок (включая XHTTP `extra`, Reality), генератор конфига (golden-файлы в `tests/.../Golden/`), пресеты.
- Перед изменением формата конфига Xray сверяйся с официальной документацией: https://xtls.github.io/config/ (TUN: `/config/inbounds/tun.html`, маршрутизация: `/config/routing.html`).

## Открытые вопросы (см. §11 в ARCHITECTURE.md)

- TUN: проверить трафик через настоящий сервер (XHTTP) и кнопку перезапуска через UAC.
- Способ встраивания Xray на Android — исследовать до M6.
