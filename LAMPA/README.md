# Исходники APK LMPC Media Center

Этот Android-клиент — форк [lampa-app/LAMPA](https://github.com/lampa-app/LAMPA), адаптированный для медиaцентра на базе [Lampac NextGen](https://github.com/lampac-nextgen/lampac).

Код приложения находится в [app/src/](app/src/), код оболочки Media Center — в [app/src/mc/](app/src/mc/). Файлы сборки: [app/build.gradle](app/build.gradle), [settings.gradle](settings.gradle), [Gradle Wrapper](gradle/wrapper/). [Инструкция по сборке APK](../ANDROID-SOURCE.md).

## Что делает APK

Вариант `mc` — лаунчер и клиент медиaцентра для Android TV: домашний экран с приложениями и лентами контента, управление пультом, выбор серверного профиля, нативное воспроизведение видео и ТВ, продолжение с таймкода, переход к следующей серии и настраиваемый автозапуск ТВ.

Профили, права, каталоги, коллекции, история и синхронизация прогресса хранятся на сервере Lampac/HomeMedia. APK показывает эти данные и отправляет события просмотра. Каналы, EPG, резервирование и буферизацию потоков предоставляет отдельный [Channel Manager](https://gitverse.ru/Lepi4/lmpc-channel-manager). [Подробное разделение возможностей APK и сервисов](../FEATURES.md).

Для опубликованного варианта mc требуется JDK 17 и Android SDK 34. На Windows: `gradlew.bat assembleMcRelease -PappVersionCode=1132 -PappVersionName=1.16.14`. Ключ подписи издателя в исходники не включён. Условия лицензирования описаны в [LICENSE-SCOPE.md](../LICENSE-SCOPE.md).

Ниже сохранено описание исходного проекта LAMPA.

<p align="center" style="text-align: center">
  <img src="https://github.com/yumata/lampa/blob/main/img/logo-icon.svg" width="25%"><br/>
</p>
  <br/>
<p align="center">
  LAMPA client browser for Android and Android TV
  <br/>
  <br/>
  <a href="https://github.com/lampa-app/LAMPA/issues">
    <img src="https://img.shields.io/badge/contributions-welcome-brightgreen.svg?style=flat" alt="CodeFactor" />
  </a>
  <a href="https://github.com/lampa-app/LAMPA/actions/workflows/android.yml" rel="nofollow">
    <img src="https://img.shields.io/github/actions/workflow/status/lampa-app/LAMPA/android.yml?logo=Github" alt="Build" />
  </a>
  <a href="https://github.com/lampa-app/LAMPA/tags" rel="nofollow">
    <img alt="GitHub tag (latest SemVer pre-release)" src="https://img.shields.io/github/v/tag/lampa-app/LAMPA?include_prereleases&label=version"/>
  </a>
</p>
<p align="center">
System requirements: Android 4.1+ (API level 16+)
</p>

### Last release links:
- [Release page](https://github.com/lampa-app/LAMPA/releases/latest)
- [Direct apk download link](https://github.com/lampa-app/LAMPA/releases/latest/download/app-lite-release.apk)
