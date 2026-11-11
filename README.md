# SimpleXray

Простой клиент для Windows к прокси-серверу на [Xray-core](https://github.com/XTLS/Xray-core). В первую очередь он рассчитан на VLESS + XHTTP + TLS (сервер на 3x-ui), но понимает и другие транспорты VLESS (TCP, WS, gRPC, Reality).

Главный экран — одна кнопка «Подключить», выбор сервера и пресет маршрутизации. Остальное лежит в настройках.

## Возможности

- **Режимы:** системный прокси (по умолчанию) или VPN (TUN, нужны права администратора).
- **Маршрутизация:** пресеты «Всё через прокси», «Всё, кроме RU» и «Только заблокированное». Есть свои пресеты (редактор, импорт и экспорт JSON) и DNS через прокси.
- **Geo-базы:** автообновление geoip/geosite (по умолчанию из runetfreedom). Перед заменой базы проверяются, при сбое восстанавливается прежняя версия.
- **Серверы:** импорт ссылки `vless://` из буфера, из картинки с QR-кодом или со снимка экрана. Флаг страны берётся из имени сервера или определяется по IP.
- **Трей:** автозапуск с Windows, журнал в настройках.
- **Восстановление сети:** приложение не оставляет систему без интернета. Прокси откатывается при отключении, при падении ядра, после аварийного завершения и при выходе из Windows.

## Сборка

Нужен .NET 10 SDK (версия закреплена в `global.json`) и Windows x64.

```powershell
powershell -ExecutionPolicy Bypass -File tools/fetch-xray.ps1   # xray.exe, wintun.dll и geo-файлы в assets/
dotnet build
dotnet test
dotnet run --project src/SimpleXray.Desktop                       # --lang en|ru
powershell -ExecutionPolicy Bypass -File tools/make-zip.ps1     # portable-архив publish/SimpleXray-<версия>-win-x64.zip
```

Версия Xray закреплена в `tools/xray-version.txt`. Параметры XHTTP меняются между версиями, поэтому ядро обновляется только вместе с проверкой против сервера.

## Устройство

- `src/SimpleXray.Core` — модели, парсер ссылок, генератор конфига Xray, пресеты, geo-обновление, управление подключением. Без UI и Windows API.
- `src/SimpleXray.App` — ViewModel и локализация (RU/EN) на Avalonia.
- `src/SimpleXray.Desktop` — приложение для Windows: окна, трей, системный прокси, запуск xray.
- `tests/` — тесты xunit. Сгенерированные конфиги проверяются настоящим `xray run -test`.

Подробности, решения и дорожная карта описаны в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Памятка для пользователей, которая кладётся в архив, — [docs/README-friends.txt](docs/README-friends.txt).
