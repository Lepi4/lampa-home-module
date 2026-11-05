(function () {
  'use strict';
  if (window.plugin_wrestling_ready) return;
  window.plugin_wrestling_ready = true;

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

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;
  var CACHE_KEY = 'wrestling_tmdb_v1';

  function cacheGet() {
    try { return JSON.parse(localStorage.getItem(CACHE_KEY) || '{}'); } catch (e) { return {}; }
  }
  function cacheSet(c) {
    try { localStorage.setItem(CACHE_KEY, JSON.stringify(c)); } catch (e) {}
  }

  function tmdbUrl(path) {
    var lang = Lampa.Storage.field('tmdb_lang') || 'ru';
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    return Lampa.TMDB.api(path + sep + 'api_key=' + Lampa.TMDB.key() + '&language=' + lang);
  }

  function tmdb(path, ok, fail) {
    $.ajax({ url: tmdbUrl(path), dataType: 'json', timeout: 15000, success: ok, error: fail });
  }

  // Карточка шоу (RAW/SmackDown) или PPV из TMDB; результат кешируется.
  function meta(item, done) {
    var cache = cacheGet();
    var key = item.tmdb ? 'tv:' + item.tmdb : 'ppv:' + (item.base || item.title || '').toLowerCase() + ':' + item.year;
    if (cache[key]) return done(cache[key]);

    function save(m) {
      var c = cacheGet();
      c[key] = m;
      cacheSet(c);
      done(m);
    }

    if (item.tmdb) {
      tmdb('tv/' + item.tmdb, function (d) {
        save({ tmdb_id: d.id, poster_path: d.poster_path, backdrop_path: d.backdrop_path, overview: d.overview, vote_average: d.vote_average, kind: 'tv' });
      }, function () { done({}); });
      return;
    }

    if (item.kind === 'ppv') {
      var q = encodeURIComponent('WWE ' + (item.base || item.title) + ' ' + item.year);
      tmdb('search/movie?query=' + q + '&year=' + item.year, function (r) {
        var res = (r.results || []).filter(function (x) {
          return (x.release_date || '').slice(0, 4) === String(item.year);
        });
        var m = res[0] || (r.results || [])[0];
        if (!m) return done({});
        save({ tmdb_id: m.id, title: m.title, poster_path: m.poster_path, backdrop_path: m.backdrop_path, overview: m.overview, vote_average: m.vote_average, release_date: m.release_date, kind: 'movie' });
      }, function () { done({}); });
      return;
    }
    done({});
  }

  var st = document.createElement('style');
  st.textContent = '.wr-st{position:absolute;z-index:3;pointer-events:none}' +
    '.wr-mark{top:.45em;left:.45em;width:1.7em;height:1.7em;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:.95em;font-weight:700;line-height:1;box-shadow:0 0 .3em rgba(0,0,0,.6)}' +
    '.wr-done{background:#2ecc71;color:#fff}' +
    '.wr-part{background:#f9a825;color:#111}' +
    '.wr-bar{left:0;right:0;bottom:0;height:.3em;background:rgba(0,0,0,.55)}' +
    '.wr-bar i{display:block;height:100%;background:#f9a825}' +
    '.wr-count{top:.45em;right:.45em;background:#f9a825;color:#111;font-weight:700;font-size:.8em;line-height:1;padding:.25em .55em;border-radius:.3em;box-shadow:0 0 .3em rgba(0,0,0,.5)}';
  document.head.appendChild(st);

  // Отметка просмотра вручную: пишем в таймлайн Lampa (тот же, что и у обычного воспроизведения)
  function mark(item, watched) {
    try {
      var tl = Lampa.Timeline.view(Lampa.Utils.hash(item.file));
      if (watched) { tl.duration = tl.duration || 3600; tl.time = tl.duration; tl.percent = 100; }
      else { tl.time = 0; tl.percent = 0; }
      Lampa.Timeline.update(tl);
    } catch (e) {}
    paintMarks();
  }

  // меню карточки (удержание кнопки «ОК»); el — элемент карточки (нужен, чтобы убрать её из «Продолжить просмотр»)
  function cardMenu(item, refresh, el, again) {
    var s = state(item).s;
    var prev = Lampa.Controller.enabled().name;
    var inCont = !!el && window.__home_inCont(el);
    var items = [];
    if (inCont) items.push({ title: 'Убрать из «Продолжить просмотр»', act: 'off' });
    if (s !== 'watched') items.push({ title: 'Просмотрено ✓', act: 'on' });
    if (!inCont && s !== '') items.push({ title: s === 'watched' ? 'Убрать из просмотренного' : 'Сбросить прогресс', act: 'off' });
    Lampa.Select.show({
      title: label(item),
      items: items,
      onBack: function () { Lampa.Controller.toggle(prev); if (again) again(); },
      onSelect: function (a) {
        mark(item, a.act === 'on');
        // в «Продолжить просмотр» выпуск остаётся только пока он начат
        if (inCont && el) { try { $(el).remove(); } catch (x) {} }
        Lampa.Controller.toggle(prev);
        if (refresh) refresh();
        Lampa.Noty.show(a.act === 'on' ? 'Отмечено как просмотренное' : (inCont ? 'Убрано из «Продолжить просмотр»' : 'Отметка снята'));
      }
    });
  }

  function paintMarks() {
    try {
      var cards = document.querySelectorAll('.card');
      for (var i = 0; i < cards.length; i++) {
        var d = cards[i].card_data;
        if (d && d.wrestling_series) {
          var vw = cards[i].querySelector('.card__view');
          if (!vw) continue;
          var left = seriesFiles.filter(function (it) { return state(it).s !== 'watched'; }).length;
          var ck = 'c:' + left + ':' + seriesFiles.length;
          if (cards[i].getAttribute('data-wr-st') === ck) continue;
          cards[i].setAttribute('data-wr-st', ck);
          $(vw).find('.wr-st').remove();
          if (left > 0 && seriesFiles.length) $(vw).append('<div class="wr-st wr-count">' + left + '</div>');
          continue;
        }
        if (!d || !d.wrestling) continue;
        var view = cards[i].querySelector('.card__view');
        if (!view) continue;
        var s = state(d.wrestling);
        if (s.s === 'started' && window.__home_inCont(cards[i])) s = { s: '', p: 0 };
        var key = s.s + ':' + Math.round(s.p);
        if (cards[i].getAttribute('data-wr-st') === key) continue;
        cards[i].setAttribute('data-wr-st', key);
        $(view).find('.wr-st').remove();
        if (s.s === 'watched') $(view).append('<div class="wr-st wr-mark wr-done">✓</div>');
        else if (s.s === 'started') $(view).append('<div class="wr-st wr-mark wr-part">▶</div><div class="wr-st wr-bar"><i style="width:' + Math.round(s.p) + '%"></i></div>');
      }
    } catch (e) {}
  }
  setInterval(paintMarks, 1200);

  // ---------- части PPV: Day 1/2, Night 1/2, Part 1/2, D1/D2 и т.п. ----------
  // Файлы НЕ склеиваются: каждая часть — отдельный эпизод. Название события нужно только затем, чтобы обе части получили
  // одну дату из TMDB и встали рядом по порядку.
  var NUMW = { one: 1, two: 2, three: 3, four: 4, i: 1, ii: 2, iii: 3, iv: 4, 'один': 1, 'два': 2, 'три': 3, 'четыре': 4,
    first: 1, second: 2, third: 3, '1st': 1, '2nd': 2, '3rd': 3 };
  var KINDW = { day: 'День', d: 'День', 'день': 'День', 'д': 'День', night: 'Ночь', n: 'Ночь', 'ночь': 'Ночь',
    part: 'Часть', pt: 'Часть', 'часть': 'Часть', show: 'Шоу', hour: 'Час', 'час': 'Час' };
  var B1 = '(?:^|[\\s._\\-\\[(])';          // граница слева (\b не работает с кириллицей)
  var B2 = '(?=$|[\\s._\\-\\])])';          // граница справа
  var RE_PART = new RegExp(B1 + '(day|night|part|show|hour|pt|d|n|день|ночь|часть|час|д)[\\s._\\-]*(\\d|one|two|three|four|iii|ii|iv|i|один|два|три|четыре)' + B2, 'i');
  var RE_ORD = new RegExp(B1 + '(1st|first|2nd|second|3rd|third)[\\s._\\-]+(day|night|part|show|hour)' + B2, 'i');
  var RE_PRE = new RegExp(B1 + '(pre[\\s._\\-]?show|kick[\\s._\\-]?off|countdown|пре[\\s._\\-]?шоу)' + B2, 'i');

  // {n, kind, text} или null; n = 0 — пре-шоу
  function splitPart(name) {
    var m = RE_PART.exec(name);
    if (m) {
      var k = KINDW[m[1].toLowerCase()];
      var n = /^\d$/.test(m[2]) ? parseInt(m[2], 10) : NUMW[m[2].toLowerCase()];
      if (k && n) return { n: n, kind: k, text: m[0] };
    }
    m = RE_ORD.exec(name);
    if (m) return { n: NUMW[m[1].toLowerCase()], kind: KINDW[m[2].toLowerCase()], text: m[0] };
    m = RE_PRE.exec(name);
    if (m) return { n: 0, kind: 'Пре-шоу', text: m[0] };
    return null;
  }

  function stripPart(title) {
    var s = String(title || '');
    [RE_PART, RE_ORD, RE_PRE].forEach(function (re) { s = s.replace(re, ' '); });
    return s.replace(/\s+/g, ' ').replace(/^[\s._\-]+|[\s._\-]+$/g, '');
  }

  // разметка списка: у PPV — base (название без метки части), part, disp (название для показа)
  function annotate(items) {
    var groups = {};
    items.forEach(function (it) {
      if (it.__ann) return;
      it.__ann = true;
      if (it.kind !== 'ppv') return;
      it.part = splitPart(String(it.file || '').replace(/\.[^.]+$/, ''));
      it.base = stripPart(it.title) || it.title;
    });
    items.forEach(function (it) {
      if (it.kind !== 'ppv') return;
      var k = (it.base || '').toLowerCase() + '|' + it.year;
      (groups[k] = groups[k] || []).push(it);
    });
    Object.keys(groups).forEach(function (k) {
      var g = groups[k];
      var parts = g.filter(function (x) { return x.part; });
      if (!parts.length) return;
      // файл без метки рядом с «Day 2» — это день 1
      var has1 = parts.some(function (x) { return x.part.n === 1; });
      g.forEach(function (x) { if (!x.part && !has1) { x.part = { n: 1, kind: parts[0].part.kind }; has1 = true; } });
    });
    items.forEach(function (it) {
      if (it.kind !== 'ppv') return;
      it.disp = (it.base || it.title) + (it.part ? ' · ' + (it.part.n === 0 ? 'Пре-шоу' : it.part.kind + ' ' + it.part.n) : '');
    });
  }

  function addDays(iso, n) {
    var d = new Date(iso + 'T12:00:00Z');
    d.setUTCDate(d.getUTCDate() + n);
    return d.toISOString().slice(0, 10);
  }

  // даты выпусков: у Raw/SmackDown дата в названии, у PPV — из TMDB (в имени файла только год); результат отсортирован по дате
  function dated(items, done) {
    var left = items.length;
    if (!left) return done([]);

    function finish() {
      items.forEach(function (it) {
        var ts;
        if (it.sdate) ts = Date.parse(it.sdate + 'T12:00:00Z');
        else if (it.kind === 'other') ts = (it.mtime || 0) * 1000;
        else ts = Date.UTC(it.year || 1970, 6, 1);     // у PPV без даты — только год
        it.ts = ts;
      });
      items.sort(function (a, b) {
        return (a.ts - b.ts) || ((a.part ? a.part.n : 0) - (b.part ? b.part.n : 0)) || ((a.mtime || 0) - (b.mtime || 0));
      });
      done(items);
    }

    items.forEach(function (it) {
      var fin = function () { if (--left === 0) finish(); };
      it.sdate = it.date || '';
      if (it.kind !== 'ppv') return fin();
      meta(it, function (m) {
        var rd = String(m.release_date || '').slice(0, 10);
        if (rd && rd.slice(0, 4) === String(it.year)) {
          // вторая ночь/день — на следующие сутки после первой
          var off = it.part && it.part.n > 0 && /^(День|Ночь|Шоу)$/.test(it.part.kind) ? it.part.n - 1 : 0;
          it.sdate = addDays(rd, off);
        }
        fin();
      });
    });
  }

  function dateText(it) {
    if (it.sdate) {
      var p = it.sdate.split('-');
      return parseInt(p[2], 10) + ' ' + MON[parseInt(p[1], 10) - 1] + ' ' + p[0];
    }
    return it.year ? it.year + ' г.' : '';
  }

  // ---------- Wrestling как сериал (строка «Мои сериалы») ----------
  var seriesFiles = [];

  function loadSeries(cb) {
    $.ajax({
      url: BASE + '/wrestling/list', dataType: 'json', timeout: 15000, cache: false,
      success: function (d) {
        var items = d.items || [];
        if (d.watching === false || d.disabled || !items.length) return cb({ items: [], off: d.watching === false });
        annotate(items);
        dated(items, function (sorted) { seriesFiles = sorted; cb({ items: sorted }); });
      },
      error: function () { cb({ items: [] }); }
    });
  }

  function seriesCard(sorted) {
    var unseen = sorted.filter(function (it) { return state(it).s !== 'watched'; }).length;
    var last = sorted.length ? (sorted[sorted.length - 1].sdate || '') : '';
    try { $.ajax({ url: BASE + '/wrestling/unseen?set=' + unseen, timeout: 8000, cache: false }); } catch (e) {}   // оболочка MC показывает это число значком
    return {
      id: 'wr-series', title: 'Wrestling', name: 'Wrestling', original_title: 'Wrestling', original_name: 'Wrestling',
      poster_path: '', backdrop_path: '', img: BASE + '/wrestling/poster.svg',
      overview: 'Raw, SmackDown и PPV по порядку выхода', vote_average: 0,
      release_date: last, first_air_date: last, source: 'wrestling', wrestling_series: true, wr_total: sorted.length
    };
  }

  // список выпусков: по возрасту, выбран первый непросмотренный; удержание «ОК» — отметка просмотра
  function showSeries(list, prev) {
    var first = -1;
    list.forEach(function (it, i) { if (first < 0 && state(it).s !== 'watched') first = i; });
    // «Продолжить»: последний начатый выпуск, а если начатых нет — первый непросмотренный
    var cont = null, contSt = null;
    list.forEach(function (it) {
      var st = state(it);
      if (st.s === 'started' && (!contSt || st.at > contSt.at)) { cont = it; contSt = st; }
    });
    if (!cont && first >= 0) { cont = list[first]; contSt = state(cont); }
    var items = list.map(function (it, i) {
      var s = state(it);
      var mk = s.s === 'watched' ? '<span style="color:#2ecc71;font-weight:700">✓</span> ' : (s.s === 'started' ? '<span style="color:#f9a825;font-weight:700">▶</span> ' : '');
      var sub = dateText(it);
      if (s.s === 'started') {
        var tl = Lampa.Timeline.view(Lampa.Utils.hash(it.file)) || {};
        sub += ' · остановлено на ' + Math.round((tl.time || 0) / 60) + ' мин';
      }
      return { title: mk + label(it), subtitle: sub, wr: it, selected: !cont && (first >= 0 ? i === first : i === list.length - 1) };
    });
    if (cont) {
      var csub = dateText(cont);
      if (contSt.s === 'started') csub += ' · остановлено на ' + Math.round(((Lampa.Timeline.view(Lampa.Utils.hash(cont.file)) || {}).time || 0) / 60) + ' мин';
      items.unshift({ title: '▶ Продолжить · ' + label(cont), subtitle: csub, wr: cont, selected: true });
    }
    Lampa.Select.show({
      title: 'Wrestling · выпусков: ' + list.length,
      items: items,
      onBack: function () { Lampa.Controller.toggle(prev); },
      onSelect: function (a) { play(a.wr, list); },
      onLong: function (a) {
        var reopen = function () { setTimeout(function () { showSeries(list, prev); }, 250); };
        Lampa.Select.close();
        setTimeout(function () { cardMenu(a.wr, reopen, null, reopen); }, 200);
      }
    });
  }

  function openSeries() {
    var prev = Lampa.Controller.enabled().name;
    loadSeries(function (r) {
      if (!r.items.length) return Lampa.Noty.show('Wrestling: выпусков нет');
      showSeries(r.items, prev);
    });
  }

  function seriesMenu(card, el) {
    var prev = Lampa.Controller.enabled().name;
    Lampa.Select.show({
      title: 'Wrestling',
      items: [
        { title: 'Открыть выпуски', act: 'open' },
        { title: 'Отметить все просмотренными', act: 'all' },
        { title: 'Снять все отметки', act: 'none' },
        { title: 'Убрать из «Мои сериалы»', subtitle: 'вернуть: Настройки → Кинопаб → Локальная библиотека → Wrestling', act: 'hide' }
      ],
      onBack: function () { Lampa.Controller.toggle(prev); },
      onSelect: function (a) {
        if (a.act === 'open') { Lampa.Controller.toggle(prev); return setTimeout(openSeries, 200); }
        if (a.act === 'all' || a.act === 'none') {
          seriesFiles.forEach(function (it) { mark(it, a.act === 'all'); });
          Lampa.Controller.toggle(prev);
          return Lampa.Noty.show(a.act === 'all' ? 'Все выпуски отмечены просмотренными' : 'Отметки сняты');
        }
        $.ajax({
          url: BASE + '/local/config', type: 'POST', contentType: 'application/json', dataType: 'json',
          data: JSON.stringify({ id: 'wrestling', patch: { watching: false } }),
          success: function () { try { $(el).remove(); } catch (x) {} Lampa.Controller.toggle(prev); Lampa.Noty.show('Wrestling убран из «Мои сериалы»'); },
          error: function () { Lampa.Controller.toggle(prev); Lampa.Noty.show('Не удалось сохранить'); }
        });
      }
    });
  }

  function load(ok, fail) {
    $.ajax({
      url: BASE + '/wrestling/list',
      dataType: 'json',
      timeout: 15000,
      cache: false,
      success: function (d) { var it = d.items || []; annotate(it); ok(it); },
      error: fail
    });
  }

  var MON = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
  // Еженедельные шоу различаются только датой: показываем «SmackDown · 16 октября 2026», остальное — как есть
  function label(item) {
    if (item.kind === 'episode' && item.show && item.date) {
      var p = item.date.split('-');
      var mo = MON[parseInt(p[1], 10) - 1];
      if (mo) return (item.show === 'raw' ? 'Raw' : 'SmackDown') + ' · ' + parseInt(p[2], 10) + ' ' + mo + ' ' + p[0];
    }
    return item.disp || item.title;
  }

  // Статус просмотра по таймлайну Lampa (правило — window.__home_state)
  function state(item) {
    try { return window.__home_state(Lampa.Timeline.view(Lampa.Utils.hash(item.file))); } catch (e) {}
    return { s: '', p: 0, at: 0 };
  }

  function buildCards(items, done) {
    var cards = [];
    var left = items.length;
    if (!left) return done(cards);
    items.forEach(function (item, i) {
      meta(item, function (m) {
        var title = label(item);
        var date = item.date || (item.year ? item.year + '-01-01' : '');
        cards[i] = {
          id: item.id,
          title: title,
          name: title,
          original_title: title,
          poster_path: m.poster_path || '',
          backdrop_path: m.backdrop_path || '',
          img: '',
          overview: m.overview || '',
          vote_average: m.vote_average || 0,
          release_date: date,
          first_air_date: date,
          source: 'wrestling',
          wrestling: item
        };
        if (--left === 0) done(cards.filter(Boolean));
      });
    });
  }

  function play(item, all) {
    // «Следующий» выпуск — тот, что вышел позже: плейлист всегда по возрастанию даты (список на экране идёт от новых к старым,
    // и без этого после самого свежего выпуска включался предыдущий)
    all = all.slice().sort(function (x, y) {
      var dx = x.sdate || x.date || '', dy = y.sdate || y.date || '';
      return dx < dy ? -1 : dx > dy ? 1 : String(x.file).localeCompare(String(y.file));
    });
    function wrCard(it) { return 'wr_' + String(it.id || it.file || 'show').replace(/[^0-9A-Za-z_-]/g, '').slice(0, 40); }
    var playlist = all.map(function (it) {
      var hash = Lampa.Utils.hash(it.file);
      return {
        title: label(it) + (it.quality ? ' (' + it.quality + 'p)' : ''),
        url: BASE + it.url,
        timeline: Lampa.Timeline.view(hash),
        wr_card: wrCard(it)
      };
    });
    // ищем по id: списки загружаются в нескольких местах, и объекты одного файла в них разные
    var index = -1;
    all.forEach(function (it, i) { if (it.id === item.id) index = i; });
    if (index < 0) { all = [item].concat(all); playlist.unshift({ title: label(item) + (item.quality ? ' (' + item.quality + 'p)' : ''), url: BASE + item.url, timeline: Lampa.Timeline.view(Lampa.Utils.hash(item.file)), wr_card: wrCard(item) }); index = 0; }
    // нативный плеер получает список только внутри данных запуска (иначе он играет один файл и закрывается): так следующий выпуск включается сам
    var current = $.extend({}, playlist[index]);
    current.wr_card = playlist[index].wr_card;
    current.home_timeline_only = true; // не приклеивать выпуск к открытому фильму TMDB
    current.playlist = playlist;
    // A native progress grant covers one collection. Per-file wr_card remains
    // the audio-memory key; progress uses one collection and distinct file hashes.
    current.home_base = BASE;
    current.tl_meta = JSON.stringify(playlist.map(function (p) {
      var hash = String(p.timeline.hash);
      return { id: 'tl-' + hash, h: hash, c: 'wr_series' };
    }));
    Lampa.Player.play(current);
    Lampa.Player.playlist(playlist);
  }

  function component(object) {
    var comp = new Lampa.InteractionCategory(object);
    var files = [];

    comp.create = function () {
      var _this = this;
      this.activity.loader(true);
      load(function (items) {
        files = items;
        buildCards(items, function (cards) {
          _this.build({ results: cards, page: 1, total_pages: 1, total_results: cards.length });
        });
      }, function () {
        _this.empty();
      });
    };

    comp.nextPageReuest = function (obj, resolve, reject) { reject(); };

    comp.cardRender = function (obj, element, card) {
      card.onMenu = function () { cardMenu(element.wrestling); };
      card.onEnter = function () {
        play(element.wrestling, files);
      };
    };

    return comp;
  }

  function addMenu() {
    if ($('.menu .menu__list [data-action="wrestling"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="wrestling">' +
      '<div class="menu__ico"><svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M12 2 9.2 8.6 2 9.3l5.4 4.7L5.8 21 12 17.3 18.2 21l-1.6-7 5.4-4.7-7.2-.7z"/></svg></div>' +
      '<div class="menu__text">Wrestling</div></li>');
    btn.on('hover:enter', function () {
      Lampa.Activity.push({ url: '', title: 'Wrestling', component: 'wrestling', page: 1 });
    });
    $('.menu .menu__list').eq(0).append(btn);
  }

  Lampa.Component.add('wrestling', component);

  // Строка «Wrestling» на главной: после строк kino.pub (индексы 0–2)
  var rowFiles = [];
  // Регистрируем строку после строк Кинопаба: Lampa вставляет строки по индексу в порядке регистрации
  var rowRegistered = false;
  function registerRow() {
  return;   // лента Wrestling на главной отключена: выпуски доступны только из бокового меню (и сериалом в «Мои сериалы»)
  if (rowRegistered) return;
  rowRegistered = true;
  Lampa.ContentRows.add({
    name: 'wrestling_row',
    title: 'Wrestling',
    index: 8,
    screen: ['main'],
    call: function () {
      return function (callback) {
        load(function (items) {
          rowFiles = items;
          if (!items.length) return callback({ results: [] });
          buildCards(items, function (cards) {
            callback({
              results: cards,
              title: 'Wrestling',
              nomore: true,
              card_events: {
                onEnter: function (target, card) {
                  if (card && card.wrestling) play(card.wrestling, rowFiles);
                },
                onMenu: function (target, card) { if (card && card.wrestling) cardMenu(card.wrestling); }
              }
            });
          });
        }, function () { callback({ results: [] }); });
      };
    }
  });
  }

  // Порядок строк на главной задаёт порядок регистрации: плагин Кинопаба вызывает это сразу после своих строк.
  // Если его нет — регистрируемся сами, когда приложение готово.
  window.__wr_registerRow = registerRow;
  // Начатые, но не досмотренные выпуски — для строки «Продолжить просмотр» (сначала недавно смотревшие)
  window.__wr_continue = function (cb) {
    load(function (items) {
      rowFiles = items;
      var started = items.map(function (it) { return { it: it, st: state(it) }; })
        .filter(function (x) { return x.st.s === 'started'; })
        .sort(function (a, b) { return b.st.at - a.st.at; })
        .map(function (x) { return x.it; });
      if (!started.length) return cb([]);
      buildCards(started, function (cards) { cb(cards); });
    }, function () { cb([]); });
  };
  window.__wr_menu = function (card, el) {
    if (card && card.wrestling_series) return seriesMenu(card, el);
    if (card && card.wrestling) cardMenu(card.wrestling, null, el);
  };
  // карточка сериала «Wrestling» для строки «Мои сериалы» (пусто, если выключено в настройках)
  window.__wr_watching = function (cb) {
    loadSeries(function (r) {
      // как у сериалов: пока есть непросмотренные выпуски — карточка в «Мои сериалы», досмотрели всё — пропадает (вернётся с новым файлом)
      var unseen = r.items.filter(function (it) { return state(it).s !== 'watched'; }).length;
      if (!unseen && r.items.length) { try { $.ajax({ url: BASE + '/wrestling/unseen?set=0', timeout: 8000, cache: false }); } catch (e) {} }   // лаунчер тоже скрывает карточку, когда всё просмотрено
      cb(unseen > 0 ? [seriesCard(r.items)] : []);
    });
  };
  window.__wr_open = function (card) {
    if (card && card.wrestling_series) return openSeries();
    if (card && card.wrestling) play(card.wrestling, rowFiles.length ? rowFiles : [card.wrestling]); };
  window.__wr_publishChannel = publishChannel;
  // Канал на главном экране Android TV
  function publishChannel() {
    if (typeof AndroidJS === 'undefined' || typeof AndroidJS.publishPluginChannel !== 'function') return;
    load(function (items) {
      if (!items.length) return;
      buildCards(items, function (cards) {
        var out = cards.slice(0, 20).map(function (c) {
          var w = c.wrestling;
          var it = { id: w.id, source: 'wrestling', type: w.kind === 'episode' ? 'tv' : 'movie', name: c.title, title: c.title };
          if (c.poster_path) it.poster_path = c.poster_path;
          if (c.backdrop_path) it.backdrop_path = c.backdrop_path;
          var va = Number(c.vote_average);
          if (isFinite(va) && va >= 0 && va <= 10) it.vote_average = va;
          return it;
        });
        try { AndroidJS.publishPluginChannel(JSON.stringify({ id: 'wrestling', title: 'Wrestling', items: out })); } catch (e) {}
      });
    }, function () {});
  }

  if (typeof AndroidJS !== 'undefined' && typeof AndroidJS.publishPluginChannel === 'function') {
    setTimeout(publishChannel, 11000);
    setInterval(publishChannel, 30 * 60 * 1000);
  }

  // Ждём, пока Кинопаб зарегистрирует свои строки (до 20 с), и только потом добавляем свою: так порядок не зависит от скорости загрузки плагинов.
  function waitAndRegister(n) {
    if (window.plugin_kinopub_ready && !window.__kp_rows_registered && n < 100) return setTimeout(function () { waitAndRegister(n + 1); }, 200);
    registerRow();
  }
  setTimeout(function () { waitAndRegister(0); }, 0);

  // Переход на страницу карточки нашего источника (строка, поиск и т.п.) запускает файл, а не TMDB-страницу
  if (!Lampa.Activity.__wr_hooked) {
    Lampa.Activity.__wr_hooked = true;
    var origPush = Lampa.Activity.push;
    Lampa.Activity.push = function (obj) {
      try {
        if (obj && obj.component === 'full') {
          var c = obj.card || {};
          // карточка сериала Wrestling (строка «Мои сериалы», поиск): открываем список выпусков, а не ищем файл с таким id
          if ((obj.source === 'wrestling' || c.source === 'wrestling') && (c.wrestling_series || String(obj.id || c.id || '') === 'wr-series')) {
            openSeries();
            return;
          }
          if ((obj.source === 'wrestling' || c.source === 'wrestling') && c.wrestling) {
            play(c.wrestling, rowFiles.length ? rowFiles : [c.wrestling]);
            return;
          }
          // открытие из канала Android TV: карточка приходит только с id файла
          if (obj.source === 'wrestling' && String(obj.id || '').indexOf('wr-') === 0) {
            var wid = String(obj.id);
            var pick = function (items) {
              rowFiles = items;
              var f = items.filter(function (i) { return i.id === wid; })[0];
              if (f) play(f, items);
              else Lampa.Noty.show('Wrestling: файл не найден');
            };
            if (rowFiles.length) pick(rowFiles); else load(pick, function () { Lampa.Noty.show('Wrestling: нет связи'); });
            return;
          }
        }
      } catch (e) {}
      return origPush.apply(this, arguments);
    };
  }

  // пункт меню только если метод включён (настраивается в «Кинопаб → Локальная библиотека»)
  function maybeMenu() {
    $.ajax({
      url: BASE + '/wrestling/list', dataType: 'json', timeout: 15000, cache: false,
      success: function (d) { if (!d.disabled) { addMenu(); if (window.__kp_placeMenu) setTimeout(window.__kp_placeMenu, 200); } },
      error: function () { addMenu(); }
    });
  }
  if (window.appready) maybeMenu();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') maybeMenu(); });
})();
