(function () {
  'use strict';
  if (window.plugin_continue_ready) return;
  window.plugin_continue_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;

  // «Продолжить» в карточке. Запоминаем, где и на чём остановились (источник, серия, позиция, признак «досмотрено»), запись общая для
  // всех устройств. Кнопка под блоком кнопок запускает ровно это: тот же источник, ту же серию (или следующую, если эта досмотрена),
  // а звуковую дорожку подставляет память дорожки плеера. Не вышло — говорим причину и предлагаем места, где тайтл есть.
  //
  // Источники подключаются через window.HomeSrc[имя] = { resume(rec, movie, media, ok, fail) }; запись делает общий перехват плеера.
  window.HomeSrc = window.HomeSrc || {};
  var NAMES = { kinopub: 'Кинопаб', anime: 'Аниме', wrestling: 'Wrestling', local: 'Локальная библиотека', url: 'Торрент' };
  var ALT_BTN = { kinopub: '.view--kinopub', anime: '.view--anime', local: '.view--local', url: '.view--torrent', torrent: '.view--torrent' };

  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }
  function fmt(sec) {
    sec = Math.max(0, Math.round(sec || 0));
    var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    return h + ':' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
  }

  var TIERS = ['SD', 'HD', 'FHD', '4K'];
  function qFromText(s) {
    s = String(s || '');
    if (/2160|\b4k\b|\buhd\b/i.test(s)) return '4K';
    if (/1080|full ?hd|remux/i.test(s)) return 'FHD';
    if (/\b720/i.test(s)) return 'HD';
    if (/\b(480|576|360)|dvd|sat ?rip|tv ?rip/i.test(s)) return 'SD';
    return '';
  }
  function normV(s) { return String(s || '').toLowerCase().replace(/[^a-zа-я0-9]/g, ''); }

  // профиль Кинопаба этого устройства (например «uhd»): от него зависит, какое качество видит сервер
  function kpq() {
    try { var p = JSON.parse(localStorage.getItem('kp_pref_v1') || '{}').profile; return p ? '&profile=' + encodeURIComponent(p) : ''; } catch (e) { return ''; }
  }

  function post(card, rec, ok) {
    try {
      $.ajax({ url: BASE + '/home/lastplay/set?card=' + encodeURIComponent(card), type: 'POST', contentType: 'application/json', data: JSON.stringify(rec), processData: false, timeout: 15000,
        // applied:false — сервер отверг запись (она старше сброса/удаления): считать список серий доставленным нельзя
        success: function (r) { if (r && r.applied === false) return; if (ok) ok(); } });
    } catch (e) {}
  }
  function getRec(card, cb) {
    $.ajax({ url: BASE + '/home/lastplay/get?card=' + encodeURIComponent(card) + kpq(), dataType: 'json', cache: false, timeout: 15000, success: function (d) { cb(d && d.src ? d : null); }, error: function () { cb(null); } });
  }

  // ---------- запись ----------
  function titleKey(data) {
    try {
      if (data && data.home_timeline_only) return '';
      var a = Lampa.Activity.active() || {};
      var c = (data && (data.card || data.movie)) || a.card || a.movie || {};
      if (c.id && /^\d+$/.test(String(c.id))) return c.id + '_' + ((c.media_type === 'tv' || c.name || c.original_name || c.number_of_seasons || c.first_air_date) ? 'tv' : 'movie');
      if (window.__apKey && Date.now() - (window.__apKeyAt || 0) < 15000) return String(window.__apKey);
    } catch (e) {}
    return '';
  }

  function isDone(tl) {
    try { if (window.__home_state) return window.__home_state(tl).s === 'watched'; } catch (e) {}
    var time = tl.time || 0, dur = tl.duration || 0, p = tl.percent || (dur ? time / dur * 100 : 0);
    return dur > 0 ? ((dur - time <= Math.min(600, dur * 0.12) && time >= dur * 0.5) || p >= 90) : p >= 90;
  }

  var MAP = {};      // хеш позиции -> { hs, card, title }
  var SENT = {};     // карточка -> { t, h }

  // Native keeps these entries verbatim when building the current file's window.
  // Preserve episode numbers per file: the following file may start another season.
  window.__home_torrent_entry = function (it) {
    var p = { u: String(it.url || ''), t: String(it.title || '').slice(0, 100), h: String(it.timeline && it.timeline.hash || '') };
    var s = parseInt(it.season || (it.hs && it.hs.season), 10), e = parseInt(it.episode || (it.hs && it.hs.ep), 10);
    if (s > 0 && s <= 999 && e > 0 && e <= 9999) { p.s = s; p.e = e; }
    return p;
  };

  function register(obj) {
    if (!obj || obj.iptv || obj.live) return;
    var key = titleKey(obj);
    if (!key) return;
    var items = (obj.playlist && obj.playlist.length) ? obj.playlist : [obj];
    items.forEach(function (it, i) {
      var h = it && it.timeline && it.timeline.hash;
      if (!h || String(h) === '0') return;
      var hs = it.hs;
      if (!hs && it.kp && it.kp.item) hs = { src: 'kinopub', item: it.kp.item, season: it.kp.season || 0, ep: it.kp.video };
      if (!hs) hs = { src: /\/local\/plex\//.test(String(it.url || '')) ? 'local' : 'url', url: String(it.url || ''), q: qFromText(it.title), season: it.season || undefined, ep: it.episode || undefined };
      if (hs.src === 'url' && items.length > 1) {
        // торрент с несколькими сериями: запоминаем окно списка вокруг текущей, чтобы после досмотра включить следующую
        var from = Math.max(0, i - 5), to = Math.min(items.length, i + 45);
        hs = $.extend({}, hs, {
          idx: i - from,
          pl: items.slice(from, to).map(window.__home_torrent_entry)
        });
        // Размер уменьшаем удалением дальних серий, сохраняя целые URL, текущую и следующую.
        while (hs.pl.length > 2 && JSON.stringify(hs).length > 30000) {
          if (hs.idx > 0) { hs.pl.shift(); hs.idx--; } else hs.pl.pop();
        }
      }
      MAP[String(h)] = { hs: hs, card: key, title: String(it.title || '').slice(0, 200), pi: i, many: items.length > 1 };
    });
  }

  // Сведения для «Продолжить» по хешу серии: нативный плеер кладёт их в ту же строку просмотра (resume), сервер пишет их вместе с прогрессом в одной транзакции.
  // Список серий торрента, если не помещается в разумный размер, не передаётся — сервер оставляет прежний, а страница досылает его своим путём.
  window.__home_resume = function (h) {
    try {
      var m = MAP[String(h)]; if (!m) return null;
      // список серий раздачи здесь НЕ повторяется: он передаётся плееру один раз на запуск (resume_playlist), у серии только её номер в нём (pi)
      var hs = m.hs, multi = hs.src === 'url' && m.many;
      var r = { src: hs.src, item: hs.item, season: hs.season, ep: hs.ep, release: hs.release, voice: hs.voice, url: hs.src === 'url' && !(multi && String(hs.url || '').length <= 300) ? hs.url : undefined, q: hs.q || undefined, title: m.title, pi: multi ? m.pi : undefined };
      return JSON.parse(JSON.stringify(r));
    } catch (e) { return null; }
  };

  // Сброс просмотра: серверу сообщаем хеш серии, он сам снимает запись «Продолжить», если она стоит на этой серии как «досмотрено».
  // Не зависит от того, запускали ли серию на этой странице; недошедший сброс помним и повторяем при следующем запуске.
  var RKEY = 'home_lp_reset_v2', RLEG = 'home_lp_reset_legacy';
  // Команда сброса хранит время действия и стабильный id: сервер снимает «Продолжить» только если запись не новее этого времени, подтверждение удаляет именно эту команду.
  // Очередь прежнего формата (список хешей без времени) переносится ОДИН раз: у таких команд дата неизвестна, поэтому они сохраняются отдельно и автоматически не применяются.
  function resetMigrate() {
    try {
      var old = localStorage.getItem('home_lp_reset');
      if (old === null) return;
      var a = JSON.parse(old) || [], leg = JSON.parse(localStorage.getItem(RLEG) || '[]'), cur = JSON.parse(localStorage.getItem(RKEY) || '[]');
      a.forEach(function (x) {
        if (typeof x === 'string') leg.push({ h: x });
        else if (x && x.h && x.u) cur.push({ h: x.h, u: x.u, id: x.id || (x.h + ':' + x.u) });
      });
      localStorage.setItem(RLEG, JSON.stringify(leg.slice(-50)));
      localStorage.setItem(RKEY, JSON.stringify(cur.slice(-30)));
      localStorage.removeItem('home_lp_reset');
    } catch (e) {}
  }
  function resetList() { resetMigrate(); try { return JSON.parse(localStorage.getItem(RKEY) || '[]') || []; } catch (e) { return []; } }
  function resetSave(a) { try { localStorage.setItem(RKEY, JSON.stringify(a.slice(-30))); } catch (e) {} }
  function resetSend(e) {
    try {
      $.ajax({ url: BASE + '/home/lastplay/reset?hash=' + encodeURIComponent(e.h) + '&u=' + e.u, timeout: 15000,
        success: function () { resetSave(resetList().filter(function (x) { return x.id !== e.id; })); } });
    } catch (x) {}
  }
  function resetNote(h) { var u = Date.now(), e = { h: h, u: u, id: h + ':' + u }; var a = resetList(); a.push(e); resetSave(a); resetSend(e); }
  function resetFlush() { resetList().forEach(resetSend); }

  function note(tl) {
    if (!tl || !tl.hash) return;
    // The native outbox owns this action, including its real position/duration.
    // The UI replay can be only a 100% marker with time=duration=0; posting it
    // again would overwrite the transaction's exact LastPlay values.
    if (tl.home_native) return;
    // сброс — собственное действие пользователя: строка, полученная с сервера, новой команды не порождает (сервер сам снимает «Продолжить» при принятии reset)
    if (!tl.received && !(tl.time || 0) && !(tl.percent || 0) && !isDone(tl)) {
      var rh = String(tl.hash);
      if (/^[0-9A-Za-z_.-]{1,60}$/.test(rh)) {
        var mm = MAP[rh]; if (mm && SENT[mm.card] && SENT[mm.card].h === tl.hash) SENT[mm.card] = null;
        resetNote(rh);
      }
      return;
    }
    if (tl.received) return;                          // полученная отметка не является новым действием страницы
    var m = MAP[String(tl.hash)];
    if (!m) return;
    var done = isDone(tl), now = Date.now(), last = SENT[m.card];
    if ((tl.time || 0) < 30 && !done) return;                                   // только что запустили
    if (!done && last && last.h === tl.hash && now - last.t < 15000) return;    // не чаще раза в 15 секунд
    // список серий торрента отправляем, пока сервер не подтвердил его получение (иначе потерянный первый пакет ломал «следующую серию»)
    var firstForHash = !(last && last.h === tl.hash && last.plOk);
    SENT[m.card] = { t: now, h: tl.hash, done: done, plOk: last && last.h === tl.hash && last.plOk };
    var hs = m.hs;
    post(m.card, {
      src: hs.src, item: hs.item, season: hs.season, ep: hs.ep, release: hs.release, voice: hs.voice, url: hs.src === 'url' ? hs.url : undefined,
      pl: hs.pl && firstForHash ? hs.pl : undefined, idx: hs.pl && firstForHash ? hs.idx : undefined, plKeep: hs.pl && !firstForHash ? true : undefined,
      q: hs.q || undefined, title: m.title, pos: Math.round(tl.time || 0), dur: Math.round(tl.duration || 0), done: done, hash: String(tl.hash), at: (tl.home_native && tl.updated) ? tl.updated : now
    }, function () { var s = SENT[m.card]; if (s && s.h === tl.hash) s.plOk = true; });
  }

  function hookRecorder() {
    var tries = 0;
    var t = setInterval(function () {
      tries++;
      try {
        if (window.Lampa && Lampa.Player && Lampa.Timeline && !Lampa.Player.__hsWrapped) {
          var op = Lampa.Player.play;
          Lampa.Player.play = function (obj) { try { register(obj); } catch (e) {} return op.apply(this, arguments); };
          Lampa.Player.__hsWrapped = true;
          var ou = Lampa.Timeline.update;
          Lampa.Timeline.update = function (tl) { var r = ou.apply(this, arguments); try { note(tl); } catch (e) {} return r; };
          clearInterval(t);
        }
      } catch (e) {}
      if (tries > 80) clearInterval(t);
    }, 500);
  }

  // ---------- запуск ----------
  function fallback(rec, movie, media, render, reason) {
    var prev = 'content';
    try { prev = Lampa.Controller.enabled().name || 'content'; } catch (e) {}
    var failedBtn = ALT_BTN[rec.src];
    var items = [];
    [['.view--kinopub', 'Кинопаб'], ['.view--anime', 'Аниме (AniLibria / AnimeLib)'], ['.view--local', 'Локальная библиотека'], ['.view--torrent', 'Торренты']].forEach(function (a) {
      if (a[0] !== failedBtn && render.find(a[0]).length) items.push({ title: 'Играть · ' + a[1], sel: a[0] });
    });
    if (failedBtn && render.find(failedBtn).length) items.push({ title: 'Выбрать вручную · ' + (NAMES[rec.src] || rec.src), sel: failedBtn });
    var title = 'Невозможно продолжить: ' + (NAMES[rec.src] || rec.src) + ' — ' + reason;
    if (!items.length) { noty(title); return; }
    Lampa.Select.show({
      title: title,
      items: items,
      onBack: function () { Lampa.Controller.toggle(prev); },
      onSelect: function (a) {
        Lampa.Controller.toggle(prev);
        setTimeout(function () { render.find(a.sel).first().trigger('hover:enter'); }, 350);
      }
    });
  }

  function run(rec, movie, media, render) {
    var A = window.HomeSrc[rec.src] || (rec.src === 'torrent' ? window.HomeSrc.url : null);
    if (!A || !A.resume) return fallback(rec, movie, media, render, 'этот источник не поддерживает продолжение');
    var settled = false;
    noty('Продолжаю: ' + (NAMES[rec.src] || rec.src) + '…');
    var timer = setTimeout(function () { if (settled) return; settled = true; fallback(rec, movie, media, render, 'источник не ответил'); }, 25000);
    try {
      A.resume(rec, movie, media,
        function () { if (settled) return; settled = true; clearTimeout(timer); },
        function (why) { if (settled) return; settled = true; clearTimeout(timer); fallback(rec, movie, media, render, why || 'нет потока'); });
    } catch (e) {
      if (!settled) { settled = true; clearTimeout(timer); fallback(rec, movie, media, render, 'ошибка запуска'); }
    }
  }

  // запуск выбранного варианта (другой источник или озвучка) для целевой серии
  function runAlt(alt, target, rec, movie, media, render) {
    var settled = false;
    var fail = function (why) { if (settled) return; settled = true; fallback(rec, movie, media, render, why || 'нет потока'); };
    var ok = function () { settled = true; };
    noty('Продолжаю: ' + alt.name + '…');
    setTimeout(function () { if (!settled) fail('вариант не ответил'); }, 25000);
    try {
      if (alt.src === 'kinopub' && window.HomeSrc.kinopub) {
        window.HomeSrc.kinopub.resume({ src: 'kinopub', item: alt.kp, season: target.season, ep: media === 'movie' ? null : target.ep, done: false }, movie, media, ok, fail);
      } else if (alt.src === 'anime' && window.HomeSrc.anime && window.HomeSrc.anime.playAlt) {
        window.HomeSrc.anime.playAlt(movie, media, alt, target, ok, fail);
      } else fail('вариант не поддерживается');
    } catch (e) { fail('ошибка запуска'); }
  }

  // «Продолжить» запускает прежний источник сразу, без вопросов
  function resume(rec, movie, media, render) { run(rec, movie, media, render); }

  // Лучшее качество: спрашиваем у сервера, где ещё есть нужная серию (или тот же фильм) и в каком качестве. Отличия допустимы: другая озвучка,
  // другой источник. Результат — отсортированный список вариантов (лучшее качество, та же озвучка, тот же источник — выше) и цель.
  function betterOptions(a, rec, media) {
    var target = (a && a.target) || {};
    if (!a || target.exists === false) return null;
    var cur = rec.q ? TIERS.indexOf(rec.q) : -1;
    var need = cur >= 0 ? cur : 1;                       // качество прошлого просмотра неизвестно — считаем HD
    var vlab = normV(rec.voice || rec.voiceLabel);
    var list = [];
    (a.alts || []).forEach(function (x) {
      if (x.tier <= need) return;
      var voices = x.src === 'kinopub' ? (x.voices || []) : (x.voice ? [x.voice] : []);
      var same = !!vlab && voices.some(function (v) { var n = normV(v); return n && (n.indexOf(vlab) >= 0 || vlab.indexOf(n) >= 0); });
      list.push({ act: 'alt', alt: x, tier: x.tier, same: same, sameSrc: x.src === rec.src, voices: voices });
    });
    if (a.torrent && a.torrent.tier > need && rec.src !== 'url') list.push({ act: 'torrent', tier: a.torrent.tier, torr: a.torrent, same: false, sameSrc: false, voices: [] });
    if (!list.length) return null;
    list.sort(function (x, y) { return (y.tier - x.tier) || ((y.same ? 1 : 0) - (x.same ? 1 : 0)) || ((y.sameSrc ? 1 : 0) - (x.sameSrc ? 1 : 0)); });
    return { target: target, list: list, hasVoice: !!vlab };
  }

  function optTitle(o) {
    if (o.act === 'torrent') return TIERS[o.tier] + ' · торренты';
    return TIERS[o.tier] + ' · ' + o.alt.name + (o.alt.src === 'anime' && o.voices.length ? ' · ' + o.voices[0] : '');
  }
  function optNote(o, hasVoice) {
    if (o.act === 'torrent') return (o.torr.coarse ? 'по сериалу в целом · ' : '') + 'откроется список раздач, позиция сохранится';
    var v = o.voices.length ? 'озвучка: ' + o.voices.join(', ') : 'озвучку выберете в плеере';
    return v + (o.voices.length && hasVoice ? (o.same ? ' · та же озвучка' : ' · другая озвучка') : '');
  }

  function startOption(o, opt, rec, movie, media, render) {
    if (o.act === 'torrent') { render.find('.view--torrent').first().trigger('hover:enter'); return; }
    runAlt(o.alt, opt.target, rec, movie, media, render);
  }

  function addBetterButton(e, movie, media, rec, render, after) {
    var episodic = media === 'tv' && rec.ep != null;
    if (!((media === 'movie' || episodic) && !(rec.pl && rec.pl.length))) return;
    var next = !!rec.done && episodic;
    $.ajax({
      url: BASE + '/home/alts?media=' + media + '&tmdb=' + movie.id + '&season=' + (rec.season || 0) + '&ep=' + (rec.ep || 0) + '&next=' + (next ? 'true' : 'false') + kpq(),
      dataType: 'json', cache: false, timeout: 20000,
      success: function (a) {
        var opt = betterOptions(a, rec, media);
        if (!opt || render.find('.view--continue-better').length || !document.body.contains(after[0])) return;
        var best = opt.list[0];
        var where = rec.done && opt.target.ep != null ? 'серия ' + opt.target.ep + (opt.target.season ? ' · сезон ' + opt.target.season : '')
          : (rec.pos > 0 ? 'с ' + fmt(rec.pos) : '');
        var more = opt.list.length > 1 ? ' · ещё ' + (opt.list.length - 1) : '';
        var text = 'Смотреть в лучшем качестве · ' + optTitle(best) + (where ? ' · ' + where : '') + more;
        var btn = $('<div class="full-start__button selector view--continue-better" style="display:block;width:100%;box-sizing:border-box;margin:.4em 0 0;text-align:center">' +
          '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor" style="vertical-align:middle;margin-right:.5em"><path d="M12 2l3 7 7 .6-5.3 4.7 1.6 7L12 17.6 5.7 21.3l1.6-7L2 9.6 9 9z"/></svg><span style="vertical-align:middle"></span></div>');
        btn.find('span').text(text);
        btn.append('<div class="cont-note" style="font-size:.78em;opacity:.7;margin-top:.15em"></div>');
        btn.find('.cont-note').text(optNote(best, opt.hasVoice));
        btn.on('hover:enter', function () {
          if (opt.list.length === 1) return setTimeout(function () { startOption(best, opt, rec, movie, media, render); }, 250);
          var prev = 'content';
          try { prev = Lampa.Controller.enabled().name || 'content'; } catch (x) {}
          Lampa.Select.show({
            title: 'Лучшее качество' + (where ? ' · ' + where : ''),
            items: opt.list.map(function (o) { return { title: optTitle(o), subtitle: optNote(o, opt.hasVoice), o: o }; }),
            onBack: function () { Lampa.Controller.toggle(prev); },
            onSelect: function (it) { Lampa.Controller.toggle(prev); setTimeout(function () { startOption(it.o, opt, rec, movie, media, render); }, 300); }
          });
        });
        after.after(btn);
        try { Lampa.Controller.collectionAppend(btn); } catch (x) {}
      }
    });
  }

  // ---------- кнопка ----------
  // Следующая серия после досмотренной: {season, ep}; null — дальше ничего нет (кнопку не показываем); undefined — не удалось узнать
  function tmdbNext(movie, season, ep) {
    try {
      var seasons = movie.seasons, last = movie.last_episode_to_air;
      if (!season || !seasons || !seasons.length || !last) return undefined;
      var cnt = 0, hasNext = false;
      seasons.forEach(function (s) { if (s.season_number === season) cnt = s.episode_count || 0; if (s.season_number === season + 1) hasNext = true; });
      var cand = null;
      if (cnt && ep + 1 <= cnt) cand = { season: season, ep: ep + 1 };
      else if (hasNext) cand = { season: season + 1, ep: 1 };
      if (!cand) return null;
      var aired = cand.season < last.season_number || (cand.season === last.season_number && cand.ep <= last.episode_number);
      return aired ? cand : null;
    } catch (e) { return undefined; }
  }
  function resolveNext(rec, movie, media, cb) {
    if (!(rec.done && media === 'tv' && rec.ep != null) || (rec.pl && rec.pl.length)) return cb(undefined);
    var ep = parseInt(rec.ep, 10), season = parseInt(rec.season, 10) || 0;
    if (rec.src === 'kinopub' && rec.item && window.HomeSrc && window.HomeSrc.kinopub && window.HomeSrc.kinopub.next) {
      return window.HomeSrc.kinopub.next({ item: rec.item, season: season, ep: ep }, function (n) { cb(n === undefined ? tmdbNext(movie, season, ep) : n); });
    }
    cb(tmdbNext(movie, season, ep));
  }

  function label(rec, voice) {
    var parts = ['Продолжить', NAMES[rec.src] || rec.src];
    if (rec.q) parts.push(rec.q);
    var hasList = rec.pl && rec.pl.length && rec.idx != null;
    if (rec.done) {
      if (hasList) {
        if (rec.idx + 1 >= rec.pl.length) return null;      // последняя серия раздачи
        parts.push('следующая: ' + (rec.pl[rec.idx + 1].t || 'серия'));
        if (voice) parts.push('звук ' + voice);
        return parts.join(' · ');
      }
      if (rec.ep == null) return null;                       // фильм досмотрен — продолжать нечего
      parts.push('следующая серия');
    } else if (rec.pos > 0) parts.push(fmt(rec.pos));
    if (hasList && !rec.done && rec.pl[rec.idx] && rec.pl[rec.idx].t) parts.push(rec.pl[rec.idx].t);
    if (rec.done && rec.nxt) { parts.push('сезон ' + rec.nxt.season); parts.push('серия ' + rec.nxt.ep); }
    else {
      if (rec.season) parts.push('сезон ' + rec.season);
      if (rec.ep != null) parts.push('серия ' + (rec.done ? (parseInt(rec.ep, 10) + 1) : rec.ep));
    }
    if (voice) parts.push('звук ' + voice);
    return parts.join(' · ');
  }

  function addButton(e, movie, media, rec, voice) {
    if (rec && rec.no_ru_audio) return;                 // серия только с субтитрами: «Продолжить» не показываем
    var render = e.object.activity.render();
    if (render.find('.view--continue').length) return;
    var text = label(rec, voice);
    if (!text) return;
    rec.voiceLabel = voice || '';
    var btn = $('<div class="full-start__button selector view--continue" style="display:block;width:100%;box-sizing:border-box;margin:.6em 0 0;text-align:center">' +
      '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor" style="vertical-align:middle;margin-right:.5em"><path d="M8 5v14l11-7z"/></svg><span style="vertical-align:middle"></span></div>');
    btn.find('span').text(text);
    btn.on('hover:enter', function () { setTimeout(function () { resume(rec, movie, media, render); }, 250); });
    btn.on('hover:long', function () {
      // удержание: забыть запись (кнопка исчезнет)
      $.ajax({ url: BASE + '/home/lastplay/delete?card=' + encodeURIComponent(movie.id + '_' + media), timeout: 15000, success: function () { try { Lampa.Favorite.remove('history', movie); } catch (e) {} btn.remove(); render.find('.view--continue-better').remove(); noty('Запись «Продолжить» удалена'); } });
    });
    var cont = render.find('.full-start-new__buttons').first();
    if (!cont.length) cont = render.find('.full-start__buttons').first();
    if (cont.length) cont.after(btn);
    else render.find('.full-start__body, .full-start-new__body').first().append(btn);
    try { Lampa.Controller.collectionAppend(btn); } catch (x) {}
    addBetterButton(e, movie, media, rec, render, btn);
  }

  function hookFull() {
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      var id = parseInt(movie.id, 10);
      if (!id || String(movie.id) !== String(id)) return;
      var card = id + '_' + media;
      getRec(card, function (rec) {
        if (!rec) return;
        resolveNext(rec, movie, media, function (nxt) {
          // досмотрена последняя вышедшая серия — продолжать нечего: кнопки «Продолжить» нет
          if (nxt === null) return;
          if (nxt) rec.nxt = nxt;
        $.ajax({
          url: BASE + '/home/audio?key=' + encodeURIComponent(card), dataType: 'json', cache: false, timeout: 8000,
          success: function (a) { addButton(e, movie, media, rec, a && a.label ? String(a.label).split(' · ')[0] : ''); },
          error: function () { addButton(e, movie, media, rec, ''); }
        });
        });
      });
    });
  }

  // ---------- запуск «по ссылке» (торренты и всё без своего адаптера) ----------
  // сервер раздачи (TorrServer) отвечает? Ответ нужен только как «жив»: тело отбрасываем сразу
  function reach(url, cb) {
    var done = false, ctl = null;
    try { ctl = new AbortController(); } catch (e) {}
    var t = setTimeout(function () { if (!done) { done = true; try { if (ctl) ctl.abort(); } catch (e) {} cb(false); } }, 7000);
    try {
      fetch(url, { mode: 'no-cors', signal: ctl ? ctl.signal : undefined }).then(function () {
        if (done) return; done = true; clearTimeout(t); try { if (ctl) ctl.abort(); } catch (e) {} cb(true);
      }, function () { if (done) return; done = true; clearTimeout(t); cb(false); });
    } catch (e) { done = true; clearTimeout(t); cb(true); }
  }

  window.HomeSrc.url = {
    resume: function (rec, movie, media, ok, fail) {
      var playlist, start;
      if (rec.pl && rec.pl.length && rec.idx != null) {
        start = rec.done ? rec.idx + 1 : rec.idx;
        if (start >= rec.pl.length) return fail('следующей серии в раздаче нет');
        playlist = rec.pl.map(function (p, i) {
          var s = parseInt(p.s, 10), e = parseInt(p.e, 10);
          // Older records know only the current episode. Never assign it to the next file.
          if (!(s > 0 && e > 0) && i === rec.idx) { s = parseInt(rec.season, 10); e = parseInt(rec.ep, 10); }
          return { title: p.t, url: p.u, timeline: Lampa.Timeline.view(p.h), season: s > 0 ? s : undefined, episode: e > 0 ? e : undefined };
        });
      } else {
        if (rec.done) return fail('серия досмотрена, следующую нужно выбрать в раздаче');
        if (!rec.url) return fail('нет ссылки на поток');
        playlist = [{ title: rec.title || movie.title || movie.name, url: rec.url, timeline: Lampa.Timeline.view(rec.hash), season: rec.season, episode: rec.ep }];
        start = 0;
      }
      // The WebView is paused while the native player runs. Pass the same
      // timeline identity and the bounded torrent window to native code so
      // HomePlayerActivity can persist position/duration without relying on
      // a later Lampa.Timeline.update callback from the page.
      var cardKey = String(movie.id) + '_' + media;
      var resumeList = playlist.map(function (p) {
        var entry = { u: String(p.url || ''), t: String(p.title || '').slice(0, 100), h: String(p.timeline && p.timeline.hash || '') };
        if (p.season > 0 && p.episode > 0) { entry.s = p.season; entry.e = p.episode; }
        return entry;
      });
      var metas = playlist.map(function (p, i) {
        var season = p.season, ep = p.episode;
        var id = media === 'tv' && season != null && ep != null
          ? 'tv-' + movie.id + '-s' + season + 'e' + ep
          : (media === 'tv' ? 'tl-' + p.timeline.hash : 'movie-' + movie.id);
        var r = { src: 'url', url: String(p.url || ''), hash: String(p.timeline && p.timeline.hash || ''), title: p.title || movie.title || movie.name, season: season, ep: ep, pi: i };
        return { id: id, h: r.hash, c: cardKey, r: r };
      });
      var native = $.extend({}, playlist[start]);
      native.playlist = playlist;
      native.tl_meta = JSON.stringify(metas);
      native.resume_playlist = JSON.stringify(resumeList);
      reach(playlist[start].url, function (alive) {
        if (!alive) return fail('сервер раздачи не отвечает (TorrServer недоступен или раздача удалена)');
        window.__apKey = movie.id + '_' + media; window.__apKeyAt = Date.now();
        Lampa.Player.play(native);
        Lampa.Player.playlist(playlist);
        ok();
      });
    }
  };

  window.HomeSrc.local = window.HomeSrc.url;


  // ---------- озвучки на карточке: где какие есть (Кинопаб, аниме-источники, торренты) ----------
  // Три части запрашиваются параллельно и появляются по мере готовности; последний результат хранится на устройстве (6 часов) и показывается
  // сразу при открытии карточки, а свежие данные подменяют его, когда придут.
  var TCOL = ['#bdbdbd', '#a5d6a7', '#81d4fa', '#ffd54f'];
  function vtag(tier) { return $('<span style="font-weight:700;margin-left:.25em"></span>').css('color', TCOL[tier] || '#fff').text(TIERS[tier]); }

  function paintVoices(box, st) {
    box.empty();
    var rows = [];
    if (st.kinopub && st.kinopub.length) rows.push({ name: 'Кинопаб', q: st.kinopub_q, items: st.kinopub, max: 6 });
    if (st.torrents && st.torrents.length) rows.push({ name: 'Торренты', q: st.torrents_q, items: st.torrents.map(function (t) { return t.voice; }), max: 7 });
    if (st.anime && st.anime.length) rows.push({ name: 'Аниме', q: st.anime_q, items: st.anime.map(function (a) { return a.voice; }), max: 6 });
    if (!rows.length) return;
    // по строке на источник: «Кинопаб   Видео: FHD WEB-DL   Перевод: озвучки»
    var grid = $('<div style="display:grid;grid-template-columns:auto auto 1fr;column-gap:1.1em;row-gap:.25em;align-items:baseline"></div>');
    rows.forEach(function (r) {
      grid.append($('<div style="color:#fff;font-weight:800;font-size:1.2em"></div>').text(r.name));
      var v = $('<div style="white-space:nowrap"></div>');
      v.append($('<span style="color:#ffb74d;font-weight:700"></span>').text('Видео: '));
      v.append($('<span style="color:#fff;font-weight:600"></span>').text(r.q || '—'));
      grid.append(v);
      var shown = r.items.slice(0, r.max).join(', ');
      if (r.items.length > r.max) shown += ' и ещё ' + (r.items.length - r.max);
      var t = $('<div></div>');
      t.append($('<span style="color:#4fc3f7;font-weight:700"></span>').text('Перевод: '));
      t.append($('<span style="color:#fff;font-weight:500"></span>').text(shown));
      grid.append(t);
    });
    box.append(grid);
  }

  function hookVoices() {
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      var id = parseInt(movie.id, 10);
      if (!id || String(movie.id) !== String(id)) return;
      var render = e.object.activity.render();
      if (render.find('.cp-voices').length) return;
      var box = $('<div class="cp-voices" style="margin:.6em 0 .2em;font-size:1.3em;line-height:1.35;text-shadow:0 1px 5px rgba(0,0,0,.95)"></div>');
      var anchor = render.find('.full-start-new__buttons, .full-start__buttons').first();
      if (anchor.length) anchor.before(box); else render.find('.full-start__body, .full-start-new__body').first().append(box);

      var ck = 'cp_voices_v2', key = media + ':' + id + kpq(), st = {};
      try {
        var all = JSON.parse(localStorage.getItem(ck) || '{}');
        if (all[key] && Date.now() - all[key].at < 6 * 3600 * 1000) { st = all[key].d || {}; paintVoices(box, st); }
      } catch (x) {}
      var save = function () {
        try {
          var all = JSON.parse(localStorage.getItem(ck) || '{}');
          all[key] = { at: Date.now(), d: st };
          var ks = Object.keys(all);
          if (ks.length > 80) ks.sort(function (a, b) { return all[a].at - all[b].at; }).slice(0, 20).forEach(function (k) { delete all[k]; });
          localStorage.setItem(ck, JSON.stringify(all));
        } catch (x) {}
      };
      ['kinopub', 'anime', 'torrents'].forEach(function (part) {
        $.ajax({
          url: BASE + '/home/voices?media=' + media + '&tmdb=' + id + '&part=' + part + kpq(), dataType: 'json', cache: false, timeout: 45000,
          success: function (d) {
            if (!d || !d[part]) return;
            st[part] = d[part];
            if (d[part + '_q'] !== undefined) st[part + '_q'] = d[part + '_q'];
            if (document.body.contains(box[0])) paintVoices(box, st);
            save();
          }
        });
      });
    });
  }

  // ---------- меню «Источник» (кнопка «Смотреть»): под каждым источником — максимальное качество ----------
  var SRCQ = {};
  function srcKey() {
    try { var c = Lampa.Activity.active().card || {}; var m = (c.name || c.original_name || c.number_of_seasons || c.first_air_date) ? 'tv' : 'movie'; return m + ':' + c.id; } catch (e) { return ''; }
  }
  function hookSourceMenu() {
    if (!Lampa.Select || Lampa.Select.__sqWrapped) return;
    var orig = Lampa.Select.show;
    Lampa.Select.show = function (p) {
      try {
        var key=srcKey(), q=SRCQ[key]||{};
        if(p&&p.items)p.items.forEach(function(it){
          var btn=it.btn&&$(it.btn), source=btn&&btn.is('.view--kinopub')?'kinopub':btn&&btn.is('.view--torrent')?'torrents':btn&&btn.is('.view--anime')?'anime':null;
          if(!source)return;
          var label=q[source]?'максимум: '+q[source]:'качество уточняется…';
          var original=String(it.subtitle||'').replace(/<span class="home-source-quality"[^>]*>[^<]*<\/span>(?:<br>)?/g,'');
          it.subtitle='<span class="home-source-quality" data-card="'+key+'" data-source="'+source+'">'+label+'</span>'+(original?'<br>'+original:'');
        });
      } catch (e) {}
      // меню «Еще» (три точки): добавляем «Избранное» — обычное избранное: добавили, и тайтл лежит там, пока не уберёте
      try {
        if (p && p.items && p.items.some(function (it) { var t = String(it.title || '').trim(); return t === 'Рекомендации' || t === 'Интересные факты'; }) && !p.__cpfav) {
          p.__cpfav = true;
          var card = Lampa.Activity.active().card;
          var prevCtl = 'full_start';
          try { prevCtl = Lampa.Controller.enabled().name || 'full_start'; } catch (x) {}
          var on = !!((Lampa.Favorite.check(card) || {}).book);
          p.items.unshift({ title: (on ? '✓ ' : '') + 'Избранное', subtitle: on ? 'лежит в избранном — нажмите, чтобы убрать' : 'добавить в избранное', cpfav: true });
          var os = p.onSelect;
          p.onSelect = function (it) {
            if (it && it.cpfav) {
              try { Lampa.Favorite.toggle('book', card); } catch (x) {}
              noty(on ? 'Убрано из избранного' : 'Добавлено в избранное');
              try { Lampa.Controller.toggle(prevCtl); } catch (x) {}
              return;
            }
            return os && os.apply(this, arguments);
          };
        }
      } catch (e) {}
      return orig.apply(this, arguments);
    };
    Lampa.Select.__sqWrapped = true;
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      var id = parseInt(movie.id, 10);
      if (!id || String(movie.id) !== String(id)) return;
      $.ajax({ url: BASE + '/home/srcq?media=' + media + '&tmdb=' + id + kpq(), dataType: 'json', cache: false, timeout: 45000, success: function (d) { if (d) {
        var key=media+':'+id;SRCQ[key]=d;
        $('.home-source-quality').each(function(){var e=$(this);if(e.attr('data-card')!==key)return;var v=d[e.attr('data-source')];e.text(v?'максимум: '+v:'качество не указано');});
      } } });
    });
  }

  // ---------- описание («Подробно») на карточке — всегда на чёрном фоне, чтобы читалось поверх обоев ----------
  function hookDescr() {
    var st = document.createElement('style');
    st.textContent = '.cp-descr{background:#000 !important;border-radius:.5em;padding:.4em 1.1em .9em !important;margin-top:.8em}' +
      '.cp-descr .full-descr__text,.cp-descr .full-descr__details,.cp-descr .full-descr__line{color:#fff !important;opacity:1 !important}' +
      '.cp-descr .full-descr__line-name{color:#b0bec5 !important}' +
      // реакции — справа в строке оценок и статуса, мельче
      '.cp-react{margin:0 0 0 auto !important;width:auto !important;flex-wrap:nowrap !important;font-size:.78em !important;align-items:center}' +
      '.full-start-new__rate-line{align-items:center}' +
      // карточка фильма поднята к шапке: убираем пустое место сверху
      'body.cp-card .activity--active .scroll__content{padding-top:.5em !important}' +
      // название и год под выделенным постером — на чёрной плашке, чтобы читались поверх обоев
      '.card.focus .card__title,.card.focus .card__age{background:#000;color:#fff !important;opacity:1 !important;border-radius:.3em;padding:.15em .45em;margin-left:-.1em;text-shadow:none}';
    document.head.appendChild(st);
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.object) return;
      var render = e.object.activity.render(), n = 0;
      var t = setInterval(function () {
        n++;
        try {
          render.find('.full-descr').each(function () { $(this).closest('.items-line').addClass('cp-descr'); });
          var rl = render.find('.full-start-new__rate-line').first(), re = render.find('.full-start-new__reactions').first();
          if (rl.length && re.length && !re.parent().is(rl)) { re.addClass('cp-react'); rl.append(re); }
          render.find('.button--book').remove();          // «Избранное» (закладки Lampa) не используется: вместо него «Мои фильмы / сериалы / аниме»
          render.find('.button--subscribe').remove();     // «Подписаться» — функция аккаунта CUB, без него только окно регистрации
          if (render.find('.cp-descr').length && n > 4) clearInterval(t);
        } catch (x) {}
        if (n > 16) clearInterval(t);
      }, 500);
    });
  }

  function start() { resetFlush(); hookRecorder(); hookFull(); hookVoices(); hookSourceMenu(); hookDescr(); }
  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
