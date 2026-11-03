# Исходный код Android APK

Исходники Android-клиента LMPC Media Center опубликованы в этом же репозитории в каталоге [LAMPA](LAMPA/).

## Происхождение

Android-клиент основан на [lampa-app/LAMPA](https://github.com/lampa-app/LAMPA). В этой копии находятся изменения LMPC: нативная оболочка Media Center, проигрыватели, профили и взаимодействие с домашним сервером. Условия стороннего кода и границы применения лицензии Lepi4 описаны в [LICENSE-SCOPE.md](LICENSE-SCOPE.md).

## Где находится код

- [Основной код и ресурсы](LAMPA/app/src/main/).
- [Оболочка и ресурсы Media Center, flavor mc](LAMPA/app/src/mc/).
- [Конфигурация приложения и зависимости](LAMPA/app/build.gradle).
- [Настройки проекта](LAMPA/settings.gradle) и [корневая конфигурация Gradle](LAMPA/build.gradle).
- [Gradle Wrapper](LAMPA/gradle/wrapper/), [gradlew](LAMPA/gradlew) и [gradlew.bat](LAMPA/gradlew.bat).

## Сборка опубликованной версии

В репозитории находится APK `release/LMPC-1.16.14-1132.apk`. Для сборки этой версии используйте JDK 17 и Android SDK Platform 34. Gradle и зависимости загружаются из репозиториев, указанных в проекте; нужен доступ в интернет.

Linux/macOS:

```bash
cd LAMPA
bash gradlew assembleMcRelease -PappVersionCode=1132 -PappVersionName=1.16.14
```

Windows PowerShell:

```powershell
cd LAMPA
.\gradlew.bat assembleMcRelease -PappVersionCode=1132 -PappVersionName=1.16.14
```

Результат находится в `LAMPA/app/build/outputs/apk/mc/release/`. Для варианта mc используется идентификатор приложения `app.lampac.home.mc`.

Приватный ключ подписи и пароли в репозиторий не включены. Для собственной подписанной сборки задайте свои параметры через `KEYSTORE_FILE`, `KEYSTORE_PASSWORD`, `RELEASE_SIGN_KEY_ALIAS`, `RELEASE_SIGN_KEY_PASSWORD` либо локальный файл `app/keystore/keystore_config`, который не следует публиковать. Без ключа издателя нельзя получить его подпись или заменить установленное им приложение обычным обновлением.

Публикация исходников не является утверждением о побайтовой воспроизводимости APK: подпись, зависимости и окружение сборки могут различаться. В рамках обновления этой документации повторная сборка APK не выполнялась.
