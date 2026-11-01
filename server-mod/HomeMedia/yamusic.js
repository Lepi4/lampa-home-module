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
      if (n && ['select', 'modal', 'input', 'settings_component'].indexOf(n) < 0) baseName = n;
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
    syncBackground();
  }

  function toggle() {
    if (!cur()) return;
    if (audio.paused) audio.play(); else audio.pause();
    updateBar();
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

  // медиа-клавиши пульта
  window.addEventListener('keydown', function (e) {
    var k = e.key, c = e.keyCode;
    // полноэкранный экран: забираем клавиши себе
    if (fullOpen) {
      var stop = true;
      if (c === 37 || k === 'ArrowLeft') prevTrack();
      else if (c === 39 || k === 'ArrowRight') next();
      else if (c === 13 || k === 'Enter' || k === 'MediaPlayPause' || c === 179) toggle();
      else if (c === 40 || k === 'ArrowDown') { closeFull(); setTimeout(nowPlaying, 200); }
      else if (c === 38 || k === 'ArrowUp') { stop(); noty('Музыка остановлена'); closeFull(); }
      else if (c === 176 || k === 'MediaTrackNext') next();
      else if (c === 177 || k === 'MediaTrackPrevious') prevTrack();
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
    else if (k === 'MediaTrackNext' || c === 176) { next(); e.preventDefault(); }
    else if (k === 'MediaTrackPrevious' || c === 177) { prevTrack(); e.preventDefault(); }
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

  setInterval(updateFull, 500);

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
        showTracks(title, acc, null, back);
      }, function () { noty('Не удалось загрузить: ' + title); });
    }
    accumulate(0, []);
  }

  function playWave() {
    noty('Запускаю «Мою волну»…');
    api('/ym/wave?station=user:onyourwave', function (d) {
      if (!d.tracks || !d.tracks.length) return noty('Волна пока пуста');
      st.batch = d.batch;
      st.queue = d.tracks.slice();
      st.station = 'user:onyourwave';
      api('/ym/wave/feedback?station=user:onyourwave&type=radioStarted' + (d.batch ? '&batch=' + encodeURIComponent(d.batch) : ''), function () {});
      playIdx(0);
      Lampa.Controller.toggle(ctrl());
    }, function () { noty('Не удалось запустить волну'); });
  }

  function search(back) {
    Lampa.Input.edit({ title: 'Поиск в Яндекс Музыке', value: '', free: true, nosave: true }, function (val) {
      if (!val) return Lampa.Controller.toggle(ctrl());
      loadTracks('Поиск: ' + val, 'search:' + val, back);
    });
  }

  function playlists(back) {
    api('/ym/playlists', function (d) {
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
          api('/ym/auth/reset', function () { noty('Вы вышли из Яндекс Музыки'); Lampa.Controller.toggle(ctrl()); });
        } else if (a.act === 'acc' && !s.authorized) login(function () { settings(back); });
        else settings(back);
      });
    });
  }

  function openMenu() {
    ctrl();
    withAuth(function () {
      var items = [];
      if (cur()) {
        items.push({ title: 'Сейчас играет', subtitle: cur().title + ' — ' + cur().artists, act: 'now' });
        items.push({ title: '■ Остановить музыку', act: 'stop' });
      }
      items.push({ title: 'Моя волна', subtitle: 'бесконечное радио по вашим вкусам', act: 'wave' });
      items.push({ title: 'Мне нравится', act: 'likes' });
      items.push({ title: 'Плейлисты', act: 'playlists' });
      items.push({ title: 'Чарт', subtitle: 'популярное сейчас', act: 'chart' });
      items.push({ title: 'Поиск', act: 'search' });
      items.push({ title: 'Настройки', act: 'settings' });
      showSelect('Яндекс Музыка', items, null, function (a) {
        var back = openMenu;
        if (a.act === 'now') openFull();
        else if (a.act === 'stop') { stop(); noty('Музыка остановлена'); Lampa.Controller.toggle(ctrl()); }
        else if (a.act === 'wave') playWave();
        else if (a.act === 'likes') loadTracks('Мне нравится', 'likes', back);
        else if (a.act === 'playlists') playlists(back);
        else if (a.act === 'chart') loadTracks('Чарт', 'chart', back);
        else if (a.act === 'search') search(back);
        else if (a.act === 'settings') settings(back);
      });
    });
  }

  // ---------- меню ----------
  function addMenu() {
    if ($('.menu .menu__list [data-action="yamusic"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="yamusic"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Яндекс Музыка</div></li>');
    btn.on('hover:enter', openMenu);
    $('.menu .menu__list').eq(0).append(btn);
    if (window.__kp_placeMenu) window.__kp_placeMenu();
  }

  window.__ym = { openFull: openFull, st: st, audio: audio, playWave: playWave, openMenu: openMenu, nowPlaying: nowPlaying, ctrl: ctrl };

  function start() {
    addMenu();
    setTimeout(addMenu, 2000);
    setTimeout(addMenu, 6000);
  }

  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
