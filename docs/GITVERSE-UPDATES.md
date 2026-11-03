# Источники обновления GitVerse

Собственные выпуски HomeMedia/APK публикуются в
[Lepi4/lampa-home-module](https://gitverse.ru/Lepi4/lampa-home-module),
Channel Manager — в [Lepi4/lmpc-channel-manager](https://gitverse.ru/Lepi4/lmpc-channel-manager).

Публичный API релизов GitVerse требует авторизацию. Для серверного обновления
используется публичный `module-release.json` в репозитории. Файлы выпуска
содержатся в Git и скачиваются через HTTPS без API-токенов и SSH-ключей на сервере.

## Контракт публикации

Манифест содержит:

- `artifact_ref`: полный SHA Git-коммита, в котором находятся файлы выпуска.
- `components.<id>`: `version`, `file`, `sha256`, `bytes` для `homemedia` либо
  `channel-manager`. Путь начинается с `release/`.
- `apk`: `versionCode`, `versionName`, `package`, `file`, `sha256`, `bytes`.
- `published_utc`; прежние поля `homemedia`, `package`, `install` сохраняются
  для совместимости с документацией и ручной установкой.

Сначала фиксируются неизменяемые файлы `release/<version>/...`, затем отдельным
коммитом обновляется манифест. При скачивании используются пути
`/api/repos/<owner>/<repo>/raw/commit/<artifact_ref>/...`.
Одинаковую версию с другими байтами публикационный скрипт отклоняет.

## Публикация

В `scripts/` рабочего проекта, а в публичной копии в `deploy/`, находится
`publish-gitverse-release.py`. До запуска нужно проверить и закоммитить исходники.
Скрипт не добавляет прочие рабочие файлы в коммит и проверяет подпись APK:

```bash
python publish-gitverse-release.py --repo /path/to/lampa-home-module \
  --component homemedia --version 2026.11.03.1 \
  --package /path/to/homemedia.tar.gz --apk /path/to/LMPC-1.16.16-1134.apk \
  --sdk /path/to/Android/Sdk --push
```

Для Channel Manager используются `--component channel-manager` и его архив;
параметры APK/SDK не нужны. Без `--push` создаются только локальные коммиты и тег.
Приватный ключ публикации остаётся у владельца репозитория.

## Обновление компонентов сервера

В root-owned конфиге контроллера `config-production.json` для собственных
компонентов указывается `source.provider=gitverse`, `repo`, `branch`,
`manifest=module-release.json`, `asset` и `format=package`.

Проверка находит новую версию. «Подготовить» скачивает пакет, проверяет размер,
SHA-256, безопасную распаковку и совместимость. Установка запускается вручную,
со штатной резервной копией и откатом. Прежние режимы автоматической проверки
и подготовки сохраняются; автоматическое применение не включается.

Для сторонних Lampac, TorrServer и yt-dlp продолжают использоваться их
официальные GitHub-репозитории. Tvheadend остаётся в режиме мониторинга.

## Установка поддержки в существующий контроллер

На целевом Proxmox запускается `deploy-gitverse-updates.py` из каталога с
`release_sources.py`, `apk_sync.py`, `sync-apk.py`, `sync-github-apk.py`,
`service_catalog.py`:

```bash
python3 deploy-gitverse-updates.py --source /path/to/deploy \
  --ct 116 --expected-hostname lampac
```

Скрипт проверяет контейнер и отсутствие заданий, сохраняет прежние файлы и конфиг
в `/opt/lmpc-control/backups/gitverse-...` на хосте и в контейнере, обновляет
контроллер и APK-таймер. Медиаслужбы не перезапускаются. Если установка не проходит,
файлы и прежний таймер восстанавливаются. HomeMedia/APK и Channel Manager должны
иметь корректные публичные манифесты до переключения.

Идентификатор CT и сеть нужно выбирать по действующей сетевой карте: домашний
CT116 и staging-контейнеры сети «Коробка» — разные установки.

Настройка периодического скачивания APK: [APK-UPDATES.md](APK-UPDATES.md).
