(function () {
  'use strict';
  if (window.plugin_cardplus_ready) return;
  window.plugin_cardplus_ready = true;

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
  var DAY = 24 * 3600 * 1000;

  // ---------- настройки (localStorage; по умолчанию всё включено, кроме переноса рейтинга) ----------
  function opt(name, def) {
    try { var v = localStorage.getItem('cp_' + name); return v === null ? def : v === '1'; } catch (e) { return def; }
  }
  function lsGet(key) { try { return JSON.parse(localStorage.getItem(key) || '{}'); } catch (e) { return {}; } }
  function lsSet(key, obj) { try { localStorage.setItem(key, JSON.stringify(obj)); } catch (e) {} }

  // ---------- стили ----------
  var css = '' +
    // цвет рейтинга на постере
    '.card .card__vote.cp-r-bad{color:#ff5a52 !important}' +
    '.card .card__vote.cp-r-mid{color:#ffb74d !important}' +
    '.card .card__vote.cp-r-ok{color:#fff176 !important}' +
    '.card .card__vote.cp-r-good{color:#7bd88f !important}' +
    '.card .card__vote.cp-r-top{color:#35e06b !important;font-weight:700}' +
    // рейтинг в правом верхнем углу (по желанию)
    '.cp-rate-top .card .card__vote{top:.4em !important;right:.4em !important;bottom:auto !important;left:auto !important}' +
    // значок «сезон / серии» на постере сериала
    '.card .card__view .card__quality.cp-season div{background:rgba(0,0,0,.75) !important;background-color:rgba(0,0,0,.75) !important;border:1px solid #f9a825 !important;color:#ffd180 !important;text-transform:none !important;font-size:.85em !important;padding:.1em .45em !important}' +
    // отметки просмотра на постере фильма: зелёная галочка — просмотрен, жёлтая «▶» с полоской — начат
    '.card .cp-st{position:absolute;z-index:3;pointer-events:none}' +
    '.card .cp-mark{top:.45em;left:.45em;width:1.7em;height:1.7em;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:.95em;font-weight:700;line-height:1;box-shadow:0 0 .3em rgba(0,0,0,.6)}' +
    '.card .cp-done{background:#2ecc71;color:#fff}' +
    '.card .cp-part{background:#f9a825;color:#111}' +
    '.card .cp-bar{left:0;right:0;bottom:0;height:.3em;background:rgba(0,0,0,.55)}' +
    '.card .cp-bar i{display:block;height:100%;background:#f9a825}' +
    // рейтинги IMDb / Кинопоиск на странице карточки
    '.full-start__rate.cp-rate{margin-right:.8em}';
  var st = document.createElement('style');
  st.id = 'cardplus-css';
  st.textContent = css;
  document.head.appendChild(st);
  if (opt('rate_top', false)) document.body.classList.add('cp-rate-top');

  // ---------- 1. цвет рейтинга ----------
  function colorVotes(root) {
    if (!opt('rate_color', true)) return;
    var els = root.querySelectorAll('.card__vote:not([data-cp])');
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      var v = parseFloat((el.textContent || '').replace(',', '.'));
      if (isNaN(v)) continue;
      el.setAttribute('data-cp', '1');
      var c = v < 5 ? 'bad' : v < 6.5 ? 'mid' : v < 7.5 ? 'ok' : v < 8.5 ? 'good' : 'top';
      el.classList.add('cp-r-' + c);
    }
  }

  // ---------- общее: видима ли карточка ----------
  function visible(el) {
    var r = el.getBoundingClientRect();
    return r.bottom > -50 && r.top < window.innerHeight + 50 && r.right > -50 && r.left < window.innerWidth + 50 && r.width > 0;
  }

  function isTv(d) { return !!(d.name || d.original_name || d.first_air_date || d.number_of_seasons); }

  // Основная информация сериала: календарный выход последнего обычного сезона, не доступность у провайдера.
  function seriesFacts(movie, detail, today) {
    var seasons = (movie.seasons || []).filter(function (s) { return Number(s.season_number) > 0; });
    var last = seasons.slice().sort(function (a, b) { return b.season_number - a.season_number; })[0];
    var season = last ? Number(last.season_number) : Number(movie.number_of_seasons) || 1;
    var planned = Number(last && last.episode_count) || 0;
    var total = Number(movie.number_of_episodes) || seasons.reduce(function (sum, s) { return sum + (Number(s.episode_count) || 0); }, 0);
    var previous = movie.last_episode_to_air;
    var previousInSeason = previous && Number(previous.season_number) === season && previous.air_date && previous.air_date <= today;
    var aired = null, future = [];
    if (detail && Array.isArray(detail.episodes)) {
      var seen = {};
      detail.episodes.forEach(function (ep) {
        if (Number(ep.episode_number) <= 0) return;
        if (ep.air_date && ep.air_date <= today) seen[ep.episode_number] = true;
        else if (!ep.air_date && previousInSeason && Number(ep.episode_number) <= Number(previous.episode_number)) seen[ep.episode_number] = true;
        if (ep.air_date && ep.air_date > today) future.push(ep.air_date);
      });
      aired = Object.keys(seen).length;
      if (!detail.episodes.length && (!last || !last.air_date || last.air_date <= today)) aired = null;
    } else if (previousInSeason) aired = Number(previous.episode_number) || null;
    else if (last && last.air_date && last.air_date > today) aired = 0;
    if (planned > 0 && aired !== null) aired = Math.min(aired, planned);
    var status = { 'Returning Series': 'В эфире', 'Ended': 'Закончен', 'Canceled': 'Отменён', 'In Production': 'В производстве', 'Planned': 'Запланирован', 'Pilot': 'Пилот' }[movie.status] || 'Неизвестен';
    var next = movie.next_episode_to_air && movie.next_episode_to_air.air_date;
    if (!next && future.length) next = future.sort()[0];
    var ended = movie.status === 'Ended' || movie.status === 'Canceled';
    var date = next && /^\d{4}-\d{2}-\d{2}$/.test(next) ? next.split('-').reverse().join('.') : '';
    return {
      season: season,
      main: 'Сезонов: ' + (Number(movie.number_of_seasons) || seasons.length || '?') + ' · Серии: ' + (aired === null ? '?' : aired) + ' из ' + (planned || '?') + ' (' + (total || '?') + ')',
      schedule: (!ended ? 'Следующая серия: ' + (date || 'дата неизвестна') + ' · ' : '') + 'Статус: ' + status
    };
  }

  function hookSeriesInfo() {
    var seasonCache = {};
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie || !isTv(e.data.movie)) return;
      var movie = e.data.movie;
      if (!/^\d+$/.test(String(movie.id))) return;
      var render = e.object.activity.render();
      var now = new Date(), today = now.getFullYear() + '-' + ('0' + (now.getMonth() + 1)).slice(-2) + '-' + ('0' + now.getDate()).slice(-2);
      function draw(detail) {
        var line = render.find('.full-start-new__details').first();
        if (!line.length) return;
        var facts = seriesFacts(movie, detail, today);
        var labels = ['title_seasons', 'title_episodes', 'full_next_episode'].map(function (key) { return Lampa.Lang.translate(key) + ':'; });
        var keep = [];
        line.children('span').not('.full-start-new__split, .cp-series-meta').each(function () {
          var text = $(this).text().trim();
          if (!labels.some(function (prefix) { return text.indexOf(prefix) === 0; })) keep.push($(this).clone());
        });
        line.empty();
        keep.forEach(function (span, i) { if (i) line.append('<span class="full-start-new__split">●</span>'); line.append(span); });
        if (keep.length) line.append('<span class="full-start-new__split">●</span>');
        line.append($('<span class="cp-series-meta"></span>').text(facts.main));
        var schedule = render.find('.cp-series-schedule');
        if (!schedule.length) { schedule = $('<div class="cp-series-schedule" style="margin-top:.35em;opacity:.85;font-size:.9em"></div>'); line.after(schedule); }
        schedule.text(facts.schedule);
        render.find('.full-start__status').addClass('hide');
      }
      draw(null);
      function loadDetails() {
        var facts = seriesFacts(movie, null, today), key = movie.id + ':' + facts.season + ':' + today;
        if (seasonCache[key]) { draw(seasonCache[key]); return; }
        $.ajax({ url: Lampa.TMDB.api('tv/' + movie.id + '/season/' + facts.season + '?api_key=' + Lampa.TMDB.key() + '&language=ru'),
          dataType: 'json', timeout: 10000, success: function (detail) { seasonCache[key] = detail; draw(detail); } });
      }
      if (!Array.isArray(movie.seasons) || !movie.status) {
        $.ajax({ url: Lampa.TMDB.api('tv/' + movie.id + '?api_key=' + Lampa.TMDB.key() + '&language=ru'), dataType: 'json', timeout: 10000,
          success: function (full) { movie = $.extend({}, movie, full); draw(null); loadDetails(); }, error: loadDetails });
      } else loadDetails();
    });
  }

  // ---------- 2. сезон и серии на постере сериала ----------
  var seasonQueue = [], seasonBusy = 0;

  function seasonLabel(j) {
    var last = j.last_episode_to_air;
    if (!last) return '';
    var n = last.season_number, ep = last.episode_number, total = 0;
    (j.seasons || []).forEach(function (s) { if (s.season_number === n) total = s.episode_count; });
    var next = j.next_episode_to_air;
    if (total && ep >= total && !next) return 'С' + n + ' · ' + total + ' эп.';
    return 'С' + n + ' · ' + ep + (total ? '/' + total : '');
  }

  function putBadge(card, text, cls) {
    if (cls === 'cp-season' && !opt('season_badge', false)) return;
    var view = card.querySelector('.card__view');
    if (!view || view.querySelector('.card__quality')) return;
    var q = document.createElement('div');
    q.className = 'card__quality ' + (cls || '');
    var inner = document.createElement('div');
    inner.textContent = text;
    q.appendChild(inner);
    view.appendChild(q);
  }

  function loadSeason(card, d) {
    // значки «С1 · 8 эп.» на постерах сериалов отключены (по умолчанию); включаются настройкой season_badge
    if (!opt('season_badge', false)) return;
    var cache = lsGet('cp_seasons_v1');
    var hit = cache[d.id];
    if (hit && Date.now() - hit.at < DAY) { if (hit.t) putBadge(card, hit.t, 'cp-season'); return; }
    if (seasonBusy >= 3) { card.removeAttribute('data-cp-s'); return; }
    seasonBusy++;
    var url = Lampa.TMDB.api('tv/' + d.id + '?api_key=' + Lampa.TMDB.key() + '&language=ru');
    new Lampa.Reguest().silent(url, function (j) {
      seasonBusy--;
      var t = '';
      try { t = seasonLabel(j); } catch (e) {}
      var c2 = lsGet('cp_seasons_v1');
      c2[d.id] = { t: t, at: Date.now() };
      lsSet('cp_seasons_v1', c2);
      if (t && document.body.contains(card)) putBadge(card, t, 'cp-season');
    }, function () { seasonBusy--; });
  }

  // ---------- 3. качество фильма на постере ----------
  var qPending = {}, qTimer = null;

  function putQuality(card, q) {
    if (!q) return;
    putBadge(card, q, '');
  }

  function flushQuality() {
    qTimer = null;
    var items = [], map = {};
    Object.keys(qPending).slice(0, 12).forEach(function (id) {
      var p = qPending[id];
      delete qPending[id];
      items.push({ id: parseInt(id.substring(1), 10), title: p.title, year: p.year, tv: id.charAt(0) === 't' });
      map[id] = p.card;
    });
    if (!items.length) return;
    $.ajax({
      url: BASE + '/kp/quality', type: 'POST', contentType: 'application/json', data: JSON.stringify(items),
      dataType: 'json', timeout: 40000,
      success: function (res) {
        var cache = lsGet('cp_quality_v2');
        Object.keys(res || {}).forEach(function (id) {
          cache[id] = { q: res[id] || '', at: Date.now() };
          if (map[id] && document.body.contains(map[id])) putQuality(map[id], res[id]);
        });
        lsSet('cp_quality_v2', cache);
      }
    });
    if (Object.keys(qPending).length) qTimer = setTimeout(flushQuality, 1500);
  }

  function loadQuality(card, d) {
    var tv = isTv(d);
    var year = String((tv ? d.first_air_date : d.release_date) || '').substring(0, 4);
    var title = tv ? (d.original_name || d.name) : (d.original_title || d.title);
    if (!year || !title) return;
    var key = (tv ? 't' : 'm') + d.id;
    var cache = lsGet('cp_quality_v2');
    var hit = cache[key];
    if (hit && Date.now() - hit.at < DAY / 2) { putQuality(card, hit.q); return; }
    qPending[key] = { card: card, title: title, year: year };
    if (!qTimer) qTimer = setTimeout(flushQuality, 700);
  }

  // ---------- 5. отметки просмотра у фильмов (ключ позиции Lampa — оригинальное название) ----------
  function paintView(card, d) {
    var view = card.querySelector('.card__view');
    if (!view) return;
    var st = { s: '', p: 0 };
    try { st = window.__home_state(Lampa.Timeline.view(Lampa.Utils.hash(d.original_title))); } catch (e) {}
    if (st.s === 'started' && window.__home_inCont(card)) st = { s: '', p: 0 };
    var key = st.s + ':' + Math.round(st.p);
    if (card.getAttribute('data-cp-v') === key) return;
    card.setAttribute('data-cp-v', key);
    $(view).find('.cp-st').remove();
    if (st.s === 'watched') $(view).append('<div class="cp-st cp-mark cp-done">✓</div>');
    else if (st.s === 'started') $(view).append('<div class="cp-st cp-mark cp-part">▶</div><div class="cp-st cp-bar"><i style="width:' + Math.round(st.p) + '%"></i></div>');
  }

  // ---------- обход карточек ----------
  function scan() {
    try {
      var root = document.querySelector('.activitys') || document.body;
      colorVotes(root);
      var cards = root.querySelectorAll('.card');
      for (var i = 0; i < cards.length; i++) {
        var card = cards[i];
        var d = card.card_data;
        if (!d || !d.id || card.classList.contains('apps-card') || d.wrestling) continue;
        if (!isTv(d) && d.original_title && opt('viewmarks', true) && visible(card)) paintView(card, d);
        if (String(d.id) !== String(parseInt(d.id, 10)) || String(d.source || 'tmdb') !== 'tmdb') continue;
        if (!visible(card)) continue;
        if (isTv(d) && opt('seasons', true) && !card.getAttribute('data-cp-s')) { card.setAttribute('data-cp-s', '1'); loadSeason(card, d); }
        if (opt('quality', true) && !card.getAttribute('data-cp-q')) {
          card.setAttribute('data-cp-q', '1');
          // фильмы из CUB и нашего сервера: метку рисует сама Lampa (release_quality); сериалам рисуем мы (cp_quality с сервера или запрос)
          if (isTv(d)) {
            if (d.cp_quality) putQuality(card, d.cp_quality);
            else if (!card.querySelector('.card__quality')) loadQuality(card, d);
          } else if (!d.release_quality && !card.querySelector('.card__quality')) loadQuality(card, d);
        }
      }
    } catch (e) {}
  }

  // ---------- 4. рейтинги IMDb и Кинопоиск на странице карточки ----------
  function rateBox(label, val, title) {
    var b = $('<div class="full-start__rate cp-rate"><div>' + val + '</div><div>' + label + '</div></div>');
    if (title) b.attr('title', title);
    return b;
  }

  function hookFull() {
    if (!opt('ratings', true)) return;
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      if (!movie.id || String(movie.id) !== String(parseInt(movie.id, 10))) return;
      $.ajax({
        url: BASE + '/kp/ratings?type=' + media + '&tmdb=' + movie.id, dataType: 'json', timeout: 30000,
        success: function (r) {
          var render = e.object.activity.render();
          var line = render.find('.full-start-new__rate-line, .full-start__rate-line').first();
          if (!line.length || line.find('.cp-rate').length) return;
          var anchor = line.find('.full-start__rate').first();
          var boxes = [];
          if (r.imdb) boxes.push(rateBox('IMDb', r.imdb, r.imdb_votes ? r.imdb_votes + ' голосов' : ''));
          if (r.kp) boxes.push(rateBox('КП', r.kp, r.kp_votes ? r.kp_votes + ' голосов' : ''));
          boxes.forEach(function (b) { if (anchor.length) anchor.after(b); else line.prepend(b); anchor = b; });
        }
      });
    });
  }

  function start() {
    hookSeriesInfo();
    hookFull();
    setInterval(scan, 900);
  }

  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
