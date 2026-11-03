# Обновление APK через локальный сервер и GitVerse

Приставки получают APK с локального Lampac: проверяют `/home/app-updates/latest`
и скачивают `/home/app-updates/apk/...`. Перед установкой клиент проверяет размер,
SHA-256, пакет, версию и подпись. GitVerse опрашивает сервер, а не каждая приставка.

`lmpc-apk-sync.timer` проверяет источник раз в 30 минут. Настройка источника:
`/opt/lmpc-control/apk-source.json` в контейнере Lampac:

```json
{
  "enabled": true,
  "source": {
    "provider": "gitverse",
    "repo": "Lepi4/lampa-home-module",
    "branch": "master",
    "manifest": "module-release.json"
  }
}
```

Манифест читается по публичному HTTPS из GitVerse без токена. APK берётся из
конкретного Git-коммита `artifact_ref`, а не из изменяемой ветки. Версия, имя
пакета, размер и SHA-256 обязательны. Ошибка сети, неполный файл или несовпадение
хеша сохраняют текущий APK и манифест. Понижение версии запрещено. При новой
версии прежний `latest.json` сохраняется как `previous-latest.json`.

Само скачивание новой APK не устанавливает её на приставки: приложение показывает
обычное предложение обновления.

```bash
systemctl status lmpc-apk-sync.timer
systemctl start lmpc-apk-sync.service
cat /opt/lampac/releases/android/latest.json

# Отключить периодическую загрузку:
systemctl disable --now lmpc-apk-sync.timer

# Включить:
systemctl enable --now lmpc-apk-sync.timer
```

Можно также установить `"enabled": false` в `apk-source.json`. Тогда даже ручной
запуск задания не скачивает файлы. Период меняется через systemd drop-in таймера.

Поддержка GitHub сохранена: для неё задаются `"provider": "github"` и `repo`.
Старый `sync-github-apk.py` остаётся совместимой точкой входа, но читает тот же
конфиг. После миграции старый GitHub-таймер выключен, чтобы не было двух расписаний.

[Компоненты сервера, публикация и установка поддержки GitVerse](GITVERSE-UPDATES.md).
