# Обновление APK через локальный сервер и GitHub

Приставки не скачивают APK напрямую с GitHub. Они периодически проверяют локальный Lampac `/home/app-updates/latest` и скачивают APK с `/home/app-updates/apk/...`; перед установкой клиент проверяет размер, SHA-256, пакет, версию и подпись.

На сервере работает `lmpc-github-apk-sync.timer`. Он раз в 30 минут смотрит последний релиз `Lepi4/lampa-home-module`, сравнивает `version_code`, проверяет размер и SHA-256 GitHub asset и атомарно обновляет `releases/android/latest.json` и APK. Откат к предыдущему APK выполняется заменой `latest.json` на резервную копию до следующего запуска синхронизатора.

Проверка вручную:

```bash
systemctl status lmpc-github-apk-sync.timer
systemctl start lmpc-github-apk-sync.service
cat /opt/lampac/releases/android/latest.json
```

Если GitHub недоступен или checksum не совпадает, текущий локальный APK не изменяется.
