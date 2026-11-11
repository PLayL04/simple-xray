# CLAUDE.md

Руководство для Claude Code по работе с этим репозиторием.

## Проект

**SimpleVpn** (рабочее название) — простой клиент для прокси на Xray (VLESS, в первую очередь VLESS + XHTTP + TLS; сервер — 3x-ui).
Цель: интерфейс простой, как у Amnezia VPN (одна кнопка «Подключить»), при гибкости v2rayN (пресеты маршрутизации, автообновление geo-баз, режим системного прокси или TUN).
Пользователи — нетехнические друзья администратора сервера.

Полная концепция, обоснование стека и дорожная карта: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). **Прочитай его перед любой существенной задачей** и обнови, если решение меняется.

Текущий статус: **M0 (каркас) готов** — решение, DI, локализация, главный экран, fetch-xray, CI. Подключение пока имитируется `StubConnectionManager`. Следующий этап — M1 (реальное подключение: парсер vless, генератор конфига, запуск xray, системный прокси).

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
src/SimpleVpn.Core      net10.0 — модели, парсер vless://, XrayConfigBuilder, пресеты, GeoUpdater,
                        хранилище (AppPaths), ConnectionManager (IConnectionManager), интерфейсы платформы.
                        Без UI и без Windows API. TreatWarningsAsErrors.
src/SimpleVpn.App       Avalonia-библиотека без Application: ViewModels, Styles/Theme.axaml,
                        Localization (Strings.resx = RU, Strings.en.resx = EN, Loc, LocExtension),
                        AddSimpleVpnApp() для DI
src/SimpleVpn.Desktop   net10.0-windows WinExe (SimpleVpn.exe): Program (DI-контейнер), App.axaml,
                        Views (MainWindow, MainView), Platform/ (WindowsAppPaths, позже — xray, прокси, UAC, трей)
src/SimpleVpn.Android   (позже — свой Application и свои View поверх SimpleVpn.App)
tests/SimpleVpn.Core.Tests
tests/SimpleVpn.App.Tests   локализация (паритет ключей RU/EN), ViewModels
tools/fetch-xray.ps1    скачивает xray.exe, wintun.dll (с проверкой .dgst) и geo-файлы runetfreedom (с проверкой sha256) в assets/
assets/                 результат fetch-xray (в .gitignore); Desktop копирует его в core/ и geo/ рядом с exe
```

Зависимости: `Core` ← `App` ← `Desktop`/`Android`. Не тяни платформенный код и Avalonia в `Core`.

## Команды

```bash
powershell -ExecutionPolicy Bypass -File tools/fetch-xray.ps1   # один раз после клонирования; -Force, -Proxy http://127.0.0.1:10809
dotnet build
dotnet test
dotnet run --project src/SimpleVpn.Desktop                       # аргументы: --lang en|ru, --software-rendering
dotnet publish src/SimpleVpn.Desktop -c Release -r win-x64 --self-contained -o publish/SimpleVpn
```

CI: `.github/workflows/build.yml` (windows-latest: build → test → fetch-xray → publish → артефакт).

## Подводные камни окружения

- `dotnet restore` / `dotnet tool install` иногда падают с «The SSL connection could not be established» к nuget.org, хотя curl работает. Это сетевой сбой — повтори команду.
- Первый запуск свежесобранного self-contained exe может «висеть» без окна 10+ секунд (сканирование антивирусом) — это не баг кода.
- Если ПК заблокирован, захват экрана показывает экран блокировки. Для проверки UI используй `PrintWindow` (флаг 2) для снимка и UI Automation (`InvokePattern`) для нажатий — кнопки Avalonia доступны через UIA по `AutomationProperties.Name`.
- В Git Bash heredoc с кириллицей и `…` внутри длинной составной команды может сломать разбор — большие файлы пиши инструментом Write.

## Ключевые принципы

1. **Простота на главном экране.** Главный экран: кнопка вкл/выкл, статус, выбор сервера, быстрый переключатель пресета маршрутизации. Всё остальное — в настройках. Иерархия настроек: *Способ подключения* (прокси / TUN) на верхнем уровне, *Маршрутизация* — внутри. Любая новая опция должна иметь разумное значение по умолчанию и не появляться на главном экране без веской причины.
2. **Никогда не оставлять пользователя без интернета.** Системный прокси откатывается при отключении, при падении xray и при следующем старте после сбоя (флаг в `state.json`). Любая ошибка в `ConnectAsync` откатывает уже применённые шаги.
3. **Одно ядро — Xray.** Конфиг Xray генерируется только в `XrayConfigBuilder` из моделей (профиль + пресет + режим + настройки). Нельзя собирать JSON конфига строками в других местах.
4. **Версия Xray зафиксирована.** Не обновляй ядро «заодно»: XHTTP-параметры меняются между версиями и должны совпадать с сервером.
5. **Пресеты — данные, не код.** Встроенные пресеты лежат в JSON-ресурсах, пользовательские — в `%APPDATA%\SimpleVpn\presets\`. Встроенные неизменяемы, их можно только дублировать.
6. **Geo-обновление безопасно:** условный GET → временный файл → проверка sha256/валидности → атомарная замена с `.bak` → перезапуск ядра, если оно запущено, и откат при неудаче. Сначала прямая загрузка, затем через активный прокси.
7. **TUN требует прав администратора.** Приложение запускается как `asInvoker` и при необходимости перезапускает себя через `runas` с флагом `--connect`. Не ставь `requireAdministrator` в манифест.
8. **Секреты.** UUID/ключи из профилей не должны попадать в логи, исключения и диагностику — маскируй их.
9. **Задел на будущее без реализации:** поле `process` в правилах (per-app), `IProfileSource` (подписки), отдельные View для Android. Не реализуй это до соответствующего этапа, но и не ломай.

## Соглашения

- Документация и общение — на русском. Идентификаторы, комментарии в коде и сообщения коммитов — на английском.
- Все строки UI — только через ресурсы локализации (`Strings.resx` — RU по умолчанию, `Strings.en.resx`). Никаких захардкоженных строк во View.
- Nullable reference types включены, предупреждения считаются ошибками в `Core`.
- Асинхронные операции ввода-вывода принимают `CancellationToken`.
- Для `Core` — тесты обязательны: парсер ссылок (включая XHTTP `extra`, Reality), генератор конфига (golden-файлы в `tests/.../Golden/`), пресеты.
- Перед изменением формата конфига Xray сверяйся с официальной документацией: https://xtls.github.io/config/ (TUN: `/config/inbounds/tun.html`, маршрутизация: `/config/routing.html`).

## Открытые вопросы (см. §11 в ARCHITECTURE.md)

- Точные ключи TUN inbound для зафиксированной версии Xray — проверить до M4.
- Названия категорий geosite/geoip в runetfreedom/russia-v2ray-rules-dat — проверить до M2.
- Способ встраивания Xray на Android — исследовать до M6.
- Финальное название приложения.
