# Pearl Rig Control

Нативная программа Windows на WPF и .NET 10 для управления и мониторинга майнинговой фермы.

## Возможности

- Реальные показатели GPU из `nvidia-smi` и OneZeroMiner API на `127.0.0.1:3001`. Если источник недоступен или показания устарели, приложение показывает «Нет данных».
- Реальные начисления пула Kryptex и курсы PRL/USD и USD/RUB. Публичный адрес выплат хранится только в профиле Windows.
- Кнопки запуска и штатной остановки OneZeroMiner. Запуск запрашивает разрешение Windows для управления GPU; остановка отправляет Ctrl+C и не завершает процесс принудительно.
- Режимы «Вручную» и «Автозапуск». В автозапуске Windows запускает программу с майнером при входе в систему; режим включается только по выбору пользователя.
- Локальные настройки имени работника и параметров GPU. Можно задать списки значений через запятую по номеру GPU, например `2400,2350`. Настройки применяются при следующем запуске майнера.
- Плавное появление окна и анимация карточек GPU и панели настроек.

## Локальные данные

Настройки фермы находятся в `%LOCALAPPDATA%\PearlRigControl\rig-config.json`, а публичный адрес выплат — в `%LOCALAPPDATA%\PearlRigControl\wallet-local.json`. Адрес не включается в репозиторий или установщик. Настройки на каждом компьютере независимы, поэтому для разных ферм задайте разные имена работников, например `pc1` и `pc2`.

Для параметров GPU формат списка и допустимые значения зависят от видеокарт и версии OneZeroMiner. Вентилятор поддерживает `*` для автоматического режима. Перед запуском проверьте значения для конкретного оборудования.

## Установка

Скачайте `PearlRigControl-Setup.exe` из [GitHub Releases](https://github.com/vovik06302-ai/pearl-rig-core/releases). Установщик включает self-contained сборку для Windows x64 и OneZeroMiner 1.8.3 из официального релиза. CI проверяет SHA-256 архива майнера перед включением его в установщик. Майнер не запускается во время установки.

## Сборка

```powershell
dotnet restore src/wpf/PearlRigCore.csproj --runtime win-x64 --source https://api.nuget.org/v3/index.json
dotnet publish src/wpf/PearlRigCore.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish/win-x64
ISCC.exe installer.iss
```

GitHub Actions выполняет публикацию WPF-приложения и собирает установщик при push и pull request.
