(function () {
  'use strict';
  if (window.plugin_tv_ready) return;
  window.plugin_tv_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;

  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M21 3H3a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h5v2h8v-2h5a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2zm0 14H3V5h18v12z"/></svg>';
  var PAGE = 100;
  var FAV_KEY = 'tv_fav_v1';

  var baseName = 'content';
  function ctrl() {
    try {
      var n = Lampa.Controller.enabled().name;
      if (n && ['select', 'modal', 'input', 'settings_component'].indexOf(n) < 0) baseName = n;
    } catch (e) {}
    return baseName;
  }

  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }

  function api(path, ok, fail, opts) {
    $.ajax($.extend({ url: BASE + path, dataType: 'json', timeout: 60000, cache: false, success: ok, error: function (x) { if (fail) fail(x); } }, opts || {}));
  }

  function two(n) { return (n < 10 ? '0' : '') + n; }
  function hm(ts) { var d = new Date(ts * 1000); return two(d.getHours()) + ':' + two(d.getMinutes()); }

  function favs() { try { return JSON.parse(localStorage.getItem(FAV_KEY) || '[]'); } catch (e) { return []; } }
  function saveFavs(a) { try { localStorage.setItem(FAV_KEY, JSON.stringify(a)); } catch (e) {} }

  function nowText(c) {
    if (!c.n) return '';
    var left = Math.max(0, Math.round((c.n.e - Date.now() / 1000) / 60));
    return hm(c.n.s) + ' ' + c.n.t + ' · ещё ' + left + ' мин' + (c.x ? ' · далее ' + hm(c.x.s) + ' ' + c.x.t : '');
  }

  // Буфер плеера в прямом эфире: 0 — без буфера (минимальная задержка), иначе секунды
  var BUF_KEY = 'tv_buffer_v1';
  var BUFS = [{ v: 0, n: 'Без буфера' }, { v: 1, n: '1 секунда' }, { v: 3, n: '3 секунды' }, { v: 5, n: '5 секунд' }, { v: 10, n: '10 секунд' }];
  function getBuf() {
    try { var v = parseInt(localStorage.getItem(BUF_KEY), 10); return isNaN(v) ? 3 : v; } catch (e) { return 3; }
  }
  function bufName(v) { for (var i = 0; i < BUFS.length; i++) if (BUFS[i].v === v) return BUFS[i].n; return v + ' с'; }

  // Автозапуск: при старте приложения сразу включать последний просмотренный канал
  var AUTO_KEY = 'tv_autostart_v1', LAST_KEY = 'tv_last_v1';
  // вывод видео: surface — аппаратный слой (по умолчанию), texture — через GPU (лечит неровный ход кадров на части приставок, напр. Amlogic)
  var SURF_KEY = 'tv_surface_v1';
  // Движок прямого эфира во встроенном плеере: exo — ExoPlayer (по умолчанию), mpv* — опытный плеер на libmpv (FFmpeg).
  // Кино и сериалы всегда идут на ExoPlayer; внешний IPTV плеер (TiviMate) выбирается отдельно и от этой настройки не зависит.
  var ENG_KEY = 'tv_engine_v1';
  var ENGINES = [
    { id: 'exo', name: 'ExoPlayer (как раньше)' },
    { id: 'mpv', name: 'mpv · аппаратный декодер с копированием (проба)' },
    { id: 'mpv_embed', name: 'mpv · прямой вывод на экран, как ExoPlayer (проба)' },
    { id: 'mpv_hw', name: 'mpv · аппаратный декодер напрямую (проба)' },
    { id: 'mpv_sw', name: 'mpv · программный декодер (проба)' }
  ];
  function getEngine() {
    try {
      var v = localStorage.getItem(ENG_KEY);
      return ENGINES.some(function (e) { return e.id === v; }) ? v : 'exo';
    } catch (e) { return 'exo'; }
  }
  function engineName() { return ENGINES.filter(function (e) { return e.id === getEngine(); })[0].name; }
  // унифицированный поток: Channel Manager сам деинтерлейсит и даёт звук AAC 256 (адреса те же, плюс ?unify=1);
  // вывод тогда через GPU (TextureView) — для прогрессивной картинки это плавнее на Amlogic
  var UNI_KEY = 'tv_unify_v1';
  function getUni() { try { return localStorage.getItem(UNI_KEY) === '1'; } catch (e) { return false; } }
  function chUrl(u) { return getUni() ? u + (u.indexOf('?') < 0 ? '?' : '&') + 'unify=1' : u; }
  function getSurf() { try { return localStorage.getItem(SURF_KEY) === 'texture' ? 'texture' : 'surface'; } catch (e) { return 'surface'; } }
  // Lampa Media Center: автозапуск телевидения — функция оболочки (mc_auto); сама Lampa в ней ТВ не включает никогда
  function inMc() { try { return !!(window.AndroidJS && typeof AndroidJS.mcDeviceId === 'function' && AndroidJS.mcDeviceId()); } catch (e) { return false; } }
  function getAuto() { if (inMc()) return false; try { return localStorage.getItem(AUTO_KEY) === '1'; } catch (e) { return false; } }
  // Внешний IPTV плеер: приложение, которое открывает кнопка «Телевидение», значок «ТВ» и автозапуск вместо встроенного плеера
  // (например, TiviMate). Приложение выбирается только вручную, автоподбора нет — VIMU или другой плеер сам не подставится.
  var EXT_KEY = 'tv_ext_v1', EXTPKG_KEY = 'tv_extpkg_v1';
  var KNOWN_IPTV = [
    { p: 'ar.tvplayer.tv', n: 'TiviMate' },
    { p: 'net.gtvbox.videoplayer', n: 'VIMU' },
    { p: 'net.gtvbox.vimuhd', n: 'VIMU HD' },
    { p: 'org.videolan.vlc', n: 'VLC' },
    { p: 'org.xbmc.kodi', n: 'Kodi' },
    { p: 'studio.scillarium.ottnavigator', n: 'OTT Navigator' }
  ];
  function nativeApps() { return !!(window.AndroidJS && AndroidJS.getInstalledApps && AndroidJS.launchApp); }
  function installedApps() { try { return JSON.parse(AndroidJS.getInstalledApps()); } catch (e) { return []; } }
  function getExt() { try { return localStorage.getItem(EXT_KEY) === '1'; } catch (e) { return false; } }
  function getExtPkg() { try { return localStorage.getItem(EXTPKG_KEY) || ''; } catch (e) { return ''; } }
  function appName(p) {
    for (var i = 0; i < KNOWN_IPTV.length; i++) if (KNOWN_IPTV[i].p === p) return KNOWN_IPTV[i].n;
    var a = installedApps().filter(function (x) { return x.p === p; })[0];
    return a ? a.n : p;
  }
  function extName() { var p = getExtPkg(); return p ? appName(p) : 'не выбран'; }
  function extReady() {
    var p = getExtPkg();
    return getExt() && !!p && nativeApps() && installedApps().some(function (x) { return x.p === p; });
  }
  function setLast(id) { try { localStorage.setItem(LAST_KEY, id); } catch (e) {} }
  function getLast() { try { return localStorage.getItem(LAST_KEY); } catch (e) { return null; } }

  // Размер шрифта списка каналов в плеере: 0 мелкий, 1 средний, 2 крупный
  // Прозрачность списка каналов: процент непрозрачности (100 — сплошной, меньше — видно эфир сквозь список); по умолчанию 70
  var ALPHA_KEY = 'tv_palpha_v1';
  var ALPHAS = [100, 85, 70, 55, 40];
  function getAlpha() {
    try { var v = parseInt(localStorage.getItem(ALPHA_KEY), 10); return ALPHAS.indexOf(v) >= 0 ? v : 70; } catch (e) { return 70; }
  }
  // Звук эфира в движке mpv: стерео 2.0 с программным декодированием (по умолчанию) или как в потоке
  // Усиление центрального канала (голоса) при сведении многоканального звука в стерео, проценты к штатному уровню (−3 дБ)
  // Автоматическая частота кадров для эфира в движке mpv: подбор режима экрана под частоту кадров канала (по умолчанию включено)
  var AFR_KEY = 'tv_afr_v1';
  // профиль устройства: максимальное качество фильмов и сериалов (высота кадра), 0 — без ограничения
  var MAXH_KEY = 'home_maxh_v1';
  var SAVER_KEY = 'home_saver_v1', WALL_KEY = 'home_wall_v1';
  var HIDEL_KEY = 'home_hide_lampac_v1';
  function getHideLampac() { try { return localStorage.getItem(HIDEL_KEY) !== '0'; } catch (e) { return true; } }   // по умолчанию кнопка «Онлайн» скрыта
  function getSaver() { try { return localStorage.getItem(SAVER_KEY) === '1'; } catch (e) { return false; } }
  function getWall() { try { return localStorage.getItem(WALL_KEY) || ''; } catch (e) { return ''; } }
  // заставка Lampa выключена по умолчанию; обои — картинка с сервера вместо фона карточек
  function applyLook() {
    try { if (window.Lampa && Lampa.Storage) Lampa.Storage.set('screensaver', getSaver() ? 'true' : 'false'); } catch (e) {}
    try { if (!getSaver() && Lampa.Screensaver && Lampa.Screensaver.disable) Lampa.Screensaver.disable(); } catch (e) {}
    try {
      var st = document.getElementById('home-wall'), w = getWall();
      if (!w) { if (st && st.parentNode) st.parentNode.removeChild(st); return; }
      if (!st) { st = document.createElement('style'); st.id = 'home-wall'; document.head.appendChild(st); }
      // обои — на главной и в списках; на карточке фильма остаётся её собственный фон из кадров (body получает класс cp-card)
      st.textContent = 'body:not(.cp-card){background:#000 url("' + BASE + '/home/wallpaper?name=' + encodeURIComponent(w) + '") center/cover no-repeat fixed !important}body:not(.cp-card) .background{opacity:0 !important}';
    } catch (e) {}
  }
  // класс cp-card на body, пока открыта карточка (страница фильма/сериала)
  setInterval(function () {
    try { var act = Lampa.Activity.active(); document.body.classList.toggle('cp-card', !!(act && act.component === 'full')); } catch (e) {}
  }, 300);
  var MAXHS = [{ v: 0, n: 'без ограничения (4K)' }, { v: 1080, n: 'до 1080p (FullHD)' }, { v: 720, n: 'до 720p' }];
  function getMaxH() { try { var v = parseInt(localStorage.getItem(MAXH_KEY), 10); return isNaN(v) ? 0 : v; } catch (e) { return 0; } }
  function maxHName() { for (var i = 0; i < MAXHS.length; i++) if (MAXHS[i].v === getMaxH()) return MAXHS[i].n; return getMaxH() + 'p'; }
  function getAfr() { try { if(window.AndroidJS&&AndroidJS.mcGetAfr)return AndroidJS.mcGetAfr();return localStorage.getItem(AFR_KEY) !== '0'; } catch (e) { return true; } }
  var FONT_KEY = 'tv_font_v1';
  var FONTS = ['Мелкий', 'Средний', 'Крупный'];
  function getFont() { try { var v = parseInt(localStorage.getItem(FONT_KEY), 10); return isNaN(v) ? 0 : v; } catch (e) { return 0; } }

  // Идентификатор этого устройства: последний канал помнится отдельно для каждого
  function devId() {
    try {
      // Lampa Media Center: устройство регистрирует оболочка, идентификатор общий
      try { if (window.AndroidJS && typeof AndroidJS.mcDeviceId === 'function') { var mv = AndroidJS.mcDeviceId(); if (mv) return mv; } } catch (e2) {}
      var v = localStorage.getItem('tv_dev_v1');
      if (!v) { v = (Date.now().toString(36) + Math.random().toString(36).slice(2, 8)).slice(0, 14); localStorage.setItem('tv_dev_v1', v); }
      return v;
    } catch (e) { return 'default'; }
  }

  function keepName(m) {
    return m === 0 ? 'выключено' : m < 0 ? 'всегда' : m < 120 ? m + ' мин после просмотра' : (m / 60) + ' ч после просмотра';
  }

  var cache = null;
  function loadChannels(cb, force) {
    if (cache && !force && Date.now() - cache.at < 60000) return cb(cache.list);
    api('/tv/channels', function (d) {
      if (d.error) return noty('ТВ: ' + (d.message || d.error));
      cache = { at: Date.now(), list: d.channels || [], loading: d.epg_loading };
      cb(cache.list);
    }, function () { noty('ТВ: нет связи с сервером Lampac'); });
  }

  // ---------- воспроизведение ----------
  function play(list, idx) {
    var c = list[idx];
    if (!c) return;
    setLast(c.id);
    var payload = {
      url: chUrl(c.url), title: c.name, iptv: true, live: true, home_base:BASE, home_tv_channel:String(c.id),
      tv_now_url: BASE + '/tv/now', tv_buffer: getBuf(), tv_font: getFont(), tv_dev: devId(), tv_surface: getUni() ? 'texture' : getSurf(), tv_engine: getEngine() === 'exo' ? '' : getEngine(), tv_panel_alpha: getAlpha(), tv_afr: getAfr() ? 1 : 0,
      playlist: list.map(function (x) { return { url: chUrl(x.url), title: x.num + '. ' + x.name }; })
    };
    if (window.AndroidJS && window.Lampa && Lampa.Android && Lampa.Android.openPlayer) {
      Lampa.Android.openPlayer(c.url, payload);
    } else {
      noty('Прямой эфир работает в приложении Lampa Home');
      try { Lampa.Player.play({ url: c.url, title: c.name }); } catch (e) {}
    }
  }

  // ---------- окна ----------
  function showSelect(title, items, back, onSelect) {
    Lampa.Select.show({
      title: title,
      items: items,
      onBack: back || function () { Lampa.Controller.toggle(ctrl()); },
      onSelect: function (a) { setTimeout(function () { onSelect(a); }, 300); }
    });
  }

  // Напоминания «включить передачу за 2 минуты до начала»: хранятся на сервере отдельно для этого устройства
  function showDay(c, back) {
    api('/tv/remind?dev=' + encodeURIComponent(devId()), function (rl) {
      var have = {};
      ((rl && rl.list) || []).forEach(function (r) { have[r.id + '|' + r.s] = 1; });
      api('/tv/day?epg=' + encodeURIComponent(c.epg), function (d) {
        var nowS = Date.now() / 1000;
        var items = (d.programs || []).map(function (p) {
          var live = p.s <= nowS && p.e > nowS;
          var mark = have[c.id + '|' + p.s] ? '⏰ ' : live ? '▶ ' : '';
          return { title: mark + hm(p.s) + '  ' + p.t, subtitle: (p.d || '').slice(0, 160), p: p };
        });
        if (!items.length) return noty('Программы нет');
        showSelect(c.num + '. ' + c.name + ' · программа (ОК на будущей передаче — напоминание)', items, back, function (a) {
          var p = a.p;
          if (!p || p.s <= Date.now() / 1000) { noty('Передача уже идёт'); return showDay(c, back); }
          api('/tv/remind/toggle?dev=' + encodeURIComponent(devId()) + '&id=' + encodeURIComponent(c.id) + '&s=' + p.s +
            '&t=' + encodeURIComponent(p.t) + '&n=' + encodeURIComponent(c.name), function (r) {
            noty(r && r.on ? 'Напоминание: включу в ' + hm(p.s - 120) : 'Напоминание снято');
            showDay(c, back);
          }, function () { noty('Нет связи с сервером'); showDay(c, back); });
        });
      });
    });
  }

  // таймер напоминаний на экране Lampa: за 2 минуты до начала открываем эфир на нужном канале
  // (когда сверху открыт плеер, страница скрыта — там напоминания обрабатывает сам плеер)
  function remindTick() {
    try { if (document.hidden) return; } catch (e) {}
    api('/tv/remind?dev=' + encodeURIComponent(devId()), function (d) {
      var now = Date.now() / 1000;
      ((d && d.list) || []).forEach(function (r) {
        if (now < r.s - 120) return;
        api('/tv/remind/done?dev=' + encodeURIComponent(devId()) + '&id=' + encodeURIComponent(r.id) + '&s=' + r.s, function () {
          loadChannels(function (list) {
            var idx = -1;
            for (var i = 0; i < list.length; i++) if (list[i].id === r.id) { idx = i; break; }
            if (idx < 0) return;
            noty('Напоминание: ' + hm(r.s) + ' ' + r.t);
            play(list, idx);
          });
        });
      });
    });
  }

  var mode = 'play'; // play | fav | epg
  var MODES = { play: 'смотреть', fav: 'избранное (отметка ★)', epg: 'программа канала' };

  function showChannels(title, list, from, back, focusIdx) {
    from = from || 0;
    var fv = favs();
    var slice = list.slice(from, from + PAGE);
    var items = [{ title: 'Нажатие на канал: ' + MODES[mode], subtitle: 'переключить режим', act: 'mode' }];
    // фокус сразу на канале (последний просмотренный), а не на переключателе режима
    var last = getLast(), focus = -1;
    if (typeof focusIdx === 'number') focus = focusIdx;
    else for (var q = from; q < from + slice.length; q++) if (list[q].id === last) { focus = q; break; }
    if (focus < 0 && slice.length) focus = from;
    slice.forEach(function (c, i) {
      items.push({
        title: (fv.indexOf(c.id) >= 0 ? '★ ' : '') + (c.src === 'ts' ? '📡 ' : c.src === 'hls' ? '🌐 ' : '') + c.num + '. ' + c.name,
        subtitle: nowText(c) || 'нет данных программы',
        idx: from + i,
        selected: from + i === focus
      });
    });
    if (from + PAGE < list.length) items.push({ title: 'Ещё…', subtitle: (list.length - from - PAGE) + ' каналов', more: true });

    showSelect(title, items, back, function (a) {
      var again = function (fi) { showChannels(title, list, from, back, fi); };
      if (a.act === 'mode') { mode = mode === 'play' ? 'fav' : mode === 'fav' ? 'epg' : 'play'; return again(from); }
      if (a.more) return showChannels(title, list, from + PAGE, back);
      var c = list[a.idx];
      if (mode === 'fav') {
        var f = favs(), k = f.indexOf(c.id);
        if (k >= 0) f.splice(k, 1); else f.push(c.id);
        saveFavs(f);
        return again(a.idx);
      }
      if (mode === 'epg') return showDay(c, function () { again(a.idx); });
      Lampa.Controller.toggle(ctrl());
      play(list, a.idx);
    });
  }

  function search(back) {
    Lampa.Input.edit({ title: 'Поиск канала', value: '', free: true, nosave: true }, function (val) {
      if (!val) return Lampa.Controller.toggle(ctrl());
      loadChannels(function (all) {
        var q = val.toLowerCase();
        var found = all.filter(function (c) { return c.name.toLowerCase().indexOf(q) >= 0; });
        if (!found.length) return noty('Каналов не найдено');
        showChannels('Поиск: ' + val, found, 0, back);
      });
    });
  }

  function nowOnAir(back) {
    loadChannels(function (all) {
      var list = all.filter(function (c) { return !!c.n; });
      if (!list.length) return noty('Программа передач ещё загружается, попробуйте через минуту');
      showChannels('Сейчас в эфире', list, 0, back);
    }, true);
  }

  // выбор внешнего IPTV плеера: сначала известные установленные, затем «Другое приложение…» со всем списком
  function pickExt(back, all) {
    if (!nativeApps()) return noty('Внешний плеер доступен в приложении Lampa Home');
    var inst = installedApps();
    var have = {};
    inst.forEach(function (x) { have[x.p] = x.n; });
    var cur = getExtPkg();
    var items;
    if (all) {
      items = inst.slice().sort(function (a, b) { return a.n.localeCompare(b.n); })
        .map(function (x) { return { title: x.n, subtitle: x.p, p: x.p, selected: x.p === cur }; });
    } else {
      items = KNOWN_IPTV.filter(function (k) { return have[k.p]; })
        .map(function (k) { return { title: k.n, subtitle: k.p, p: k.p, selected: k.p === cur }; });
      items.push({ title: 'Другое приложение…', subtitle: 'весь список установленных', other: true });
    }
    showSelect('Внешний IPTV плеер', items, function () { settings(back); }, function (a) {
      if (a.other) return pickExt(back, true);
      try { localStorage.setItem(EXTPKG_KEY, a.p); localStorage.setItem(EXT_KEY, '1'); } catch (e) {}
      noty('IPTV плеер: внешний (' + appName(a.p) + ')');
      settings(back);
    });
  }

  function settings(back) {
    api('/tv/status', function (s) {
      var items = [
        { title: 'Адрес Channel Manager', subtitle: (s.url || 'не задан') + (s.ok ? ' · каналов: ' + s.channels : s.error ? ' · ошибка' : ''), act: 'url' },
        { title: 'Буфер видео', subtitle: bufName(getBuf()) + ' · больше — реже подвисания, меньше — быстрее переключение', act: 'buf' },
        { title: 'При запуске приложения', subtitle: getAuto() ? 'сразу включать телевидение (последний канал)' : 'обычная главная Lampa', act: 'auto' },
        { title: 'IPTV плеер', subtitle: getExt() ? 'внешний: ' + extName() + ' · «Телевидение» и автозапуск открывают это приложение' : 'внутренний (плеер Lampa Home)', act: 'ext' },
        { title: 'Внешний IPTV плеер', subtitle: extName() + ' · выбирается вручную, автоматически не подставляется', act: 'extpkg' },
        { title: 'Движок эфира', subtitle: engineName() + ' · только для встроенного плеера ТВ; кино и сериалы не затрагивает', act: 'engine' },
        { title: 'Унифицированный поток', subtitle: (getUni() ? 'включён: сервер деинтерлейсит, звук AAC 256, вывод через GPU' : 'выключен: как отдаёт источник') + ' · для приставок с рывками и миганием', act: 'uni' },
        { title: 'Вывод видео', subtitle: (getSurf() === 'texture' ? 'через GPU (плавнее на ТОКС и Amlogic)' : 'аппаратный слой (по умолчанию)') + ' · если кадры идут рывками — переключите', act: 'surf' },
        { title: 'Шрифт списка каналов', subtitle: FONTS[getFont()] + ' · масштаб списка и программы', act: 'font' },
        { title: 'Прозрачность списка каналов', subtitle: getAlpha() + '% непрозрачности · меньше — лучше виден эфир за списком', act: 'alpha' },
        (window.AndroidJS && AndroidJS.showServerDialog) ? { title: 'Адрес сервера (приложение)', subtitle: (function () { try { return AndroidJS.getServerUrl() || 'не задан'; } catch (e) { return ''; } })() + ' · если сервер сменил адрес', act: 'srvurl' } : null,
        { title: 'Меню «Источник»: «Онлайн» (Lampac)', subtitle: getHideLampac() ? 'скрыт (остаются Кинопаб, Аниме, Торренты)' : 'показан', act: 'hidel' },
        { title: 'Заставка Lampa', subtitle: getSaver() ? 'включена' : 'выключена', act: 'saver' },
        { title: 'Обои', subtitle: getWall() || 'не выбраны (стандартный фон)', act: 'wall' },
        { title: 'Профиль устройства: максимальное качество', subtitle: maxHName() + ' · для фильмов и сериалов: выше этого плеер не выбирает (FullHD-телевизору 4K не нужен)', act: 'maxh' },
        { title: 'Частота кадров экрана (АФР)', subtitle: (getAfr() ? 'включена: экран подстраивается под канал' : 'выключена: режим экрана не меняется') + ' · если на каком-то канале цвета бледные, попробуйте выключить (движок mpv)', act: 'afr' },
        { title: 'Последний канал наготове', subtitle: keepName(s.keep_min) + ' · канал держится открытым на сервере, поэтому «Телевидение» запускается мгновенно', act: 'keep', keep: s.keep_min },
        { title: 'Обновить каналы и программу', subtitle: 'загрузить заново из Channel Manager', act: 'refresh' },
        { title: 'Программа передач', subtitle: s.epg_loading ? 'загружается…' : (s.epg_channels ? s.epg_channels + ' каналов, обновлена ' + (s.epg_age_min != null ? s.epg_age_min + ' мин назад' : '—') : (s.epg_error || 'нет данных')), act: 'epg' },
        { title: 'Раздел «ТВ»', subtitle: s.enable === false ? 'выключен' : 'включён', act: 'enable' }
      ];
      showSelect('ТВ · настройки', items.filter(Boolean), back, function (a) {
        if (a.act === 'keep') {
          var seq = [0, 60, 360, 1440, -1], i = seq.indexOf(a.keep);
          var nxt = seq[(i + 1) % seq.length];
          api('/tv/keep?min=' + nxt, function () { noty('Наготове: ' + keepName(nxt)); settings(back); });
        } else if (a.act === 'refresh') {
          api('/tv/refresh', function () { cache = null; noty('Обновление запущено, программа загрузится за ~30 секунд'); settings(back); });
        } else if (a.act === 'ext') {
          if (!getExt() && !getExtPkg()) { pickExt(back); return; }
          try { localStorage.setItem(EXT_KEY, getExt() ? '0' : '1'); } catch (e) {}
          noty(getExt() ? 'IPTV плеер: внешний (' + extName() + ')' : 'IPTV плеер: внутренний');
          settings(back);
        } else if (a.act === 'extpkg') {
          pickExt(back);
        } else if (a.act === 'uni') {
          try { localStorage.setItem(UNI_KEY, getUni() ? '0' : '1'); } catch (e) {}
          noty('Унифицированный поток: ' + (getUni() ? 'включён' : 'выключен') + ' (применится при следующем запуске канала)');
          settings(back);
        } else if (a.act === 'engine') {
          var ei = 0;
          ENGINES.forEach(function (e, i) { if (e.id === getEngine()) ei = i; });
          try { localStorage.setItem(ENG_KEY, ENGINES[(ei + 1) % ENGINES.length].id); } catch (e) {}
          noty('Движок эфира: ' + engineName() + ' (применится при следующем запуске канала)');
          settings(back);
        } else if (a.act === 'surf') {
          try { localStorage.setItem(SURF_KEY, getSurf() === 'texture' ? 'surface' : 'texture'); } catch (e) {}
          noty('Вывод видео: ' + (getSurf() === 'texture' ? 'через GPU' : 'аппаратный слой') + ' (применится при следующем запуске канала)');
          settings(back);
        } else if (a.act === 'alpha') {
          try { localStorage.setItem(ALPHA_KEY, String(ALPHAS[(ALPHAS.indexOf(getAlpha()) + 1) % ALPHAS.length])); } catch (e) {}
          noty('Прозрачность списка: ' + getAlpha() + '% (применится при следующем запуске канала)');
          settings(back);
        } else if (a.act === 'afr') {
          try { var enabled=!getAfr();localStorage.setItem(AFR_KEY,enabled?'1':'0');if(window.AndroidJS&&AndroidJS.mcChooseAfr)AndroidJS.mcChooseAfr(enabled);else if(window.AndroidJS&&AndroidJS.mcSetAfr)AndroidJS.mcSetAfr(enabled); } catch (e) {}
          noty('АФР эфира: ' + (getAfr() ? 'включена' : 'выключена') + ' (применится при следующем запуске канала)');
          settings(back);
        } else if (a.act === 'font') {
          try { localStorage.setItem(FONT_KEY, String((getFont() + 1) % 3)); } catch (e) {}
          noty('Шрифт: ' + FONTS[getFont()]);
          settings(back);
        } else if (a.act === 'srvurl') {
          try { AndroidJS.showServerDialog(); } catch (e) {}
        } else if (a.act === 'hidel') {
          try { localStorage.setItem(HIDEL_KEY, getHideLampac() ? '0' : '1'); } catch (e) {}
          noty('«Онлайн» (Lampac) в меню «Источник»: ' + (getHideLampac() ? 'скрыт' : 'показан') + ' (применится в следующей карточке)');
          settings(back);
        } else if (a.act === 'saver') {
          try { localStorage.setItem(SAVER_KEY, getSaver() ? '0' : '1'); } catch (e) {}
          applyLook();
          noty('Заставка Lampa: ' + (getSaver() ? 'включена' : 'выключена'));
          settings(back);
        } else if (a.act === 'wall') {
          api('/home/wallpapers', function (d) {
            var files = (d && d.files) || [];
            var items = [{ title: 'Без обоев (стандартный фон)', v: '', selected: !getWall() }].concat(files.map(function (f) { return { title: f, v: f, selected: getWall() === f }; }));
            if (!files.length) noty('Положите картинки на сервер: database/wallpapers');
            showSelect('Обои', items, function () { settings(back); }, function (w) {
              try { localStorage.setItem(WALL_KEY, w.v); } catch (e) {}
              applyLook();
              noty(w.v ? 'Обои: ' + w.v : 'Обои сняты');
              settings(back);
            });
          }, function () { noty('Не удалось получить список обоев'); settings(back); });
        } else if (a.act === 'maxh') {
          showSelect('Максимальное качество', MAXHS.map(function (m) { return { title: m.n, v: m.v, selected: getMaxH() === m.v }; }), function () { settings(back); }, function (m) {
            try { localStorage.setItem(MAXH_KEY, String(m.v)); } catch (e) {}
            noty('Профиль устройства: ' + maxHName());
            settings(back);
          });
        } else if (a.act === 'auto') {
          try { localStorage.setItem(AUTO_KEY, getAuto() ? '0' : '1'); } catch (e) {}
          noty(getAuto() ? 'При запуске включится последний канал' : 'Автозапуск ТВ выключен');
          settings(back);
        } else if (a.act === 'buf') {
          showSelect('Буфер видео', BUFS.map(function (b) { return { title: b.n, v: b.v, selected: getBuf() === b.v }; }), function () { settings(back); }, function (b) {
            try { localStorage.setItem(BUF_KEY, String(b.v)); } catch (e) {}
            noty('Буфер: ' + bufName(b.v));
            settings(back);
          });
        } else if (a.act === 'url') {
          Lampa.Input.edit({ title: 'Адрес Channel Manager (IP:порт)', value: s.url || 'http://', free: true, nosave: true }, function (val) {
            if (!val) return settings(back);
            api('/tv/config/set?url=' + encodeURIComponent(val), function (r) {
              cache = null;
              noty(r.ok ? 'Подключено: каналов ' + r.channels : 'Не удалось подключиться: ' + (r.error || ''));
              settings(back);
            }, function () { noty('Ошибка сохранения'); });
          });
        } else if (a.act === 'enable') {
          api('/tv/config/set?enable=' + (s.enable === false ? 'true' : 'false'), function () {
            noty(s.enable === false ? 'Раздел ТВ включён' : 'Раздел ТВ выключен (появится после перезапуска)');
            settings(back);
          });
        } else settings(back);
      });
    });
  }

  function openTv() {
    ctrl();
    api('/tv/status', function (s) {
      if (s.enable === false) return noty('Раздел ТВ выключен в настройках');
      var items = [];
      if (s.ok) {
        items.push({ title: 'Все каналы', subtitle: s.channels + ' каналов', act: 'all' });
        items.push({ title: 'Избранное', subtitle: favs().length + ' каналов', act: 'fav' });
        items.push({ title: 'Сейчас в эфире', subtitle: 'каналы с программой передач', act: 'now' });
        items.push({ title: 'Поиск канала', act: 'search' });
      } else {
        items.push({ title: 'Нет связи с Channel Manager', subtitle: (s.url || 'адрес не задан') + (s.error ? ' · ' + s.error : ''), act: 'settings' });
      }
      items.push({ title: 'Настройки', act: 'settings' });
      showSelect('ТВ', items, null, function (a) {
        var back = openTv;
        if (a.act === 'all') loadChannels(function (l) { showChannels('Все каналы', l, 0, back); }, true);
        else if (a.act === 'fav') {
          loadChannels(function (l) {
            var f = favs();
            var list = l.filter(function (c) { return f.indexOf(c.id) >= 0; });
            if (!list.length) { noty('Избранного пока нет: в списке каналов переключите режим на «избранное»'); return openTv(); }
            showChannels('Избранное', list, 0, back);
          });
        } else if (a.act === 'now') nowOnAir(back);
        else if (a.act === 'search') search(back);
        else if (a.act === 'settings') settings(back);
      });
    }, function () { noty('ТВ: сервер не отвечает'); });
  }

  // ---------- меню ----------
  // «Телевидение» — сразу эфир: последний канал с сервера (общий), запасной вариант — первый
  // n — необязательный номер канала в списке (от 1); без него — последний канал устройства
  function quickPlay(n) {
    if (typeof n !== 'number') n = 0;
    if (getExt()) {
      if (extReady()) {
        try { if (AndroidJS.launchApp(getExtPkg())) return; } catch (e) {}
        noty('Не удалось открыть ' + extName() + ' — включаю встроенный плеер');
      } else {
        noty('Внешний IPTV плеер не выбран или не установлен — включаю встроенный');
      }
    }
    loadChannels(function (list) {
      if (!list.length) return noty('ТВ: каналы не найдены. Проверьте адрес Channel Manager в настройках');
      if (n > 0) return play(list, Math.min(n, list.length) - 1);
      api('/tv/last?dev=' + encodeURIComponent(devId()), function (d) {
        var idx = 0;
        for (var i = 0; i < list.length; i++) if (list[i].id === (d && d.id)) { idx = i; break; }
        play(list, idx);
      }, function () { play(list, 0); });
    });
  }

  function addMenu() {
    if ($('.menu .menu__list [data-action="livetv"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="livetv"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Телевидение</div></li>');
    btn.on('hover:focus', function () { api('/tv/warm?dev=' + encodeURIComponent(devId()), function () {}); });
    btn.on('hover:enter', quickPlay);
    $('.menu .menu__list').eq(0).append(btn);
    var cfg = $('<li class="menu__item selector" data-action="livetv_cfg"><div class="menu__ico">' + ICON + '</div><div class="menu__text">ТВ: каналы и настройки</div></li>');
    cfg.on('hover:enter', openTv);
    $('.menu .menu__list').eq(0).append(cfg);
    if (window.__kp_placeMenu) window.__kp_placeMenu();
  }

  // автозапуск: один раз после старта, только если приложение открыто на главной (не по ссылке на карточку)
  function autoStart() {
    if (!getAuto()) return;
    if (!(getExt() ? nativeApps() : (window.AndroidJS && Lampa.Android && Lampa.Android.openPlayer))) return;
    var tries = 0;
    var wait = setInterval(function () {
      tries++;
      var comp = '';
      try { comp = Lampa.Activity.active().component; } catch (e) {}
      if (tries > 20) return clearInterval(wait);
      if (comp !== 'main') { if (comp) clearInterval(wait); return; }
      clearInterval(wait);
      quickPlay();
    }, 1500);
  }

  // открыть эфир на канале по id (вызывает дежурный напоминаний приложения)
  function playChannel(id, title) {
    loadChannels(function (list) {
      var idx = -1;
      for (var i = 0; i < list.length; i++) if (list[i].id === id) { idx = i; break; }
      if (idx < 0) return noty('Канал напоминания не найден');
      if (title) noty('Напоминание: ' + title);
      play(list, idx);
    });
  }

  function start() {
    autoStart();
    // в приложении напоминания ведёт дежурный (он работает и под открытым плеером); в браузере — таймер страницы
    var nativeWatch = false;
    try { if (window.AndroidJS && AndroidJS.tvRemindStart) nativeWatch = !!AndroidJS.tvRemindStart(BASE, devId()); } catch (e) {}
    if (!nativeWatch) setInterval(remindTick, 20000);
    // раздел показываем, только если он включён в настройках сервера
    api('/tv/config', function (c) {
      if (c && c.enable === false) return;
      addMenu();
      setTimeout(addMenu, 2000);
      setTimeout(addMenu, 6000);
    }, function () { addMenu(); });
  }

  // ---------- общая синхронизация просмотренного через сервер (весь контент, в том числе не из kino.pub) ----------
  // Штатная синхронизация Lampac переносит только карточки TMDB; здесь — таблица по хэшу файла: свежая отметка побеждает.
  (function tlsync() {
    if (window.plugin_tlsync_ready) return;
    window.plugin_tlsync_ready = true;
    // курсор и поколение привязаны к серверу: смена адреса не наследует чужой since
    var SK = (function () { var h = 0, s = String(BASE); for (var i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return (h >>> 0).toString(36); })();
    try { localStorage.removeItem('tl_sync_cursor_v2'); localStorage.removeItem('tl_sync_epoch'); } catch (e) {}          // ключи прежней версии без привязки к серверу больше не используются
    var TLS = String(Date.now());           // сеанс страницы; event = сеанс:время действия назначается один раз и хранится вместе с пакетом
    var pullSeq = 0, appliedSeq = 0, seenS = {}, pulledOnce = false, sentCard = {};
    function ackKey(h, ev) { return String(h) + '|' + (ev == null ? '' : String(ev)); }
    function copyJson(v) { return JSON.parse(JSON.stringify(v)); }
    function isMap(v) { return v !== null && typeof v === 'object' && !Array.isArray(v); }

    // ================= единое версионированное состояние =================
    // Один ключ home_tl_sync_v2_<SK>: epoch, cursor, known (подтверждённое сервером), pending (собственные неподтверждённые пакеты целиком), absent (подтверждённое, отсутствующее
    // в авторитетном полном снимке), rejected (терминальные отказы сервера), projection (входящие значения, ещё не применённые к Lampa). Любое изменение — копия → проверка →
    // ОДИН setItem → замена оперативного состояния: при отказе записи состояние остаётся прежним целиком. Ошибка ЧТЕНИЯ хранилища не превращается в пустое состояние:
    // инициализация останавливается, исходные ключи сохраняются, чтение повторяется.
    var SKEY = 'home_tl_sync_v2_' + SK;
    var LEG = { known: 'home_tl_known_' + SK, pending: 'home_tl_pending_' + SK, absent: 'home_tl_absent_' + SK, ev: 'home_tl_ev_' + SK, cursor: 'tl_sync_cursor_v2_' + SK, epoch: 'tl_sync_epoch_' + SK };
    function validateState(s) {
      if (!isMap(s) || s.version !== 2 || typeof s.epoch !== 'string' || !(s.cursor >= 0) || Math.floor(s.cursor) !== s.cursor) throw new Error('Invalid sync state');
      ['known', 'pending', 'absent', 'rejected', 'projection', 'evHint'].forEach(function (k) { if (!isMap(s[k])) throw new Error('Invalid sync map: ' + k); });
      Object.keys(s.pending).forEach(function (h) {
        var p = s.pending[h];
        if (!isMap(p) || String(p.h) !== h || typeof p.u !== 'number' || typeof p.event !== 'string') throw new Error('Invalid pending: ' + h);
      });
    }
    function preserveCorrupt(key, raw) { try { localStorage.setItem(key + '_corrupt', raw); } catch (e) {} }
    function readLegacyMap(key) {
      var raw = localStorage.getItem(key);                 // ошибка чтения выходит наружу
      if (raw === null) return {};
      var v; try { v = JSON.parse(raw); } catch (e) { preserveCorrupt(key, raw); throw e; }
      if (!isMap(v)) { preserveCorrupt(key, raw); throw new Error('Invalid map: ' + key); }
      return v;
    }
    function loadLegacy() {
      var epoch = localStorage.getItem(LEG.epoch), cur = localStorage.getItem(LEG.cursor);
      return { version: 2, epoch: epoch === null ? '0' : String(epoch), cursor: parseInt(cur, 10) || 0, known: readLegacyMap(LEG.known), pending: readLegacyMap(LEG.pending),
               absent: readLegacyMap(LEG.absent), rejected: {}, projection: {}, evHint: readLegacyMap(LEG.ev) };
    }
    function openStore() {
      var raw = localStorage.getItem(SKEY), state;
      if (raw === null) { state = loadLegacy(); validateState(state); localStorage.setItem(SKEY, JSON.stringify(state)); }       // прежние ключи не удаляются
      else { try { state = JSON.parse(raw); } catch (e) { preserveCorrupt(SKEY, raw); throw e; } validateState(state); }
      return {
        peek: function () { return state; },                                   // оперативное состояние, не изменять напрямую
        commit: function (next) { next = copyJson(next); validateState(next); localStorage.setItem(SKEY, JSON.stringify(next)); state = next; },
        change: function (edit) { var next = copyJson(state); if (edit(next) === false) return false; this.commit(next); return true; }
      };
    }
    var S = null, openErr = null;
    // диагностический журнал (приложение собирает и отправляет сам, с ограничением по сроку и объёму); вне приложения ничего не делает
    function dl(l, c, k, x) { try { if (window.AndroidJS && typeof AndroidJS.dlog === 'function') AndroidJS.dlog(l, c, k || '', x || ''); } catch (e) {} }
    function tryOpen() { if (S) return true; try { S = openStore(); openErr = null; return true; } catch (e) { openErr = e; dl('e', 'tl_state_open', String((e && e.message) || e).slice(0, 40), ''); return false; } }
    // 'ok' | 'noop' | 'fail'
    function safeChange(edit) { if (!S) return 'fail'; try { return S.change(edit) === false ? 'noop' : 'ok'; } catch (e) { return 'fail'; } }
    var memCapture = {};                                      // новые собственные действия, которые не удалось сохранить: держатся в памяти и пересохраняются
    function archive(key, entry) {
      try { var a = JSON.parse(localStorage.getItem(key) || '[]') || []; a.push(entry); localStorage.setItem(key, JSON.stringify(a.slice(-30))); } catch (e) {}
    }

    // порядок событий — как на сервере: пустой < «сеанс:номер» по числам < прочее по ordinal
    function evParts(e) { var i = e.lastIndexOf(':'); if (i <= 0) return null; var a = e.slice(0, i), b = e.slice(i + 1); if (a === '' || b === '' || isNaN(Number(a)) || isNaN(Number(b))) return null; return [Number(a), Number(b)]; }
    function evRank(e) { return e === '' ? 0 : evParts(e) ? 1 : 2; }
    function cmpEvent(a, b) {
      a = a == null ? '' : String(a); b = b == null ? '' : String(b);
      if (a === b) return 0;
      var ra = evRank(a), rb = evRank(b); if (ra !== rb) return ra < rb ? -1 : 1;
      if (ra === 1) { var x = evParts(a), y = evParts(b), c = x[0] !== y[0] ? (x[0] < y[0] ? -1 : 1) : (x[1] < y[1] ? -1 : x[1] > y[1] ? 1 : 0); if (c) return c; }
      return a < b ? -1 : 1;
    }
    function cmpPair(u1, e1, u2, e2) { return u1 !== u2 ? (u1 < u2 ? -1 : 1) : cmpEvent(e1, e2); }

    function store() {
      var name = 'file_view';
      try { if (Lampa.Account.Permit.sync) name += '_' + Lampa.Account.Permit.account.profile.id; } catch (e) {}
      var v = null;
      try { v = Lampa.Storage.get(name, '{}'); } catch (e) {}
      if (typeof v === 'string') { try { v = JSON.parse(v); } catch (e) { v = null; } }
      if (!v || typeof v !== 'object') { try { v = JSON.parse(localStorage.getItem(name) || '{}'); } catch (e) { v = {}; } }
      return v || {};
    }

    function makeRow(h, r, u, st) {
      var hint = st && st.evHint && st.evHint[h];
      var ev = hint && hint.u === u && hint.e ? { e: hint.e, s: hint.s || TLS } : { e: TLS + ':' + u, s: TLS };        // event прежней версии переносится, если это то же действие
      var row = { h: String(h), t: r.time || 0, d: r.duration || 0, p: r.percent || 0, u: u, c: hashCard()[h] || '', event: ev.e, session: ev.s };
      if (!(r.percent > 0 || r.time > 0) && r.updated) row.op = 'reset';        // обнулённая отметка со штампом — явный сброс просмотра
      return row;
    }

    // Разные действия могут иметь один updated: идентичность задаётся отдельным номером, а повтор использует уже готовый пакет.
    var actionSeq = 0;
    function makeExplicitRow(h, cur) {
      var row = makeRow(h, cur, cur.updated, S && S.peek());
      actionSeq = Math.max(actionSeq, cur.updated) + 1;
      row.event = TLS + ':' + actionSeq;
      row.session = TLS;
      return row;
    }

    // Явное новое действие пользователя (вызывается из обёртки Timeline.update, не из обхода file_view): пакет фиксируется целиком ДО отправки.
    // Оно снимает запреты (rejected / absent / ожидающая проекция) этого хеша.
    function captureExplicit(h, cur) {
      var u = cur.updated; if (!u) return;
      var row = makeExplicitRow(h, cur);
      var res = safeChange(function (s) {
        s.pending[row.h] = row; delete s.rejected[row.h]; delete s.projection[row.h]; if (s.absent[row.h] && u > s.absent[row.h].u) delete s.absent[row.h];
      });
      if (res === 'fail') { memCapture[row.h] = row; dl('e', 'tl_capture_mem', 'state_write_failed', 'h=' + row.h); }
      else delete memCapture[row.h];
    }

    // Сначала переносим аварийную очередь: входящая проекция не должна затереть действие, которое пока хранится только в памяти.
    function flushMemCapture() {
      var keys = Object.keys(memCapture);
      if (!keys.length) return true;
      if (!tryOpen()) return false;
      var batch = copyJson(memCapture);
      var res = safeChange(function (s) {
        keys.forEach(function (h) {
          var row = batch[h], own = s.pending[h];
          if (own && cmpPair(own.u, own.event, row.u, row.event) > 0) return;
          s.pending[h] = row;
          delete s.rejected[h]; delete s.projection[h];
          if (s.absent[h] && row.u > s.absent[h].u) delete s.absent[h];
        });
      });
      if (res !== 'ok') return false;
      keys.forEach(function (h) {
        var now = memCapture[h];
        if (now && now.event === batch[h].event && now.u === batch[h].u) delete memCapture[h];
      });
      return true;
    }

    // можно ли захватывать/отправлять строку автоматически (по обходу file_view или ради регистрации карточки)
    function allowAutomatic(st, h, u) {
      if (st.projection[h]) return false;                                  // серверное значение ещё не применено — не превращать его в своё действие
      var rj = st.rejected[h]; if (rj && u <= rj.u) return false;          // терминальный отказ сервера
      var ab = st.absent[h]; if (ab && u <= ab.u) return false;            // отсутствует в авторитетном снимке
      return true;
    }

    function push() {
      if (!pulledOnce || !tryOpen()) return;
      // 0) собственные действия, которые не удалось сохранить, пересохраняются
      if (!flushMemCapture()) return;
      var v = store(), st = S.peek();
      // 1) автоматический захват: строки file_view, которых сервер ещё не подтвердил
      var toCapture = [];
      Object.keys(v).forEach(function (h) {
        var r = v[h];
        if (!r || typeof r !== 'object' || h === '0') return;
        if (!(r.percent > 0 || r.time > 0) && !r.updated) return;   // пустые без штампа не нужны; сброс со штампом — нужен
        var u = r.updated || 1, p0 = st.pending[h];
        if (!allowAutomatic(st, h, u)) return;
        if (u > (st.known[h] || 0) && (!p0 || p0.u < u)) toCapture.push(makeRow(h, r, u, st));
      });
      if (toCapture.length) safeChange(function (s) { toCapture.forEach(function (row) { if (!s.pending[row.h] || s.pending[row.h].u < row.u) s.pending[row.h] = row; if (s.absent[row.h] && row.u > s.absent[row.h].u) delete s.absent[row.h]; }); });
      st = S.peek();
      // 2) отправка: полные пакеты pending; подтверждённые строки без event — только регистрация карточки (на равном u сервер лишь дополнит карточку)
      var out = [];
      Object.keys(st.pending).forEach(function (h) { out.push(st.pending[h]); });
      Object.keys(v).forEach(function (h) {
        var r = v[h];
        if (!r || typeof r !== 'object' || h === '0' || st.pending[h] || memCapture[h]) return;
        if (!(r.percent > 0 || r.time > 0) && !r.updated) return;
        var u = r.updated || 1, cardKey = hashCard()[h] || '';
        if (!allowAutomatic(st, h, u)) return;                          // и регистрация карточки подчиняется запретам
        if ((st.known[h] || 0) >= u && cardKey && !sentCard[h]) out.push({ h: h, t: r.time || 0, d: r.duration || 0, p: r.percent || 0, u: u, c: cardKey });
      });
      if (!out.length) return;
      out.sort(function (a, b) { return b.u - a.u; });
      out = out.slice(0, 3000);
      $.ajax({ url: BASE + '/home/tl/set', type: 'POST', contentType: 'application/json', data: JSON.stringify(out), timeout: 20000,
        success: function (d) {
          var st2 = {};
          try { ((d && d.results) || []).forEach(function (x) { st2[ackKey(x.h, x.event)] = x; }); } catch (e) {}
          var refresh = false, acked = [];
          // подтверждения и отказы всей пачки — ОДНА запись состояния: удаление pending и обновление known/rejected не расходятся
          var res = safeChange(function (s) {
            var changed = false;
            out.forEach(function (r) {
              var a = st2[ackKey(r.h, r.event)], status = a && a.status, own = s.pending[r.h];
              var isOwn = !!(r.event && own && own.event === r.event);        // поздний ответ про прежнее событие не трогает более новое
              if (status === 'accepted' || status === 'duplicate' || status === 'stale') {
                if (r.event && !isOwn) { if ((s.known[r.h] || 0) < r.u) { s.known[r.h] = r.u; changed = true; } return; }
                s.known[r.h] = Math.max(s.known[r.h] || 0, r.u); changed = true;
                if (isOwn) { delete s.pending[r.h]; if (status !== 'accepted') refresh = true; }
                acked.push(r);
              } else if (status === 'invalid' && isOwn) {
                // терминальный отказ: запоминается отдельно от диагностического архива и запрещает автоматический повтор; known не продвигается
                s.rejected[r.h] = { u: own.u, event: own.event, reason: (a && a.reason) || 'invalid', row: copyJson(own) };
                dl('w', 'tl_rejected', (a && a.reason) || 'invalid', 'h=' + r.h);
                delete s.pending[r.h]; changed = true;
                archive('home_tl_rejected_' + SK, { at: Date.now(), reason: (a && a.reason) || 'invalid', row: own });
              }
            });
            return changed ? undefined : false;
          });
          if (res === 'ok') acked.forEach(function (r) { if (r.c) sentCard[r.h] = true; });
          // после подтверждения серверное значение применяется по ревизии: строка могла уже попасть в ранее потреблённое приращение
          if (res === 'ok' && refresh) { S.peek().cursor = S.peek().cursor; setTimeout(function () { pullFrom0(); }, 200); }
        } });
    }
    function pullFrom0() { if (!S) return; safeChange(function (s) { s.cursor = 0; }); pull(); }

    function applyRow(r, v, s, fullSnap, knownOld, required, seenNew) {
      var l0 = v[r.h], lu0 = (l0 && typeof l0 === 'object' && l0.updated) || 0;
      var pend = s.pending[r.h];
      // полный снимок: локальное неотправленное действие без записи pending (появилось до захвата) фиксируется как pending и остаётся
      if (!pend && fullSnap && lu0 > (knownOld[r.h] || 0) && lu0 > r.u) { s.pending[r.h] = makeRow(r.h, l0, lu0, s); return; }
      if (pend) {
        var c = cmpPair(pend.u, pend.event, r.u, r.event);               // собственное неподтверждённое действие: порядок решает сервер по паре (u, event)
        if (c > 0) return;                                              // локальное побеждает: payload не трогаем, оно будет доставлено
        delete s.pending[r.h];                                          // совпало (это наше, уже принятое) или сервер новее
        if (c < 0) archive('home_tl_rejected_' + SK, { at: Date.now(), reason: 'superseded_by_server', row: pend });
      }
      seenNew[r.h] = r.s || 0;
      s.known[r.h] = Math.max(s.known[r.h] || 0, r.u);
      delete s.absent[r.h];                                             // сервер снова знает эту строку
      var rejected = s.rejected[r.h];
      if (rejected && cmpPair(r.u, r.event, rejected.u, rejected.event) >= 0) delete s.rejected[r.h];
      var l = v[r.h], lu = (l && typeof l === 'object' && l.updated) || 0;
      var same = l && typeof l === 'object' && Math.abs((l.time || 0) - (r.t || 0)) <= 1 && Math.abs((l.percent || 0) - (r.p || 0)) < 0.1 && Math.abs((l.duration || 0) - (r.d || 0)) <= 1;
      if (fullSnap ? (r.u === lu && same) : (r.u < Math.max(lu, 1) || (r.u === lu && same))) { delete s.projection[r.h]; return; }
      var row = { hash: r.h, percent: r.p, time: r.t, duration: r.d, updated: r.u, received: true };
      s.projection[r.h] = row; required.push(row);                      // полное содержимое сохраняется вместе с курсором и применяется отдельным обработчиком
    }

    function pull() {
      if (!tryOpen()) return;
      var mySeq = ++pullSeq, since = S.peek().cursor, ep = S.peek().epoch;
      $.ajax({ url: BASE + '/home/tl/all?since=' + since + '&epoch=' + encodeURIComponent(ep), dataType: 'json', timeout: 20000, cache: false, success: function (d) {
        if (mySeq < appliedSeq) return;          // более поздний запрос уже применён: устаревший ответ не затирает новое
        if (!flushMemCapture()) return;
        appliedSeq = mySeq;
        if (!S) return;
        var s0 = S.peek(), next = copyJson(s0), knownOld = s0.known, fullSnap = false;
        if (d && d.epoch && String(d.epoch) !== next.epoch) {              // сервер сменил поколение: ответ — полный снимок НОВОГО поколения
          next.epoch = String(d.epoch); next.cursor = 0; fullSnap = true;
          dl('i', 'tl_epoch_change', next.epoch, '');
          next.known = {}; next.projection = {};                           // подтверждения и неприменённые проекции прежнего поколения недействительны
        }
        var rows = (d && d.rows) || [], v = store(), mx = next.cursor, required = [], seenNew = {};
        rows.forEach(function (r) {
          if ((r.s || 0) > mx) mx = r.s;
          if (!fullSnap && (r.s || 0) < (seenS[r.h] || 0)) return;           // строка с меньшей серверной ревизией, чем уже применённая, — устаревшая
          applyRow(r, v, next, fullSnap, knownOld, required, seenNew);
        });
        if (fullSnap) {
          // Авторитетный полный снимок: подтверждённая прежним поколением строка, которой в нём нет, больше не существует. Устойчивая отметка absent запрещает ВСЕ автоматические
          // отправки этой строки; снятие отображения — received-обновлением (не reset) через ту же долговечную очередь проекции; настоящее pending не затрагивается.
          var inSnap = {}; rows.forEach(function (r) { inSnap[r.h] = 1; });
          var dropped = [];
          Object.keys(v).forEach(function (h) {
            var l = v[h]; if (!l || typeof l !== 'object' || inSnap[h] || next.pending[h]) return;
            var lu = l.updated || 0;
            if (lu > 0 && lu <= (knownOld[h] || 0)) {
              next.known[h] = lu; next.absent[h] = { u: lu, epoch: next.epoch };
              next.projection[h] = { hash: h, time: 0, duration: 0, percent: 0, updated: lu, received: true };
              dropped.push({ h: h, u: lu, t: l.time || 0, p: l.percent || 0 });
            }
          });
          if (dropped.length) archive('home_tl_dropped_' + SK, { at: Date.now(), epoch: next.epoch, rows: dropped });
        }
        if (mx > next.cursor) next.cursor = mx;
        try { S.commit(next); }
        catch (e) { return; }                                              // не записали — курсор не сдвинут, те же строки придут снова; seenS/sentCard не менялись
        Object.keys(seenNew).forEach(function (h) { seenS[h] = seenNew[h]; });
        if (fullSnap) { seenS = {}; Object.keys(seenNew).forEach(function (h) { seenS[h] = seenNew[h]; }); sentCard = {}; }
        pulledOnce = true;
        drain();
        if (fullSnap) setTimeout(push, 500);          // неотправленное повторяется сразу, не ожидая следующего круга
      }, error: function () { dl('w', 'tl_pull_error', '', ''); } });
    }

    // Применение сохранённых входящих значений к Lampa. Запись удаляется только после проверки фактического значения в file_view; ошибка оставляет её для повтора (в том числе после перезапуска).
    function verifyApplied(h, row) {
      var x = store()[h];
      return !!x && Math.abs((x.time || 0) - (row.time || 0)) <= 1 && Math.abs((x.percent || 0) - (row.percent || 0)) < 0.1 && (x.updated || 0) === (row.updated || 0);
    }
    function drain() {
      if (!S) return;
      var s = S.peek();
      Object.keys(s.projection).forEach(function (h) {
        if (memCapture[h] || S.peek().pending[h]) return;                   // в том числе новое действие, которое пока не удалось сохранить
        var row = S.peek().projection[h], ok = false;
        if (!row) return;
        try { Lampa.Timeline.update(copyJson(row)); ok = verifyApplied(h, row); } catch (e) { ok = false; }
        if (ok) safeChange(function (n) { if (JSON.stringify(n.projection[h]) !== JSON.stringify(row)) return false; delete n.projection[h]; });
      });
    }

    function cycle() { if (!tryOpen() || !flushMemCapture()) return; drain(); pull(); setTimeout(push, 4000); }
    setTimeout(cycle, 3000);
    setInterval(cycle, 20000);
    // сразу после изменения отметки (серия досмотрена, пауза, сброс) собственное действие фиксируется целиком и уходит на сервер
    var pushTimer = 0;
    function pushSoon() { clearTimeout(pushTimer); pushTimer = setTimeout(push, 2500); }
    try {
      if (window.Lampa && Lampa.Timeline && typeof Lampa.Timeline.update === 'function' && !Lampa.Timeline.__tlWrapped) {
        var origUpd = Lampa.Timeline.update;
        Lampa.Timeline.update = function (p) {
          var r = origUpd.apply(this, arguments);
          try {
            if (p && p.home_native) return r;
            if (!p || !p.received) {
              tryOpen();
              if (p && p.hash) { var cur = store()[p.hash]; if (cur && typeof cur === 'object' && cur.updated) captureExplicit(String(p.hash), cur); }
              pushSoon();
            }
          } catch (e) {}
          return r;
        };
        Lampa.Timeline.__tlWrapped = true;
      }
    } catch (e) {}
    try { document.addEventListener('visibilitychange', function () { if (!document.hidden) setTimeout(cycle, 1500); }); } catch (e) {}
    tryOpen(); drain();
  })();

  // реестр «хэш отметки → карточка тайтла» (tmdb_tv / tmdb_movie): заполняется при запуске плеера, уходит на сервер вместе с отметкой,
  // чтобы сервер строил «Продолжить просмотр», «Мои сериалы» и обмен с kino.pub только из своей базы
  var HC_KEY = 'home_hashcard_v1';
  function hashCard() {
    if (window.__hashCard) return window.__hashCard;
    var o = {};
    try { o = JSON.parse(localStorage.getItem(HC_KEY) || '{}') || {}; } catch (e) {}
    window.__hashCard = o;
    return o;
  }
  function saveHashCard() {
    try {
      var o = hashCard(), ks = Object.keys(o);
      if (ks.length > 1500) ks.slice(0, ks.length - 1200).forEach(function (k) { delete o[k]; });
      localStorage.setItem(HC_KEY, JSON.stringify(o));
    } catch (e) {}
  }
  function registerHashes(data, key) {
    if (!key || !data) return;
    var o = hashCard();
    var add = function (tl) { if (!(tl && tl.hash) || String(tl.hash) === '0') return; var h = String(tl.hash); delete o[h]; o[h] = key; };
    add(data.timeline);
    try { (data.playlist || []).forEach(function (it) { add(it && it.timeline); }); } catch (e) {}
    saveHashCard();
  }

  // ---------- параметры воспроизведения кино и сериалов: ключ тайтла (память звуковой дорожки) и профиль устройства ----------
  (function playerExtras() {
    if (window.plugin_pextras_ready) return;
    window.plugin_pextras_ready = true;
    function titleKey(data) {
      try {
        if (data && data.wr_card) return data.wr_card;
        if (data && data.home_timeline_only) return '';
        var a = Lampa.Activity.active() || {};
        var c = (data && (data.card || data.movie)) || a.card || a.movie || {};
        if (c.id && /^\d+$/.test(String(c.id))) return c.id + '_' + ((c.media_type === 'tv' || c.name || c.original_name || c.number_of_seasons || c.first_air_date) ? 'tv' : 'movie');
        if (window.__apKey && Date.now() - (window.__apKeyAt || 0) < 15000) return String(window.__apKey);
      } catch (e) {}
      return '';
    }
    var tries = 0;
    var t = setInterval(function () {
      tries++;
      try {
        if (window.Lampa && Lampa.Android && typeof Lampa.Android.openPlayer === 'function') {
          clearInterval(t);
          if (Lampa.Android.__pxWrapped) return;
          var orig = Lampa.Android.openPlayer;
          Lampa.Android.openPlayer = function (url, data) {
            try {
              if (window.AndroidJS && AndroidJS.mcSetAfr) AndroidJS.mcSetAfr(getAfr());
              if (data && typeof data === 'object') data.tv_afr = getAfr() ? 1 : 0;
              if (data && typeof data === 'object' && !data.live && !data.iptv) {
                var k = titleKey(data);
                if (k) { data.audio_key = k; registerHashes(data, k); }
                data.home_base = BASE;
                // буфер фильмов и сериалов, МБ (0 — стандартный); ТВ-эфир этого не касается
                try { var vb = parseInt(localStorage.getItem('home_vodbuf_v1'), 10); if (vb > 0) data.vod_buf_mb = vb; } catch (e3) {}
                // торренты и остальные источники: плеер сам отправляет прогресс серий на сервер (Кинопаб передаёт свои ключи в startPlay)
                try {
                  if (!data.tl_meta) {
                    var known = k && /^\d+_(tv|movie)$/.test(k);
                    var kp = known ? k.split('_') : [], tmdb = kp[0], media = kp[1];
                    var mk = function (tl, it) {
                      if (!tl || !tl.hash || String(tl.hash) === '0') return null;
                      var wrc = (it && it.wr_card) || (data && data.wr_card) || '';
                      if (wrc) return { id: 'tl-' + tl.hash, h: String(tl.hash), c: wrc };
                      // сезон и серия: у обычных элементов — season/episode, у аниме-источников — внутри hs
                      var se = it && (parseInt(it.season, 10) || parseInt(it.hs && it.hs.season, 10)), ep = it && (parseInt(it.episode, 10) || parseInt(it.hs && it.hs.ep, 10));
                      var id = !known ? 'tl-' + tl.hash : media === 'tv' ? (se && ep ? 'tv-' + tmdb + '-s' + se + 'e' + ep : 'tl-' + tl.hash) : 'movie-' + tmdb;
                      return { id: id, h: String(tl.hash), c: known ? k : '' };
                    };
                    var pl = data.playlist;
                    var meta = pl && pl.length ? pl.map(function (it) { return mk(it && it.timeline, it); }) : [mk(data.timeline, data)];
                    if (meta.some(function (row) { return !!row; })) data.tl_meta = JSON.stringify(meta);
                  }
                } catch (e2) {}
                // сведения для «Продолжить» уходят вместе со строкой просмотра (resume): нативная очередь доставляет их вместе с позицией
                try {
                  if (data.tl_meta && typeof window.__home_resume === 'function') {
                    var tm2 = JSON.parse(data.tl_meta), anyPi = false;
                    tm2.forEach(function (e) { if (e && e.h) { var rr = window.__home_resume(e.h); if (rr) { e.r = rr; if (rr.pi !== undefined) anyPi = true; } } });
                    data.tl_meta = JSON.stringify(tm2);
                    // общий список серий раздачи — один раз на запуск (иначе он повторялся бы в каждой серии и не помещался в Intent)
                    if (anyPi && data.playlist && data.playlist.length) {
                      data.resume_playlist = JSON.stringify(data.playlist.slice(0, 300).map(function (x) { return window.__home_torrent_entry(x || {}); }));
                    }
                  }
                } catch (e4) {}
                var mh = getMaxH();
                if (mh > 0) data.max_height = mh;
              }
            } catch (e) {}
            return orig.apply(this, arguments);
          };
          Lampa.Android.__pxWrapped = true;
        }
      } catch (e) {}
      if (tries > 60) clearInterval(t);
    }, 500);
  })();

  // ---------- общие закладки (Избранное, Позже, Смотрю…) через сервер ----------
  // Закладки Lampa лежали только на устройстве. Теперь каждое устройство отправляет изменения (добавили/убрали) в единую базу на сервере и
  // забирает чужие; с папками kino.pub сервер обменивается сам. Побеждает более свежее изменение, удаление доходит до всех.
  (function bmsync() {
    if (window.plugin_bmsync_ready) return;
    window.plugin_bmsync_ready = true;
    var CATS = ['book', 'like', 'wath', 'look', 'viewed', 'scheduled', 'continued', 'thrown'];
    var KEY='home_bm_state_v2', busy=false, applying=false;
    var state={snap:{},pending:{},incoming:[],cursor:0,clock:0};
    try{var saved=JSON.parse(localStorage.getItem(KEY)||'null');if(saved)state=saved;
      else{state.snap=JSON.parse(localStorage.getItem('home_bm_snap_v1')||'{}')||{};state.cursor=0;}}catch(e){}
    function saveState(){try{localStorage.setItem(KEY,JSON.stringify(state));return true;}catch(e){return false;}}
    function identity(cat,k){return cat+'|'+k;}
    function cardKey(c) {
      var id = parseInt(c && c.id, 10);
      if (!id || String(c.id) !== String(id)) return '';
      var tv = c.media_type === 'tv' || c.first_air_date || c.number_of_seasons;
      return (tv ? 'tv' : 'movie') + ':' + id;
    }
    function slim(c) {
      var o = {};
      ['id', 'title', 'name', 'original_title', 'original_name', 'poster_path', 'backdrop_path', 'release_date', 'first_air_date', 'vote_average',
        'genre_ids', 'original_language', 'origin_country', 'number_of_seasons', 'media_type'].forEach(function (k) { if (c[k] !== undefined && c[k] !== null) o[k] = c[k]; });
      if (c.overview) o.overview = String(c.overview).slice(0, 300);
      o.source = 'tmdb';
      return o;
    }
    function localState() {
      var out = {};
      CATS.forEach(function (cat) {
        out[cat] = {};
        var arr = [];
        arr = Lampa.Favorite.get({ type: cat });
        if(!Array.isArray(arr))throw new Error('Bookmark storage unavailable');
        arr.forEach(function (c) { var k = cardKey(c); if (k) out[cat][k] = c; });
      });
      return out;
    }

    function capture(){
      var local;try{local=localState();}catch(e){return false;}
      CATS.forEach(function (cat) {
        var was=state.snap[cat]||{},now=local[cat];
        function remember(k,del){state.clock=Math.max(Date.now(),state.clock+1);state.pending[identity(cat,k)]={cat:cat,k:k,card:del?undefined:slim(now[k]),u:state.clock,d:del};}
        Object.keys(now).forEach(function(k){if(!was[k])remember(k,false);});
        Object.keys(was).forEach(function(k){if(!now[k])remember(k,true);});
        state.snap[cat]={};Object.keys(now).forEach(function(k){state.snap[cat][k]=1;});
      });
      return saveState();
    }
    function project(){
      // Persist the incoming projection before touching Favorite; a restart can replay it.
      if(!saveState())return false;
      applying=true;
      try{
        var local=localState();
        state.incoming.forEach(function(r){
          state.clock=Math.max(state.clock,r.u||0);
          if(CATS.indexOf(r.cat)<0||state.pending[identity(r.cat,r.k)])return;
          var has=!!local[r.cat][r.k];
          state.snap[r.cat]=state.snap[r.cat]||{};
          if(r.d){if(has)Lampa.Favorite.remove(r.cat,local[r.cat][r.k]);delete state.snap[r.cat][r.k];}
          else if(r.card&&r.card.id){if(!has)Lampa.Favorite.add(r.cat,r.card);state.snap[r.cat][r.k]=1;}
        });
      }catch(e){return false;}finally{applying=false;}
      var rows=state.incoming,cursor=state.cursor;
      state.cursor=state.nextCursor==null?state.cursor:state.nextCursor;state.incoming=[];
      if(!saveState()){state.incoming=rows;state.cursor=cursor;return false;}
      return true;
    }
    function push(done) {
      if(!capture())return done(false);
      var rows=Object.keys(state.pending).map(function(k){return state.pending[k];});
      if(!rows.length)return done(true);
      $.ajax({ url: BASE + '/home/bm/set', type: 'POST', contentType: 'application/json', data: JSON.stringify(rows), timeout: 20000,
        success: function (reply) {
          if(!reply||reply.ok!==true)return done(false);
          // Only acknowledge the sent operation, never a newer click made in flight.
          var before=Object.assign({},state.pending),cursor=state.cursor;
          rows.forEach(function(r){var k=identity(r.cat,r.k);if(state.pending[k]&&state.pending[k].u===r.u)delete state.pending[k];});
          state.cursor=0; // Reconcile rejected/stale writes against the authoritative snapshot.
          if(!saveState()){state.pending=before;state.cursor=cursor;return done(false);}
          done(true);
        },
        error: function () { done(false); } });
    }

    function pull(done) {
      $.ajax({ url: BASE + '/home/bm/all?since=' + state.cursor, dataType: 'json', timeout: 20000, cache: false, success: function (d) {
        if(!d||!Array.isArray(d.rows))return done();
        if(!capture())return done();
        state.incoming=d.rows;state.nextCursor=state.cursor;
        d.rows.forEach(function(r){state.nextCursor=Math.max(state.nextCursor,r.s||0);});
        project();done();
      }, error: function () { if (done) done(); } });
    }

    function cycle() {
      if (busy) return;
      busy = true;
      if(!project()){busy=false;return;}
      push(function(ok){if(!ok){busy=false;return;}pull(function(){busy=false;});});
    }
    setTimeout(cycle, 5000);
    setInterval(cycle, 20000);
    var soon = 0;
    function pushSoon() { if(applying)return;capture();clearTimeout(soon);soon=setTimeout(cycle,2500); }
    try {
      ['add', 'remove', 'toggle'].forEach(function (fn) {
        var orig = Lampa.Favorite[fn];
        if (typeof orig !== 'function' || orig.__bmWrapped) return;
        var w = function () { var r = orig.apply(this, arguments); try { pushSoon(); } catch (e) {} return r; };
        w.__bmWrapped = true;
        Lampa.Favorite[fn] = w;
      });
    } catch (e) {}
    try { document.addEventListener('visibilitychange', function () { if (!document.hidden) setTimeout(cycle, 1500); }); } catch (e) {}
  })();

  // ---------- торренты: размер — всегда сортировка, мёртвые раздачи скрыты, 4K и HDR по профилю устройства ----------
  (function torrentTune() {
    if (window.plugin_torrenttune_ready) return;
    window.plugin_torrenttune_ready = true;

    function height(r) {
      var t = String(r.Title || '');
      var h = 0;
      if (/\b(2160p?|4k|uhd)\b/i.test(t)) h = 2160;
      else if (/\b1080[pi]?\b|\bfull ?hd\b/i.test(t)) h = 1080;
      else if (/\b720p?\b/i.test(t)) h = 720;
      try {
        var fp = String(r.ffprobe || '');
        var m = fp.match(/'height': (\d+)/), mw = fp.match(/'width': (\d+)/);
        if (m || mw) {
          // класс по ширине: кадр 1920x804 — это 1080p
          var vh = m ? parseInt(m[1], 10) || 0 : 0, vw = mw ? parseInt(mw[1], 10) || 0 : 0;
          var cl = (vw >= 3200 || vh >= 2000) ? 2160 : (vw >= 2400 || vh >= 1300) ? 1440 : (vw >= 1800 || vh >= 1000) ? 1080 : (vw >= 1200 || vh >= 700) ? 720 : vh;
          h = Math.max(h, cl);
        }
      } catch (e) {}
      return h;
    }
    function isHdr(r) { return /\b(hdr10\+?|hdr|dolby ?vision|dovi|dv)\b/i.test(String(r.Title || '')); }

    function filterResults(data) {
      if (!data || !data.Results) return data;
      var mh = getMaxH();
      data.Results = data.Results.filter(function (r) {
        if (parseInt(r.Seeders, 10) <= 0) return false;             // мёртвые раздачи
        if (mh > 0) {
          var h = height(r);
          if (h > mh && h > 0) return false;                         // качество выше профиля устройства
          if (mh <= 1080 && isHdr(r)) return false;                  // FullHD-профиль: без HDR
        }
        return true;
      });
      return data;
    }

    var tries = 0;
    var t = setInterval(function () {
      tries++;
      try {
        if (window.Lampa && Lampa.Parser && typeof Lampa.Parser.get === 'function') {
          clearInterval(t);
          if (!Lampa.Parser.__ttWrapped) {
            var og = Lampa.Parser.get;
            Lampa.Parser.get = function (params, ok, fail) {
              return og.call(this, params, function (data) { try { filterResults(data); } catch (e) {} ok(data); }, fail);
            };
            Lampa.Parser.__ttWrapped = true;
          }
        }
      } catch (e) {}
      if (tries > 60) clearInterval(t);
    }, 500);

    // сортировка торрентов — всегда по размеру (по весу)
    setInterval(function () {
      try { if (Lampa.Storage.get('torrents_sort', 'popular') !== 'Size') Lampa.Storage.set('torrents_sort', 'Size'); } catch (e) {}
    }, 8000);
  })();

  // ---------- автоповтор постеров ----------
  // Картинка с сервера не загрузилась (сервер перезапускался, сеть мигнула) — Lampa сама её не перезапрашивает и на постере остаётся
  // подпись с адресом. Перезапрашиваем через 1,5 / 4 / 9 секунд.
  (function imgRetry() {
    if (window.plugin_imgretry_ready) return;
    window.plugin_imgretry_ready = true;
    window.addEventListener('error', function (e) {
      try {
        var t = e.target;
        if (!t || t.tagName !== 'IMG') return;
        var src = t.getAttribute('data-home-src') || t.currentSrc || t.src || '';
        if (src.indexOf('/tmdb/img/') < 0) return;
        var n = parseInt(t.getAttribute('data-home-try') || '0', 10);
        if (n >= 3) return;
        t.setAttribute('data-home-src', src.split('?')[0]);
        t.setAttribute('data-home-try', String(n + 1));
        setTimeout(function () {
          if (!document.body.contains(t)) return;
          t.src = t.getAttribute('data-home-src') + '?r=' + (n + 1);
        }, [1500, 4000, 9000][n]);
      } catch (x) {}
    }, true);
  })();

  // ---------- постеры и данные TMDB через сервер Lampac ----------
  // Устройство брало картинки и описания прямо из интернета (десятки запросов на старте). Сервер отдаёт их по локальной сети
  // и держит на диске: первый запрос ~0,2 с, повторные ~10 мс. Включается только если сервер на пробную картинку ответил.
  (function tmdbLocal() {
    if (window.plugin_tmdblocal_ready) return;
    window.plugin_tmdblocal_ready = true;
    try {
      if (!(window.Lampa && Lampa.TMDB && Lampa.TMDB.image && Lampa.TMDB.api)) return;
      var oImg = Lampa.TMDB.image, oApi = Lampa.TMDB.api;
      var probe = new Image(), done = false;
      var enable = function () {
        if (done) return; done = true;
        Lampa.TMDB.image = function (u) { u = String(u == null ? '' : u); return BASE + '/tmdb/img/' + u.replace(/^\/+/, ''); };
        Lampa.TMDB.api = function (u) { u = String(u == null ? '' : u); return BASE + '/tmdb/api/3/' + u.replace(/^\/+/, ''); };
      };
      probe.onload = enable;
      probe.onerror = function () { done = true; };
      setTimeout(function () { done = true; }, 6000);
      probe.src = BASE + '/tmdb/img/t/p/w92/qvktm0BHcnmDpul4Hz01GIazWPr.jpg';
      window.__tmdbOrig = { image: oImg, api: oApi };
    } catch (e) {}
  })();

  // выход из сна: приложение вернулось на передний план (процесс жив, страница не перезагружалась) — тот же автозапуск, что при старте
  var wakeAt = 0;
  function wakeStart() {
    if (!getAuto()) return;
    if (!(getExt() ? nativeApps() : (window.AndroidJS && Lampa.Android && Lampa.Android.openPlayer))) return;
    var comp = '';
    try { comp = Lampa.Activity.active().component; } catch (e) {}
    try { if (Lampa.Select.opened && Lampa.Select.opened()) return; } catch (e) {}
    if (comp && comp !== 'main') return;           // человек уже где-то в приложении — не перебиваем
    if (Date.now() - wakeAt < 30000) return;
    wakeAt = Date.now();
    quickPlay();
  }

  // «Телевидение (Lampa Home)» — отдельная точка входа приложения для программ автоматизации (TvLaunchActivity)
  function launchStart() {
    var ch = -1;
    try { ch = window.AndroidJS && AndroidJS.tvLaunchTake ? AndroidJS.tvLaunchTake() : -1; } catch (e) {}
    if (typeof ch !== 'number' || ch < 0) return;
    wakeAt = Date.now();
    quickPlay(ch);
  }
  // холодный старт: страница только загрузилась, команда ждёт в приложении
  (function launchWait() {
    var tries = 0;
    var t = setInterval(function () {
      tries++;
      var pending = -1;
      try { pending = window.AndroidJS && AndroidJS.tvLaunchPeek ? AndroidJS.tvLaunchPeek() : -1; } catch (e) {}
      if (pending < 0 || tries > 60) { if (tries > 60 || pending < 0 && tries > 3) clearInterval(t); return; }
      var comp = '';
      try { comp = Lampa.Activity.active().component; } catch (e) {}
      if (comp === 'main') { clearInterval(t); launchStart(); }
    }, 1500);
  })();

  // ---------- настройки устройства на сервере ----------
  // Профиль устройства (качество, движок эфира, буфер, заставка, обои…) хранится в единой базе и меняется в веб-панели сервера
  // (адрес /home). Устройство при запуске и раз в минуту забирает его; изменения, сделанные на самом устройстве, уходят на сервер.
  (function devsync() {
    if (window.plugin_devsync_ready) return;
    window.plugin_devsync_ready = true;
    var MAP = { maxh: MAXH_KEY, saver: SAVER_KEY, wall: WALL_KEY, hidel: HIDEL_KEY, auto: AUTO_KEY, engine: ENG_KEY, buf: BUF_KEY, font: FONT_KEY, alpha: ALPHA_KEY, afr: AFR_KEY, vodbuf: 'home_vodbuf_v1' };
    var REV = {};
    Object.keys(MAP).forEach(function (k) { REV[MAP[k]] = k; });
    var KP_PREF = 'kp_pref_v1';     // настройки плагина Кинопаба (JSON); из них устройство отдаёт серверу только выбранный профиль (kpprof)
    var proto = Storage.prototype, origSet = proto.setItem, applying = false, pending = {}, pushT = 0;
    var OUTBOX='home_dev_outbox_v2', serial=Date.now(), sending=0;
    try{pending=JSON.parse(localStorage.getItem(OUTBOX)||'{}')||{};}catch(e){}
    Object.keys(pending).forEach(function(k){serial=Math.max(serial,+pending[k].revision||0);});
    function persist(){try{localStorage.setItem(OUTBOX,JSON.stringify(pending));return true;}catch(e){return false;}}
    function schedule(ms){clearTimeout(pushT);if(Object.keys(pending).length)pushT=setTimeout(flush,ms);}

    function flush() {
      if(sending){schedule(500);return;}
      if(!persist()){schedule(5000);return;}
      var ks = Object.keys(pending);
      sending=ks.length;
      function finished(){sending--;if(!sending)schedule(5000);}
      ks.forEach(function (k) {
        var entry=pending[k];
        api('/home/dev/set?dev=' + encodeURIComponent(devId()) + '&k=' + k + '&v=' + encodeURIComponent(entry.value), function (d) {
          if(d&&d.ok&&pending[k]&&pending[k].revision===entry.revision){delete pending[k];if(!persist())pending[k]=entry;}
          finished();
        }, finished);
      });
    }
    function changed(key,value){
      if(applying)return;
      var field=REV[key],v=String(value);
      try {
        if (key === KP_PREF) {
          var kp = JSON.parse(value);
          if(kp&&kp.profile){field='kpprof';v=String(kp.profile);}
        }
        if(field){pending[field]={value:v,revision:++serial};persist();schedule(600);}
      } catch (e) {}
    }
    // The profile facade owns native Storage methods; subscribe through its API.
    if(window.HomeStorage)HomeStorage.onChange(changed);
    else proto.setItem=function(key,value){origSet.apply(this,arguments);if(this===localStorage)changed(key,value);};
    function applyValue(key,value){localStorage.setItem(key,value);if(key===AFR_KEY&&window.AndroidJS&&AndroidJS.mcSetAfr)AndroidJS.mcSetAfr(String(value)!=='0');}

    function model() {
      var m = (navigator.userAgent.match(/;\s*([^;)]+?)\s+Build\//) || [])[1];
      return m || navigator.platform || '';
    }
    function current() {
      var o = {};
      Object.keys(MAP).forEach(function (k) { try { var v = localStorage.getItem(MAP[k]); if (v !== null) o[k] = v; } catch (e) {} });
      try { var kpp = JSON.parse(localStorage.getItem(KP_PREF) || '{}'); if (kpp.profile) o.kpprof = String(kpp.profile); } catch (e) {}
      try { if (window.AndroidJS && typeof AndroidJS.appVersion === 'function') o.version = AndroidJS.appVersion(); } catch (e) {}
      return o;
    }
    // Older APKs already expose a Storage facade but have no subscription API.
    // Poll just these device keys so the server update fixes those clients too.
    var observed=current();
    if(!window.HomeStorage)setInterval(function(){var now=current();Object.keys(now).forEach(function(k){
      if(Object.prototype.hasOwnProperty.call(observed,k)&&observed[k]!==now[k])changed(k==='kpprof'?KP_PREF:MAP[k],k==='kpprof'?JSON.stringify({profile:now[k]}):now[k]);
    });observed=now;},1000);
    function hello() {
      var url = '/home/dev/hello?dev=' + encodeURIComponent(devId()) + '&name=' + encodeURIComponent(model()) + '&init=' + encodeURIComponent(JSON.stringify(current()));
      api(url, function (d) {
        var cfg = (d && d.cfg) || {}, changed = false;
        applying = true;
        try {
          Object.keys(cfg).forEach(function (k) {
            if (!MAP[k] || pending[k]) return;
            var v = String(cfg[k]);
            if (localStorage.getItem(MAP[k]) !== v) { applyValue(MAP[k], v); changed = true; }
          });
          if (cfg.kpprof && !pending.kpprof) {
            var cur = {};
            try { cur = JSON.parse(localStorage.getItem(KP_PREF) || '{}') || {}; } catch (e) {}
            if (String(cur.profile || 'main') !== String(cfg.kpprof)) { cur.profile = String(cfg.kpprof); applyValue(KP_PREF, JSON.stringify(cur)); changed = true; }
          }
        } catch (e) {}
        applying = false;
        observed=current();
        if (changed) { try { applyLook(); } catch (e) {} }
      }, function () {});
    }
    setTimeout(hello, 2500);
    setInterval(hello, 60000);
    schedule(600);
  })();

  // кнопка «Онлайн» плагина Lampac в карточке (по настройке скрываем: есть Кинопаб, Аниме и Торренты)
  try {
    if (window.Lampa && Lampa.Listener) Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !getHideLampac()) return;
      var n = 0, t = setInterval(function () {
        n++;
        try { e.object.activity.render().find('.lampac--button, .view--online').remove(); } catch (x) {}
        if (n > 12) clearInterval(t);
      }, 700);
    });
  } catch (e) {}
  // Прокси поиска торрентов (/home/jr) больше не нужен: украинские раздачи фильтрует сам JacRed. Возвращаем прямой адрес сервера, если он был подменён
  (function jrBack() {
    try {
      var cur = String(Lampa.Storage.get('jackett_url', '') || '');
      if (/\/home\/jr\/?$/.test(cur)) Lampa.Storage.set('jackett_url', cur.replace(/\/home\/jr\/?$/, ''));
    } catch (e) {}
  })();
  setTimeout(function () {
    try {
      var cur = String(Lampa.Storage.get('jackett_url', '') || '');
      if (/\/home\/jr\/?$/.test(cur)) Lampa.Storage.set('jackett_url', cur.replace(/\/home\/jr\/?$/, ''));
    } catch (e) {}
  }, 4000);
  setTimeout(applyLook, 2500);
  setInterval(applyLook, 60000);

  window.__tv = { openTv: openTv, quickPlay: quickPlay, playChannel: playChannel, wakeStart: wakeStart, launchStart: launchStart };
  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
