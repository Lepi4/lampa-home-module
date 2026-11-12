(function () {
  'use strict';
  if (window.plugin_kinopub_ready) return;
  window.plugin_kinopub_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;
  var API = BASE + '/kp/api/';
  var PREF_KEY = 'kp_pref_v1';

  // ---------- настройки на стороне плагина (качество) ----------
  function prefs() {
    try { return $.extend({ quality: 'auto', profile: 'main', viaServer: true }, JSON.parse(localStorage.getItem(PREF_KEY) || '{}')); }
    catch (e) { return { quality: 'auto', profile: 'main', viaServer: true }; }
  }
  function setPref(k, v) {
    var p = prefs();
    p[k] = v;
    try { localStorage.setItem(PREF_KEY, JSON.stringify(p)); } catch (e) {}
  }

  // Контроллер, в который возвращаемся из наших окон выбора. Окно выбора/модалку нельзя запоминать как «предыдущее»:
  // при вызове из штатного списка кнопок оно уже закрывается, и «Назад» уходит в пустоту.
  var lastBase = 'content';
  try {
    if (Lampa.Controller.listener && Lampa.Controller.listener.follow) {
      Lampa.Controller.listener.follow('toggle', function (e) {
        if (e && e.name && ['select', 'modal', 'loading', 'head', 'menu'].indexOf(e.name) < 0) lastBase = e.name;
      });
    }
  } catch (e) {}

  function baseCtrl() {
    var n = '';
    try { n = Lampa.Controller.enabled().name; } catch (e) {}
    return (!n || ['select', 'modal', 'loading'].indexOf(n) >= 0) ? lastBase : n;
  }

  // ---------- сеть ----------
  function hdr() { return { 'X-KP-Profile': prefs().profile }; }

  function get(path, ok, fail) {
    if(window.HomeUser&&/^watching\/(toggle|marktime)\?/.test(path)){if(ok)setTimeout(function(){ok({local:true});},0);return;}
    if(window.HomeUser&&/^watching\/togglewatchlist\?/.test(path)){
      var c=currentCard;if(!c||!c.id){if(fail)fail();return;}
      var media=cardMedia(c);$.ajax({url:BASE+'/kp/watchlist/toggle?type='+media+'&tmdb='+c.id,dataType:'json',success:ok,error:fail});return;
    }
    $.ajax({ url: API + path, dataType: 'json', timeout: 30000, cache: false, headers: hdr(), success: ok, error: function (x) { if (fail) fail(x); } });
  }
  function post(path, data, ok, fail) {
    $.ajax({
      url: API + path, type: 'POST', contentType: 'application/json', data: JSON.stringify(data), headers: hdr(),
      dataType: 'json', timeout: 30000, success: ok, error: function (x) { if (fail) fail(x); }
    });
  }

  // ---------- авторизация (общий токен на сервере) ----------
  var authState = null; // null = неизвестно, true/false

  function checkAuth(cb) {
    $.ajax({
      url: BASE + '/kp/status', dataType: 'json', timeout: 15000, cache: false, headers: hdr(),
      success: function (d) { authState = !!d.authorized; cb(authState); },
      error: function () { cb(false); }
    });
  }

  function ensureAuth(ok) {
    checkAuth(function (a) {
      if (a) return ok();
      showAuth(ok);
    });
  }

  function showAuth(ok, profileId) {
    var H = profileId ? { 'X-KP-Profile': profileId } : hdr();
    var prev = baseCtrl();
    $.ajax({
      url: BASE + '/kp/auth/start', dataType: 'json', timeout: 30000, cache: false, headers: H,
      success: function (d) {
        var html = $('<div style="text-align:center;padding:1em 0;line-height:1.6">' +
          '<div style="font-size:1.2em">Откройте на телефоне или компьютере</div>' +
          '<div style="font-size:1.5em;margin:.3em 0"><b>' + d.verification_uri + '</b></div>' +
          '<div style="font-size:1.2em">и введите код</div>' +
          '<div style="font-size:3em;letter-spacing:.15em;margin-top:.2em"><b>' + d.user_code + '</b></div></div>');
        var timer = setInterval(poll, Math.max(3, d.interval || 5) * 1000);
        var done = false;
        function stop() { done = true; clearInterval(timer); }
        function poll() {
          if (done) return;
          $.ajax({
            url: BASE + '/kp/auth/poll', dataType: 'json', timeout: 20000, cache: false, headers: H,
            success: function (r) {
              if (r.status === 'ok') {
                stop(); Lampa.Modal.close(); authState = true;
                Lampa.Noty.show('Кинопаб: вход выполнен');
                Lampa.Controller.toggle(prev);
                ok();
              } else if (r.status === 'expired') {
                stop(); Lampa.Modal.close(); Lampa.Noty.show('Код истёк, откройте раздел заново');
                Lampa.Controller.toggle(prev);
              }
            }
          });
        }
        Lampa.Modal.open({
          title: 'Кинопаб — вход',
          html: html,
          size: 'medium',
          onBack: function () { stop(); Lampa.Modal.close(); Lampa.Controller.toggle(prev); }
        });
      },
      error: function () { Lampa.Noty.show('Кинопаб: сервер недоступен'); }
    });
  }

  // ---------- настройки устройства kino.pub (HEVC, HDR, 4K, сервер, тип потока) ----------
  var dev = null;
  var devProfile = null;

  function devLoad(cb) {
    var pr = prefs().profile;
    if (dev && devProfile === pr) return cb(dev);
    dev = null;
    get('device/info', function (d) {
      dev = { id: d.device.id, settings: d.device.settings };
      devProfile = pr;
      cb(dev);
    }, function () { cb(null); });
  }

  function listSel(s) {
    var r = null;
    (s.value || []).forEach(function (x) { if (x.selected) r = x; });
    return r;
  }

  var STREAM = { 1: 'http', 2: 'hls', 3: 'hls2', 4: 'hls4' };
  var LOC = { 1: 'nl', 3: 'ru' };

  function streamType() {
    if (!dev || !dev.settings.streamingType) return 'hls4';
    var x = listSel(dev.settings.streamingType);
    return x ? (STREAM[x.id] || 'hls4') : 'hls4';
  }
  function serverLoc() {
    if (!dev || !dev.settings.serverLocation) return 'nl';
    var x = listSel(dev.settings.serverLocation);
    return x ? (LOC[x.id] || 'nl') : 'nl';
  }

  function applySetting(patch, done, label) {
    devLoad(function (d) {
      if (!d) return Lampa.Noty.show('Кинопаб: нет связи');
      Lampa.Noty.show('Кинопаб: сохраняю…');
      post('device/' + d.id + '/settings', patch, function (r) {
        if (r && r.settings) dev.settings = r.settings;
        Lampa.Noty.show('Кинопаб: ' + (label || 'настройка') + ' сохранена');
        // пересоздаём список чуть позже: предыдущее окно выбора успевает закрыться
        setTimeout(done, 350);
      }, function () { Lampa.Noty.show('Кинопаб: не удалось сохранить настройку'); setTimeout(done, 350); });
    });
  }

  // ---------- устройства (профили) kino.pub: подключение по коду, необязательное ----------
  function openDevices(back) {
    loadProfiles(function (list) {
      var cur = prefs().profile;
      Lampa.Select.show({
        title: 'Устройства Кинопаб',
        items: list.map(function (p) {
          return {
            title: p.label,
            subtitle: (p.authorized ? 'подключено' : 'не подключено') + (p.id === cur ? ' · используется на этом клиенте' : ''),
            p: p
          };
        }),
        onBack: function () { back(); },
        onSelect: function (a) { setTimeout(function () { deviceMenu(a.p, back); }, 300); }
      });
    });
  }

  function deviceMenu(p, back) {
    var items = [];
    if (!p.authorized) {
      items.push({ title: 'Подключить по коду', subtitle: 'код нужно ввести на kino.pub/device', act: 'connect' });
    } else {
      items.push({ title: 'Использовать на этом клиенте', act: 'use' });
      items.push({ title: 'Применить настройки профиля заново', subtitle: 'HEVC / HDR / 4K и название устройства', act: 'apply' });
      items.push({ title: 'Отключить', subtitle: 'убрать устройство из аккаунта и забыть вход', act: 'disconnect' });
    }
    items.push({ title: 'Назад', act: 'back' });

    Lampa.Select.show({
      title: p.label,
      items: items,
      onBack: function () { openDevices(back); },
      onSelect: function (a) {
        var H = { 'X-KP-Profile': p.id };
        if (a.act === 'connect') {
          showAuth(function () { setTimeout(function () { openDevices(back); }, 300); }, p.id);
        } else if (a.act === 'use') {
          setPref('profile', p.id); dev = null; authState = null;
          Lampa.Noty.show('Кинопаб: на этом клиенте используется «' + p.label + '»');
          setTimeout(function () { openDevices(back); }, 300);
        } else if (a.act === 'apply') {
          $.ajax({ url: BASE + '/kp/profile/apply', dataType: 'json', cache: false, headers: H,
            success: function () { dev = null; Lampa.Noty.show('Кинопаб: настройки профиля применены'); setTimeout(function () { openDevices(back); }, 300); },
            error: function () { Lampa.Noty.show('Кинопаб: не удалось применить'); setTimeout(function () { openDevices(back); }, 300); } });
        } else if (a.act === 'disconnect') {
          $.ajax({ url: BASE + '/kp/auth/reset', dataType: 'json', cache: false, headers: H,
            success: function (r) {
              dev = null; authState = null;
              Lampa.Noty.show(r && r.unlinked ? 'Кинопаб: устройство отключено и удалено из аккаунта' : 'Кинопаб: вход забыт (удалите устройство в кабинете kino.pub, если оно осталось)');
              setTimeout(function () { openDevices(back); }, 300);
            },
            error: function () { Lampa.Noty.show('Кинопаб: не удалось отключить'); setTimeout(function () { openDevices(back); }, 300); } });
        } else {
          openDevices(back);
        }
      }
    });
  }

  var profileList = null;

  function profileLabel() {
    var cur = prefs().profile;
    var l = cur;
    (profileList || []).forEach(function (p) { if (p.id === cur) l = p.label; });
    return l;
  }

  function loadProfiles(cb) {
    $.ajax({
      url: BASE + '/kp/profiles', dataType: 'json', timeout: 15000, cache: false,
      success: function (d) { profileList = d.profiles || []; cb(profileList); },
      error: function () { cb(profileList || []); }
    });
  }

  // Профиль выбирается для этого клиента; если он ещё не авторизован, предлагаем вход по коду
  function chooseProfile(done) {
    loadProfiles(function (list) {
      var cur = prefs().profile;
      Lampa.Select.show({
        title: 'Профиль',
        items: list.map(function (p) {
          return { title: p.label, subtitle: p.authorized ? '' : 'нужен вход по коду', v: p.id, selected: p.id === cur };
        }),
        onBack: function () { done(); },
        onSelect: function (b) {
          setPref('profile', b.v);
          dev = null;
          authState = null;
          setTimeout(function () { ensureAuth(done); }, 350);
        }
      });
    });
  }

  // Тест серверов: сервер скачивает несколько сегментов с каждой площадки и меряет скорость
  function runSpeedTest(s) {
    Lampa.Noty.show('Кинопаб: тест серверов, около 15 секунд…');
    $.ajax({
      url: BASE + '/kp/test/servers', dataType: 'json', timeout: 120000, cache: false, headers: hdr(),
      success: function (d) {
        var cur = (listSel(s.serverLocation) || {}).id;
        var items = (d.results || []).map(function (r) {
          return {
            title: r.label + (r.loc === d.recommended ? '  ★ быстрее' : '') + (r.id === cur ? '  (выбран)' : ''),
            subtitle: r.ok ? (r.mbit + ' Мбит/с · задержка ' + r.latency_ms + ' мс') : ('недоступен: ' + (r.error || '')),
            v: r.id, ok: r.ok
          };
        });
        items.push({ title: 'Назад', back: true });
        Lampa.Select.show({
          title: 'Тест серверов — выберите сервер',
          items: items,
          onBack: function () { openSettings(); },
          onSelect: function (b) {
            if (b.back || !b.ok) return openSettings();
            applySetting({ serverLocation: b.v }, openSettings, 'Сервер');
          }
        });
      },
      error: function () { Lampa.Noty.show('Кинопаб: тест серверов не удался'); }
    });
  }

  var settingsPrev = null;

  function openSettings() {
    // контроллер, в который возвращаемся по «Назад», запоминаем один раз, а не при каждом пересоздании списка
    var cur = Lampa.Controller.enabled().name;
    if (cur !== 'select' && cur !== 'modal') settingsPrev = cur;
    var prev = settingsPrev || 'content';
    devLoad(function (d) {
      if (!d) return Lampa.Noty.show('Кинопаб: нет связи');
      var s = d.settings;
      var q = prefs().quality;
      var items = [
        { title: 'Профиль', subtitle: profileLabel(), key: 'profile' },
        { title: 'Устройства Кинопаб', subtitle: 'подключение по коду: 1080p и 4K (необязательно)', key: 'devices' },
        { title: 'Локальная библиотека', subtitle: 'Plex и папка Wrestling: адрес вручную, включение', key: 'local' },
        { title: 'Качество', subtitle: q === 'auto' ? 'авто (максимальное)' : q, key: 'quality' },
        { title: 'Видео через сервер', subtitle: prefs().viaServer ? 'включено (рекомендуется)' : 'выключено', key: 'viaServer' },
        { title: 'Сервер', subtitle: (listSel(s.serverLocation) || {}).label || '', key: 'serverLocation' },
        { title: 'Тип потока', subtitle: (listSel(s.streamingType) || {}).label || '', key: 'streamingType' },
        { title: 'Тест серверов', subtitle: 'скорость и задержка: Нидерланды / Россия', key: 'speedtest' }
      ];
      [['mixedPlaylist', 'Смешанный плейлист']].forEach(function (t) {
        if (s[t[0]]) items.push({ title: t[1], subtitle: s[t[0]].value ? 'включено' : 'выключено', key: t[0], toggle: true });
      });
      items.push({ title: 'Выйти из аккаунта', subtitle: 'потребуется новый вход по коду', key: 'logout' });

      Lampa.Select.show({
        title: 'Кинопаб · настройки',
        items: items,
        onBack: function () { Lampa.Controller.toggle(prev); },
        onSelect: function (a) {
          if (a.key === 'local') {
            openLocalSettings(openSettings);
          } else if (a.key === 'devices') {
            openDevices(openSettings);
          } else if (a.key === 'profile') {
            chooseProfile(openSettings);
          } else if (a.key === 'viaServer') {
            setPref('viaServer', !prefs().viaServer);
            setTimeout(openSettings, 350);
          } else if (a.key === 'quality') {
            var qs = ['auto', '2160p', '1080p', '720p', '480p'];
            Lampa.Select.show({
              title: 'Качество',
              items: qs.map(function (x) { return { title: x === 'auto' ? 'авто (максимальное)' : x, v: x, selected: x === q }; }),
              onBack: function () { openSettings(); },
              onSelect: function (b) { setPref('quality', b.v); openSettings(); }
            });
          } else if (a.key === 'serverLocation' || a.key === 'streamingType') {
            Lampa.Select.show({
              title: a.title,
              items: s[a.key].value.map(function (x) { return { title: x.label, v: x.id, selected: !!x.selected }; }),
              onBack: function () { openSettings(); },
              onSelect: function (b) { var p = {}; p[a.key] = b.v; applySetting(p, openSettings, a.title); }
            });
          } else if (a.key === 'speedtest') {
            runSpeedTest(s);
          } else if (a.toggle) {
            var p = {};
            p[a.key] = s[a.key].value ? 0 : 1;
            applySetting(p, openSettings, a.title);
          } else if (a.key === 'logout') {
            $.ajax({ url: BASE + '/kp/auth/reset', dataType: 'json', headers: hdr(), success: function () { authState = false; dev = null; Lampa.Noty.show('Кинопаб: выход выполнен'); Lampa.Controller.toggle(prev); } });
          }
        }
      });
    });
  }

  // ---------- карточки ----------
  function splitTitle(t) {
    var p = (t || '').split(' / ');
    return { ru: p[0] || t, orig: p[1] || p[0] || t };
  }

  function toCard(it) {
    var t = splitTitle(it.title);
    var p = it.posters || {};
    var serial = it.type !== 'movie' && it.type !== '3d' && it.type !== 'concert' && it.type !== 'documovie';
    var date = it.year ? it.year + '-01-01' : '';
    var badge = '';
    if (serial && it['new'] > 0) badge = '+' + it['new'];
    else if (it.quality >= 2160) badge = '4K';

    var c = {
      id: 'kp' + it.id,
      kp_id: it.id,
      kp_type: it.type,
      title: t.ru,
      original_title: t.orig,
      img: p.medium || p.big || p.small || '',
      poster_path: '',
      backdrop_path: '',
      overview: it.plot || '',
      vote_average: it.kinopoisk_rating || it.imdb_rating || 0,
      quality: badge,
      source: 'kinopub',
      kp: it
    };

    // name и first_air_date включают в Lampa пометку «сериал», поэтому задаём их только сериалам
    if (serial) {
      c.name = t.ru;
      c.original_name = t.orig;
      c.first_air_date = date;
    } else {
      c.release_date = date;
    }
    return c;
  }

  function page(items, current, total) {
    return { results: items.map(toCard), page: current || 1, total_pages: total || 1, total_results: items.length };
  }

  // mode: watching | fresh_movie | fresh_serial | bookmark:<id>
  // Сервер сопоставляет элементы kino.pub с TMDB и отдаёт штатные карточки Lampa (с полями kp_*).
  function fetchItems(mode, pg, ok, fail, session) {
    $.ajax({
      url: BASE + '/kp/cards?mode=' + encodeURIComponent(mode) + '&page=' + (pg || 1) + (session ? '&catalog_session='+encodeURIComponent(session) : '') + (mode === 'top_new' ? '&dev=' + encodeURIComponent((window.AndroidJS && AndroidJS.mcDeviceId && AndroidJS.mcDeviceId()) || '') : ''),
      dataType: 'json', timeout: 35000, cache: false, headers: hdr(),
      success: function (d) {
        var results = (d.results || []).map(function (r) { return r.kp_legacy ? toCard(r.kp) : r; });
        if (!window.HomeUser && mode === 'history' && (pg || 1) === 1) {
          var seen = {};
          results.forEach(function (r) { seen[r.id] = 1; });
          try {
            Lampa.Favorite.get({ type: 'history' }).forEach(function (c) {
              if (!seen[c.id]) { seen[c.id] = 1; results.push(c); }
            });
          } catch (e) {}
        }
        ok({ results: results, page: d.page || pg || 1, total_pages: d.total_pages || 1, total_results: results.length, has_more:d.has_more, retry_page:d.retry_page });
      },
      error: function (x) { if (fail) fail(x); }
    });
  }

  // Штатное открытие страницы карточки Lampa
  function nativeOpen(card) {
    Lampa.Activity.push({
      url: '', component: 'full', id: card.id,
      method: card.name ? 'tv' : 'movie', card: card, source: card.source || 'tmdb'
    });
  }

  // ---------- воспроизведение ----------
  var RANK = ['2160p', '1080p', '720p', '480p', '360p'];

  function b64url(str) {
    var b = btoa(unescape(encodeURIComponent(str)));
    return b.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  // Через сервер: плеер Lampa/Android общается только с Lampac, у kino.pub сложная цепочка сертификатов
  // Внешний плеер (Vimu и др.) не выбирает лучший вариант из мастер-плейлиста сам — отдаём ему только максимальный
  function externalPlayer() {
    try {
      var p = Lampa.Storage.get('player', 'inner');
      return !!p && p !== 'inner';
    } catch (e) { return false; }
  }

  function withLoc(url) {
    var u = url.split('?')[0] + '?loc=' + serverLoc();
    if (!prefs().viaServer) return u;
    return BASE + '/kp/stream/' + b64url(u) + '.m3u8' + (externalPlayer() ? '?q=max' : '?codec=h264');
  }

  var lastPickQ = '';
  function pickUrl(files) {
    lastPickQ = '';
    if (!files || !files.length) return null;
    var by = {};
    files.forEach(function (f) { by[f.quality] = f; });
    var pref = prefs().quality;
    var f = null;
    if (pref !== 'auto' && by[pref]) f = by[pref];
    if (!f) {
      for (var i = 0; i < RANK.length && !f; i++) if (by[RANK[i]]) f = by[RANK[i]];
    }
    f = f || files[0];
    lastPickQ = String(f.quality || '');
    var u = f.url || {};
    var url = u[streamType()] || u.hls4 || u.hls2 || u.hls || u.http;
    return url ? withLoc(url) : null;
  }

  // Единое правило просмотра: просмотрено — досмотрено до конца или до конца осталось не больше 10 минут (и пройдена половина);
  // начато — от 30 секунд или 2%.
  if (!window.__home_state) window.__home_state = function (tl) {
    var time = (tl && tl.time) || 0, dur = (tl && tl.duration) || 0;
    var p = (tl && tl.percent) || (dur ? time / dur * 100 : 0);
    var done = dur > 0 ? ((dur - time <= Math.min(600, dur * 0.12) && time >= dur * 0.5) || p >= 90) : false;
    if (done) return { s: 'watched', p: 100, at: (tl && tl.updated) || 0 };
    if (time >= 30 || p >= 2) return { s: 'started', p: Math.max(p, 3), at: (tl && tl.updated) || 0 };
    return { s: '', p: 0, at: (tl && tl.updated) || 0 };
  };
  // карточка лежит в строке «Продолжить просмотр»: там жёлтая метка «начато» не нужна
  if (!window.__home_inCont) window.__home_inCont = function (card) {
    try { return $.trim($(card).closest('.items-line').find('.items-line__title').first().text()) === 'Продолжить просмотр'; } catch (e) { return false; }
  };

  // Нативный плеер получает список только внутри данных запуска: без него играет один файл и закрывается,
  // следующая серия сама не включается. Копия нужна, чтобы не было циклической ссылки (элемент внутри собственного списка).
  function startPlay(playlist, index) {
    var cur = $.extend({}, playlist[index]);
    cur.playlist = playlist;
    // ключи строк timecode по сериям: плеер сам отправляет прогресс на сервер (см. HomePlayerActivity.tlSend)
    try {
      var tm = currentCard && parseInt(currentCard.id, 10);
      cur.tl_meta = JSON.stringify(playlist.map(function (p) {
        var k = p.kp;
        if (!tm || !k || !k.native) return null;
        return { id: k.season ? 'tv-' + tm + '-s' + k.season + 'e' + k.video : 'movie-' + tm, h: k.hash, c: tm + '_' + (k.season ? 'tv' : 'movie') };
      }));
    } catch (e) {}
    Lampa.Player.play(cur);
    Lampa.Player.playlist(playlist);
  }

  function seasonParam(season) { return season ? '&season=' + season : ''; }

  function report(itemId, season, video, hash) {
    try {
      var tl = Lampa.Timeline.view(hash);
      var t = Math.round(tl.time || 0);
      var pct = tl.percent || 0;
      if (window.__home_state(tl).s === 'watched') get('watching/toggle?id=' + itemId + '&video=' + video + '&status=1' + seasonParam(season));
      else if (t > 60) get('watching/marktime?id=' + itemId + '&video=' + video + '&time=' + t + seasonParam(season));
    } catch (e) {}
  }

  // ---------- позиция просмотра -> сервер (общая для всех клиентов и для kino.pub) ----------
  // Штатный плагин TimeCode шлёт позицию, только если сам опознал ключ по странице/закладкам. Мы знаем
  // идентичность точно (TMDB id + сезон + серия), поэтому отправляем строку сами: раз в минуту и при закрытии.
  var posState = null;

  function samplePosition() {
    if (!posState) return;
    try {
      var cur = Lampa.Player.playdata();
      if (cur && cur.kp && cur.timeline && cur.timeline.time > 1) {
        posState.seen[cur.kp.hash] = {
          kp: cur.kp,
          time: Math.round(cur.timeline.time),
          duration: Math.round(cur.timeline.duration || 0),
          percent: cur.timeline.percent || 0
        };
      }
    } catch (e) {}
  }

  function pushPositions() {
    if (!posState || !posState.tmdb) return;
    var rows = [];
    Object.keys(posState.seen).forEach(function (h) {
      var r = posState.seen[h];
      if (r.pushed === r.time) return;
      r.pushed = r.time;
      var pct = r.percent || (r.duration ? r.time / r.duration * 100 : 0);
      rows.push({
        id: posState.season ? 'tv-' + posState.tmdb + '-s' + posState.season + 'e' + r.kp.video : 'movie-' + posState.tmdb,
        hash: h,
        card: posState.tmdb + '_' + (posState.season ? 'tv' : 'movie'),
        position: r.time,
        duration: r.duration,
        percent: Math.round(pct * 10) / 10,
        watched_at: Date.now()
      });
    });
    if (!rows.length) return;
    postTl(rows);
  }

  // Отметки просмотра уходят в общую таблицу сервера (/home/tl/set): у каждого действия свой event «сеанс:номер», время — момент действия
  // Приложение: отметка кладётся в ту же устойчивую очередь, что и отчёты плеера (AndroidJS.tlEnqueue). Браузер без приложения: своя очередь в localStorage
  // с повтором раз в 20 секунд; строка снимается только по ответу сервера на её собственное событие. Повтор несёт тот же event.
  var TL_SESSION = String(Date.now()), tlSeq = 0, TLQ = 'home_tl_q_v1', TLQ_TTL = 7 * 24 * 3600 * 1000;
  var TLQA = 'home_tl_q_arch_v1', memQ = null;           // memQ — очередь, которую не удалось записать в localStorage (остаётся в памяти страницы)
  function tlq() { if (memQ) return memQ; try { return JSON.parse(localStorage.getItem(TLQ) || '{}') || {}; } catch (e) { return {}; } }
  function tlqSave(q) { try { localStorage.setItem(TLQ, JSON.stringify(q)); memQ = null; return true; } catch (e) { memQ = q; return false; } }
  // недоставленное (срок вышел, сервер отклонил) не исчезает молча: уходит в ограниченный архив с причиной и исходными event/u
  function tlqArchive(row, reason) {
    try {
      var a = JSON.parse(localStorage.getItem(TLQA) || '[]') || [];
      a.push({ row: row, reason: reason, at: Date.now() }); localStorage.setItem(TLQA, JSON.stringify(a.slice(-50)));
    } catch (e) {}
  }
  function tlqFlush() {
    var q = tlq(), keys = Object.keys(q), now = Date.now(), rows = [];
    keys.forEach(function (k) { if (now - (q[k]._t || 0) > TLQ_TTL) { tlqArchive(q[k], 'ttl'); delete q[k]; } else rows.push(q[k]); });
    tlqSave(q);
    if (!rows.length) return;
    $.ajax({ url: BASE + '/home/tl/set', type: 'POST', contentType: 'application/json', data: JSON.stringify({ rows: rows.map(function (r) { var c = $.extend({}, r); delete c._t; return c; }) }), timeout: 20000,
      success: function (d) {
        var st = {}; try { ((d && d.results) || []).forEach(function (x) { st[x.h + '|' + (x.event || '')] = x.status; }); } catch (e) {}
        var cur = tlq();
        rows.forEach(function (r) {
          var s = st[r.h + '|' + r.event], k = r.h + '|' + r.id;
          if (!cur[k] || cur[k].event !== r.event) return;
          if (s === 'accepted' || s === 'duplicate' || s === 'stale') delete cur[k];
          else if (s === 'invalid') { tlqArchive(cur[k], 'invalid'); delete cur[k]; }
        });
        tlqSave(cur);
      } });
  }
  setInterval(tlqFlush, 20000); setTimeout(tlqFlush, 4000);
  function postTl(rows) {
    var out = rows.map(function (r) {
      return { id: r.id, h: r.hash, c: r.card, t: r.position || 0, d: r.duration || 0, p: r.percent || 0, u: r.watched_at || Date.now(),
               event: (r.manual ? 'manual:' : '') + TL_SESSION + ':' + (++tlSeq), session: TL_SESSION, op: r.op || (r.duration > 0 && r.percent >= 90 ? 'completed' : 'progress') };
    });
    try {
      if(window.HomeUser && window.AndroidJS && typeof AndroidJS.tlEnqueueOwned==='function'){if(AndroidJS.tlEnqueueOwned(JSON.stringify(out),JSON.stringify(HomeUser.owner)))return out;}
      else if (window.AndroidJS && typeof AndroidJS.tlEnqueue === 'function' && AndroidJS.tlEnqueue(JSON.stringify(out))) return out;
    } catch (e) {}
    var q = tlq(); out.forEach(function (r) { r._t = Date.now(); q[r.h + '|' + r.id] = r; });
    tlqSave(q);                 // при сбое записи очередь остаётся в памяти страницы (memQ) и повторяется; без памяти страницы действие уже не сохранить
    tlqFlush();
    return out;
  }

  window.__homeTlQ = { post: postTl, flush: tlqFlush, pending: function(card){var q=tlq();return Object.keys(q).some(function(k){return !q[k].c||q[k].c===card;});} };          // для проверок: отправка отметки и сброс резервной очереди

  // Share one confirmation poll when a whole season is marked at once.
  var markWaiters=[],markPollTimer=0;
  function finishWaiter(w, ok) { if (typeof w.done === 'function') w.done(!!ok); }
  function clearEpisodeMarkWaiters() {
    if (markPollTimer) { clearTimeout(markPollTimer); markPollTimer = 0; }
    markWaiters.forEach(function (w) { finishWaiter(w, false); });
    markWaiters = [];
  }
  function pollEpisodeMarks(){
    markPollTimer=0;
    if(!markWaiters.length)return;
    $.ajax({url:BASE+'/home/tl/all',dataType:'json',cache:false,timeout:3000,
      success:function(d){finish(d.rows||[]);},error:function(){finish([]);}});
    function finish(rows){
      var byId={};rows.forEach(function(r){if(r.id)byId[r.id]=r;});
      var pending=[];var callbacks=[];
      markWaiters.forEach(function(w){
        if(w.token&&(!window.HomeUser||HomeUser.owner.token!==w.token)){callbacks.push(function(){finishWaiter(w,false);});return;}
        var found=byId[w.sent.id];
        if(found&&found.u>=w.sent.u){
          var confirmed=found.op===w.sent.op;
          if(confirmed)try{var tl=Lampa.Timeline.view(w.sent.h);tl.time=found.t;tl.duration=found.d;tl.percent=found.p;tl.updated=found.u;tl.received=true;Lampa.Timeline.update(tl);}catch(e){}
          callbacks.push(function(){finishWaiter(w,confirmed);});
        }else if(Date.now()>=w.deadline)callbacks.push(function(){finishWaiter(w,false);});
        else pending.push(w);
      });
      markWaiters=pending;
      callbacks.forEach(function(f){f();});
      if(markWaiters.length&&!markPollTimer)markPollTimer=setTimeout(pollEpisodeMarks,450);
    }
  }
  window.HomeEpisodeMark=function(row,done){
    row.manual=true;
    var sent=postTl([row]);
    if(!sent||!sent.length){finishWaiter({done:done},false);return;}
    markWaiters.push({sent:sent[0],deadline:Date.now()+14000,token:window.HomeUser&&HomeUser.owner.token,done:done});
    if(!markPollTimer)markPollTimer=setTimeout(pollEpisodeMarks,150);
  };
  window.__homeClearEpisodeMarks=clearEpisodeMarkWaiters;

  function nativeOwnsProgress() { return !!(window.AndroidJS && AndroidJS.tlEnqueue); }
  function startPositionPush(item, season, playlist) {
    if (nativeOwnsProgress()) { posState = null; return; }
    stopPositionPush(false);
    var tmdb = currentCard && parseInt(currentCard.id, 10);
    // многосерийные «фильмы» имеют свой ключ и в общую схему не входят
    var native = playlist.length && playlist[0].kp && playlist[0].kp.native;
    posState = { tmdb: tmdb || 0, season: season, seen: {}, timer: null, sampler: null, enabled: !!(tmdb && native) };
    if (!posState.enabled) return;
    posState.sampler = setInterval(samplePosition, 10000);
    posState.timer = setInterval(function () { samplePosition(); pushPositions(); }, 60000);
  }

  function stopPositionPush(final) {
    if (!posState) return;
    clearInterval(posState.sampler);
    clearInterval(posState.timer);
    if (final && posState.enabled) {
      samplePosition();
      pushPositions();
      kickSync(4000);
    }
    posState = null;
  }

  // Название, из которого Lampa считает ключ позиции: оригинальное название карточки TMDB
  var currentCard = null;

  function hasNativeKey(item, season, count) {
    var orig = '';
    if (currentCard) orig = (season ? currentCard.original_name : currentCard.original_title) || currentCard.original_name || currentCard.original_title || '';
    if (!orig) orig = splitTitle(item.title).orig || '';
    return !!orig && (!!season || count <= 1);
  }

  function nativeHash(item, season, v, count) {
    var orig = '';
    if (currentCard) orig = (season ? currentCard.original_name : currentCard.original_title) || currentCard.original_name || currentCard.original_title || '';
    if (!orig) orig = splitTitle(item.title).orig || '';
    // многосерийные «фильмы» (части) оставляем со своим ключом
    if (!season && count > 1) return Lampa.Utils.hash('kp' + v.id);
    if (!orig) return Lampa.Utils.hash('kp' + v.id);
    return Lampa.Utils.hash(season ? [season, season > 10 ? ':' : '', v.number, orig].join('') : orig);
  }

  function playList(item, season, list, index) {
    var prev = baseCtrl();
    // ключ тайтла для памяти звуковой дорожки (общая для серий сериала)
    try { window.__apKey = (currentCard && currentCard.id) ? currentCard.id + '_' + (season ? 'tv' : 'movie') : 'kp' + item.id; window.__apKeyAt = Date.now(); } catch (x) {}
    devLoad(function () {
      var title = splitTitle(item.title).ru;
      var playlist = [];
      list.forEach(function (v) {
        var url = pickUrl(v.files);
        if (!url) return;
        var qLab = /2160|4k/i.test(lastPickQ) ? '4K' : /1080/.test(lastPickQ) ? 'FHD' : /720/.test(lastPickQ) ? 'HD' : /\d/.test(lastPickQ) ? 'SD' : '';
        var hash = nativeHash(item, season, v, list.length);
        var tl = Lampa.Timeline.view(hash);
        var w = v.watching || {};
        if (!window.HomeUser && !tl.time && w.time && w.status !== 1 && v.duration) {
          tl.time = w.time;
          tl.duration = v.duration;
          tl.percent = Math.round(w.time / v.duration * 100);
          try { Lampa.Timeline.update(tl); } catch (e) {}
        }
        var name = season ? title + ' · S' + season + 'E' + v.number + (v.title ? ' · ' + splitTitle(v.title).ru : '') : (v.title ? title + ' · ' + v.title : title);
        playlist.push({ title: name, url: url, timeline: tl, hls_type: 'hlsjs', kp: { item: item.id, season: season, video: v.number, hash: hash, native: hasNativeKey(item, season, list.length) }, hs: { src: 'kinopub', item: item.id, season: season || 0, ep: v.number, q: qLab } });
      });
      if (!playlist.length) return Lampa.Noty.show('Кинопаб: нет доступного потока');

      var start = Math.min(index, playlist.length - 1);
      hookHls();
      startPlay(playlist, start);

      startPositionPush(item, season, playlist);

      Lampa.Player.callback(function () {
        stopPositionPush(!nativeOwnsProgress());
        if (!nativeOwnsProgress()) { var cur = playlist[start]; if (cur) report(cur.kp.item, cur.kp.season, cur.kp.video, cur.kp.hash); }
        Lampa.Controller.toggle(prev);
      });
    });
  }

  // «Продолжить» из карточки (continue.js): та же серия или следующая, если эта досмотрена
  window.HomeSrc = window.HomeSrc || {};
  window.HomeSrc.kinopub = {
    // Какая серия идёт после rec (досмотренной) по данным Кинопаба: cb({season, ep}) или cb(null), если дальше ничего нет; cb(undefined) — не удалось узнать
    next: function (rec, cb) {
      get('items/' + rec.item, function (d) {
        var item = d && d.item;
        if (!item || !item.seasons || !item.seasons.length) return cb(undefined);
        var seasons = item.seasons.slice().sort(function (a, b) { return a.number - b.number; });
        var si = -1, ei = -1;
        seasons.forEach(function (s, i) { if (s.number === rec.season) si = i; });
        if (si < 0) return cb(undefined);
        seasons[si].episodes.forEach(function (e, i) { if (e.number === rec.ep) ei = i; });
        if (ei < 0) return cb(undefined);
        ei++;
        if (ei >= seasons[si].episodes.length) { si++; ei = 0; }
        if (si >= seasons.length || !seasons[si].episodes[ei]) return cb(null);
        cb({ season: seasons[si].number, ep: seasons[si].episodes[ei].number });
      }, function () { cb(undefined); });
    },
    resume: function (rec, movie, media, ok, fail) {
      currentCard = movie;
      get('items/' + rec.item, function (d) {
        var item = d && d.item;
        if (!item) return fail('Кинопаб не вернул тайтл');
        var list, season = 0, idx = 0;
        if (item.seasons && item.seasons.length) {
          var seasons = item.seasons.slice().sort(function (a, b) { return a.number - b.number; });
          var si = -1, ei = -1;
          seasons.forEach(function (s, i) { if (s.number === rec.season) si = i; });
          if (si < 0) return fail('сезон ' + rec.season + ' не найден');
          seasons[si].episodes.forEach(function (e, i) { if (e.number === rec.ep) ei = i; });
          if (ei < 0) return fail('серия ' + rec.ep + ' не найдена');
          if (rec.done) {
            ei++;
            if (ei >= seasons[si].episodes.length) {
              si++; ei = 0;
              if (si >= seasons.length) return fail('следующей серии нет');
            }
          }
          season = seasons[si].number; list = seasons[si].episodes; idx = ei;
        } else {
          list = item.videos || [];
          if (rec.ep != null) list.forEach(function (v, i) { if (v.number === rec.ep) idx = i; });
        }
        if (!list[idx] || !pickUrl(list[idx].files)) return fail('нет доступного потока');
        playList(item, season, list, idx);
        ok();
      }, function () { fail('Кинопаб не отвечает'); });
    }
  };

  window.__kp_open = function (kpId) { try { openItem({ kp_id: kpId }); } catch (e) {} };
  // Lampa Media Center: «Смотреть все» из ленты лаунчера — страница раздела со своей бесконечной прокруткой (kp_list подгружает следующие страницы сам)
  window.__kp_section = function (title, mode) { try { openSection(title, mode, true); } catch (e) {} };

  function openItem(card) {
    var id = card.kp_id || (card.kp && card.kp.id);
    if (!id) return;
    // Going back from episodes supplies only kp_id; keep the canonical TMDB identity.
    if (!card.id && currentCard && (currentCard.kp_id || (currentCard.kp && currentCard.kp.id)) == id) card = currentCard;
    currentCard = card;
    var prev = baseCtrl();
    Lampa.Noty.show('Кинопаб: загрузка…');
    get('items/' + id, function (d) {
      var item = d.item;
      if (item.seasons && item.seasons.length) {
        Lampa.Select.show({
          title: splitTitle(item.title).ru,
          items: item.seasons.map(function (s) {
            var w = s.episodes.filter(function (e) { return epDone(item, s, e); }).length;
            return { title: 'Сезон ' + s.number, subtitle: 'просмотрено ' + w + ' из ' + s.episodes.length, season: s };
          }),
          onBack: function () { Lampa.Controller.toggle(prev); },
          onSelect: function (a) { openEpisodes(item, a.season, prev); },
          onLong: function (a) { seasonMenu(item, a.season, prev, card); }
        });
      } else if (item.videos && item.videos.length > 1) {
        Lampa.Select.show({
          title: splitTitle(item.title).ru,
          items: item.videos.map(function (v, i) { return { title: v.title || ('Часть ' + (i + 1)), idx: i }; }),
          onBack: function () { Lampa.Controller.toggle(prev); },
          onSelect: function (a) { playList(item, 0, item.videos, a.idx); }
        });
      } else if (item.videos && item.videos.length) {
        playList(item, 0, item.videos, 0);
      } else {
        Lampa.Noty.show('Кинопаб: видео не найдено');
      }
    }, function () {
      Lampa.Noty.show('Кинопаб: не удалось загрузить');
    });
  }

  // ---------- отметки просмотра: серия и сезон (удержание «ОК» в списке) ----------
  function epLocalHash(item, season, e) {
    return nativeHash(item, season.number, { number: e.number, id: e.id }, season.episodes.length);
  }

  function epDone(item, season, e) {
    var local = '';
    try { local = window.__home_state(Lampa.Timeline.view(epLocalHash(item, season, e))).s; } catch (x) {}
    return (window.HomeUser ? false : e.watched === 1) || local === 'watched';
  }

  // status: 1 — просмотрено, 0 — снять. Сервер kino.pub (меняем только если состояние отличается) и локальный таймлайн (метка в списке)
  function setEpisode(item, season, e, status, done) {
    if (window.HomeUser && currentCard && currentCard.id) {
      var duration = Lampa.Timeline.view(epLocalHash(item, season, e)).duration || e.duration || 3600;
      return window.HomeEpisodeMark({ id:'tv-'+currentCard.id+'-s'+season.number+'e'+e.number,
        hash:epLocalHash(item,season,e),card:currentCard.id+'_tv',position:status?duration:0,
        duration:duration,percent:status?100:0,watched_at:Date.now(),op:status?'completed':'reset' }, done);
    }
    var finish = function () {
      try {
        var tl = Lampa.Timeline.view(epLocalHash(item, season, e));
        if (status === 1) { tl.duration = tl.duration || e.duration || 3600; tl.time = tl.duration; tl.percent = 100; }
        else { tl.time = 0; tl.percent = 0; }
        Lampa.Timeline.update(tl);
        if(window.HomeUser&&currentCard&&currentCard.id)postTl([{id:'tv-'+currentCard.id+'-s'+season.number+'e'+e.number,hash:epLocalHash(item,season,e),card:currentCard.id+'_tv',position:tl.time||0,duration:tl.duration||0,percent:tl.percent||0,watched_at:Date.now(),op:status===1?'completed':'reset'}]);
      } catch (x) {}
      if (done) done(true);
    };
    var server = e.watched === 1;
    var w = e.watching || {};
    if ((status === 1 && !server) || (status === 0 && server))
      get('watching/toggle?id=' + item.id + '&season=' + season.number + '&video=' + e.number, finish, finish);
    else if (status === 0 && w.time > 0)
      get('watching/marktime?id=' + item.id + '&season=' + season.number + '&video=' + e.number + '&time=0', finish, finish);
    else finish();
  }

  function episodeMenu(item, season, idx, prev) {
    var e = season.episodes[idx];
    var done = epDone(item, season, e);
    var reopen = function () {
      get('items/' + item.id, function (d) {
        var it = d.item;
        var s = (it.seasons || []).filter(function (x) { return x.number === season.number; })[0];
        if (s) openEpisodes(it, s, prev); else openItem({ kp_id: item.id });
      }, function () { Lampa.Controller.toggle(prev); });
    };
    Lampa.Select.close();
    setTimeout(function () {
      Lampa.Select.show({
        title: e.number + '. ' + splitTitle(e.title).ru,
        items: [{ title: done ? 'Снять отметку просмотра' : 'Отметить просмотренным', act: 'toggle' }],
        onBack: reopen,
        onSelect: function () {
          setEpisode(item, season, e, done ? 0 : 1, function (confirmed) {
            if(!confirmed){Lampa.Noty.show('Отметка ожидает синхронизации');reopen();return;}
            Lampa.Noty.show(done ? 'Отметка снята' : 'Отмечено как просмотренное');
            setTimeout(reopen, 200);
          });
        }
      });
    }, 200);
  }

  function seasonMenu(item, season, prev, card) {
    var reopen = function () { setTimeout(function () { openItem(card || { kp_id: item.id }); }, 200); };
    Lampa.Select.close();
    setTimeout(function () {
      Lampa.Select.show({
        title: 'Сезон ' + season.number,
        items: [{ title: 'Отметить весь сезон просмотренным', act: 1 }, { title: 'Снять отметки со всего сезона', act: 0 }],
        onBack: reopen,
        onSelect: function (a) {
          var list = season.episodes, left = list.length, pending = false;
          if (!left) return reopen();
          Lampa.Noty.show(a.act ? 'Отмечаю сезон…' : 'Снимаю отметки…');
          list.forEach(function (e) {
            setEpisode(item, season, e, a.act, function (confirmed) { pending = pending || !confirmed; if (--left === 0) { Lampa.Noty.show(pending ? 'Часть отметок ожидает синхронизации' : 'Готово'); reopen(); } });
          });
        }
      });
    }, 200);
  }

  function openEpisodes(item, season, prev) {
    var first = 0;
    season.episodes.forEach(function (e, i) { if (!first && !epDone(item, season, e)) first = i + 1; });
    Lampa.Select.show({
      home_episodes: true,
      title: splitTitle(item.title).ru + ' · сезон ' + season.number + ' (удержание ОК — отметка)',
      items: season.episodes.map(function (e, i) {
        var w = e.watching || {};
        var local = { s: '' };
        try { local = window.__home_state(Lampa.Timeline.view(nativeHash(item, season.number, { number: e.number, id: e.id }, season.episodes.length))); } catch (x) {}
        var done = epDone(item, season, e);
        if(window.HomeUser)w={};
        var part = !done && (w.time > 60 || local.s === 'started');
        var sub = done ? 'просмотрено' : (w.time > 60 ? 'остановлено на ' + Math.round(w.time / 60) + ' мин' : (part ? 'начато' : ''));
        var mk = done ? '<span style="color:#2ecc71;font-weight:700">✓</span> ' : (part ? '<span style="color:#f9a825;font-weight:700">▶</span> ' : '');
        return { title: mk + e.number + '. ' + splitTitle(e.title).ru, subtitle: sub, idx: i, selected: (first ? i === first - 1 : i === 0) };
      }),
      onBack: function () { openItem({ kp_id: item.id }); },
      onSelect: function (a) { playList(item, season.number, season.episodes, a.idx); },
      onLong: function (a) { episodeMenu(item, season, a.idx, prev); }
    });
  }

  // ---------- действия над карточкой (долгое нажатие): «Я смотрю», закладки, просмотр ----------
  function postForm(path, data, ok, fail) {
    $.ajax({
      url: API + path, type: 'POST', data: data, dataType: 'json', timeout: 30000, headers: hdr(),
      success: ok, error: function (x) { if (fail) fail(x); }
    });
  }

  function isSerial(item) {
    return item.type !== 'movie' && item.type !== '3d' && item.type !== 'concert' && item.type !== 'documovie';
  }

  function openCardMenu(card) {
    var id = card && (card.kp_id || (card.kp && card.kp.id));
    if (!id) return;
    var prev = baseCtrl();

    get('items/' + id, function (d) {
      var item = d.item;
      var title = splitTitle(item.title).ru;
      var items = [];
      if (isSerial(item)) {
        items.push({ title: item.subscribed ? 'Убрать из «Я смотрю»' : 'Добавить в «Я смотрю»', act: 'watchlist' });
      }
      items.push({ title: 'Закладки kino.pub', subtitle: (item.bookmarks && item.bookmarks.length) ? item.bookmarks.map(function (f) { return f.title; }).join(', ') : 'не в закладках', act: 'bookmarks' });
      items.push({ title: 'Отметить просмотренным', act: 'seen', st: 1 });
      items.push({ title: 'Снять отметку просмотра', act: 'seen', st: 0 });

      Lampa.Select.show({
        title: title,
        items: items,
        onBack: function () { Lampa.Controller.toggle(prev); },
        onSelect: function (a) {
          if (a.act === 'watchlist') {
            get('watching/togglewatchlist?id=' + id, function () {
              Lampa.Noty.show(item.subscribed ? 'Убрано из «Я смотрю»' : 'Добавлено в «Я смотрю»');
              Lampa.Controller.toggle(prev);
            }, function () { Lampa.Noty.show('Кинопаб: не удалось изменить'); Lampa.Controller.toggle(prev); });
          } else if (a.act === 'bookmarks') {
            setTimeout(function () { openFolders(id, title, prev); }, 350);
          } else if (a.act === 'seen') {
            markSeen(item, a.st, prev);
          }
        }
      });
    }, function () { Lampa.Noty.show('Кинопаб: не удалось загрузить'); });
  }

  function markSeen(item, status, prev) {
    var calls = [];
    if (item.seasons && item.seasons.length) {
      item.seasons.forEach(function (s) {
        s.episodes.forEach(function (e) {
          var already = e.watched === 1;
          if ((status === 1 && !already) || (status === 0 && already))
            calls.push('watching/toggle?id=' + item.id + '&season=' + s.number + '&video=' + e.number);
        });
      });
    } else if (item.videos && item.videos.length) {
      item.videos.forEach(function (v) {
        var already = v.watched === 1;
        if ((status === 1 && !already) || (status === 0 && already))
          calls.push('watching/toggle?id=' + item.id + '&video=' + v.number);
      });
    }
    if (!calls.length) {
      Lampa.Noty.show('Ничего менять не нужно');
      return Lampa.Controller.toggle(prev);
    }
    var left = calls.length;
    Lampa.Noty.show(status ? 'Отмечаю просмотренным…' : 'Снимаю отметки…');
    calls.forEach(function (c) {
      get(c, function () { if (--left === 0) { Lampa.Noty.show('Готово'); Lampa.Controller.toggle(prev); } },
        function () { if (--left === 0) { Lampa.Noty.show('Готово, часть запросов не прошла'); Lampa.Controller.toggle(prev); } });
    });
  }

  // Папки закладок kino.pub для элемента: нажатие переключает принадлежность папке
  function openFolders(id, title, prev) {
    get('bookmarks', function (all) {
      get('bookmarks/get-item-folders?item=' + id, function (cur) {
        var have = {};
        (cur.folders || []).forEach(function (f) { have[f.id] = true; });
        var items = (all.items || []).map(function (f) {
          return { title: (have[f.id] ? '✓  ' : '·  ') + f.title, subtitle: f.count + ' шт.', folder: f, has: !!have[f.id] };
        });
        items.push({ title: '+  Создать папку', create: true });

        Lampa.Select.show({
          title: title + ' · закладки',
          items: items,
          onBack: function () { Lampa.Controller.toggle(prev); },
          onSelect: function (a) {
            if (a.create) {
              Lampa.Input.edit({ title: 'Название папки', value: '', free: true, nosave: true }, function (val) {
                if (!val) return openFolders(id, title, prev);
                postForm('bookmarks/create', { title: val }, function () {
                  Lampa.Noty.show('Папка создана');
                  setTimeout(function () { openFolders(id, title, prev); }, 350);
                }, function () { Lampa.Noty.show('Не удалось создать папку'); });
              });
              return;
            }
            // remove-item принимает только JSON, add — и форму, и JSON
            (a.has ? post : postForm)(a.has ? 'bookmarks/remove-item' : 'bookmarks/add', { item: id, folder: a.folder.id }, function () {
              Lampa.Noty.show(a.has ? 'Убрано из «' + a.folder.title + '»' : 'Добавлено в «' + a.folder.title + '»');
              setTimeout(function () { openFolders(id, title, prev); }, 350);
            }, function () { Lampa.Noty.show('Кинопаб: не удалось изменить закладки'); });
          }
        });
      });
    });
  }

  // ---------- сетка раздела ----------
  function component(object) {
    var catalogSession=Date.now().toString(36)+'-'+Math.random().toString(36).slice(2);
    var comp = new Lampa.InteractionCategory(object);
    var sectionEnded=false, shown=0, pumps=0, pumping=false, busy=false, destroyed=false;
    var cards=[], focused=-1, seen=Object.create(null), nextPage=1, retryTimer=null, failures=0, built=false;
    var ageTries=0, ageTimer=null;
    var ageWait=[2000,4000,8000,8000];
    var originalDestroy=comp.destroy;
    // InteractionCategory increments page before the request and keeps its cards private.
    // Own the cursor: only a successful response advances it, appending never rebuilds the grid.
    comp.next=function(){ loadPage(); };
    comp.destroy=function(){
      destroyed=true;
      if(retryTimer!==null)clearTimeout(retryTimer);
      if(ageTimer!==null){clearTimeout(ageTimer);ageTimer=null;}
      cards=[];
      return originalDestroy.apply(comp,arguments);
    };
    var stopLogged='';
    function catLog(code, x) {
      try { if (window.AndroidJS && AndroidJS.dlog) AndroidJS.dlog('i', code, String(object.kp || ''), String(x || '').slice(0, 150)); } catch (e) {}
    }
    function sectionPage(res){
      var more=typeof res.has_more==='boolean'?res.has_more:(res.total_pages||1)>(res.page||1);
      sectionEnded=!more;
      res.results=(res.results||[]).filter(function(c){
        var id=c.id!=null?c.id:(c.kp_id!=null?'kp:'+c.kp_id:c.video_id);
        if(id==null)return true;
        var key=(c.provider||c.media_type||(c.name?'tv':'movie'))+':'+id;
        if(seen[key])return false;
        seen[key]=true;return true;
      });
      var n=res.results.length;
      shown+=n;
      if(more)res.total_pages=Math.max(res.total_pages||1,500);
      var last=n?(res.results[n-1].title||res.results[n-1].name||''):'';
      catLog('cat_page','p='+(res.page||1)+' n='+n+' shown='+shown+' more='+(more?1:0)+' end='+(sectionEnded?1:0)+' last='+String(last).slice(0,40));
      return res;
    }
    function nearEnd(){
      return shown<36||(focused>=0&&cards.length-focused<=12);
    }
    function pump(){
      if(destroyed)return;
      var why=sectionEnded?'ended':!nearEnd()?'not_near':'';
      if(why){
        if(!stopLogged||stopLogged!==why){ stopLogged=why; catLog('cat_stop', why+' shown='+shown+' pumps='+pumps); }
        return;
      }
      if(pumping||busy||retryTimer!==null||ageTimer!==null)return;
      pumping=true; pumps++;
      setTimeout(function(){ pumping=false; if(!destroyed&&!sectionEnded)comp.next(); }, 40);
    }

    function retry(){
      busy=false;failures++;
      catLog('cat_retry','page='+nextPage+' shown='+shown+' attempt='+failures);
      // Retain every displayed card and retry the SAME cursor; never turn a timeout into end-of-list.
      if(failures===1)Lampa.Noty.show('Не удалось загрузить следующую страницу. Повторяем…');
      retryTimer=setTimeout(function(){retryTimer=null;loadPage();},Math.min(15000,1000*Math.pow(2,Math.min(failures-1,4))));
    }
    function retryAge(){
      if(destroyed)return;
      if(ageTries>=ageWait.length){
        try{comp.activity.loader(false);}catch(e){}
        Lampa.Noty.show('Возраст ещё проверяется');
        return;
      }
      var wait=ageWait[ageTries++];
      ageTimer=setTimeout(function(){ageTimer=null;loadPage();},wait);
    }
    function loadPage(){
      if(destroyed||sectionEnded||busy||retryTimer!==null||ageTimer!==null)return;
      busy=true;
      var requested=nextPage;
      fetchItems(object.kp,requested,function(res){
        if(destroyed)return;
        if(res.retry_page===true){
          busy=false;failures=0;retryAge();
          return;
        }
        busy=false;failures=0;ageTries=0;
        object.page=requested;nextPage=requested+1;
        res=sectionPage(res);
        if(!built){if(res.results.length||sectionEnded){comp.build(res);built=true;}}
        else{comp.append(res,true);comp.limit();}
        pump();
      },function(){if(!destroyed)retry();},catalogSession);
    }
    comp.create=function(){this.activity.loader(true);loadPage();};

    comp.cardRender = function (obj, element, card) {
      var index=cards.length;
      cards.push(card);
      var prevFocus=card.onFocus;
      card.onFocus=function(){focused=index;if(typeof prevFocus==='function')prevFocus.apply(card,arguments);pump();};
      if(element.provider==='youtube'){
        card.onEnter=function(){if(window.__yt)window.__yt.play(element.video_id,element.title);};
        try{card.render().find('img').attr('src',element.thumb);}catch(e){}
      }
      if(element.history_card)card.onMenu=function(){rowMenu('history',card.render(),element);};
      if (element.source === 'kinopub') {
        card.onMenu = function () { openCardMenu(element); };
        card.onEnter = function () { openItem(element); };
      }
    };

    return comp;
  }

  Lampa.Component.add('kp_list', component);

  function openSection(title, mode, fromShell) {
    var show=function () {
      Lampa.Activity.push({ url: '', title: title, component: 'kp_list', page: 1, kp: mode });
      // Глубина после открытия раздела. Иначе «Назад» снимает главную Lampa, которая была на экране до пуша.
      if (fromShell) { window.__mc_base = Lampa.Activity.all().length; window.__mc_ret = 2; }
    };
    if(window.HomeUser&&!/^fresh_|^bookmark:/.test(mode))show();else ensureAuth(show);
  }

  // ---------- закладки ----------
  // Общий список: закладки Lampa + папки kino.pub
  var LAMPA_BOOKMARKS = [
    ['book', 'title_book'],
    ['like', 'title_like'],
    ['wath', 'title_wath'],
    ['history', 'title_history']
  ];

  function lampaCount(type) {
    try { return Lampa.Favorite.get({ type: type }).length; } catch (e) { return 0; }
  }

  function openBookmarks() {
    var prev = baseCtrl();

    function show(folders, kpState) {
      var items = LAMPA_BOOKMARKS.map(function (b) {
        var name = Lampa.Lang.translate(b[1]);
        return { title: 'Lampa · ' + name, subtitle: lampaCount(b[0]) + ' шт.', lampa: b[0], name: name };
      });
      if (kpState === 'noauth') {
        items.push({ title: 'Кинопаб · войти в аккаунт', subtitle: 'чтобы увидеть закладки kino.pub', login: true });
      } else {
        folders.forEach(function (f) {
          items.push({ title: 'Кинопаб · ' + f.title, subtitle: f.count + ' шт.', kp_folder: f.id, name: f.title });
        });
      }
      Lampa.Select.show({
        title: 'Закладки',
        items: items,
        onBack: function () { Lampa.Controller.toggle(prev); },
        onSelect: function (a) {
          if (a.lampa) {
            Lampa.Activity.push({ url: '', title: a.name, component: 'favorite', type: a.lampa, page: 1 });
          } else if (a.kp_folder) {
            Lampa.Activity.push({ url: '', title: a.name, component: 'kp_list', page: 1, kp: 'bookmark:' + a.kp_folder });
          } else if (a.login) {
            showAuth(openBookmarks);
          }
        }
      });
    }

    checkAuth(function (ok) {
      if (!ok) return show([], 'noauth');
      get('bookmarks', function (d) { show(d.items || [], 'ok'); }, function () { show([], 'ok'); });
    });
  }

  function openKinopubMenu() {
    ensureAuth(openSettings);
  }

  // ---------- боковое меню ----------
  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M4 5h16v12H4z M2 19h20v2H2z M10 8l5 3-5 3z" fill-rule="evenodd"/></svg>';

  function addMenu(action, title, fn) {
    if ($('.menu .menu__list [data-action="' + action + '"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="' + action + '">' +
      '<div class="menu__ico">' + ICON + '</div><div class="menu__text">' + title + '</div></li>');
    btn.on('hover:enter', fn);
    $('.menu .menu__list').eq(0).append(btn);
  }

  // Plex: пункт меню только если библиотека включена и работает
  function addPlexMenu() {
    $.ajax({
      url: BASE + '/local/status', dataType: 'json', timeout: 60000, cache: false,
      success: function (d) {
        var plex = (d.sources || []).filter(function (x) { return x.id === 'plex'; })[0];
        if (!plex || !plex.enable || plex.state !== 'ok') return;
        addMenu('plex', 'Plex', openPlexMenu);
        placeMenu();
      }
    });
  }

  function openPlexMenu() {
    var prev = baseCtrl();
    $.ajax({
      url: BASE + '/local/sections', dataType: 'json', timeout: 60000, cache: false,
      success: function (d) {
        var items = [{ title: 'Недавно добавлено', subtitle: (d.total || 0) + ' шт. · все разделы', key: '' }];
        (d.sections || []).forEach(function (sec) { items.push({ title: sec.title, subtitle: sec.count + ' шт.', key: sec.key }); });
        Lampa.Select.show({
          title: 'Plex',
          items: items,
          onBack: function () { Lampa.Controller.toggle(prev); },
          onSelect: function (a) {
            setTimeout(function () {
              Lampa.Activity.push({ url: '', title: a.key ? 'Plex · ' + a.title : 'Plex', component: 'kp_list', page: 1, kp: a.key ? 'plex:' + a.key : 'plex' });
            }, 300);
          }
        });
      },
      error: function () { Lampa.Noty.show('Plex: нет связи'); }
    });
  }

  function addMenus() {
    addMenu('kp_watching', 'Мои сериалы', function () { openSection('Мои сериалы', 'watching'); });
    addMenu('kp_watching_anime', 'Мои аниме', function () { openSection('Мои аниме', 'watching_anime'); });
    addMenu('kp_movies', 'Новые фильмы', function () { Lampa.Activity.push({ url: '', title: 'Новые фильмы', component: 'kp_list', page: 1, kp: 'new_movie' }); });
    addMenu('kp_serials', 'Новые сериалы', function () { Lampa.Activity.push({ url: '', title: 'Новые сериалы', component: 'kp_list', page: 1, kp: 'new_serial' }); });
    addMenu('kp_anime', 'Аниме', function () { Lampa.Activity.push({ url: '', title: 'Аниме', component: 'kp_list', page: 1, kp: 'anime' }); });
    addMenu('kp_ru_serials', 'Отечественные сериалы', function () { Lampa.Activity.push({ url: '', title: 'Отечественные сериалы', component: 'kp_list', page: 1, kp: 'ru_serial' }); });
    addMenu('kp_ru_movies', 'Отечественные фильмы', function () { Lampa.Activity.push({ url: '', title: 'Отечественные фильмы', component: 'kp_list', page: 1, kp: 'ru_movie' }); });
    addMenu('kp_my_movies', 'Мои фильмы', function () { Lampa.Activity.push({ url: '', title: 'Мои фильмы', component: 'kp_list', page: 1, kp: 'my_movies' }); });
    addMenu('kp_more', 'Кинопаб', openKinopubMenu);

    // Штатный пункт «История» открывает единую историю (Lampa + Кинопаб + торренты)
    var hist = $('.menu .menu__list [data-action="history"]');
    if (hist.length && !hist.data('kp_hooked')) {
      hist.data('kp_hooked', true);
      hist.off('hover:enter').on('hover:enter', function () {
        Lampa.Activity.push({ url: '', title: 'История', component: 'kp_list', page: 1, kp: 'history' });
      });
    }

    // Штатный пункт «Закладки» Lampa открывает общий список (Lampa + kino.pub)
    var fav = $('.menu .menu__list [data-action="favorite"]');
    if (fav.length && !fav.data('kp_hooked')) {
      fav.data('kp_hooked', true);
      fav.off('hover:enter').on('hover:enter', openBookmarks);
    }
  }

  // ---------- строки на главной ----------
  // Долгое удержание на карточке строки: убрать из «Продолжить просмотр» / «Я смотрю»
  function rowMenu(mode, target, card) {
    if (!card || !card.id) return;
    if(mode==='history'&&card.history_card){
      var controller=baseCtrl();
      Lampa.Select.show({title:card.title||card.name,items:[{title:'Убрать из истории'}],
        onBack:function(){Lampa.Controller.toggle(controller);},onSelect:function(){
          $.ajax({url:BASE+'/home/history/hide',type:'POST',contentType:'application/json',dataType:'json',data:JSON.stringify({card:card.history_card}),
            success:function(){Lampa.Noty.show('Убрано из истории');Lampa.Controller.toggle(controller);try{Lampa.Activity.active().activity.refresh();}catch(e){}},
            error:function(){Lampa.Noty.show('Не удалось изменить историю');Lampa.Controller.toggle(controller);}});
        }});return;
    }
    var media = (card.name || card.original_name || card.number_of_seasons) ? 'tv' : 'movie';
    var prev = baseCtrl();
    var title = mode === 'continue' ? 'Убрать из «Продолжить просмотр»' : (mode === 'my_movies' ? 'Убрать из «Мои фильмы»' : (mode === 'watching_anime' ? 'Убрать из «Мои аниме»' : 'Убрать из «Мои сериалы»'));
    Lampa.Select.show({
      title: card.title || card.name || 'Действия',
      items: [{ title: title, act: 'remove' }, { title: 'Открыть карточку', act: 'open' }],
      onBack: function () { Lampa.Controller.toggle(prev); },
      onSelect: function (a) {
        setTimeout(function () {
          if (a.act === 'open') { if (card.source === 'kinopub') openItem(card); else nativeOpen(card); return; }
          var url = mode === 'continue'
            ? BASE + '/kp/continue/hide?type=' + media + '&tmdb=' + card.id
            : BASE + '/kp/watching/remove?type=' + media + '&tmdb=' + card.id;
          $.ajax({
            url: url, dataType: 'json', timeout: 40000, cache: false, headers: hdr(),
            success: function () {
              $(target).closest('.card').remove();
              if (mode === 'continue') { try { Lampa.Favorite.remove('history', card); } catch (e) {} }   // значок воспроизведения на постере
              Lampa.Noty.show('Убрано');
              Lampa.Controller.toggle(prev);
            },
            error: function () { Lampa.Noty.show('Не удалось убрать'); Lampa.Controller.toggle(prev); }
          });
        }, 300);
      }
    });
  }

  function addRow(name, title, mode, index, noAuth) {
    if(window.HomeUser){var rule=(HomeUser.owner.profile.rows||{})[mode];if(rule){if(rule.enabled===false)return;index=rule.order;}}

    Lampa.ContentRows.add({
      name: name,
      title: title,
      index: index,
      screen: ['main'],
      call: function () {
        if (!noAuth && authState === false) return;
        return function (callback) {
          // в «Продолжить просмотр» — начатые выпуски Wrestling, в «Мои сериалы» — сериал Wrestling (их позиции хранит таймлайн Lampa)
          var wr = [];
          var step = function (res) {
            var results = wr.concat(res.results || []);
            if (!results.length) return callback({ results: [] });
            var ev = {
                onEnter: function (target, card) {
                  if (card && card.provider==='youtube') {if(window.__yt)window.__yt.play(card.video_id,card.title);}
                  else if (card && card.source === 'wrestling') { if (window.__wr_open) window.__wr_open(card); }
                  else if (card && card.source === 'kinopub') openItem(card);
                  else if (card) nativeOpen(card);
                }
            };
            if (mode === 'history' || mode === 'continue' || mode === 'watching' || mode === 'watching_anime' || mode === 'my_movies') ev.onMenu = function (target, card) { if (card && card.source === 'wrestling') { if (window.__wr_menu) window.__wr_menu(card); return; } rowMenu(mode, target, card); };
            callback({ results: results, title: title, nomore: true, card_events: ev });
          };
          var go = function () { fetchItems(mode, 1, step, function () { step({ results: [] }); }); };
          // начатые выпуски Wrestling в «Продолжить просмотр» не показываем: там только фильмы, а сериал Wrestling живёт в «Мои сериалы»
          if (mode === 'continue') go();
          else if (mode === 'watching' && window.__wr_watching) window.__wr_watching(function (cards) { wr = cards || []; go(); });
          else go();
        };
      }
    });
  }

  // Порядок бокового меню: Главная → Мои сериалы → Новые фильмы → Новые сериалы → Мои аниме → Аниме → Отечественные сериалы → Отечественные фильмы → Мои фильмы → Рестлинг → остальное
  function placeMenu() {
    var list = $('.menu .menu__list').eq(0);
    var anchor = list.find('[data-action="main"]').first();
    if (!anchor.length) return;
    // «Телевидение» — самая верхняя строка меню
    var tvItem = list.find('[data-action="livetv"]').first();
    if (tvItem.length && list.children().first()[0] !== tvItem[0]) list.prepend(tvItem);
    ['kp_watching', 'kp_movies', 'kp_serials', 'kp_watching_anime', 'kp_anime', 'kp_ru_serials', 'kp_ru_movies', 'kp_my_movies', 'wrestling', 'plex', 'livetv_cfg', 'yamusic', 'youtube', 'apps'].forEach(function (a) {
      var el = list.find('[data-action="' + a + '"]').first();
      if (!el.length) return;
      if (anchor.next()[0] !== el[0]) el.insertAfter(anchor);
      anchor = el;
      if (a === 'apps') {
        // закреплённые программы — сразу под пунктом «Приложения»
        list.find('[data-action^="app_"]').each(function () {
          if (anchor.next()[0] !== this) $(this).insertAfter(anchor);
          anchor = $(this);
        });
      }
    });
  }

  // Media Center: из настроек Lampa убираем разделы, которые задаются на сервере и не меняются на устройстве
  function trimSettings() {
    try {
      if (!(window.AndroidJS && typeof AndroidJS.mcDeviceId === 'function' && AndroidJS.mcDeviceId())) return;
      var folders = ['account', 'parser', 'tmdb', 'plugins', 'parental_control', 'shots', 'remote_configuration'];
      var params = ['protocol', 'request_caching'];
      var apply = function (root) {
        var r = $(root || '.settings');
        folders.forEach(function (c) { r.find('.settings-folder[data-component="' + c + '"]').remove(); });
        params.forEach(function (n) { r.find('.settings-param[data-name="' + n + '"]').remove(); });
      };
      Lampa.Settings.listener.follow('open', function (e) { apply(e && e.body ? e.body : null); setTimeout(apply, 50); });
      var mo = new MutationObserver(function () { apply(); });
      mo.observe(document.body, { childList: true, subtree: true });
    } catch (e) {}
  }

  // Любой переход на страницу карточки нашего источника открывает kino.pub, а не TMDB-страницу Lampa
  // Если приложение открыто сразу с карточки (канал на главном экране, ссылка), в стеке экранов нет главной,
  // и «Назад» сразу предлагает выйти. Подкладываем главную под карточку.
  function hasMain() {
    try { return Lampa.Activity.all().some(function (a) { return a.component === 'main'; }); } catch (e) { return true; }
  }

  function mainParams() {
    var src = Lampa.Storage.field('source');
    return { url: '', title: Lampa.Lang.translate('title_main') + ' - ' + String(src || 'tmdb').toUpperCase(), component: 'main', source: src, page: 1 };
  }

  function dropStaleBottom() {
    try {
      var arr = Lampa.Activity.all();
      if (arr.length >= 3 && arr[0].component === 'full' && arr[1].component === 'main') {
        var old = arr.shift();
        try { old.activity.destroy(); } catch (e) {}
      }
    } catch (e) {}
  }

  function fixStack() {
    try {
      var arr = Lampa.Activity.all();
      if (arr.length === 1 && arr[0].component === 'full') Lampa.Activity.push(Lampa.Activity.extractObject(arr[0]));
    } catch (e) {}
  }

  function hookActivity() {
    if (Lampa.Activity.__kp_hooked) return;
    Lampa.Activity.__kp_hooked = true;
    var orig = Lampa.Activity.push;
    Lampa.Activity.push = function (obj) {
      try {
        if (obj && obj.component === 'full') {
          var c = obj.card || {};
          if (obj.source === 'kinopub' || c.source === 'kinopub') {
            openItem(c.kp_id ? c : { kp_id: parseInt(String(obj.id || c.id).replace('kp', ''), 10) });
            return;
          }
        }
      } catch (e) {}
      try {
        if (obj && obj.component === 'full' && !hasMain()) {
          orig.call(this, mainParams());
          var r = orig.apply(this, arguments);
          setTimeout(dropStaleBottom, 800);
          return r;
        }
      } catch (e) {}
      return orig.apply(this, arguments);
    };
  }

  // Строки главной строятся один раз при открытии, и пока интерфейс открыт, новое (карточки, приложения, выпуски) не появляется.
  // Обновляем главную: при возвращении в приложение, при возврате на главную с других экранов (если данным больше 2 минут)
  // и в простое раз в 5 минут. Не трогаем, пока открыто меню/настройки/плеер или недавно нажимали кнопки.
  function hookAutoRefresh() {
    if (window.__kp_autorefresh) return;
    window.__kp_autorefresh = true;
    var builtAt = Date.now(), lastKey = Date.now(), hiddenAt = 0;
    document.addEventListener('keydown', function () { lastKey = Date.now(); }, true);

    function onMain() {
      try {
        var a = Lampa.Activity.active();
        if (!a || a.component !== 'main') return false;
        var c = Lampa.Controller.enabled().name;
        if (c !== 'items_line' && c !== 'content' && c !== 'head' && c !== 'menu') return false;
        // шаблоны .selectbox и .settings всегда есть в DOM, поэтому состояние берём у самой Lampa
        if (Lampa.Select.opened && Lampa.Select.opened()) return false;
        if (Lampa.Player.opened && Lampa.Player.opened()) return false;
        if (document.querySelector('.modal')) return false;
        var bc = document.body.classList;
        if (bc.contains('settings--open') || bc.contains('search--open')) return false;
        return true;
      } catch (e) { return false; }
    }

    function refreshMain(why) {
      if (!onMain()) return;
      builtAt = Date.now();
      try { Lampa.Activity.refresh(); } catch (e) {}
    }

    Lampa.Listener.follow('activity', function (e) {
      if (!e || e.component !== 'main') return;
      if (e.type === 'create') builtAt = Date.now();
      else if (e.type === 'start' && Date.now() - builtAt > 120000) setTimeout(function () { refreshMain('start'); }, 400);
    });

    document.addEventListener('visibilitychange', function () {
      if (document.hidden) { hiddenAt = Date.now(); return; }
      if (hiddenAt && Date.now() - hiddenAt > 30000 && Date.now() - builtAt > 60000) setTimeout(function () { refreshMain('visible'); }, 800);
    });

    setInterval(function () {
      if (Date.now() - builtAt > 5 * 60000 && Date.now() - lastKey > 60000) refreshMain('idle');
    }, 30000);
  }

  // Запоминаем экземпляр hls.js, который создаёт плеер Lampa: нужен, чтобы вернуть автоматический выбор качества
  function hookHls() {
    if (!window.Hls || window.Hls.__kp_hooked) return;
    var Orig = window.Hls;
    var P = new Proxy(Orig, {
      construct: function (target, args) {
        var inst = new (Function.prototype.bind.apply(target, [null].concat(args)))();
        window.__kp_hls = inst;
        return inst;
      }
    });
    P.__kp_hooked = true;
    window.Hls = P;
  }

  // В меню качества плеера (уровни hls.js) добавляем «Авто»
  function hookQualityMenu() {
    if (Lampa.Select.__kp_hooked) return;
    Lampa.Select.__kp_hooked = true;
    var orig = Lampa.Select.show;
    Lampa.Select.show = function (params) {
      try {
        var items = params && params.items;
        var isLevels = window.__kp_hls && Array.isArray(items) && items.length && typeof items[0].bitrate !== 'undefined' && typeof items[0].height !== 'undefined';
        if (isLevels) {
          var back = Lampa.Controller.enabled().name;
          var hls = window.__kp_hls;
          var autoItem = { title: 'Авто', kp_auto: true, selected: !!hls.autoLevelEnabled };
          if (autoItem.selected) items.forEach(function (i) { i.selected = false; });
          var onSelect = params.onSelect;
          params = $.extend({}, params, {
            items: [autoItem].concat(items),
            onSelect: function (a) {
              if (a && a.kp_auto) {
                hls.currentLevel = -1;
                items.forEach(function (i) { i.selected = false; });
                $('.player-panel__quality').text('AUTO');
                Lampa.Controller.toggle(back);
              } else if (onSelect) onSelect(a);
            }
          });
        }
      } catch (e) {}
      return orig.call(this, params);
    };
  }

  // ---------- страница карточки: кнопка «Кинопаб» рядом с торрентами ----------
  var lookupCache = {};

  function lookupKp(media, tmdbId, cb) {
    var key = media + ':' + tmdbId;
    if (lookupCache[key] !== undefined) return cb(lookupCache[key]);
    $.ajax({
      url: BASE + '/kp/lookup?type=' + media + '&tmdb=' + tmdbId, dataType: 'json', timeout: 40000, cache: false, headers: hdr(),
      success: function (d) { lookupCache[key] = d.kp || 0; cb(lookupCache[key]); },
      error: function () { cb(0); }
    });
  }

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function fmtLeft(sec) {
    var h = Math.floor(sec / 3600), m = Math.round((sec % 3600) / 60);
    if (m === 60) { h++; m = 0; }
    return (h ? h + ' ч ' : '') + m + ' мин';
  }

  // Недосмотренное: «Серия 3 · осталось 42 мин · закончится в 21:17» (данные общие для торрентов, Кинопаба и онлайна)
  function showProgress(render, media, tmdb) {
    $.ajax({
      url: BASE + '/kp/progress?type=' + media + '&tmdb=' + tmdb, dataType: 'json', timeout: 20000, cache: false,
      success: function (d) {
        var p = d && d.progress;
        if (!p || render.find('.kp-progress').length) return;
        var end = new Date(Date.now() + p.remaining * 1000);
        var what = (media === 'tv' && p.season) ? 'S' + p.season + 'E' + p.episode + ' · ' : '';
        var el = $('<div class="kp-progress" style="margin:.6em 0 .2em;font-size:1.15em;opacity:.9">' +
          '▶ ' + what + 'осталось ' + fmtLeft(p.remaining) + ' · закончится в ' + pad2(end.getHours()) + ':' + pad2(end.getMinutes()) + '</div>');
        var anchor = render.find('.full-start__buttons, .full-start-new__buttons').first();
        if (anchor.length) anchor.before(el); else render.find('.full-start__body, .full-start-new__body').first().append(el);
      }
    });
  }

  // ---------- «Я смотрю» в меню долгого нажатия на постере (для любого тайтла, не только из kino.pub) ----------
  var wlState = {};

  // название личного списка: сериалы — «Мои сериалы» (kino.pub «Я смотрю» + наши), фильмы — «Мои фильмы» (то, что собираемся посмотреть)
  function wlName(media,anime,card){var kind=card&&card.home_kind;if(kind==='cartoon_movie')return 'Мои мультфильмы';if(kind==='cartoon_series')return 'Мои мультсериалы';if(kind&&kind.indexOf('anime_')===0)return 'Мои аниме';return media==='tv'?(anime?'Мои аниме':'Мои сериалы'):'Мои фильмы';}

  function cardMedia(d) { return (d.name || d.original_name || d.first_air_date) ? 'tv' : 'movie'; }
  // аниме-карточка (жанр «мультфильм» + япония): личный список для неё называется «Мои аниме»
  function isAnimeCard(d) {
    if (d.home_kind) return d.home_kind.indexOf('anime_') === 0;
    try {
      var g = d.genre_ids || (d.genres || []).map(function (x) { return x.id; });
      if (!g || g.indexOf(16) < 0) return false;
      var oc = d.origin_country || [];
      return d.original_language === 'ja' || oc.indexOf('JP') >= 0;
    } catch (e) { return false; }
  }

  function toggleWatch(d) {
    var media = cardMedia(d), id = parseInt(d.id, 10);
    // желаемое состояние фиксируем при нажатии (по известному на экране) и повторяем его же при сбое — не переключаем заново
    var want = wlState[media + id] === true ? 0 : (wlState[media + id] === false ? 1 : null);
    var send = function (retry) {
      $.ajax({
        url: BASE + '/kp/watchlist/toggle?type=' + media + '&tmdb=' + id + (want === null ? '' : '&on=' + want), dataType: 'json', timeout: 40000, cache: false, headers: hdr(),
        success: function (r) {
          wlState[media + id] = !!r.on;
          Lampa.Noty.show(r.on ? 'Добавлено в «' + wlName(media,isAnimeCard(d),d) + '»' + (r.kino ? ' (и в kino.pub)' : '') : 'Убрано из «' + wlName(media,isAnimeCard(d),d) + '»');
        },
        error: function () {
          if (retry && want !== null) return send(false);
          Lampa.Noty.show('Не удалось изменить «' + wlName(media,isAnimeCard(d),d) + '»');
        }
      });
    };
    send(true);
  }

  function hookCardMenu() {
    try {
      if (window.__kp_card_menu) return;
      window.__kp_card_menu = true;
      var lastLong = null;
      // запоминаем карточку, на которой удерживали кнопку
      document.addEventListener('hover:long', function (e) {
        try {
          var el = e.target && e.target.closest ? e.target.closest('.card') : null;
          lastLong = el && el.card_data ? el.card_data : null;
        } catch (x) { lastLong = null; }
      }, true);

      var origShow = Lampa.Select.show;
      Lampa.Select.show = function (p) {
        try {
          var d = lastLong;
          // меню действий карточки: у его пунктов есть поле where (закладки/избранное)
          if (d && p && p.items && p.items.some(function (i) { return i && i.where; })
              && String(d.id) === String(parseInt(d.id, 10)) && String(d.source || 'tmdb') === 'tmdb') {
            var prevCtrl = Lampa.Controller.enabled().name;
            var on = wlState[cardMedia(d) + d.id];
            var wn = wlName(cardMedia(d),isAnimeCard(d),d);
            p.items.unshift({ title: on ? wn + ' ✓ — убрать' : 'Добавить в «' + wn + '»', kpwatch: true });
            var os = p.onSelect;
            p.onSelect = function (a) {
              if (a && a.kpwatch) {
                toggleWatch(d);
                try { Lampa.Select.close(); } catch (x) {}
                try { Lampa.Controller.toggle(prevCtrl); } catch (x) {}
                return;
              }
              return os ? os.apply(this, arguments) : undefined;
            };
            lastLong = null;
          }
        } catch (x) {}
        return origShow.apply(this, arguments);
      };
    } catch (e) {}
  }

  // Если в TMDB для тайтла нет русского названия или описания (бывает у свежих), берём их из Кинопаба: название, сюжет, «В ролях» и режиссёр
  function enrichFromKp(render, movie, kp) {
    var cyr = /[А-Яа-яЁё]/;
    var title = movie.title || movie.name || '', ov = movie.overview || '';
    var needTitle = !cyr.test(title), needOv = !ov || !cyr.test(ov);
    if (!needTitle && !needOv) return;
    get('items/' + kp, function (d) {
      var it = d && d.item; if (!it) return;
      try {
        if (needTitle) {
          var ru = splitTitle(it.title).ru;
          if (ru && cyr.test(ru)) {
            render.find('.full-start-new__title, .full-start__title').first().text(ru);
            if (movie.title) movie.title = ru; else movie.name = ru;
          }
        }
        if (needOv && it.plot) {
          var box = render.find('.full-descr__text').first();
          if (box.length) {
            var extra = '';
            if (it.director) extra += '<br><br><b>Режиссёр:</b> ' + $('<div>').text(it.director).html();
            if (it.cast) extra += '<br><b>В ролях:</b> ' + $('<div>').text(it.cast).html();
            box.html($('<div>').text(it.plot).html() + extra);
          }
          movie.overview = it.plot;
        }
      } catch (x) {}
    });
  }

  function hookFullPage() {
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      if (!movie.id || isNaN(parseInt(movie.id, 10)) || String(movie.id).indexOf('wr-') === 0) return;
      showProgress(e.object.activity.render(), media, movie.id);

      // «Я смотрю»: список общий (kino.pub + свои отметки), отмечать можно любой тайтл
      $.ajax({
        url: BASE + '/kp/watchlist/state?type=' + media + '&tmdb=' + movie.id, dataType: 'json', timeout: 40000, cache: false, headers: hdr(),
        success: function (st) {
          if(st.kind)movie.home_kind=st.kind;
          var render = e.object.activity.render();
          if (render.find('.view--watchlist').length) return;
          var on = !!st.on;
          var wb = $('<div class="full-start__button selector view--watchlist"><svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M12 5C7 5 2.7 8.1 1 12c1.7 3.9 6 7 11 7s9.3-3.1 11-7c-1.7-3.9-6-7-11-7zm0 11.5A4.5 4.5 0 1 1 12 7.5a4.5 4.5 0 0 1 0 9zm0-7a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z"/></svg><span></span></div>');
          var label = function () {
            wb.find('span').text(on ? wlName(media,isAnimeCard(movie),movie) + ' ✓' : 'В «' + wlName(media,isAnimeCard(movie),movie) + '»');
            wb.toggleClass('wl-on', on);
            render.find('.view--watchlist-menu span').text(on ? wlName(media,isAnimeCard(movie),movie) + ' ✓' : 'В «' + wlName(media,isAnimeCard(movie),movie) + '»');
            wb.attr('title', on ? wlName(media,isAnimeCard(movie),movie) + ' — убрать' : 'Добавить в «' + wlName(media,isAnimeCard(movie),movie) + '»');
            wlState[media + movie.id] = on;
          };
          label();
          wb.on('hover:enter', function () {
            setTimeout(function () {
              var want = on ? 0 : 1;          // желаемое состояние фиксируется при нажатии; повтор после сбоя отправляет то же самое
              var send = function (retry) {
                $.ajax({
                  url: BASE + '/kp/watchlist/toggle?type=' + media + '&tmdb=' + movie.id + '&on=' + want, dataType: 'json', timeout: 40000, cache: false, headers: hdr(),
                  success: function (r) {
                    on = !!r.on; label();
                    Lampa.Noty.show(on ? 'Добавлено в «' + wlName(media) + '»' + (r.kino ? ' (и в kino.pub)' : '') : 'Убрано из «' + wlName(media) + '»');
                  },
                  error: function () {
                    if (retry) return send(false);
                    Lampa.Noty.show('Не удалось изменить «' + wlName(media) + '»');
                  }
                });
              };
              send(true);
            }, 350);
          });
          var anchor = render.find('.view--torrent').first();
          var opt = render.find('.full-start-new__buttons .button--options').first();
          if (opt.length) opt.before(wb);
          else if (anchor.length) anchor.after(wb); else render.find('.full-start__buttons, .buttons--container').first().append(wb);
          // Lampa собирает кнопки для пульта один раз при открытии карточки: добавляем нашу в этот список сразу,
          // иначе фокус «перепрыгивает» через неё, пока не закроют какое-нибудь меню
          try { Lampa.Controller.collectionAppend(wb); } catch (x) {}
          // копия в скрытом контейнере: её показывает меню кнопки «Смотреть»
          try {
            var cont = render.find('.buttons--container').first();
            if (cont.length && !cont.find('.view--watchlist-menu').length) {
              var wm = wb.clone(false).removeClass('view--watchlist').addClass('view--watchlist-menu');
              wm.on('hover:enter', function () { wb.trigger('hover:enter'); });
              cont.append(wm);
              setTimeout(function () { try { cont.append(wm); } catch (x2) {} }, 2500);   // «В Мои фильмы» — в самом низу меню «Источник»
            }
          } catch (x) {}
        }
      });

      // Локальная библиотека (Plex): если тайтл есть — кнопка «Локально»
      lookupLocal(media, movie.id, function (found) {
        if (!found) return;
        var render = e.object.activity.render();
        if (render.find('.view--local').length) return;
        var lb = $('<div class="full-start__button selector view--local">' +
          '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M4 4h16v12H4zM8 20h8v-2H8z"/></svg><span>Локально</span></div>');
        lb.on('hover:enter', function () { setTimeout(function () { openLocal($.extend({}, movie), media, found); }, 350); });
        var anchor = render.find('.view--kinopub').first();
        if (!anchor.length) anchor = render.find('.view--torrent').first();
        if (anchor.length) anchor.after(lb); else render.find('.full-start__buttons, .buttons--container').first().append(lb);
      });

      lookupKp(media, movie.id, function (kp) {
        if (!kp) return;
        var render = e.object.activity.render();
        enrichFromKp(render, movie, kp);
        if (render.find('.view--kinopub').length) return;
        var btn = $('<div class="full-start__button selector view--kinopub">' +
          '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>' +
          '<span>Кинопаб</span></div>');
        btn.on('hover:enter', function () { setTimeout(function () { openItem($.extend({}, movie, { kp_id: kp })); }, 350); });
        btn.on('hover:long', function () { setTimeout(function () { openCardMenu({ kp_id: kp }); }, 350); });
        var after = render.find('.view--torrent').first();
        if (after.length) after.after(btn); else render.find('.full-start__buttons, .buttons--container').first().append(btn);
      });
    });
  }

  // ---------- каналы на главном экране Android TV (приложение «Lampa Home», AndroidJS.publishPluginChannel) ----------
  function channelsSupported() {
    return typeof AndroidJS !== 'undefined' && typeof AndroidJS.publishPluginChannel === 'function';
  }

  function toChannelItem(c) {
    if (!c || c.kp_legacy || c.source === 'kinopub' || !c.id || String(c.id) !== String(parseInt(c.id, 10))) return null;
    var title = c.title || c.name;
    if (!title) return null;
    var tv = c.media_type ? c.media_type === 'tv' : !!(c.name || c.original_name);
    var it = { id: String(c.id), source: 'tmdb', type: tv ? 'tv' : 'movie', name: title, title: title };
    if (c.original_title || c.original_name) { it.original_title = c.original_title || c.original_name; }
    if (c.poster_path) it.poster_path = c.poster_path;
    if (c.backdrop_path) it.backdrop_path = c.backdrop_path;
    if (c.overview) it.overview = String(c.overview).slice(0, 400);
    var va = Number(c.vote_average);
    if (isFinite(va) && va >= 0 && va <= 10) it.vote_average = va;
    if (c.release_date) it.release_date = String(c.release_date);
    if (c.first_air_date) it.first_air_date = String(c.first_air_date);
    return it;
  }

  var CHANNELS = [
    { id: 'kp-continue', title: 'Продолжить просмотр', mode: 'continue', needAuth: false },
    { id: 'kp-watching', title: 'Мои сериалы', mode: 'watching', needAuth: true },
    { id: 'new-movies', title: 'Новые фильмы', mode: 'new_movie', needAuth: false },
    { id: 'new-serials', title: 'Новые сериалы', mode: 'new_serial', needAuth: false }
  ];

  function clearChannel(id) {
    try { if (typeof AndroidJS.clearPluginChannel === 'function') AndroidJS.clearPluginChannel(id); } catch (e) {}
  }

  function publishChannels() {
    if (!channelsSupported()) return;
    clearChannel('kp-movies');
    clearChannel('kp-serials');
    checkAuth(function (authorized) {
      CHANNELS.forEach(function (ch) {
        if (ch.needAuth && !authorized) return;
        fetchItems(ch.mode, 1, function (res) {
          var cards = res.results;
          // «Мои сериалы» — только где есть новые (непросмотренные) серии
          if (ch.mode === 'watching') cards = cards.filter(function (c) { return c.kp_new > 0; });
          var items = cards.map(toChannelItem).filter(Boolean).slice(0, 20);
          if (!items.length) { if (ch.mode === 'watching' || ch.mode === 'continue') clearChannel(ch.id); return; }
          try { AndroidJS.publishPluginChannel(JSON.stringify({ id: ch.id, title: ch.title, items: items })); } catch (e) {}
        }, function () {});
      });
    });
  }

  function scheduleChannels() {
    if (!channelsSupported()) return;
    setTimeout(publishChannels, 9000);
    setInterval(publishChannels, 30 * 60 * 1000);
  }

  // ---------- локальная библиотека (Plex): кнопка «Локально» на странице карточки ----------
  var localCache = {};
  var localCfgCache = null;

  function lookupLocal(media, tmdbId, cb) {
    var key = media + ':' + tmdbId;
    if (localCache[key] !== undefined) return cb(localCache[key]);
    $.ajax({
      url: BASE + '/local/lookup?type=' + media + '&tmdb=' + tmdbId, dataType: 'json', timeout: 60000, cache: false,
      success: function (d) { localCache[key] = d && d.found ? d : null; cb(localCache[key]); },
      error: function () { cb(null); }
    });
  }

  function localPlayback(cb) {
    if (localCfgCache) return cb(localCfgCache);
    $.ajax({
      url: BASE + '/local/config', dataType: 'json', timeout: 20000, cache: false,
      success: function (d) {
        var plex = (d.sources || []).filter(function (s) { return s.id === 'plex'; })[0] || {};
        localCfgCache = { playback: plex.playback || 'file' };
        cb(localCfgCache);
      },
      error: function () { cb({ playback: 'file' }); }
    });
  }

  // Плеер для локальной библиотеки на этом устройстве: android — внешний Android-плеер (Vimu и др.) через мост приложения,
  // inner — встроенный (Plex транскодирует в HLS). В приложении «Lampa Home» по умолчанию внешний.
  function androidBridge() {
    return typeof AndroidJS !== 'undefined' && typeof AndroidJS.openPlayer === 'function' && Lampa.Android && typeof Lampa.Android.openPlayer === 'function';
  }

  function localPlayerPref() {
    var p = prefs().localPlayer;
    if (!p) p = 'inner';   // по умолчанию штатный плеер Lampa; внешний (VIMU) — запасной, выбирается в настройках
    return (p === 'android' && !androidBridge()) ? 'inner' : p;
  }

  function localUrl(rk, mode) {
    // внешний плеер получает файл целиком (любой кодек), встроенному нужен HLS-транскод
    if (localPlayerPref() === 'android' || externalPlayer() || mode !== 'hls') return BASE + '/local/plex/file/' + rk;
    return BASE + '/local/plex/hls/' + rk + '.m3u8';
  }

  // После возврата из внешнего плеера отправляем позиции просмотра на сервер (мост перенесёт их в kino.pub)
  var extPending = null;
  document.addEventListener('visibilitychange', function () {
    if (document.hidden || !extPending) return;
    var ep = extPending; extPending = null;
    setTimeout(function () {
      var rows = [];
      ep.playlist.forEach(function (p) {
        var v = Lampa.Timeline.view(p.kp.hash);
        if (!v || !(v.time > 1) || !ep.tmdb) return;
        rows.push({
          id: ep.season ? 'tv-' + ep.tmdb + '-s' + ep.season + 'e' + p.kp.video : 'movie-' + ep.tmdb,
          hash: p.kp.hash, card: ep.tmdb + '_' + (ep.season ? 'tv' : 'movie'),
          position: Math.round(v.time), duration: Math.round(v.duration || 0), percent: v.percent || 0, watched_at: Date.now()
        });
      });
      if (rows.length) postTl(rows);
      kickSync(3000);
    }, 1500);
  });

  // episodes: [{ rk, season, episode, title, duration, viewOffset }]; для фильма — один элемент (season 0)
  function playLocal(movie, media, episodes, index) {
    currentCard = movie;
    var prev = baseCtrl();
    localPlayback(function (cfg) {
      var season = media === 'tv' ? episodes[index].season : 0;
      var list = media === 'tv' ? episodes.filter(function (e) { return e.season === season; }) : episodes;
      var multi = media === 'movie' && list.length > 1;   // серии-«фильмы» Plex
      var startAt = media === 'tv' ? list.indexOf(episodes[index]) : (multi ? index : 0);
      var origName = media === 'tv' ? (movie.original_name || movie.original_title || movie.name) : (movie.original_title || movie.title);
      var title = movie.title || movie.name;
      var playlist = [];

      list.forEach(function (e, li) {
        var number = media === 'tv' ? e.episode : (multi ? li + 1 : 1);
        var hash = Lampa.Utils.hash(media === 'tv' ? [season, season > 10 ? ':' : '', number, origName].join('') : (multi ? origName + '·' + number : origName));
        var tl = Lampa.Timeline.view(hash);
        if (!window.HomeUser && !tl.time && e.viewOffset > 60 && e.duration) {
          tl.time = e.viewOffset; tl.duration = e.duration; tl.percent = Math.round(e.viewOffset / e.duration * 100);
          try { Lampa.Timeline.update(tl); } catch (x) {}
        }
        var entry = {
          title: media === 'tv' ? title + ' · S' + season + 'E' + number + (e.title ? ' · ' + e.title : '') : (multi && e.title ? e.title : title),
          url: localUrl(e.rk, cfg.playback),
          timeline: tl,
          kp: { item: 0, season: season, video: number, hash: hash, native: true }
        };
        if (!externalPlayer() && cfg.playback === 'hls') entry.hls_type = 'hlsjs';
        playlist.push(entry);
      });

      if (localPlayerPref() === 'android') {
        // внешний плеер: позиции вернутся при возврате в приложение, тогда же отправляем их на сервер
        var cur = playlist[Math.max(0, startAt)];
        extPending = { tmdb: parseInt(movie.id, 10) || 0, season: season, playlist: playlist };
        try { Lampa.Android.openPlayer(cur.url, { title: cur.title, timeline: cur.timeline, playlist: playlist, card: movie }); }
        catch (x) { Lampa.Noty.show('Не удалось открыть внешний плеер'); }
        return;
      }

      startPositionPush(null, season, playlist);
      startPlay(playlist, Math.max(0, startAt));
      Lampa.Player.callback(function () {
        stopPositionPush(true);
        Lampa.Controller.toggle(prev);
      });
    });
  }

  function openLocal(movie, media, found) {
    var prev = baseCtrl();
    if (media === 'movie' && found.parts && found.parts.length > 1) {
      // многосерийный «фильм»: серии в Plex — отдельные элементы. Показываем список и играем подряд.
      var origP = movie.original_title || movie.title;
      var parts = found.parts.map(function (p, i) {
        return { rk: p.rk, season: 0, episode: i + 1, title: p.title, duration: p.duration || 0, viewOffset: p.viewOffset || 0, watched: p.watched };
      });
      var firstOpen = -1;
      var items = parts.map(function (p, i) {
        var st = { s: '' };
        try { st = window.__home_state(Lampa.Timeline.view(Lampa.Utils.hash(origP + '·' + (i + 1)))); } catch (x) {}
        var done = p.watched || st.s === 'watched';
        var part = !done && (st.s === 'started' || p.viewOffset > 60);
        if (firstOpen < 0 && !done) firstOpen = i;
        var mk = done ? '<span style="color:#2ecc71;font-weight:700">✓</span> ' : (part ? '<span style="color:#f9a825;font-weight:700">▶</span> ' : '');
        var sub = (p.duration ? Math.round(p.duration / 60) + ' мин' : '') + (done ? ' · просмотрено' : (p.viewOffset > 60 ? ' · остановлено на ' + Math.round(p.viewOffset / 60) + ' мин' : ''));
        return { title: mk + (p.title || ('Серия ' + (i + 1))), subtitle: sub, idx: i, selected: false };
      });
      items.forEach(function (it, i) { it.selected = firstOpen >= 0 ? i === firstOpen : i === 0; });
      return Lampa.Select.show({
        title: (movie.title || movie.name) + ' · серий: ' + parts.length,
        items: items,
        onBack: function () { Lampa.Controller.toggle(prev); },
        onSelect: function (a) { playLocal(movie, 'movie', parts, a.idx); }
      });
    }
    if (media === 'movie') {
      var info = found.info || {};
      return playLocal(movie, 'movie', [{ rk: found.rk, season: 0, episode: 1, title: '', duration: info.duration || 0, viewOffset: info.viewOffset || 0 }], 0);
    }
    $.ajax({
      url: BASE + '/local/episodes?rk=' + found.rk + '&tmdb=' + movie.id, dataType: 'json', timeout: 60000, cache: false,
      success: function (d) {
        var eps = d.episodes || [];
        if (!eps.length) return Lampa.Noty.show('Локальная библиотека: серий нет');
        var seasons = {};
        eps.forEach(function (e) { (seasons[e.season] = seasons[e.season] || []).push(e); });
        var items = Object.keys(seasons).map(function (n) {
          var w = seasons[n].filter(function (e) { return e.watched; }).length;
          return { title: n === '0' ? 'Спецвыпуски' : 'Сезон ' + n, subtitle: seasons[n].length + ' сер.' + (w ? ' · просмотрено ' + w : ''), n: parseInt(n, 10) };
        });
        var pickEpisodes = function (n) {
          Lampa.Select.show({
            title: (movie.name || movie.title) + ' · ' + (n === 0 ? 'спецвыпуски' : 'сезон ' + n),
            items: seasons[n].map(function (e, i) {
              return { title: e.episode + '. ' + (e.title || 'Серия ' + e.episode), subtitle: e.watched ? 'просмотрено' : (e.viewOffset > 60 ? 'остановлено на ' + Math.round(e.viewOffset / 60) + ' мин' : ''), e: e };
            }),
            onBack: function () { openLocal(movie, media, found); },
            onSelect: function (a) { playLocal(movie, 'tv', eps, eps.indexOf(a.e)); }
          });
        };
        if (items.length === 1) return pickEpisodes(items[0].n);
        Lampa.Select.show({
          title: movie.name || movie.title, items: items,
          onBack: function () { Lampa.Controller.toggle(prev); },
          onSelect: function (a) { pickEpisodes(a.n); }
        });
      },
      error: function () { Lampa.Noty.show('Локальная библиотека: нет связи'); }
    });
  }

  // ---------- настройки локальной библиотеки (адрес вручную, включение/отключение) ----------
  function localPost(id, patch, cb) {
    $.ajax({ url: BASE + '/local/config', type: 'POST', contentType: 'application/json', data: JSON.stringify({ id: id, patch: patch }),
      dataType: 'json', success: function () { localCfgCache = null; localCache = {}; if (cb) cb(); }, error: function () { Lampa.Noty.show('Не удалось сохранить'); if (cb) cb(); } });
  }

  function editText(title, value, cb) {
    Lampa.Input.edit({ title: title, value: value || '', free: true, nosave: true }, function (val) { if (val !== undefined && val !== null) cb(String(val).replace(/^\s+|\s+$/g, '')); });
  }

  function openLocalSettings(back) {
    var playerLabel = localPlayerPref() === 'android' ? 'внешний Android-плеер (Vimu и др.)' : 'встроенный (транскод Plex в HLS)';
    var ownItem = [{ title: 'Плеер на этом устройстве', subtitle: playerLabel + (androidBridge() ? '' : ' · внешний недоступен в браузере'), own: true }];
    $.when(
      $.ajax({ url: BASE + '/local/config', dataType: 'json', cache: false }),
      $.ajax({ url: BASE + '/local/status', dataType: 'json', cache: false, timeout: 60000 })
    ).then(function (c, s) {
      var cfg = c[0].sources, st = s[0].sources;
      var STATE = { ok: 'работает', off: 'выключено', not_configured: 'не настроено', unreachable: 'нет связи', no_folder: 'папка не найдена' };
      Lampa.Select.show({
        title: 'Локальная библиотека',
        items: ownItem.concat(cfg.map(function (src) {
          var x = st.filter(function (q) { return q.id === src.id; })[0] || {};
          var where = src.type === 'plex' ? (src.url || 'адрес не задан') : (src.path || '');
          return { title: src.title, subtitle: (STATE[x.state] || x.state) + (x.count ? ' · ' + x.count + ' шт.' : '') + ' · ' + where, src: src };
        })),
        onBack: function () { back(); },
        onSelect: function (a) {
          if (a.own) {
            setPref('localPlayer', localPlayerPref() === 'android' ? 'inner' : 'android');
            return setTimeout(function () { openLocalSettings(back); }, 300);
          }
          setTimeout(function () { localSourceMenu(a.src, back); }, 300);
        }
      });
    }, function () { Lampa.Noty.show('Локальная библиотека: сервер не отвечает'); });
  }

  function localSourceMenu(src, back) {
    var again = function () { setTimeout(function () { openLocalSettings(back); }, 300); };
    var items = [{ title: src.enable === false ? 'Включить' : 'Выключить', subtitle: 'метод можно отключить совсем', act: 'toggle' }];
    if (src.type === 'plex') {
      items.push({ title: 'Адрес сервера', subtitle: src.url || 'не задан', act: 'url' });
      items.push({ title: 'Токен Plex', subtitle: src.has_token ? 'задан' : 'не задан', act: 'token' });
      items.push({ title: 'Воспроизведение', subtitle: src.playback === 'hls' ? 'транскод Plex (HLS) — для встроенного плеера' : 'прямой файл (внешний плеер, Vimu и др.)', act: 'mode' });
    } else {
      items.push({ title: 'Путь к папке', subtitle: src.path || '', act: 'path' });
      if (src.id === 'wrestling') items.push({ title: 'Показывать в «Мои сериалы»', subtitle: src.watching === false ? 'выключено' : 'включено — один сериал «Wrestling» со всеми выпусками по датам', act: 'watching' });
    }
    items.push({ title: 'Назад', act: 'back' });

    Lampa.Select.show({
      title: src.title,
      items: items,
      onBack: again,
      onSelect: function (a) {
        if (a.act === 'toggle') localPost(src.id, { enable: src.enable === false }, again);
        else if (a.act === 'url') editText('Адрес Plex, например http://адрес:32400', src.url, function (v) { localPost(src.id, { url: v }, again); });
        else if (a.act === 'token') editText('Токен Plex (X-Plex-Token)', '', function (v) { if (v) localPost(src.id, { token: v }, again); else again(); });
        else if (a.act === 'path') editText('Путь к папке в контейнере Lampac', src.path, function (v) { localPost(src.id, { path: v }, again); });
        else if (a.act === 'watching') localPost(src.id, { watching: src.watching === false }, again);
        else if (a.act === 'mode') localPost(src.id, { playback: src.playback === 'hls' ? 'file' : 'hls' }, again);
        else again();
      }
    });
  }

  // ---------- плашка «KP» на постерах, если тайтл есть в Кинопаб ----------
  var hasCache = {};
  var hasQueue = {};
  var hasTimer = null;

  function loadHasCache() {
    try {
      var raw = JSON.parse(localStorage.getItem('kp_has_v1') || '{}');
      var now = Date.now();
      Object.keys(raw).forEach(function (k) { if (now - raw[k].t < 6 * 3600 * 1000) hasCache[k] = raw[k]; });
    } catch (e) {}
  }
  function saveHasCache() {
    try { localStorage.setItem('kp_has_v1', JSON.stringify(hasCache)); } catch (e) {}
  }

  function addBadge(el, kp, extra) {
    var view = el.querySelector('.card__view');
    if (!view || view.querySelector('.card__kp')) return;
    var b = document.createElement('div');
    b.className = 'card__kp';
    b.textContent = extra ? 'KP ' + extra : 'KP';
    view.appendChild(b);
  }

  function flushHas() {
    hasTimer = null;
    var keys = Object.keys(hasQueue).slice(0, 40);
    if (!keys.length) return;
    var batch = {};
    keys.forEach(function (k) { batch[k] = hasQueue[k]; delete hasQueue[k]; });
    $.ajax({
      url: BASE + '/kp/has?keys=' + encodeURIComponent(keys.join(',')), dataType: 'json', timeout: 60000, cache: false, headers: hdr(),
      success: function (d) {
        keys.forEach(function (k) {
          hasCache[k] = { kp: d[k] || 0, t: Date.now() };
          (batch[k] || []).forEach(function (el) { if (hasCache[k].kp) addBadge(el, hasCache[k].kp); });
        });
        saveHasCache();
        if (Object.keys(hasQueue).length) hasTimer = setTimeout(flushHas, 300);
      }
    });
  }

  // Shots — функция CUB, без аккаунта CUB не нужна: убираем пункт меню и кнопку на странице карточки
  function hideShots() {
    $('.menu__item').filter(function () { return $.trim($(this).text()) === 'Shots'; }).remove();
    $('.shots-view-button').remove();
  }

  function scanCards() {
    hideShots();
    var cards = document.querySelectorAll('.card:not([data-kp-scanned])');
    for (var i = 0; i < cards.length; i++) {
      var el = cards[i];
      var d = el.card_data;
      if (!d) continue;
      el.setAttribute('data-kp-scanned', '1');
      if (d.kp_id) { addBadge(el, d.kp_id, d.kp_new > 0 ? '+' + d.kp_new : ''); continue; }
      var id = parseInt(d.id, 10);
      if (!id || String(d.id) !== String(id) || d.source === 'wrestling') continue;
      var key = (d.name || d.original_name ? 'tv' : 'movie') + ':' + id;
      if (hasCache[key]) { if (hasCache[key].kp) addBadge(el, hasCache[key].kp); continue; }
      (hasQueue[key] = hasQueue[key] || []).push(el);
    }
    if (Object.keys(hasQueue).length && !hasTimer) hasTimer = setTimeout(flushHas, 400);
  }

  function hookBadges() {
    var style = document.createElement('style');
    style.textContent = '.card__kp{position:absolute;top:.45em;right:.45em;z-index:3;background:#f9a825;color:#111;' +
      'font-weight:700;font-size:.75em;line-height:1;padding:.22em .45em;border-radius:.3em;box-shadow:0 0 .3em rgba(0,0,0,.5)}';
    document.head.appendChild(style);
    loadHasCache();
    var t = null;
    new MutationObserver(function () {
      if (t) return;
      t = setTimeout(function () { t = null; scanCards(); }, 350);
    }).observe(document.body, { childList: true, subtree: true });
    scanCards();
  }

  // Мост на сервере переносит позиции между Lampa и kino.pub. Запускаем через 20 с после последнего обновления
  // таймлайна (то есть после паузы или конца просмотра), а не во время воспроизведения.
  var kickTimer = null;
  function kickSync(delay) {
    clearTimeout(kickTimer);
    kickTimer = setTimeout(function () {
      $.ajax({ url: BASE + '/kp/sync/run', timeout: 120000, cache: false });
    }, delay);
  }

  function hookSyncKick() {
    try {
      if (Lampa.Timeline && Lampa.Timeline.listener && Lampa.Timeline.listener.follow) {
        Lampa.Timeline.listener.follow('update', function () { kickSync(20000); });
      }
    } catch (e) {}
    kickSync(12000);
  }

  // Свой логотип вместо штатного (шапка и приветственный экран): домик с «play»
  function brandLogo() {
    var svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">' +
      '<defs><linearGradient id="a" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffcf4a"/><stop offset="1" stop-color="#ff8a1f"/></linearGradient></defs>' +
      '<path fill="url(#a)" fill-rule="evenodd" d="M256 70 L440 232 Q452 243 452 259 L452 396 Q452 424 424 424 L88 424 Q60 424 60 396 L60 259 Q60 243 72 232 Z ' +
      'M222 232 L222 352 Q222 366 235 359 L334 300 Q346 293 334 286 L235 225 Q222 218 222 232 Z"/></svg>';
    var uri = 'data:image/svg+xml;utf8,' + encodeURIComponent(svg);
    var st = document.createElement('style');
    st.textContent = 'img[src$="logo-icon.svg"],img[src$="logo.svg"]{content:url("' + uri + '")!important}';
    document.head.appendChild(st);
  }

  // Shots (CUB) не нужен: не даём плагину зарегистрировать строку на главной
  function blockShotsRow() {
    try {
      var CR = Lampa.ContentRows, orig = CR.add;
      CR.add = function (row) {
        var t = row ? String(row.name || '') + ' ' + String(row.title || '') : '';
        if (/shots/i.test(t)) return;
        return orig.apply(this, arguments);
      };
    } catch (e) {}
  }

  // Долгое удержание: перехватываем раньше штатного меню карточки (не зависим от внутренностей Lampa)
  function hookRowLong() {
    document.addEventListener('hover:long', function (e) {
      try {
        var el = e.target && e.target.closest ? e.target.closest('.card') : null;
        if (!el || !el.card_data) return;
        // у выпусков Wrestling своё меню (просмотрено / убрать из просмотренного) вместо стандартного меню Lampa
        if (el.card_data.wrestling || el.card_data.wrestling_series) {
          e.stopImmediatePropagation();
          e.preventDefault();
          if (window.__wr_menu) window.__wr_menu(el.card_data, el);
          return;
        }
        var mode = null;
        var line = el.closest('.items-line');
        if (line) {
          var t = $.trim($(line).find('.items-line__title').first().text());
          if (t === 'Продолжить просмотр') mode = 'continue';
          else if (t === 'Мои сериалы') mode = 'watching';
          else if (t === 'Мои аниме') mode = 'watching_anime';
          else if (t === 'Мои фильмы') mode = 'my_movies';
        } else {
          var act = Lampa.Activity.active();
          if (act && act.component === 'kp_list' && act.kp === 'watching') mode = 'watching';
          else if (act && act.component === 'kp_list' && act.kp === 'watching_anime') mode = 'watching_anime';
          else if (act && act.component === 'kp_list' && act.kp === 'continue') mode = 'continue';
          else if (act && act.component === 'kp_list' && act.kp === 'my_movies') mode = 'my_movies';
        }
        if (!mode) return;
        e.stopImmediatePropagation();
        e.preventDefault();
        rowMenu(mode, el, el.card_data);
      } catch (x) {}
    }, true);
  }

  // Кнопка Home, когда Lampa Home — домашний экран: закрыть окна и открыть главную
  window.__homeEntered = function () {
    try {
      if (Lampa.Select.opened && Lampa.Select.opened()) Lampa.Select.close();
      if ($('.modal').length) Lampa.Modal.close();
      var comp = Lampa.Activity.active() && Lampa.Activity.active().component;
      if (comp !== 'main') Lampa.Activity.push(mainParams());
      else Lampa.Controller.toContent();
    } catch (e) {}
  };

  // «Назад» из приложения: настройки и поиск закрываем контроллером Lampa (history.back() их не закрывает и зацикливается)
  // Keep episode lists inside the actual viewport, including a safe bottom gap.
  if (Lampa.Select.listener && !window.__homeEpisodeViewport) {
    window.__homeEpisodeViewport = true;
    $('<style>.home-series-select .selectbox__content{display:flex;flex-direction:column;height:100vh!important;max-height:100vh;box-sizing:border-box;padding-bottom:1em;overflow:hidden}.home-series-select .selectbox__head{flex:none}.home-series-select .selectbox__body{height:0!important;min-height:0;flex:1;overflow:hidden}.home-series-select .scroll--mask{-webkit-mask-image:none;mask-image:none}</style>').appendTo('head');
    Lampa.Select.listener.follow('fullshow', function(e) { e.html.toggleClass('home-series-select', !!e.active.home_episodes); });
  }
  // Lampa Media Center: оболочка присваивает window.__mc_ret = 1 при открытии карточки; запоминаем глубину стека Lampa на этой карточке,
  // чтобы «Назад» с неё вёл на оболочку, как бы ни выглядел прежний путь по Lampa
  try {
    var mcV = 0;
    Object.defineProperty(window, '__mc_ret', {
      configurable: true,
      get: function () { return mcV; },
      set: function (v) {
        if (v === 1) {
          var current = Lampa.Activity.active();
          var fresh = current && current.component !== 'main' && current !== window.__mc_shell_origin && window.__mc_lastT && (Date.now() - window.__mc_lastT) < 4000;
          if (fresh) { mcV = 2; window.__mc_base = window.__mc_lastLen; } else { mcV = 3; window.__mc_base = 0; }   // 3 — карточка ещё не открылась
        } else { mcV = v; }
      }
    });
    Lampa.Listener.follow('activity', function (e) {
      if (e && e.type === 'start') {
        window.__mc_lastLen = Lampa.Activity.all().length; window.__mc_lastT = Date.now();
        if (mcV === 3 && Lampa.Activity.active() !== window.__mc_shell_origin && Lampa.Activity.active().component !== 'main') { mcV = 2; window.__mc_base = window.__mc_lastLen; }
      }
    });
    // страховка: если Lampa сама ушла с карточки глубже базы (любой путь назад), возвращаем оболочку
    setInterval(function () {
      try {
        if (mcV === 2 && window.__mc_base && Lampa.Activity.all().length < window.__mc_base && !(Lampa.Select.opened && Lampa.Select.opened())) {
          mcV = 0; window.__mc_base = 0;
          if (window.AndroidJS && AndroidJS.exit) AndroidJS.exit();
        }
      } catch (e1) {}
    }, 400);
  } catch (e0) {}

  window.__homeBack = function () {
    try {
      if ($('body').hasClass('settings--open') || $('body').hasClass('search--open')) { Lampa.Controller.back(); return; }
      // Lampa Media Center: «Назад» на главной странице Lampa — сразу на оболочку (диалог выхода не нужен)
      if (window.AndroidJS && typeof AndroidJS.mcDeviceId === 'function' && AndroidJS.mcDeviceId() && !(Lampa.Select.opened && Lampa.Select.opened()) && !$('.modal').length) {
        var ac = Lampa.Activity.active();
        if (ac && ac.component === 'main') { AndroidJS.exit(); return; }
      }
      // Lampa Media Center: карточка открыта из оболочки — «Назад» возвращает на главный экран оболочки, а не на главную Lampa
      if (window.__mc_ret && !(Lampa.Select.opened && Lampa.Select.opened()) && !$('.modal').length) {
        var all = Lampa.Activity.all();
        // база — глубина стека сразу после открытия той самой карточки (до неё мог остаться любой прежний путь Lampa)
        var base = window.__mc_ret === 2 ? window.__mc_base : 0;   // 3 — открытие ещё не подтверждено: прежнее правило
        var prev = all.length > 1 ? all[all.length - 2] : null;
        if ((base && all.length <= base) || (!base && (all.length <= 2 || !prev || prev.component === 'main'))) {
          window.__mc_ret = 0; window.__mc_base = 0; try { Lampa.Activity.back(); } catch (e2) {}
          if (window.AndroidJS && AndroidJS.exit) { AndroidJS.exit(); return; }
        }
      }
    } catch (e) {}
    window.history.back();
  };

  // остаток подписки Кинопаба в шапке главной Lampa: нет аккаунта — не показываем, 0 — «истекла»
  function dayWord(n) { var m = n % 10, h = n % 100; return (h > 10 && h < 20) ? 'дней' : m === 1 ? 'день' : (m >= 2 && m <= 4) ? 'дня' : 'дней'; }
  function showSub() {
    $.ajax({ url: BASE + '/home/kp/sub?profile=' + encodeURIComponent(prefs().profile || 'main'), dataType: 'json', timeout: 10000, cache: false,
      success: function (d) {
        $('#kp-sub').remove();
        if (!d || !d.has) return;
        var low = d.expired || d.days <= 5;
        var el = $('<div id="kp-sub" style="font-size:1.1em;padding:.3em .8em;border-radius:1em;margin-right:1em;background:' + (low ? '#c62828' : 'rgba(255,255,255,.15)') + ';color:#fff;white-space:nowrap">Кинопаб: ' + (d.expired || d.days <= 0 ? 'истекла' : d.days + ' ' + dayWord(d.days)) + '</div>');
        var t = $('.head .head__time').first();
        if (t.length) t.before(el); else $('.head__body').first().append(el);
      }
    });
  }

  // Lampa Media Center: клавиша Escape с клавиатуры/пульта приходит в страницу как keydown 27, а не системным «Назад» — ведём её тем же путём
  try {
    if (window.AndroidJS && typeof AndroidJS.mcDeviceId === 'function' && AndroidJS.mcDeviceId()) {
      window.addEventListener('keydown', function (e) {
        if (e.keyCode === 27 && window.__homeBack) { e.preventDefault(); e.stopImmediatePropagation(); window.__homeBack(); }
      }, true);
    }
  } catch (e5) {}

  function start() {
    try { $('<style>.view--watchlist.wl-on svg{color:#F9A825}</style>').appendTo('head'); } catch (e) {}
    setTimeout(showSub, 4000); setInterval(showSub, 30 * 60 * 1000);
    hookCardMenu();
    trimSettings();
    hookRowLong();
    blockShotsRow();
    window.__kp_placeMenu = placeMenu;
    setInterval(placeMenu, 3000);
    addPlexMenu();
    setTimeout(fixStack, 3500);
    setTimeout(fixStack, 8000);
    brandLogo();
    scheduleChannels();
    // один раз на устройстве: фильмы и серии по умолчанию играет штатный плеер Lampa (внешний — запасной, меняется в настройках)
    // «Штатный» плеер Lampa в приложении = наш нативный плеер; «Android» = внешний (VIMU)
    try {
      if (androidBridge() && !window.__home_player_route) {
        window.__home_player_route = true;
        var rf = Lampa.Storage.field;
        Lampa.Storage.field = function (n) {
          var v = rf.apply(this, arguments);
          return ((n === 'player' || n === 'player_torrent') && v === 'inner') ? 'android' : v;
        };
        var op = Lampa.Android.openPlayer;
        Lampa.Android.openPlayer = function (link, data) {
          data = data || {};
          // торренты (ссылка на TorrServer) выбирают плеер своей настройкой «player_torrent», остальное — «player»
          var isTor = !!(data.torrent_hash || (data.timeline && data.timeline.hash && /\/stream[\/?]/.test(String(link))) || /\/stream[\/?].*[?&]link=/.test(String(link)));
          if (Lampa.Storage.get(isTor ? 'player_torrent' : 'player', 'inner') !== 'android') data.home_player = true;
          return op.call(this, link, data);
        };
      }
    } catch (e) {}
    // Сообщаем приложению выбранные в Lampa типы плеера: решение «встроенный/внешний» принимается нативно (Lampa зовёт AndroidJS.openPlayer мимо JS-обёрток)
    function pushPlayerPrefs() {
      try { if (window.AndroidJS && typeof AndroidJS.mcPlayerPrefs === 'function') AndroidJS.mcPlayerPrefs(String(Lampa.Storage.get('player', 'inner')), String(Lampa.Storage.get('player_torrent', 'inner'))); } catch (eP) {}
    }
    pushPlayerPrefs(); setInterval(pushPlayerPrefs, 3000);
    // Страховка: Lampa зовёт AndroidJS.openPlayer напрямую (мимо Lampa.Android.openPlayer) — решение «встроенный/внешний» принимаем и здесь, по настройкам Lampa
    try {
      if (window.AndroidJS && typeof AndroidJS.openPlayer === 'function' && !window.__home_ajs_wrapped) {
        window.__home_ajs_wrapped = true;
        var ajsOp = AndroidJS.openPlayer.bind(AndroidJS);
        AndroidJS.openPlayer = function (link, json) {
          try {
            var d = JSON.parse(json || '{}');
            if (!d.live && !d.iptv && d.home_player === undefined) {
              var tor = !!(d.torrent_hash || /\/stream[\/?].*[?&]link=/.test(String(link)));
              var pref = Lampa.Storage.get(tor ? 'player_torrent' : 'player', 'inner');
              if (pref !== 'android') d.home_player = true;
              json = JSON.stringify(d);
            }
          } catch (eA) {}
          return ajsOp(link, json);
        };
      }
    } catch (eW) {}
    try {
      // Lampa на Android предлагает для торрентов только «Android»: добавляем «Встроенный» (наш плеер)
      try { Lampa.Params.select('player_torrent', { inner: 'Встроенный', android: 'Android (внешний)' }, 'inner'); } catch (ePT) {}
      // Клиент торрентов: на Android Lampa по умолчанию отдаёт торрент приложению TorrServe (у него свой диалог выбора плеера) —
      // включаем внутренний клиент Lampa, тогда раздача играет выбранным в Lampa плеером (встроенным или внешним)
      // адрес TorrServer задаётся на сервере (веб-панель → TorrServer) и подставляется в Lampa при каждом запуске
      try {
        $.ajax({ url: BASE + '/home/torrserver', dataType: 'json', timeout: 8000, cache: false, success: function (d) {
          if (d && d.url && Lampa.Storage.get('torrserver_url', '') !== d.url) Lampa.Storage.set('torrserver_url', d.url);
        } });
      } catch (eTS) {}
      // (переключатели Lampa хранят значение строкой 'true'/'false', булево значение настройка не читает)
      if (!localStorage.getItem('home_inner_torrclient_v2')) {
        localStorage.setItem('home_inner_torrclient_v2', '1');
        Lampa.Storage.set('internal_torrclient', 'true');
      }
      // один раз: торренты по умолчанию тоже играет встроенный плеер (переключается в настройках Lampa: Плеер для торрентов)
      if (!localStorage.getItem('home_inner_torrent_v1')) {
        localStorage.setItem('home_inner_torrent_v1', '1');
        Lampa.Storage.set('player_torrent', 'inner');
      }
      if (!localStorage.getItem('home_inner_player_v1')) {
        localStorage.setItem('home_inner_player_v1', '1');
        Lampa.Storage.set('player', 'inner');
      }
    } catch (e) {}
    hookSyncKick();
    hookFullPage();
    hookBadges();
    hookHls();
    hookQualityMenu();
    hookActivity();
    hookAutoRefresh();
    addMenus();
    placeMenu();
    setTimeout(placeMenu, 1500);
    setTimeout(placeMenu, 4000);
    checkAuth(function () {});
    addRow('kp_row_continue', 'Продолжить просмотр', 'continue', 0, true);
    addRow('kp_row_history', 'История', 'history', 0.5, true);
    addRow('kp_row_watching', 'Мои сериалы', 'watching', 1);
    addRow('kp_row_movies', 'Новые фильмы', 'new_movie', 2, true);
    addRow('kp_row_serials', 'Новые сериалы', 'new_serial', 3, true);
    addRow('kp_row_top', 'Лучшие новинки', 'top_new', 4, true);
    addRow('kp_row_watching_anime', 'Мои аниме', 'watching_anime', 4);
    addRow('kp_row_anime', 'Аниме', 'anime', 5, true);
    addRow('kp_row_ru_serial', 'Отечественные сериалы', 'ru_serial', 6, true);
    addRow('kp_row_ru_movie', 'Отечественные фильмы', 'ru_movie', 7, true);
    addRow('kp_row_mymovies', 'Мои фильмы', 'my_movies', 8, true);
    addRow('kp_row_cartoon', 'Мультфильмы', 'new_cartoon', 7.9, true);
    addRow('kp_row_mycartoons','Мои мультфильмы','my_cartoons',8.1,true);
    addRow('kp_row_cartoonseries','Мультсериалы','new_cartoon_series',7.95,true);
    addRow('kp_row_mycartoonseries','Мои мультсериалы','watching_cartoons',1.1,true);   // над Wrestling (индекс 8)
    window.__kp_rows_registered = true;
    if (window.__wr_registerRow) window.__wr_registerRow();
    devLoad(function () {});
  }

  window.kp_plugin = { fetchItems: fetchItems, openItem: openItem, pickUrl: pickUrl, publishChannels: publishChannels };

  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
