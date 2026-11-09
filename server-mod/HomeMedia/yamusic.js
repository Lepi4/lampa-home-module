(function () {
  'use strict';
  if (window.plugin_yamusic_ready) return;
  window.plugin_yamusic_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;

  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M12 3v10.55A4 4 0 1 0 14 17V7h4V3h-6z"/></svg>';

  // ---------- вспомогательное ----------
  // Базовый контроллер экрана (не окно выбора/ввода): в него возвращаемся, когда наше окно закрылось
  var baseName = 'content';
  function ctrl() {
    try {
      var n = Lampa.Controller.enabled().name;
      if (n && n !== 'ym_card' && ['select', 'modal', 'input', 'keyboard', 'keybord', 'settings_component'].indexOf(n) < 0) baseName = n;
    } catch (e) {}
    return baseName;
  }

  function api(path, ok, fail, opts) {
    $.ajax($.extend({ url: BASE + path, dataType: 'json', timeout: 60000, cache: false, success: ok, error: function (x) { if (fail) fail(x); } }, opts || {}));
  }

  function fmt(sec) {
    sec = Math.max(0, Math.floor(sec || 0));
    var m = Math.floor(sec / 60), s = sec % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }

  // ---------- плеер (HTML5 audio, играет и пока листаешь Lampa) ----------
  var audio = new Audio();
  audio.preload = 'auto';
  var st = { queue: [], idx: -1, station: null, batch: null, liked: {}, fetching: false, started: false };

  var bar = null;
  function ensureBar() {
    if (bar) return;
    var css = document.createElement('style');
    css.textContent = '.ym-bar{position:fixed;left:1.2em;bottom:1.2em;z-index:9999;display:none;align-items:center;max-width:46%;' +
      'background:rgba(20,20,30,.88);color:#fff;border-radius:.6em;padding:.45em .8em .45em .45em;font-size:1.05em;pointer-events:none;box-shadow:0 .2em 1em rgba(0,0,0,.5)}' +
      '.ym-bar img{width:2.6em;height:2.6em;border-radius:.35em;margin-right:.7em;object-fit:cover;background:#333}' +
      '.ym-bar .ym-t{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:600}' +
      '.ym-bar .ym-a{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;opacity:.75;font-size:.85em}' +
      '.ym-bar .ym-p{height:.18em;background:rgba(255,255,255,.25);margin-top:.3em;border-radius:.1em}' +
      '.ym-bar .ym-p i{display:block;height:100%;width:0;background:#fc0;border-radius:.1em}' +
      '.ym-bar .ym-i{min-width:0;flex:1}';
    document.head.appendChild(css);
    bar = $('<div class="ym-bar"><img/><div class="ym-i"><div class="ym-t"></div><div class="ym-a"></div><div class="ym-p"><i></i></div></div></div>');
    $('body').append(bar);
  }

  function cur() { return st.queue[st.idx] || null; }

  function updateBar() {
    var t = cur();
    ensureBar();
    if (cardOpen) { bar.hide(); return; }
    if (!t) { bar.hide(); return; }
    bar.css('display', 'flex');
    bar.find('img').attr('src', t.cover || '');
    bar.find('.ym-t').text((audio.paused ? '⏸ ' : '♪ ') + t.title);
    bar.find('.ym-a').text(t.artists + ' · ' + fmt(audio.currentTime) + ' / ' + fmt(audio.duration || t.duration));
    var d = audio.duration || t.duration || 0;
    bar.find('.ym-p i').css('width', d ? Math.min(100, audio.currentTime / d * 100) + '%' : '0');
  }

  setInterval(function () { if (cur()) updateBar(); }, 1000);

  function feedback(type, track, seconds) {
    if (!st.station || !track) return;
    api('/ym/wave/feedback?station=' + encodeURIComponent(st.station) + '&type=' + type + '&track=' + encodeURIComponent(track.id) +
      (st.batch ? '&batch=' + encodeURIComponent(st.batch) : '') + (seconds ? '&seconds=' + Math.round(seconds) : ''), function () {});
  }

  function moreWave() {
    if (!st.station || st.fetching) return;
    st.fetching = true;
    var last = st.queue[st.queue.length - 1];
    api('/ym/wave?station=' + encodeURIComponent(st.station) + (last ? '&queue=' + encodeURIComponent(last.id) : ''), function (d) {
      st.fetching = false;
      st.batch = d.batch || st.batch;
      (d.tracks || []).forEach(function (t) { st.queue.push(t); });
    }, function () { st.fetching = false; });
  }

  function playIdx(i) {
    var t = st.queue[i];
    if (!t) return;
    var prev = cur();
    if (prev && st.station && audio.currentTime > 1) feedback(audio.currentTime >= (audio.duration || 0) - 2 ? 'trackFinished' : 'skip', prev, audio.currentTime);
    st.idx = i;
    audio.src = BASE + '/ym/stream/' + encodeURIComponent(t.id);
    var p = audio.play();
    if (p && p.catch) p.catch(function () {});
    if (st.station) feedback('trackStarted', t);
    updateBar();
    updateFull();
    paintNow();
    pushSession();
    if (st.station && st.queue.length - i <= 2) moreWave();
  }

  function next() {
    if (st.idx + 1 < st.queue.length) playIdx(st.idx + 1);
    else if (st.station) { moreWave(); noty('Загружаю следующие треки…'); }
    else { audio.pause(); updateBar(); }
  }

  function prevTrack() {
    if (audio.currentTime > 5) { audio.currentTime = 0; return; }
    if (st.idx > 0) playIdx(st.idx - 1);
  }

  function stop() {
    var t = cur();
    if (t && st.station) feedback('skip', t, audio.currentTime);
    audio.pause();
    audio.removeAttribute('src');
    st.queue = []; st.idx = -1; st.station = null;
    updateBar();
    paintNow();
    syncBackground();
  }

  function toggle() {
    if (!cur()) return;
    if (audio.paused) audio.play(); else audio.pause();
    updateBar();
    paintNow();
  }

  audio.addEventListener('ended', function () {
    var t = cur();
    if (t && st.station) feedback('trackFinished', t, audio.duration);
    next();
  });
  var errStreak = 0;
  audio.addEventListener('error', function () {
    if (!cur()) return;
    errStreak++;
    if (errStreak >= 3) {
      noty('Яндекс Музыка: треки не воспроизводятся. Проверьте аккаунт и подписку (Настройки)');
      errStreak = 0;
      audio.pause();
      return;
    }
    noty('Не удалось воспроизвести: ' + cur().title);
    setTimeout(next, 800);
  });
  audio.addEventListener('playing', function () { errStreak = 0; });
  // команды из системной медиа-сессии (кнопки пульта при свёрнутом приложении)
  window.__ymCmd = function (c) {
    if (c === 'next') next();
    else if (c === 'prev') prevTrack();
    else if (c === 'pause') { if (!audio.paused) audio.pause(); }
    else if (c === 'play') { if (cur() && audio.paused) audio.play(); }
    else if (c === 'toggle') toggle();
    else if (c === 'stop') stop();
    updateBar(); updateFull();
  };

  // Кнопка Play/Pause на пульте: 'next' — одно нажатие = следующий трек, 'pause' — одно = пауза, двойное = следующий
  function ppMode() {
    try { return localStorage.getItem('ym_pp') === 'pause' ? 'pause' : 'next'; } catch (e) { return 'next'; }
  }

  function pushSession() {
    try {
      if (!(window.AndroidJS && AndroidJS.setNowPlaying)) return;
      var t = cur();
      if (!t) { AndroidJS.setNowPlaying(JSON.stringify({ active: false })); return; }
      AndroidJS.setNowPlaying(JSON.stringify({
        active: true, title: t.title, artist: t.artists, album: t.album, cover: (t.cover || '').replace('400x400', '800x800'),
        playing: !audio.paused, pos: audio.currentTime || 0, dur: audio.duration || t.duration || 0, pp: ppMode()
      }));
    } catch (e) {}
  }

  // играем в фоне (Android TV): приложение не засыпает при сворачивании, пока идёт музыка
  function syncBackground() {
    try { if (window.AndroidJS && AndroidJS.setBackgroundAudio) AndroidJS.setBackgroundAudio(!!cur() && !audio.paused); } catch (e) {}
    pushSession();
  }
  audio.addEventListener('pause', function () { updateBar(); syncBackground(); });
  audio.addEventListener('playing', function () { updateBar(); syncBackground(); });
  audio.addEventListener('emptied', syncBackground);
  audio.addEventListener('loadedmetadata', pushSession);
  audio.addEventListener('seeked', pushSession);

  // видео из Lampa — музыку ставим на паузу
  try {
    Lampa.Player.listener.follow('start', function () { if (!audio.paused) audio.pause(); });
  } catch (e) {}

  // << и >> на пульте: разные приставки шлют их как перемотку, следующий трек или старый код Android
  function skipKey(k, c) {
    if (k === 'MediaTrackNext' || k === 'MediaFastForward' || k === 'MediaSkipForward' || c === 176 || c === 87 || c === 90 || c === 228 || c === 417 || c === 272) return 1;
    if (k === 'MediaTrackPrevious' || k === 'MediaRewind' || k === 'MediaSkipBackward' || c === 177 || c === 88 || c === 89 || c === 227 || c === 412 || c === 273) return -1;
    return 0;
  }

  // медиа-клавиши пульта
  window.addEventListener('keydown', function (e) {
    var k = e.key, c = e.keyCode;
    if (cardOpen) {
      var skip = skipKey(k, c);
      if (skip) { if (skip > 0) next(); else prevTrack(); e.preventDefault(); e.stopImmediatePropagation(); return; }
      if (dialogOpen()) return;
      if (c === 38 || c === 19 || k === 'ArrowUp') cardMove(-1);
      else if (c === 40 || c === 20 || k === 'ArrowDown') cardMove(1);
      else if (c === 37 || c === 21 || k === 'ArrowLeft') cardShift(-1);
      else if (c === 39 || c === 22 || k === 'ArrowRight') cardShift(1);
      else if (c === 13 || c === 23 || c === 66 || k === 'Enter') cardEnter();
      else if (k === 'MediaPlayPause' || c === 179 || c === 85) toggle();
      else if (k === 'MediaStop' || c === 178) { stop(); paintNow(); }
      else if ([4, 8, 27, 461, 10009].indexOf(c) >= 0 || k === 'Escape' || k === 'GoBack' || k === 'BrowserBack') cardBack();
      else return;
      e.preventDefault(); e.stopImmediatePropagation();
      return;
    }
    // полноэкранный экран: забираем клавиши себе
    if (fullOpen) {
      var stop = true;
      if (c === 37 || k === 'ArrowLeft') prevTrack();
      else if (c === 39 || k === 'ArrowRight') next();
      else if (c === 13 || k === 'Enter' || k === 'MediaPlayPause' || c === 179) toggle();
      else if (c === 40 || k === 'ArrowDown') { closeFull(); setTimeout(nowPlaying, 200); }
      else if (c === 38 || k === 'ArrowUp') { stop(); noty('Музыка остановлена'); closeFull(); }
      else if (skipKey(k, c) > 0) next();
      else if (skipKey(k, c) < 0) prevTrack();
      else if (c === 178 || k === 'MediaStop') { stop = true; closeFull(); }
      else if ([4, 8, 27, 461, 10009].indexOf(c) >= 0 || k === 'Escape' || k === 'GoBack' || k === 'BrowserBack') closeFull();
      e.preventDefault(); e.stopImmediatePropagation();
      updateFull(); updateBar();
      return;
    }
    if (!cur()) return;
    if (k === 'MediaPlayPause' || c === 179) {
      // двойное нажатие Play/Pause — следующий трек
      var now = Date.now();
      if (now - lastPP < 450) { toggle(); next(); lastPP = 0; } else { toggle(); lastPP = now; }
      e.preventDefault();
    }
    else if (skipKey(k, c) > 0) { next(); e.preventDefault(); }
    else if (skipKey(k, c) < 0) { prevTrack(); e.preventDefault(); }
    else if (k === 'MediaStop' || c === 178) { stop(); e.preventDefault(); }
  }, true);

  // ---------- полноэкранный «Сейчас играет»: ←/→ предыдущий/следующий, OK пауза, ↓ меню ----------
  var full = null, fullOpen = false, lastPP = 0;

  function ensureFull() {
    if (full) return;
    var css = document.createElement('style');
    css.textContent = '.ym-full{position:fixed;left:0;top:0;right:0;bottom:0;z-index:10000;display:none;flex-direction:column;align-items:center;justify-content:center;' +
      'background:radial-gradient(ellipse at center,#2a2540 0%,#0d0c14 80%);color:#fff;text-align:center}' +
      '.ym-full img{width:22em;height:22em;max-width:50vh;max-height:50vh;border-radius:.8em;object-fit:cover;box-shadow:0 1em 3em rgba(0,0,0,.6);background:#333}' +
      '.ym-full .ym-ft{font-size:2.2em;font-weight:700;margin-top:.7em;max-width:80%}' +
      '.ym-full .ym-fa{font-size:1.4em;opacity:.8;margin-top:.2em;max-width:80%}' +
      '.ym-full .ym-fp{width:60%;height:.35em;background:rgba(255,255,255,.2);border-radius:.2em;margin-top:1.2em}' +
      '.ym-full .ym-fp i{display:block;height:100%;width:0;background:#fc0;border-radius:.2em}' +
      '.ym-full .ym-ftm{font-size:1.1em;opacity:.7;margin-top:.5em}' +
      '.ym-full .ym-fh{position:absolute;bottom:1.5em;font-size:1.05em;opacity:.55}' +
      '.ym-full.ym-pip{flex-direction:row;justify-content:flex-start;text-align:left;padding:0 3vw;font-size:2.6vw}' +
      '.ym-full.ym-pip img{width:34vh;height:34vh;flex:none;max-width:34vh;max-height:34vh;margin-right:3vw;border-radius:.4em}' +
      '.ym-full.ym-pip .ym-ft{font-size:1.5em;margin-top:0;max-width:60vw}.ym-full.ym-pip .ym-fa{font-size:1em;max-width:60vw}' +
      '.ym-full.ym-pip .ym-fp{width:40vw;margin-top:.5em}.ym-full.ym-pip .ym-ftm,.ym-full.ym-pip .ym-fh{display:none}';
    document.head.appendChild(css);
    full = $('<div class="ym-full"><img/><div class="ym-ft"></div><div class="ym-fa"></div><div class="ym-fp"><i></i></div><div class="ym-ftm"></div>' +
      '<div class="ym-fh">← предыдущий · → следующий · OK пауза · ↑ стоп · ↓ меню · Назад закрыть</div></div>');
    $('body').append(full);
  }

  function updateFull() {
    if (!fullOpen && !pipMode) return;
    var t = cur();
    if (!t) { if (pipMode) return; return closeFull(); }
    var cover = (t.cover || '').replace('400x400', '800x800');
    if (full.find('img').attr('src') !== cover) full.find('img').attr('src', cover);
    full.find('.ym-ft').text((audio.paused ? '⏸ ' : '') + t.title);
    full.find('.ym-fa').text(t.artists + (t.album ? ' · ' + t.album : ''));
    var d = audio.duration || t.duration || 0;
    full.find('.ym-fp i').css('width', d ? Math.min(100, audio.currentTime / d * 100) + '%' : '0');
    full.find('.ym-ftm').text(fmt(audio.currentTime) + ' / ' + fmt(d) + (st.station ? ' · Моя волна' : ' · ' + (st.idx + 1) + ' из ' + st.queue.length));
  }

  function openFull() {
    if (!cur()) return;
    ctrl();
    ensureFull();
    fullOpen = true;
    full.css('display', 'flex');
    updateFull();
  }

  function closeFull() {
    fullOpen = false;
    if (full) full.hide();
    try { Lampa.Controller.toggle(ctrl()); } catch (e) {}
  }

  // Окно «картинка в картинке» (приложение свёрнуто, играет музыка): компактная карточка трека
  var pipMode = false;
  window.__ymPip = function (on) {
    pipMode = !!on;
    ensureFull();
    full.toggleClass('ym-pip', pipMode);
    if (pipMode) { full.css('display', 'flex'); updateFull(); }
    else if (!fullOpen) full.hide();
  };

  setInterval(function () {
    updateFull();
    if (!cardOpen || !card) return;
    card.css('z-index', dialogOpen() ? 1 : 10050);
    if (!dialogOpen()) focusCardController();
    paintNow();
  }, 500);

  // Системная «Назад» в приложении приходит как history.back(), а не как клавиша: перехватываем раньше Lampa
  function atRoot() {
    try {
      if (Lampa.Select.opened && Lampa.Select.opened()) return false;
      if ($('.modal').length) return false;
      // окна настроек, поиска, ввода и т.п. — не «корень»: «Назад» должна закрыть их, а не гаситься (иначе из настроек не выйти)
      if ($('body').hasClass('settings--open') || $('body').hasClass('search--open')) return false;
      var cn = Lampa.Controller.enabled().name || '';
      if (/settings|search|input|keyboard|keybord|select|modal|player|filter|extensions|plugins/i.test(cn)) return false;
      return Lampa.Activity.all().length <= 1;
    } catch (e) { return false; }
  }

  window.addEventListener('popstate', function (e) {
    if (cardOpen && !dialogOpen()) {
      e.stopImmediatePropagation();
      try { history.pushState(null, '', location.href); } catch (x) {}
      cardBack();
      return;
    }
    var swallow = false;
    // настройки и поиск закрываются контроллером Lampa (как по клавише Escape): popstate их сам не закрывает,
    // из-за чего из настроек нельзя было выйти кнопкой «Назад»
    if (!fullOpen && ($('body').hasClass('settings--open') || $('body').hasClass('search--open'))) {
      e.stopImmediatePropagation();
      try { history.pushState(null, '', location.href); } catch (x) {}
      try { Lampa.Controller.back(); } catch (x) {}
      return;
    }
    if (fullOpen) { closeFull(); swallow = true; }
    // серия «Назад»: на корне Lampa сначала останавливаем музыку, и только потом выходим
    else if (cur() && atRoot()) { stop(); noty('Музыка остановлена'); swallow = true; }
    // Lampa Home как домашний экран: Назад на главной не должно закрывать приложение
    else if (atRoot() && window.AndroidJS && AndroidJS.isHomeMode && AndroidJS.isHomeMode()) swallow = true;
    if (!swallow) return;
    e.stopImmediatePropagation();
    try { history.pushState(null, '', location.href); } catch (x) {} // вернуть съеденную запись истории
  }, true);

  function startQueue(tracks, index, station) {
    st.queue = tracks.slice();
    st.station = station || null;
    st.batch = null;
    playIdx(index || 0);
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

  function login(done) {
    if (card) card.css('z-index', 1);
    var prev = ctrl();
    api('/ym/auth/start', function (d) {
      if (!d.user_code) { noty('Яндекс: не удалось начать вход'); return; }
      var polling = true;
      var tick = function () {
        if (!polling) return;
        api('/ym/auth/poll', function (r) {
          if (r.state === 'ok') {
            polling = false;
            try { Lampa.Select.close(); } catch (e) {}
            noty('Яндекс Музыка подключена' + (r.account && r.account.login ? ': ' + r.account.login : ''));
            setTimeout(function () { done(); }, 400);
          } else if (r.state === 'pending') setTimeout(tick, 4000);
          else { polling = false; noty('Вход не удался: ' + (r.error || r.state)); }
        }, function () { setTimeout(tick, 5000); });
      };
      setTimeout(tick, 4000);
      Lampa.Select.show({
        title: 'Вход в Яндекс Музыку',
        items: [
          { title: 'Код: ' + d.user_code, subtitle: 'Откройте ' + d.verification_url + ' на телефоне или компьютере и введите этот код', wait: true },
          { title: 'Отмена', cancel: true }
        ],
        onBack: function () { polling = false; Lampa.Controller.toggle(prev); },
        onSelect: function (a) { if (a.cancel) { polling = false; Lampa.Controller.toggle(prev); } }
      });
    }, function () { noty('Яндекс: сервер не отвечает'); });
  }

  function withAuth(fn) {
    api('/ym/status', function (s) {
      if (s.authorized) fn(s); else login(function () { fn({}); });
    }, function () { noty('Яндекс Музыка: нет связи с сервером'); });
  }

  var PAGE = 150;

  function showTracks(title, tracks, station, back, from) {
    from = from || 0;
    var slice = tracks.slice(from, from + PAGE);
    var items = slice.map(function (t, i) {
      return {
        title: (t.available === false ? '✕ ' : '') + t.title,
        subtitle: t.artists + (t.album ? ' · ' + t.album : '') + (t.duration ? ' · ' + fmt(t.duration) : ''),
        idx: from + i
      };
    });
    if (from + PAGE < tracks.length) items.push({ title: 'Ещё…', subtitle: (tracks.length - from - PAGE) + ' треков', more: true });
    showSelect(title, items, back, function (a) {
      if (a.more) return showTracks(title, tracks, station, back, from + PAGE);
      startQueue(tracks, a.idx, station);
      Lampa.Controller.toggle(ctrl());
    });
  }

  function loadTracks(title, mode, back) {
    noty('Загрузка…');
    function accumulate(page, acc) {
      api('/ym/tracks?mode=' + encodeURIComponent(mode) + '&page=' + page, function (d) {
        acc = acc.concat(d.tracks || []);
        if (d.has_more && page < 9 && mode === 'likes') return accumulate(page + 1, acc);
        if (!acc.length) return noty('Ничего не найдено');
        if (cardOpen) {
          cardList = { title: title, tracks: acc };
          listIndex = 0;
          zone = 'list';
          paintList();
          paintFocus();
          return;
        }
        showTracks(title, acc, null, back);
      }, function () { noty('Не удалось загрузить: ' + title); });
    }
    accumulate(0, []);
  }

  function playStation(id, title) {
    noty('Запускаю «' + title + '»…');
    api('/ym/wave?station=' + encodeURIComponent(id), function (d) {
      if (!d.tracks || !d.tracks.length) return noty('«' + title + '» пока пуста');
      st.batch = d.batch;
      st.queue = d.tracks.slice();
      st.station = id;
      api('/ym/wave/feedback?station=' + encodeURIComponent(id) + '&type=radioStarted' + (d.batch ? '&batch=' + encodeURIComponent(d.batch) : ''), function () {});
      playIdx(0);
      if (cardOpen) { zone = 'ctrl'; ctrlIndex = 1; paintNow(); paintFocus(); }
      else Lampa.Controller.toggle(ctrl());
    }, function () { noty('Не удалось запустить «' + title + '»'); });
  }

  function playWave() { playStation('user:onyourwave', 'Моя волна'); }

  var STATIONS = [
    { id: 'genre:rusrock', title: 'Русский рок' },
    { id: 'genre:rock', title: 'Иностранный рок' },
    { id: 'genre:ruspop', title: 'Русская поп-музыка' },
    { id: 'genre:pop', title: 'Поп' },
    { id: 'genre:rusrap', title: 'Русский рэп' },
    { id: 'genre:rap', title: 'Рэп и хип-хоп' },
    { id: 'genre:metal', title: 'Метал' },
    { id: 'genre:electronics', title: 'Электроника' },
    { id: 'genre:jazz', title: 'Джаз' },
    { id: 'genre:dance', title: 'Танцевальная' },
    { id: 'genre:rusestrada', title: 'Русская эстрада' }
  ];

  function openStations() {
    showItems('Станции', STATIONS.map(function (s) {
      return { title: s.title, sub: '', run: function () { playStation(s.id, s.title); } };
    }));
  }

  function showItems(title, items) {
    cardList = { title: title, items: items || [] };
    listIndex = 0;
    zone = cardList.items.length ? 'list' : 'nav';
    paintList();
    paintFocus();
  }

  function playDay() {
    noty('Загружаю плейлист дня…');
    api('/ym/day', function (d) {
      var tracks = d.tracks || [];
      if (!tracks.length) return noty('Плейлист дня пуст');
      if (!cardOpen) return startQueue(tracks, 0, null);
      showItems(d.title || 'Плейлист дня', tracks.map(function (t, i) {
        return { title: (t.available === false ? '✕ ' : '') + t.title, sub: t.artists || '', run: function () { startQueue(tracks, i, null); zone = 'ctrl'; paintNow(); paintFocus(); } };
      }));
    }, function () { noty('Не удалось загрузить плейлист дня'); });
  }

  function openWave() {
    noty('Загружаю настройки волны…');
    api('/ym/wave/categories', function (d) {
      var groups = d.groups || [];
      var current = { diversity: d.diversity, moodEnergy: d.mood, language: d.language };
      var items = [{ title: 'Играть', sub: 'с текущими настройками', run: function () { playWave(); } }];
      groups.forEach(function (g) {
        items.push({ title: g.title, sub: g.currentName || '', run: function () { showWaveGroup(groups, current, g); } });
      });
      showItems('Моя волна', items);
    }, function () { showItems('Моя волна', [{ title: 'Играть', sub: '', run: function () { playWave(); } }]); });
  }

  function showWaveGroup(groups, current, group) {
    var items = [{ title: '← К настройкам', sub: '', run: function () { openWave(); } }];
    (group.values || []).forEach(function (v) {
      items.push({
        title: v.name, sub: v.value === group.current ? 'сейчас' : '',
        run: function () {
          var next = { diversity: current.diversity, mood: current.moodEnergy, language: current.language };
          if (group.id === 'diversity') next.diversity = v.value;
          else if (group.id === 'moodEnergy') next.mood = v.value;
          else next.language = v.value;
          api('/ym/wave/settings?diversity=' + encodeURIComponent(next.diversity || '') + '&mood=' + encodeURIComponent(next.mood || '') + '&language=' + encodeURIComponent(next.language || ''), function (r) {
            if (!r || r.ok === false) return noty('Не удалось сохранить настройку');
            playWave();
          }, function () { noty('Не удалось сохранить настройку'); });
        }
      });
    });
    showItems(group.title, items);
  }

  function search(back) {
    if (card) card.css('z-index', 1);
    Lampa.Input.edit({ title: 'Поиск в Яндекс Музыке', value: '', free: true, nosave: true }, function (val) {
      if (!val) { if (!cardOpen) Lampa.Controller.toggle(ctrl()); else restoreCard(); return; }
      loadTracks('Поиск: ' + val, 'search:' + val, back);
    });
  }

  function playlists(back) {
    api('/ym/playlists', function (d) {
      if (cardOpen) {
        cardList = { title: 'Плейлисты', playlists: d.playlists || [] };
        listIndex = 0;
        zone = cardList.playlists.length ? 'list' : 'nav';
        paintList();
        paintFocus();
        if (!cardList.playlists.length) noty('Плейлистов нет');
        return;
      }
      var items = (d.playlists || []).map(function (p) { return { title: p.title, subtitle: p.count + ' треков', p: p }; });
      if (!items.length) return noty('Плейлистов нет');
      showSelect('Плейлисты', items, back, function (a) {
        loadTracks(a.p.title, 'playlist:' + a.p.uid + ':' + a.p.kind, function () { playlists(back); });
      });
    }, function () { noty('Не удалось загрузить плейлисты'); });
  }

  function nowPlaying() {
    var t = cur();
    if (!t) return;
    var items = [
      { title: audio.paused ? '▶ Продолжить' : '⏸ Пауза', act: 'toggle' },
      { title: '⏭ Следующий', act: 'next' },
      { title: '⏮ Предыдущий', act: 'prev' },
      { title: st.liked[t.id] ? '♡ Убрать из «Мне нравится»' : '♥ Мне нравится', act: 'like' },
      { title: 'Очередь', subtitle: (st.queue.length - st.idx - 1) + ' далее', act: 'queue' },
      { title: '■ Остановить', act: 'stop' }
    ];
    showSelect(t.title + ' — ' + t.artists, items, null, function (a) {
      if (a.act === 'toggle') toggle();
      else if (a.act === 'next') next();
      else if (a.act === 'prev') prevTrack();
      else if (a.act === 'stop') stop();
      else if (a.act === 'like') {
        var on = !st.liked[t.id];
        api('/ym/like?id=' + encodeURIComponent(t.id + (t.album_id ? ':' + t.album_id : '')) + '&on=' + on, function () {
          st.liked[t.id] = on; noty(on ? 'Добавлено в «Мне нравится»' : 'Убрано');
        });
      } else if (a.act === 'queue') {
        return showTracks('Очередь', st.queue, st.station, nowPlaying, st.idx);
      }
      Lampa.Controller.toggle(ctrl());
    });
  }

  function settings(back) {
    if (card) card.css('z-index', 1);
    api('/ym/status', function (s) {
      var q = { lossless: 'Без потерь (FLAC)', hq: 'Высокое (AAC)', nq: 'Обычное (MP3)' };
      var items = [
        { title: 'Аккаунт', subtitle: s.authorized ? (s.login || 'подключён') + (s.plus ? ' · Плюс' : ' · без подписки') : 'не подключён', act: 'acc' },
        { title: 'Качество', subtitle: q[s.quality] || s.quality, act: 'q' },
        { title: 'Кнопка Play/Pause на пульте', subtitle: ppMode() === 'next' ? 'одно нажатие — следующий трек, двойное — пауза' : 'одно нажатие — пауза, двойное — следующий трек', act: 'pp' }
      ];
      if (s.authorized) items.push({ title: 'Выйти из аккаунта', act: 'out' });
      showSelect('Яндекс Музыка · настройки', items, back, function (a) {
        if (a.act === 'pp') {
          try { localStorage.setItem('ym_pp', ppMode() === 'next' ? 'pause' : 'next'); } catch (e) {}
          pushSession();
          noty(ppMode() === 'next' ? 'Play/Pause: одно нажатие — следующий трек' : 'Play/Pause: одно нажатие — пауза');
          settings(back);
        } else if (a.act === 'q') {
          showSelect('Качество', Object.keys(q).map(function (k) { return { title: q[k], k: k, selected: s.quality === k }; }), function () { settings(back); }, function (b) {
            api('/ym/quality?q=' + b.k, function () { noty('Качество: ' + q[b.k]); settings(back); });
          });
        } else if (a.act === 'out') {
          api('/ym/auth/reset', function () { noty('Вы вышли из Яндекс Музыки'); if (!cardOpen) Lampa.Controller.toggle(ctrl()); });
        } else if (a.act === 'acc' && !s.authorized) login(function () { settings(back); });
        else settings(back);
      });
    });
  }

  var card = null, cardOpen = false, cardFromShell = false, ymPushed = false, ymClosing = false;
  var zone = 'nav', navIndex = 0, listIndex = 0, ctrlIndex = 0;
  var cardList = null;
  var NAV = [
    { id: 'wave', title: 'Моя волна', sub: 'настроение, характер и язык' },
    { id: 'day', title: 'Плейлист дня', sub: 'подборка на сегодня' },
    { id: 'stations', title: 'Станции', sub: 'рок, поп, рэп и другое' },
    { id: 'likes', title: 'Мне нравится' },
    { id: 'playlists', title: 'Плейлисты' },
    { id: 'chart', title: 'Чарт', sub: 'популярное сейчас' },
    { id: 'search', title: 'Поиск' },
    { id: 'settings', title: 'Настройки' }
  ];
  var CTRLS = [
    { cmd: 'prev', title: 'Предыдущий' },
    { cmd: 'toggle', title: 'Пауза' },
    { cmd: 'next', title: 'Следующий' },
    { cmd: 'stop', title: 'Стоп' },
    { cmd: 'like', title: 'Нравится' }
  ];

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
    });
  }

  function dialogOpen() {
    try {
      if (typeof Lampa.Select.opened === 'function' && Lampa.Select.opened()) return true;
      if ($('body').hasClass('selectbox--open') || $('body').hasClass('settings--open') || $('body').hasClass('search--open') || $('body').hasClass('modal--open')) return true;
      var name = Lampa.Controller.enabled().name || '';
      if (name === 'input' || name === 'keyboard' || name === 'keybord') return true;
    } catch (e) {}
    return false;
  }

  function bindCardController() {
    if (window.__ymCardController) return;
    window.__ymCardController = true;
    try {
      Lampa.Controller.add('ym_card', {
        toggle: function () {},
        up: function () { if (cardOpen && !dialogOpen()) cardMove(-1); },
        down: function () { if (cardOpen && !dialogOpen()) cardMove(1); },
        left: function () { if (cardOpen && !dialogOpen()) cardShift(-1); },
        right: function () { if (cardOpen && !dialogOpen()) cardShift(1); },
        enter: function () { if (cardOpen && !dialogOpen()) cardEnter(); },
        back: function () { if (cardOpen) cardBack(); },
        gone: function () {},
        destroy: function () {}
      });
    } catch (e) {}
  }

  function focusCardController() {
    if (!cardOpen || dialogOpen()) return;
    bindCardController();
    try {
      if (Lampa.Controller.enabled().name !== 'ym_card') Lampa.Controller.toggle('ym_card');
    } catch (e) {}
  }

  function fromLauncher() {
    try {
      if (window.__ymFromShell) return true;
      if (!window.__mc_ret) return false;
      var ac = Lampa.Activity.active();
      return !ac || ac.component === 'main' || ac.component === 'ym_card';
    } catch (e) { return false; }
  }

  function ensureCard() {
    if (card) return;
    var css = document.createElement('style');
    css.textContent = '.ym-card{position:fixed;left:0;top:0;right:0;bottom:0;height:100%;overflow:hidden;z-index:10050;display:none;background:#12121a;color:#fff;font-size:1.15em}' +
      '.ym-card .ym-wrap{display:flex;height:100%;max-height:100%;overflow:hidden}' +
      '.ym-card .ym-nav{width:22em;flex:none;background:#1b1b27;padding:1em .7em;overflow:auto;box-sizing:border-box}' +
      '.ym-card .ym-brand{font-size:1.35em;font-weight:700;margin:0 .5em .15em}.ym-card .ym-brand small{display:block;font-size:.55em;font-weight:400;opacity:.6;margin-top:.25em}' +
      '.ym-card .ym-hit{padding:.5em .7em;border-radius:.45em;margin:.1em 0}' +
      '.ym-card .ym-hit small{display:block;opacity:.65;font-size:.78em;margin-top:.1em}' +
      '.ym-card .ym-hit.on{background:#fc0;color:#161616}.ym-card .ym-hit.on small{opacity:.8}' +
      '.ym-card .ym-main{flex:1;min-width:0;min-height:0;height:100%;overflow:hidden;display:flex;flex-direction:column;padding:1em 1.2em 6.4em;box-sizing:border-box}' +
      '.ym-card .ym-now{display:flex;gap:1em;align-items:center;flex:none}' +
      '.ym-card .ym-cover{width:8em;height:8em;flex:none;border-radius:.55em;object-fit:cover;background:#333;display:flex;align-items:center;justify-content:center;font-size:2.4em}' +
      '.ym-card .ym-title{font-size:1.45em;font-weight:700;line-height:1.2}.ym-card .ym-sub{opacity:.75;margin-top:.2em}' +
      '.ym-card .ym-line{height:.28em;background:rgba(255,255,255,.22);border-radius:.2em;margin-top:.55em}.ym-card .ym-line i{display:block;height:100%;width:0;background:#fc0}' +
      '.ym-card .ym-time{opacity:.7;margin-top:.3em;font-size:.9em}' +
      '.ym-card .ym-ctrls{position:absolute;left:23.2em;right:1.1em;bottom:1.7em;display:flex;gap:.45em;z-index:2}' +
      '.ym-card .ym-ctrls .ym-hit{flex:1;margin:0;text-align:center;background:#2c2c3c;color:#fff;font-size:1.15em;font-weight:700;padding:.62em .25em;border-radius:.4em}' +
      '.ym-card .ym-ctrls .ym-hit.on{background:#fc0;color:#161616}' +
      '.ym-card .ym-list{flex:1;min-height:0;overflow:auto;margin-top:.7em}.ym-card .ym-list h3{margin:.2em 0 .4em;font-size:1.1em}' +
      '.ym-card .ym-row{display:flex;justify-content:space-between;gap:1em}.ym-card .ym-row b{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
      '.ym-card .ym-row span{flex:none;opacity:.65}.ym-card .ym-foot{position:absolute;left:23.6em;right:1.3em;bottom:.25em;margin:0;opacity:.45;font-size:.8em}';
    document.head.appendChild(css);
    card = $('<div class="ym-card"><div class="ym-wrap"><div class="ym-nav"><div class="ym-brand">Яндекс Музыка<small class="ym-foot-note"></small></div><div class="ym-nav-items"></div></div>' +
      '<div class="ym-main"><div class="ym-now"><div class="ym-cover">♫</div><div class="ym-meta"><div class="ym-title"></div><div class="ym-sub"></div><div class="ym-line"><i></i></div><div class="ym-time"></div></div></div><div class="ym-list"></div><div class="ym-ctrls"></div><div class="ym-foot"></div></div></div></div>');
    $('body').append(card);
  }

  function paintNav() {
    if (!card) return;
    card.find('.ym-foot-note').text(cardFromShell ? 'Назад — в лаунчер' : 'Назад — закрыть');
    card.find('.ym-nav-items').html(NAV.map(function (item, i) {
      return '<div class="ym-hit" data-zone="nav" data-i="' + i + '">' + esc(item.title) + (item.sub ? '<small>' + esc(item.sub) + '</small>' : '') + '</div>';
    }).join(''));
  }

  function paintNow() {
    if (!cardOpen || !card) return;
    var t = cur();
    var cover = card.find('.ym-cover');
    if (!t) {
      if (cover.is('img')) cover.replaceWith('<div class="ym-cover">♫</div>');
      card.find('.ym-title').text('Ничего не играет');
      card.find('.ym-sub').text('Выберите раздел слева');
      card.find('.ym-line i').css('width', '0');
      card.find('.ym-time').text('');
      paintCtrls(null);
      return;
    }
    var src = (t.cover || '').replace('400x400', '800x800');
    if (!cover.is('img')) cover.replaceWith('<img class="ym-cover" alt="">');
    var img = card.find('.ym-cover');
    if (img.attr('src') !== src) img.attr('src', src);
    card.find('.ym-title').text(t.title);
    card.find('.ym-sub').text(t.artists + (t.album ? ' · ' + t.album : ''));
    var d = audio.duration || t.duration || 0;
    card.find('.ym-line i').css('width', d ? Math.min(100, audio.currentTime / d * 100) + '%' : '0');
    card.find('.ym-time').text(fmt(audio.currentTime) + ' / ' + fmt(d));
    paintCtrls(t);
  }

  function paintCtrls(t) {
    var ctrls = card.find('.ym-ctrls');
    if (!ctrls.children().length) {
      ctrls.html(CTRLS.map(function (b, i) {
        return '<div class="ym-hit" data-zone="ctrl" data-i="' + i + '"></div>';
      }).join(''));
    }
    ctrls.find('[data-i="0"]').text('Предыдущий');
    ctrls.find('[data-i="1"]').text(!t || audio.paused ? 'Слушать' : 'Пауза');
    ctrls.find('[data-i="2"]').text('Следующий');
    ctrls.find('[data-i="3"]').text('Стоп');
    ctrls.find('[data-i="4"]').text(t && st.liked[t.id] ? 'В любимых' : 'Нравится');
  }

  function paintList() {
    if (!card) return;
    var box = card.find('.ym-list');
    if (!cardList) { box.html('<div class="ym-sub">Моя волна, плейлист дня, плейлисты и чарт — слева.</div>'); return; }
    var html = '<h3>' + esc(cardList.title) + '</h3>';
    if (cardList.items) {
      if (!cardList.items.length) html += '<div class="ym-sub">Пусто</div>';
      cardList.items.forEach(function (it, i) {
        html += '<div class="ym-hit ym-row" data-zone="list" data-i="' + i + '"><b>' + esc(it.title) + '</b><span>' + esc(it.sub || '') + '</span></div>';
      });
      box.html(html);
      return;
    }
    if (cardList.playlists) {
      if (!cardList.playlists.length) html += '<div class="ym-sub">Плейлистов нет</div>';
      cardList.playlists.forEach(function (p, i) {
        html += '<div class="ym-hit ym-row" data-zone="list" data-i="' + i + '"><b>' + esc(p.title) + '</b><span>' + (p.count || 0) + '</span></div>';
      });
    } else {
      (cardList.tracks || []).forEach(function (t, i) {
        html += '<div class="ym-hit ym-row" data-zone="list" data-i="' + i + '"><b>' + esc((t.available === false ? '✕ ' : '') + t.title) + '</b><span>' + esc(t.artists || '') + (t.duration ? ' · ' + fmt(t.duration) : '') + '</span></div>';
      });
    }
    box.html(html);
  }

  function paintFocus() {
    if (!card) return;
    card.find('.ym-hit').removeClass('on');
    var index = zone === 'nav' ? navIndex : zone === 'ctrl' ? ctrlIndex : listIndex;
    var el = card.find('.ym-hit[data-zone="' + zone + '"][data-i="' + index + '"]');
    if (!el.length) { zone = 'nav'; el = card.find('.ym-hit[data-zone="nav"][data-i="' + navIndex + '"]'); }
    el.addClass('on');
    if (el[0] && el[0].scrollIntoView) el[0].scrollIntoView({ block: 'nearest' });
  }

  function goZone(next) {
    zone = next;
    paintFocus();
  }

  function cardMove(delta) {
    if (zone === 'ctrl') {
      if (delta < 0) goZone(card.find('.ym-hit[data-zone="list"]').length ? 'list' : 'nav');
      return;
    }
    if (zone === 'list') {
      var rows = card.find('.ym-hit[data-zone="list"]');
      if (!rows.length) return;
      if (delta > 0 && listIndex >= rows.length - 1) { goZone('ctrl'); return; }
      if (delta < 0 && listIndex <= 0) return;
      listIndex += delta;
      paintFocus();
      return;
    }
    navIndex = (navIndex + delta + NAV.length) % NAV.length;
    paintFocus();
  }

  function cardShift(dir) {
    if (zone === 'ctrl') {
      var n = card.find('.ym-hit[data-zone="ctrl"]').length || CTRLS.length;
      if (dir > 0 && ctrlIndex < n - 1) { ctrlIndex++; paintFocus(); return; }
      if (dir < 0 && ctrlIndex > 0) { ctrlIndex--; paintFocus(); return; }
      if (dir < 0) goZone('nav');
      return;
    }
    if (zone === 'list') {
      if (dir < 0) goZone('nav');
      else goZone('ctrl');
      return;
    }
    if (dir > 0) goZone(card.find('.ym-hit[data-zone="list"]').length ? 'list' : 'ctrl');
  }

  function cardCmd(cmd) {
    var t = cur();
    if (cmd === 'prev') prevTrack();
    else if (cmd === 'toggle') toggle();
    else if (cmd === 'next') next();
    else if (cmd === 'stop') { stop(); noty('Музыка остановлена'); }
    else if (cmd === 'like' && t) {
      var on = !st.liked[t.id];
      api('/ym/like?id=' + encodeURIComponent(t.id + (t.album_id ? ':' + t.album_id : '')) + '&on=' + on, function () {
        st.liked[t.id] = on; paintNow(); noty(on ? 'Добавлено в «Мне нравится»' : 'Убрано');
      });
    }
    paintNow();
    paintFocus();
  }

  function cardAct(id) {
    if (id === 'wave') return openWave();
    if (id === 'day') return playDay();
    if (id === 'stations') return openStations();
    if (id === 'likes') return loadTracks('Мне нравится', 'likes', openCard);
    if (id === 'playlists') return playlists(openCard);
    if (id === 'chart') return loadTracks('Чарт', 'chart', openCard);
    if (id === 'search') return search(openCard);
    if (id === 'settings') return settings(restoreCard);
  }

  function cardEnter() {
    if (zone === 'nav') return cardAct(NAV[navIndex].id);
    if (zone === 'ctrl') return cardCmd(CTRLS[ctrlIndex].cmd);
    if (!cardList) return;
    if (cardList.items) {
      var item = cardList.items[listIndex];
      if (item && item.run) item.run();
      return;
    }
    if (cardList.playlists) {
      var p = cardList.playlists[listIndex];
      if (p) loadTracks(p.title, 'playlist:' + p.uid + ':' + p.kind, openCard);
      return;
    }
    if (cardList.tracks && cardList.tracks[listIndex]) {
      startQueue(cardList.tracks, listIndex, null);
      zone = 'ctrl';
      paintNow();
      paintFocus();
    }
  }

  function hookBack() {
    var current = window.__homeBack;
    if (current && current.__ym) return;
    var prev = current;
    var wrapped = function () {
      if (cardOpen) { cardBack(); return; }
      if (typeof prev === 'function') return prev();
      try { history.back(); } catch (e) {}
    };
    wrapped.__ym = true;
    window.__homeBack = wrapped;
  }

  // Лаунчер прячется только когда Lampa открыла не главную страницу. Карточка — слой поверх главной,
  // поэтому без своего экрана плитка минуту показывает «Открываю» и затем «Не удалось открыть раздел».
  function YmActivity() {
    var root = $('<div class="ym-activity"></div>');
    this.create = function () { try { this.activity.loader(false); } catch (e) {} };
    this.start = function () { focusCardController(); };
    this.pause = function () {};
    this.stop = function () {};
    this.destroy = function () { try { root.remove(); } catch (e) {} };
    this.render = function () { return root; };
  }
  try { Lampa.Component.add('ym_card', YmActivity); } catch (e) {}

  function screenSpec() {
    return { url: '', title: 'Яндекс Музыка', component: 'ym_card', page: 1, stamp: Date.now() };
  }

  function ensureShellActivity() {
    try {
      var ac = Lampa.Activity.active();
      if (ac && ac.component === 'ym_card' && ac === window.__mc_nav_origin) {
        try { Lampa.Activity.replace(screenSpec()); } catch (e1) { Lampa.Activity.push(screenSpec()); }
      } else if (!(ac && ac.component === 'ym_card')) Lampa.Activity.push(screenSpec());
      ymPushed = true;
    } catch (e) { return; }
    var n = 0;
    var t = setInterval(function () {
      n++;
      try {
        var cur = Lampa.Activity.active();
        if (cur && cur.component === 'ym_card' && cur.activity) cur.activity.is_started = true;
      } catch (e2) {}
      if (n > 20) clearInterval(t);
    }, 100);
  }

  function leaveCard() {
    if (ymClosing) return;
    ymClosing = true;
    var exitShell = cardFromShell;
    var pushed = ymPushed;
    cardOpen = false;
    cardFromShell = false;
    ymPushed = false;
    cardList = null;
    stop();
    if (card) card.hide();
    if (pushed) {
      try {
        var ac = Lampa.Activity.active();
        if (ac && ac.component === 'ym_card') Lampa.Activity.back();
      } catch (e) {}
    }
    ymClosing = false;
    if (exitShell) {
      try { if (window.AndroidJS && AndroidJS.exit) { AndroidJS.exit(); return; } } catch (e2) {}
    }
    try { Lampa.Controller.toggle(baseName === 'ym_card' ? 'content' : ctrl()); } catch (e3) {}
  }

  function restoreCard() {
    if (!card || !cardOpen) return;
    card.css('z-index', 10050);
    paintFocus();
    focusCardController();
  }

  function cardBack() {
    if (dialogOpen()) { try { Lampa.Controller.back(); } catch (e) {} return; }
    if (zone !== 'nav') { zone = 'nav'; paintFocus(); return; }
    leaveCard();
  }

  function openCard() {
    hookBack();
    var fromShell = fromLauncher();
    if (fromShell || !cardOpen) cardFromShell = fromShell;
    window.__ymFromShell = 0;
    ensureCard();
    cardOpen = true;
    zone = 'nav';
    navIndex = 0;
    cardList = null;
    card.css('display', 'flex');
    paintNav();
    paintNow();
    paintList();
    paintFocus();
    if (bar) bar.hide();
    card.css('z-index', 10050);
    focusCardController();
    if (cardFromShell) ensureShellActivity();
    withAuth(function () {});
  }

  function openMenu() {
    openCard();
  }

  // ---------- меню ----------
  function addMenu() {
    if ($('.menu .menu__list [data-action="yamusic"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="yamusic"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Яндекс Музыка</div></li>');
    btn.on('hover:enter', openMenu);
    $('.menu .menu__list').eq(0).append(btn);
    if (window.__kp_placeMenu) window.__kp_placeMenu();
  }

  window.__ym = { openFull: openFull, st: st, audio: audio, playWave: playWave, openMenu: openMenu, openCard: openCard, nowPlaying: nowPlaying, ctrl: ctrl };

  function start() {
    addMenu();
    setTimeout(addMenu, 2000);
    setTimeout(addMenu, 6000);
  }

  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
