(function () {
  'use strict';
  if (!window.HomeUser || !HomeUser.owner || !HomeUser.owner.profile || HomeUser.owner.profile.is_child) return;
  if (window.plugin_vk_ready) return;
  window.plugin_vk_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;
  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M4 7h3.2c.3 2.4 1.4 4 3.3 4.6V7h3v4.2c1.7-.2 3-1.4 3.5-4.2H20c-.6 3.6-2.4 5.6-4.2 6.4C18 14.2 19.6 16 20.4 19h-3.2c-.6-2.2-1.8-3.8-3.7-4.1V19h-3v-4.4C8.2 15 6.6 16.8 6 19H3.2C4.2 15.6 6 13.2 8.2 12.2 6.2 11.2 4.6 9.4 4 7z"/></svg>';

  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }
  function api(path, ok, fail) {
    return $.ajax({ url: BASE + path, dataType: 'json', timeout: 25000, cache: false, success: ok, error: function (x) { if (fail) fail(x); } });
  }
  function maxH() { try { var v = parseInt(localStorage.getItem('home_maxh_v1'), 10); return isNaN(v) || v <= 0 ? 1080 : v; } catch (e) { return 1080; } }
  function ctrl() {
    try { var n = Lampa.Controller.enabled().name; return n && n !== 'select' && n !== 'modal' ? n : 'content'; }
    catch (e) { return 'content'; }
  }
  function nf(n) {
    n = parseInt(n, 10) || 0;
    if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + ' млн';
    if (n >= 1e3) return Math.round(n / 1e3) + ' тыс.';
    return n ? String(n) : '';
  }
  function fmt(sec) {
    sec = Math.max(0, Math.floor(sec || 0));
    var m = Math.floor(sec / 60), s = sec % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  var playGen = 0, playXhr = null, playProgressXhr = null;
  function cancelPlay() {
    playGen++;
    try { if (playXhr) playXhr.abort(); } catch (e) {}
    try { if (playProgressXhr) playProgressXhr.abort(); } catch (e) {}
    playXhr = playProgressXhr = null;
  }
  function play(id, title) {
    cancelPlay();
    var gen = playGen;
    var active = null;
    try { active = window.Lampa && Lampa.Activity ? Lampa.Activity.active() : null; } catch (e) {}
    var owner = window.HomeUser ? JSON.stringify(HomeUser.owner) : '';
    function stale() {
      if (gen !== playGen) return true;
      if (window.HomeUser && JSON.stringify(HomeUser.owner) !== owner) return true;
      try { if (window.Lampa && Lampa.Activity && active !== Lampa.Activity.active()) return true; } catch (e) {}
      return false;
    }
    var back = ctrl();
    playXhr = api('/vk/video?id=' + encodeURIComponent(id) + '&maxh=' + maxH(), function (d) {
      playXhr = null;
      if (stale()) return;
      if (!d || !d.video) return noty('VK: нет подходящего MP4');
      var data = { url: d.video, title: title || d.title || 'VK Видео', headers: d.headers || {}, home_player: true, home_timeline_only: true, home_vk_id: d.id, home_base: BASE };
      function launch(progress) {
        if (stale()) return;
        if (window.HomeUser && window.Lampa && Lampa.Utils && Lampa.Utils.hash) {
          var hash = String(Lampa.Utils.hash('vk:' + d.id));
          data.audio_key = 'vk_' + d.id;
          data.tl_meta = JSON.stringify([{ id: 'vk-' + d.id, h: hash, c: 'vk_' + d.id }]);
          data.timeline = { hash: hash, time: (progress && progress.position) || 0, duration: d.duration || (progress && progress.duration) || 0, percent: (progress && progress.percent) || 0 };
        }
        try { Lampa.Player.play(data); } catch (e) { noty('VK: не удалось запустить плеер'); }
        setTimeout(function () { if (stale()) return; try { Lampa.Controller.toggle(back); } catch (e) {} }, 700);
      }
      if (window.HomeUser) {
        playProgressXhr = api('/vk/progress?id=' + encodeURIComponent(d.id), function (p) { playProgressXhr = null; if (!stale()) launch(p); }, function () { playProgressXhr = null; if (!stale()) launch(null); });
      } else launch(null);
    }, function (x) {
      playXhr = null;
      if (stale()) return;
      var err = x && x.responseJSON && x.responseJSON.error;
      noty(err === 'no_supported_mp4' ? 'VK: нет MP4 в пределах качества' : err === 'rate_limited' ? 'VK: слишком много запросов' : 'VK: ролик не открылся');
    });
  }

  function show(title, items, onPick) {
    if (!items.length) { noty('Ничего не найдено'); return; }
    Lampa.Select.show({
      title: title,
      items: items,
      onBack: function () { Lampa.Controller.toggle(ctrl()); },
      onSelect: function (a) { setTimeout(function () { onPick(a); }, 200); }
    });
  }

  function markShell() {
    if (window.__mc_ret) { window.__mc_base = Lampa.Activity.all().length; window.__mc_ret = 2; }
  }
  function openCards(title, vk) {
    Lampa.Activity.push({ url: '', title: title, component: 'vk_list', page: 1, vk: vk });
    if (window.__mc_ret === 1 || window.__mc_ret === 3) markShell();
  }
  function openAlbum(id, title) { openCards(title || 'Плейлист VK', { mode: 'album', id: id }); }
  function openAlbums() { openCards('VK Видео', { mode: 'albums' }); }
  function openChannel(id, title) { openCards(title || 'Канал VK', { mode: 'channel', id: id }); }
  function openChannels() { openCards('Каналы VK', { mode: 'channels' }); }
  function feed() { openCards('Мой VK', { mode: 'feed' }); }
  function followChannel(id, on, ok) {
    if (on) $.ajax({ url: BASE + '/vk/channels/remove?id=' + encodeURIComponent(id), method: 'POST', timeout: 20000, success: ok, error: function () { noty('Не удалось отписаться'); } });
    else $.ajax({ url: BASE + '/vk/channels/add', method: 'POST', contentType: 'application/json', data: JSON.stringify({ id: id }), timeout: 20000, success: ok, error: function () { noty('Не удалось подписаться'); } });
  }

  function savePack(name) {
    try { HomeUser.owner.profile.settings.vk_profile = name; } catch (e) {}
    $.ajax({ url: BASE + '/home/profiles/me/settings', type: 'POST', contentType: 'application/json', data: JSON.stringify({ vk_profile: name }), timeout: 8000 });
  }
  function packs() {
    api('/vk/profiles', function (d) {
      var cur = d.current;
      var items = (d.profiles || []).map(function (p) {
        return { title: (p.name === cur ? '✓ ' : '') + (p.label || p.name), subtitle: (p.subs || 0) + ' плейлистов', name: p.name, label: p.label || p.name };
      });
      items.push({ title: '＋ Новый набор', act: 'new' });
      show('Набор плейлистов', items, function (a) {
        if (a.act === 'new') {
          Lampa.Input.edit({ title: 'Имя набора', value: '', free: true, nosave: true }, function (v) {
            v = (v || '').replace(/\s+/g, ' ').trim();
            if (!v) return;
            api('/vk/profiles?add=' + encodeURIComponent(v), function (d2) {
              var created = ((d2 && d2.profiles) || []).filter(function (p) { return (p.label || '') === v; })[0];
              if (!created) { noty('Имя: 1–32 символа, без / ? & #'); return; }
              savePack(created.name); noty('Набор: ' + created.label);
            }, function () { noty('Не удалось создать набор'); });
          });
          return;
        }
        savePack(a.name); noty('Набор: ' + a.label);
      });
    }, function () { noty('Наборы не открылись'); });
  }

  function followAlbum(id, on, title, ok) {
    if (on) $.ajax({ url: BASE + '/vk/subs/remove?id=' + encodeURIComponent(id), method: 'POST', timeout: 20000, success: ok, error: function () { noty('Не удалось отписаться'); } });
    else $.ajax({ url: BASE + '/vk/subs/add', method: 'POST', contentType: 'application/json', dataType: 'json', data: JSON.stringify({ id: id, title: title || '' }), timeout: 20000, success: ok, error: function () { noty('Не удалось подписаться'); } });
  }
  function addByLink() {
    Lampa.Input.edit({ title: 'Ссылка на плейлист или канал', value: '', nosave: true, onBack: function () { Lampa.Controller.toggle(ctrl()); } }, function (v) {
      if (!v) return;
      $.ajax({ url: BASE + '/vk/subs/add', method: 'POST', contentType: 'application/json', dataType: 'json', data: JSON.stringify({ url: v }), timeout: 20000,
        success: function (d) { noty(d && d.kind === 'channel' ? 'Канал добавлен' : 'Плейлист добавлен'); openAlbums(); },
        error: function () { noty('Не добавлено'); }
      });
    });
  }

  function search() {
    Lampa.Input.edit({ title: 'Поиск VK', value: '', nosave: true, onBack: function () { Lampa.Controller.toggle(ctrl()); } }, function (q) {
      if (!q) return;
      api('/vk/search?q=' + encodeURIComponent(q), function (d) {
        var items = [];
        (d.channels || []).forEach(function (c) {
          var bits = [];
          var followers = nf(c.followers);
          if (followers) bits.push(followers + ' подписчиков');
          if (c.description) bits.push(c.description);
          items.push({ title: c.title || c.id, subtitle: bits.join(' · '), channel: c });
        });
        (d.videos || []).forEach(function (v) { items.push({ title: v.title, subtitle: fmt(v.duration), v: v }); });
        (d.albums || []).forEach(function (a) { items.push({ title: 'Плейлист: ' + (a.title || a.id), subtitle: 'открыть', album: a }); });
        show('VK · ' + q, items, function (a) {
          if (a.channel) { openChannel(a.channel.id, a.channel.title); return; }
          if (a.album) { openAlbum(a.album.id, a.album.title); return; }
          play(a.v.id, a.v.title);
        });
      }, function () { noty('VK: поиск не удался'); });
    });
  }

  function poster(url) {
    if (!url) return '';
    try { return BASE + '/vk/p?u=' + btoa(unescape(encodeURIComponent(url))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
    catch (e) { return ''; }
  }
  function cardOf(v) {
    return { id: 'vk' + String(v.id).replace('-', 'm'), vk_id: v.id, vk_channel: v.channel_id || '', vk_channel_name: v.channel || '', seen: !!v.seen, title: v.title || 'VK', name: v.title || 'VK', overview: v.album_title || '', img: poster(v.thumb), quality: v.duration ? fmt(v.duration) : '', source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } };
  }
  function channelCard(c) {
    return { id: 'vkc' + String(c.id).replace('-', 'm'), vk_channel: c.id, title: c.title || c.id, name: c.title || 'Канал', overview: c.description || '', img: poster(c.thumb), source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } };
  }
  function albumCard(a) {
    return { id: 'vka' + String(a.id).replace('-', 'm'), vk_album: a.id, title: a.title || a.id, name: a.title || 'Плейлист', overview: '', img: poster(a.thumb), source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } };
  }

  function vkComponent(object) {
    var comp = new Lampa.InteractionCategory(object);
    var info = null;
    comp.create = function () {
      var self = this, vk = object.vk || {};
      this.activity.loader(true);
      var path = vk.mode === 'album' ? '/vk/album?id=' + encodeURIComponent(vk.id)
        : vk.mode === 'channel' ? '/vk/channel?id=' + encodeURIComponent(vk.id)
        : vk.mode === 'channels' ? '/vk/channels'
        : vk.mode === 'feed' ? '/vk/feed' : '/vk/subs';
      api(path, function (d) {
        var results;
        if (vk.mode === 'albums') results = (d.albums || []).map(albumCard);
        else if (vk.mode === 'channels') results = (d.channels || []).map(channelCard);
        else results = (d.results || []).map(cardOf);
        if (vk.mode === 'channel' || vk.mode === 'album') {
          info = d.info || { id: vk.id, title: object.title || '', subscribed: false, kind: vk.mode === 'album' ? 'album' : 'channel' };
          info.shown = (d.results || []).length;
          info.total = info.total || info.count || 0;
          results.unshift({ id: 'vkhead', vk_head: true, title: info.title || object.title || '', name: info.title || '', overview: info.description || '', img: poster(info.thumb), source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0 });
        }
        if (vk.mode === 'albums') results.push({ id: 'vkadd', vk_add: true, title: 'Добавить', name: 'Добавить', overview: 'плейлист или канал', source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } });
        if (!results.length) { self.empty(); return; }
        self.build({ results: results, page: 1, total_pages: 1, total_results: results.length });
        try { self.render().addClass(vk.mode === 'channels' ? 'vk-channels' : 'yt-wide'); } catch (e) {}
      }, function () { self.empty(); noty('VK: не загрузилось'); });
    };
    comp.nextPageReuest = function (obj, resolve, reject) { reject(); };
    function paintSeen(card, element) {
      var node = null;
      try { node = card.render(true); } catch (e) {}
      if (!node) {
        try { var wrapped = card.render(); node = wrapped && wrapped.jquery ? wrapped[0] : wrapped; } catch (e) {}
      }
      if (!node || !node.querySelector) return;
      var view = node.querySelector('.card__view') || node;
      var old = view.querySelector('.vk-seen');
      if (old) old.remove();
      if (!element.seen) return;
      var mark = document.createElement('div');
      mark.className = 'vk-seen';
      mark.textContent = '\u2713';
      view.appendChild(mark);
    }
    function fillHeader(card) {
      var i = info || {};
      var el = $(card.render());
      el.addClass('vk-headcard');
      var h = $('<div class="vk-chead"></div>');
      if (i.thumb) h.append($('<img class="vk-chead__ava">').attr('src', poster(i.thumb)));
      var box = $('<div class="vk-chead__box"></div>');
      box.append($('<div class="vk-chead__name"></div>').text(i.title || object.title || ''));
      var followers = nf(i.followers);
      if (i.kind === 'album' && i.count) box.append($('<div class="vk-chead__meta"></div>').text(i.count + ' видео'));
      else if (followers) box.append($('<div class="vk-chead__meta"></div>').text(followers + ' подписчиков'));
      if (i.total && i.shown && i.total > i.shown) box.append($('<div class="vk-chead__meta"></div>').text('Показаны первые ' + i.shown + ' из ' + i.total));
      if (i.description) box.append($('<div class="vk-chead__descr"></div>').text(i.description));
      var btn = $('<div class="vk-chead__btn"></div>').text(i.subscribed ? 'Отписаться' : 'Подписаться').data('on', !!i.subscribed);
      box.append(btn);
      h.append(box);
      el.children().hide();
      el.append(h);
      card.onEnter = function () {
        var on = !!btn.data('on');
        var done = function () {
          btn.data('on', !on);
          btn.text(on ? 'Подписаться' : 'Отписаться');
          noty(on ? 'Отписка' : 'Подписка: ' + (i.title || ''));
        };
        if (i.kind === 'album') followAlbum(i.id, on, i.title, done);
        else followChannel(i.id, on, done);
      };
      card.onMenu = function () {};
    }
    comp.cardRender = function (obj, element, card) {
      if (element.vk_head) return fillHeader(card);
      paintSeen(card, element);
      var keepFocus = card.onFocus;
      card.onFocus = function () { paintSeen(card, element); if (typeof keepFocus === 'function') keepFocus.apply(card, arguments); };
      card.onEnter = function () {
        if (element.vk_add) return addByLink();
        if (element.vk_id) return play(element.vk_id, element.title);
        if (element.vk_album) openAlbum(element.vk_album, element.title);
        if (element.vk_channel && !element.vk_id) openChannel(element.vk_channel, element.title);
      };
      card.onMenu = function () {
        if (!element.vk_id && !element.vk_channel) return;
        var back = ctrl();
        function openMenu(subscribed) {
          var items = [];
          if (element.vk_channel) items.push({ title: subscribed ? 'Отписаться' : 'Подписаться', sub: true, on: subscribed });
          if (element.vk_channel && element.vk_id) items.push({ title: 'Открыть канал', open: true });
          if (element.vk_id) items.push({ title: element.seen ? 'Снять отметку' : 'Просмотрено', mark: true });
          show(element.title || 'VK', items, function (a) {
            if (a.open) { openChannel(element.vk_channel, element.vk_channel_name || element.title); return; }
            if (a.sub) {
              followChannel(element.vk_channel, a.on, function () { noty(a.on ? 'Отписка' : 'Подписка: ' + (element.vk_channel_name || '')); Lampa.Controller.toggle(back); });
              return;
            }
            if (!a.mark) return;
          var next = !element.seen;
          $.ajax({ url: BASE + '/vk/seen?id=' + encodeURIComponent(element.vk_id) + '&on=' + (next ? 1 : 0), method: 'POST', timeout: 15000,
            success: function () {
              element.seen = next;
              if (next && (object.vk || {}).mode === 'feed') { try { $(card.render()).remove(); } catch (e) {} }
              else paintSeen(card, element);
              noty(next ? 'Просмотрено' : 'Отметка снята');
            },
            error: function () { noty('Не удалось сохранить отметку'); }
          });
            Lampa.Controller.toggle(back);
          });
        }
        if (!element.vk_channel) return openMenu(false);
        api('/vk/channels', function (d) {
          var on = (d.channels || []).some(function (c) { return c.id === element.vk_channel; });
          openMenu(on);
        }, function () { openMenu(false); });
      };
    };
    return comp;
  }

  function addRow() {
    if (!Lampa.ContentRows || window.__vk_row) return;
    window.__vk_row = true;
    Lampa.ContentRows.add({
      name: 'vk_row_feed', title: 'Мой VK', index: 7.96, screen: ['main'],
      call: function () {
        return function (callback) {
          api('/vk/feed', function (d) {
            var items = (d.results || []).map(cardOf);
            callback({ results: items, title: 'Мой VK', nomore: true, card_events: {
              onEnter: function (t, card) { if (card && card.vk_id) play(card.vk_id, card.title); },
              onMenu: function (t, card) {
                if (!card || !card.vk_id) return;
                $.ajax({ url: BASE + '/vk/seen?id=' + encodeURIComponent(card.vk_id) + '&on=1', method: 'POST', timeout: 15000,
                  success: function () { try { $(t).remove(); } catch (e) {} noty('Просмотрено'); },
                  error: function () { noty('Не удалось сохранить отметку'); }
                });
              }
            } });
          }, function () { callback({ results: [] }); });
        };
      }
    });
  }

  function addMenu() {
    if ($('.menu .menu__list [data-action="vk"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="vk"><div class="menu__ico">' + ICON + '</div><div class="menu__text">VK Видео</div></li>');
    btn.on('hover:enter', function () {
      show('VK Видео', [
        { title: 'Мой VK', go: feed },
        { title: 'Поиск', go: search },
        { title: 'Каналы', go: openChannels },
        { title: 'Плейлисты', go: openAlbums },
        { title: 'Набор плейлистов', go: packs }
      ], function (a) { a.go(); });
    });
    $('.menu .menu__list').eq(0).append(btn);
  }

  function start() {
    try { Lampa.Component.add('vk_list', vkComponent); } catch (e) {}
    addRow(); addMenu(); setTimeout(addMenu, 2000);
  }
  window.__vk = { play: play, feed: feed, search: search, openAlbum: openAlbum, openChannel: openChannel, channels: openChannels, open: function () { openAlbums(); } };
  var seenStyle = document.createElement('style');
  seenStyle.textContent = '.vk-seen{position:absolute;top:.4em;left:.4em;background:#2ecc71;color:#fff;font-weight:700;border-radius:.3em;padding:.15em .4em;z-index:2}' +
    '.vk-headcard{width:100% !important;margin-bottom:.6em}' +
    '.vk-chead{display:flex;gap:1.4em;align-items:flex-start;padding:.5em 1em;border-radius:.6em}' +
    '.vk-chead__ava{width:7em;height:7em;border-radius:50%;object-fit:cover;background:#222;flex:none}' +
    '.vk-chead__box{flex:1;min-width:0}' +
    '.vk-chead__name{font-size:1.7em;font-weight:700}' +
    '.vk-chead__meta{opacity:.7;margin-top:.2em}' +
    '.vk-chead__descr{margin-top:.5em;opacity:.85;line-height:1.4;max-height:6em;overflow:hidden;white-space:pre-line}' +
    '.vk-chead__btn{display:inline-block;margin-top:.7em;padding:.45em 1.1em;border-radius:.4em;background:rgba(255,255,255,.14)}' +
    '.vk-headcard.focus .vk-chead__btn{background:#fff;color:#000}' +
    '.vk-headcard.focus .vk-chead{background:rgba(0,0,0,.45)}';
  document.head.appendChild(seenStyle);
  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
