(function () {
  'use strict';
  if (window.plugin_anime_ready) return;
  window.plugin_anime_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;

  // «Аниме онлайн» по принципу Кинопаба: источники AniLibria (потоки не ниже 1080p), озвучка видна, на постере метка «AL»,
  // просмотр пишется в общую базу (те же отметки, что у торрентов и Кинопаба), последняя серия предлагается как «Продолжить».

  function api(path, ok, fail) {
    $.ajax({ url: BASE + path, dataType: 'json', timeout: 90000, cache: false, success: ok, error: function (x) { if (fail) fail(x); } });
  }
  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }
  // Контроллер, в который возвращаемся из наших окон. Окно выбора/модалку/загрузку запоминать нельзя: когда мы читаем контроллер, штатное окно
  // (например «Источник») ещё закрывается, и «Назад» потом вёл бы в уже закрытое окно — кнопка залипала.
  var lastBase = 'content';
  function baseCtrl() {
    var n = '';
    try { n = Lampa.Controller.enabled().name; } catch (e) {}
    if (n && ['select', 'modal', 'loading', 'input', 'settings_component'].indexOf(n) < 0) lastBase = n;
    return lastBase;
  }
  try {
    if (Lampa.Controller.listener && Lampa.Controller.listener.follow) {
      Lampa.Controller.listener.follow('toggle', function (e) {
        if (e && e.name && ['select', 'modal', 'loading', 'input', 'settings_component', 'head', 'menu'].indexOf(e.name) < 0) lastBase = e.name;
      });
    }
  } catch (e) {}
  function mins(sec) { return Math.round((sec || 0) / 60) + ' мин'; }

  // ---------- наличие в источниках (метка AL на постерах) ----------
  var HAS_KEY = 'home_al_has_v1';
  var has = {};
  try { has = JSON.parse(localStorage.getItem(HAS_KEY) || '{}') || {}; } catch (e) {}
  function saveHas() {
    try {
      var ks = Object.keys(has);
      if (ks.length > 1500) ks.slice(0, 400).forEach(function (k) { delete has[k]; });
      localStorage.setItem(HAS_KEY, JSON.stringify(has));
    } catch (e) {}
  }
  function fresh(h) { return h && Date.now() - h.t < (h.v === 1 ? 6 * 3600 * 1000 : 3600 * 1000); }

  function animeLike(d) {
    if (!d) return false;
    var g = d.genre_ids || (d.genres || []).map(function (x) { return x.id; });
    if (!g || g.indexOf(16) < 0) return false;
    var lang = d.original_language;
    var oc = d.origin_country || [];
    return lang === 'ja' || oc.indexOf('JP') >= 0 || (!lang && !oc.length);
  }

  function addBadge(el) {
    var view = el.querySelector('.card__view');
    if (!view || view.querySelector('.card__al')) return;
    var b = document.createElement('div');
    b.className = 'card__al';
    b.textContent = 'AL';
    view.appendChild(b);
  }

  var queue = {}, timer = null, retries = {};
  function flush() {
    timer = null;
    var keys = Object.keys(queue).slice(0, 40);
    if (!keys.length) return;
    var batch = {};
    keys.forEach(function (k) { batch[k] = queue[k]; delete queue[k]; });
    api('/anime/has?keys=' + encodeURIComponent(keys.join(',')), function (d) {
      keys.forEach(function (k) {
        var v = d[k];
        if (v === null || v === undefined) {                       // сервер ещё проверяет — спросим позже
          retries[k] = (retries[k] || 0) + 1;
          if (retries[k] <= 4) setTimeout(function () { (queue[k] = queue[k] || []).push.apply(queue[k], batch[k] || []); if (!timer) timer = setTimeout(flush, 300); }, 7000);
          return;
        }
        has[k] = { v: v ? 1 : 0, t: Date.now() };
        if (v) (batch[k] || []).forEach(addBadge);
      });
      saveHas();
      if (Object.keys(queue).length && !timer) timer = setTimeout(flush, 300);
    }, function () { /* нет связи — молча */ });
  }

  function scan() {
    var cards = document.querySelectorAll('.card:not([data-al-scanned])');
    for (var i = 0; i < cards.length; i++) {
      var el = cards[i], d = el.card_data;
      if (!d) continue;
      el.setAttribute('data-al-scanned', '1');
      var id = parseInt(d.id, 10);
      if (!id || String(d.id) !== String(id) || d.source === 'wrestling') continue;
      if (!animeLike(d)) continue;
      var key = ((d.name || d.original_name) ? 'tv' : 'movie') + ':' + id;
      if (fresh(has[key])) { if (has[key].v) addBadge(el); continue; }
      (queue[key] = queue[key] || []).push(el);
    }
    if (Object.keys(queue).length && !timer) timer = setTimeout(flush, 400);
  }

  function hookBadges() {
    var st = document.createElement('style');
    st.textContent = '.card__al{position:absolute;top:1.9em;right:.45em;z-index:3;background:#29b6f6;color:#06202e;font-weight:700;' +
      'font-size:.75em;line-height:1;padding:.22em .45em;border-radius:.3em;box-shadow:0 0 .3em rgba(0,0,0,.5)}';
    document.head.appendChild(st);
    var t = null;
    new MutationObserver(function () {
      if (t) return;
      t = setTimeout(function () { t = null; scan(); }, 400);
    }).observe(document.body, { childList: true, subtree: true });
    scan();
  }

  // ---------- выбор и воспроизведение ----------
  function showSelect(title, items, back, onSelect, onLong) {
    Lampa.Select.show({
      title: title, items: items, home_episodes: true,
      onBack: back,
      onSelect: function (a) { setTimeout(function () { onSelect(a); }, 250); },
      onLong: onLong ? function (a) { onLong(a); } : undefined
    });
  }

  function voiceText(s) {
    var v = s.voice || '';
    if (s.voicers && s.voicers.length) v += ' (' + s.voicers.join(', ') + ')';
    return v;
  }

  function sourceTitle(d, s) {
    return (d.media === 'tv' ? 'Сезон ' + s.season : 'Фильм') + ' · ' + (s.source === 'AL' ? 'AL' : s.source + ' · ' + s.voice) + ' · ' + s.episodes.length + ' эп.';
  }

  function epLabel(d, s, e) {
    return (d.media === 'tv' ? 'S' + s.season + ' E' + e.n + ' · ' : '') + e.title;
  }

  function findSource(d, rel) {
    var r = null;
    d.sources.forEach(function (s) { if (s.release === rel) r = s; });
    return r;
  }

  function play(d, s, idx) {
    var mh = 0;
    try { mh = parseInt(localStorage.getItem('home_maxh_v1'), 10) || 0; } catch (x) {}
    var playlist = s.episodes.map(function (e) {
      var u = e.url;
      if (u.charAt(0) === '/') u = BASE + u + (mh > 0 ? '&h=' + mh : '');      // поток AnimeLib выбирается сервером по профилю устройства
      return { title: epLabel(d, s, e), url: u, timeline: Lampa.Timeline.view(e.hash), hs: { src: 'anime', release: s.release, season: s.season, ep: e.n, voice: s.voice || '', q: /2160/.test(s.quality || '') ? '4K' : /1080/.test(s.quality || '') ? 'FHD' : /720/.test(s.quality || '') ? 'HD' : '' } };
    });
    window.__apKey = d.card;               // карточка тайтла: отметка просмотра уходит на сервер вместе с ней
    window.__apKeyAt = Date.now();
    var cur = $.extend({}, playlist[idx]);
    cur.playlist = playlist;
    // A popup may be above another title. Its activity is not playback identity.
    var active = Lampa.Activity.active() || {}, visibleCard = active.card || active.movie;
    var visibleMedia = visibleCard && (visibleCard.media_type || (visibleCard.name || visibleCard.original_name || visibleCard.first_air_date ? 'tv' : 'movie'));
    cur.card = visibleCard && String(visibleCard.id) === String(d.tmdb) && visibleMedia === d.media
      ? $.extend({}, visibleCard) : { id: d.tmdb, media_type: d.media, title: d.orig, original_title: d.orig };
    if (d.media === 'tv') { cur.card.name = cur.card.name || d.orig; cur.card.original_name = cur.card.original_name || d.orig; }
    cur.home_base = BASE;
    cur.audio_key = d.card;
    cur.tl_meta = JSON.stringify(playlist.map(function (p) {
      return { id: d.media === 'tv' ? 'tv-' + d.tmdb + '-s' + s.season + 'e' + p.hs.ep : 'movie-' + d.tmdb,
        h: String(p.timeline.hash), c: d.card };
    }));
    var e = s.episodes[idx];
    api('/anime/last/set?card=' + encodeURIComponent(d.card) + '&source=' + encodeURIComponent(s.source) + '&voice=' + encodeURIComponent(s.voice || '') +
      '&season=' + s.season + '&ep=' + e.n, function () {});
    Lampa.Player.play(cur);
    Lampa.Player.playlist(playlist);
  }

  // ---------- меню: сезоны -> озвучки -> серии ----------
  var TIERN = ['SD', 'HD', 'FHD', '4K'];
  function normV(x) { return String(x || '').toLowerCase().replace(/[^a-zа-я0-9]/g, ''); }
  function qTier(q) { q = String(q || ''); return /2160/.test(q) ? 3 : /1080/.test(q) ? 2 : /720/.test(q) ? 1 : 0; }
  function srcName(src) { return src === 'AL' ? 'AniLibria' : src; }
  function plural(n, a, b, c) { var m = n % 100, k = n % 10; return (m > 10 && m < 20) ? c : k === 1 ? a : (k > 1 && k < 5) ? b : c; }
  function epsWord(n) { return n + ' ' + plural(n, 'серия', 'серии', 'серий'); }

  function seasonsOf(d) {
    var m = {};
    (d.sources || []).forEach(function (x) { var k = x.season || 0; (m[k] = m[k] || []).push(x); });
    return m;
  }

  // прогресс сезона: серии объединяются по номеру (состояние просмотра у источников общее — ключ позиции один)
  function seasonStats(list) {
    var st = {};
    list.forEach(function (x) {
      x.episodes.forEach(function (e) {
        var cur = st[e.n];
        if (e.state === 'watched' || (e.state === 'started' && cur !== 'watched')) st[e.n] = e.state;
        else if (cur === undefined) st[e.n] = '';
      });
    });
    var total = 0, watched = 0, started = 0;
    Object.keys(st).forEach(function (k) { total++; if (st[k] === 'watched') watched++; else if (st[k] === 'started') started++; });
    return { total: total, watched: watched, started: started };
  }

  function showEpisodes(d, s, prev, focusN, back) {
    back = back || function () { openMenu(d, prev); };
    var firstUnseen = -1;
    s.episodes.forEach(function (e, i) { if (firstUnseen < 0 && e.state !== 'watched') firstUnseen = i; });
    var items = s.episodes.map(function (e, i) {
      var mk = e.state === 'watched' ? '✓ ' : e.state === 'started' ? '▶ ' : '';
      var sub = e.state === 'started' ? 'остановлено на ' + mins(e.t) + ' · ' : '';
      sub += mins(e.dur) + ' · озвучка: ' + voiceText(s);
      var sel = focusN ? e.n === focusN : (firstUnseen >= 0 ? i === firstUnseen : i === s.episodes.length - 1);
      return { title: mk + epLabel(d, s, e), subtitle: sub, idx: i, selected: sel };
    });
    showSelect(sourceTitle(d, s) + ' · ' + (s.quality || '1080p'), items, back, function (a) {
      Lampa.Controller.toggle(prev);
      play(d, s, a.idx);
    }, function (a) {
      var e = s.episodes[a.idx];
      var done = e.state === 'watched';
      Lampa.Select.close();
      function reopen() { showEpisodes(d, s, prev, e.n, back); }
      setTimeout(function () {
        var busy = false;
        Lampa.Select.show({ title: epLabel(d,s,e), items:[{title:done?'Снять отметку просмотра':'Отметить просмотренным'}],
          onBack:reopen,onBeforeClose:function(){return !busy;},onSelect:function(){
            if(busy)return;busy=true;
            if(!window.HomeEpisodeMark){noty('Синхронизация ещё загружается');busy=false;reopen();return;}
            var duration=e.dur||Lampa.Timeline.view(e.hash).duration||1500;
            window.HomeEpisodeMark({id:d.media==='tv'?'tv-'+d.tmdb+'-s'+s.season+'e'+e.n:'movie-'+d.tmdb,
              hash:String(e.hash),card:d.card,position:done?0:duration,duration:duration,percent:done?0:100,
              watched_at:Date.now(),op:done?'reset':'completed'},function(confirmed){
                busy=false;
                if(confirmed){e.state=done?'':'watched';e.t=done?0:duration;}
                noty(confirmed?(done?'Отметка снята':'Отмечено просмотренным'):'Отметка ожидает синхронизации');
                reopen();
              });
          }});
      },200);
    });
  }

  // Озвучки сезона (или фильма): одинаковые озвучки из разных источников склеены в одну строку «озвучка (качество)»; источник выбирается сам
  // (самый полный и качественный), сменить его можно удержанием ОК. Отмечена озвучка, на которой смотрели (даже в другом сезоне).
  function groupVoices(list, health) {
    var rank = { AL: 0, AnimeLib: 1, Kodik: 2 };
    health = health || {};
    var okOf = function (x) { var h = health[x.source]; return h ? (h.ok ? 0 : 1) : 0; };
    var msOf = function (x) { var h = health[x.source]; return h && h.ms ? h.ms : 0; };
    var g = {}, order = [];
    list.forEach(function (x) {
      var k = x.vkey || normV(x.voice);
      if (!g[k]) { g[k] = { key: k, label: x.vlabel || x.voice, srcs: [] }; order.push(k); }
      g[k].srcs.push(x);
    });
    var res = order.map(function (k) {
      var gr = g[k], max = 0;
      gr.srcs.forEach(function (x) { max = Math.max(max, x.episodes.length); });
      // по умолчанию: больше серий (варианты с меньше чем 90% серий — ниже), затем доступный сервер (и быстрее отвечающий), затем качество, затем источник
      gr.srcs.sort(function (a, b) {
        var fa = a.episodes.length >= max * 0.9 ? 0 : 1, fb = b.episodes.length >= max * 0.9 ? 0 : 1;
        var ms = Math.abs(msOf(a) - msOf(b)) > 800 ? (msOf(a) - msOf(b)) : 0;     // разница меньше 0,8 с — не различаем
        return (fa - fb) || (okOf(a) - okOf(b)) || ms || (qTier(b.quality) - qTier(a.quality)) || ((rank[a.source] || 9) - (rank[b.source] || 9)) || (b.episodes.length - a.episodes.length);
      });
      return gr;
    });
    res.sort(function (a, b) { return (qTier(b.srcs[0].quality) - qTier(a.srcs[0].quality)) || String(a.label).localeCompare(String(b.label)); });
    return res;
  }

  function prefKey(d, gr) { return 'anime_src_' + d.card + '_' + gr.key; }
  function bestSrc(d, gr) {
    try {
      var p = localStorage.getItem(prefKey(d, gr));
      if (p) for (var i = 0; i < gr.srcs.length; i++) if (gr.srcs[i].source + '|' + gr.srcs[i].release === p) return gr.srcs[i];
    } catch (e) {}
    return gr.srcs[0];
  }

  function pickSource(d, gr, season, prev, back) {
    var cur = bestSrc(d, gr);
    var items = gr.srcs.map(function (x) {
      return { title: (x === cur ? '✓ ' : '') + srcName(x.source) + ' · ' + (x.quality || '1080p'), subtitle: epsWord(x.episodes.length) + ' · озвучка: ' + voiceText(x), src: x };
    });
    showSelect(gr.label + ' · выбор источника', items, function () { openVoices(d, season, prev, back); }, function (a) {
      try { localStorage.setItem(prefKey(d, gr), a.src.source + '|' + a.src.release); } catch (e) {}
      showEpisodes(d, a.src, prev, null, function () { openVoices(d, season, prev, back); });
    });
  }

  function openVoices(d, season, prev, back) {
    var list = (d.sources || []).filter(function (x) { return (x.season || 0) === season; });
    var groups = groupVoices(list, d.health);
    if (!groups.length) return noty('В этом сезоне нет озвучек');
    if (groups.length === 1) return showEpisodes(d, bestSrc(d, groups[0]), prev, null, back);
    var last = d.last || null, lk = last ? (last.vkey || normV(last.voice)) : '';
    var sel = -1;
    var items = groups.map(function (gr, i) {
      var x = bestSrc(d, gr);
      var mine = !!lk && gr.key === lk;
      if (sel < 0 && mine) sel = i;
      var st = seasonStats([x]);
      var sub = epsWord(x.episodes.length) + (st.watched ? ' · просмотрено ' + st.watched : '');
      if (mine) sub += ' · вы смотрите на этой озвучке';
      if (x.voicers && x.voicers.length) sub += ' · ' + x.voicers.slice(0, 4).join(', ');
      return { title: (mine ? '★ ' : '') + gr.label + ' (' + TIERN[qTier(x.quality)] + ')', subtitle: sub, gr: gr };
    });
    if (sel < 0) sel = 0;
    items[sel].selected = true;
    showSelect((season ? 'Сезон ' + season : 'Озвучки') + ' · выберите озвучку (удержание — сменить источник)', items, back,
      function (a) {
        var x = bestSrc(d, a.gr);
        showEpisodes(d, x, prev, null, function () { openVoices(d, season, prev, back); });
      },
      function (a) { Lampa.Select.close(); setTimeout(function () { pickSource(d, a.gr, season, prev, back); }, 250); });
  }

  function openMenu(d, prev) {
    var close = function () { Lampa.Controller.toggle(prev); };
    if (d.media !== 'tv') return openVoices(d, 0, prev, close);
    var by = seasonsOf(d);
    var keys = Object.keys(by).map(Number).sort(function (x, y) { return x - y; });
    if (keys.length === 1 && !d.resume) return openVoices(d, keys[0], prev, close);
    var items = [];
    var r = d.resume;
    if (r && !r.finished) {
      var rs = findSource(d, r.release);
      var re = null;
      if (rs) rs.episodes.forEach(function (e) { if (e.n === r.ep) re = e; });
      if (rs && re) items.push({
        title: '▶ Продолжить: сезон ' + rs.season + ', серия ' + re.n,
        subtitle: (r.next ? 'следующая после просмотренной' : 'остановлено на ' + mins(r.pos)) + ' · озвучка: ' + voiceText(rs) + ' · ' + srcName(rs.source),
        act: 'resume', rel: r.release, n: r.ep
      });
    } else if (r && r.finished) {
      items.push({ title: 'Всё просмотрено', subtitle: 'можно пересмотреть с любой серии ниже', act: 'none' });
    }
    var focus = d.last && by[d.last.season] ? d.last.season : null, firstOpen = null;
    keys.forEach(function (k) {
      var st = seasonStats(by[k]);
      var all = st.total > 0 && st.watched >= st.total;
      if (firstOpen === null && !all) firstOpen = k;
      var mk = all ? '✓ ' : (st.watched || st.started) ? '▶ ' : '';
      var sub = epsWord(st.total);
      if (all) sub += ' · просмотрен';
      else if (st.watched) sub += ' · просмотрено ' + st.watched + ' из ' + st.total;
      if (!all && (st.started || st.watched)) sub += ' · идёт просмотр';
      var nv = groupVoices(by[k], d.health).length;
      sub += ' · ' + nv + ' ' + plural(nv, 'озвучка', 'озвучки', 'озвучек');
      items.push({ title: mk + 'Сезон ' + k, subtitle: sub, act: 'season', season: k });
    });
    var pick = items.filter(function (x) { return x.act === 'season' && (focus !== null ? x.season === focus : x.season === firstOpen); })[0];
    if (!items.some(function (x) { return x.act === 'resume'; }) && pick) pick.selected = true;
    showSelect('Аниме · сезоны', items, close, function (a) {
      if (a.act === 'none') return openMenu(d, prev);
      if (a.act === 'season') return openVoices(d, a.season, prev, function () { openMenu(d, prev); });
      var s2 = findSource(d, a.rel);
      if (!s2) return;
      Lampa.Controller.toggle(prev);
      var idx = 0;
      s2.episodes.forEach(function (e, i) { if (e.n === a.n) idx = i; });
      play(d, s2, idx);
    });
  }

  // «Продолжить» из карточки (continue.js): место, которое помнит сервер (учитывает озвучку и «следующую серию»)
  window.HomeSrc = window.HomeSrc || {};
  window.HomeSrc.anime = {
    // воспроизвести выбранный вариант (источник + озвучка) для указанной серии: нужен окну «Продолжить» при смене источника
    playAlt: function (movie, media, alt, target, ok, fail) {
      api('/anime/item?tmdb=' + movie.id + '&media=' + media, function (d) {
        if (!d || !d.ok || !d.sources) return fail('аниме-источники не ответили');
        var rs = findSource(d, alt.release);
        if (!rs) return fail('этот вариант сейчас недоступен');
        var idx = media === 'movie' ? 0 : -1;
        if (idx < 0) rs.episodes.forEach(function (e, i) { if (e.n === target.ep) idx = i; });
        if (idx < 0) return fail('серия не найдена в этом варианте');
        play(d, rs, idx);
        ok();
      }, function () { fail('аниме-источники не отвечают'); });
    },
    resume: function (rec, movie, media, ok, fail) {
      api('/anime/item?tmdb=' + movie.id + '&media=' + media, function (d) {
        if (!d || !d.ok || !d.sources || !d.sources.length) return fail('в аниме-источниках нет потока 1080p');
        var r = d.resume;
        if (!r) return fail('нет сохранённого места');
        if (r.finished) return fail('всё просмотрено');
        var rs = findSource(d, r.release);
        if (!rs) return fail('прежняя озвучка недоступна');
        var idx = -1;
        rs.episodes.forEach(function (e, i) { if (e.n === r.ep) idx = i; });
        if (idx < 0) return fail('серия не найдена');
        play(d, rs, idx);
        ok();
      }, function () { fail('аниме-источники не отвечают'); });
    }
  };

  function openAnime(movie, media) {
    var prev = baseCtrl();
    noty('Аниме: загружаю источники…');
    api('/anime/item?tmdb=' + movie.id + '&media=' + media, function (d) {
      if (!d || !d.ok || !d.sources || !d.sources.length) return noty('В аниме-источниках нет потока 720p и выше для этого тайтла');
      openMenu(d, prev);
    }, function () { noty('Не удалось получить аниме-источники'); });
  }

  // ---------- кнопка в карточке ----------
  function hookFull() {
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      var id = parseInt(movie.id, 10);
      if (!id || String(movie.id) !== String(id) || movie.source === 'wrestling' || !animeLike(movie)) return;
      var key = media + ':' + id;
      var tries = 0;
      var check = function () {
        tries++;
        api('/anime/has?keys=' + key, function (d) {
          var v = d[key];
          if (v === null || v === undefined) { if (tries < 8) setTimeout(check, 3000); return; }
          has[key] = { v: v ? 1 : 0, t: Date.now() }; saveHas();
          if (!v) return;
          var render = e.object.activity.render();
          if (render.find('.view--anime').length) return;
          var btn = $('<div class="full-start__button selector view--anime">' +
            '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg><span>Аниме</span></div>');
          btn.on('hover:enter', function () { setTimeout(function () { openAnime(movie, media); }, 300); });
          var anchor = render.find('.view--kinopub').first();
          if (!anchor.length) anchor = render.find('.view--torrent').first();
          if (anchor.length) anchor.after(btn); else render.find('.full-start__buttons, .buttons--container').first().append(btn);
        });
      };
      check();
    });
  }

  function start() {
    hookBadges();
    hookFull();
  }
  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
