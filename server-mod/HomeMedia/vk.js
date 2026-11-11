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
  function fmt(sec) {
    sec = Math.max(0, Math.floor(sec || 0));
    var m = Math.floor(sec / 60), s = sec % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function play(id, title) {
    var back = ctrl();
    api('/vk/video?id=' + encodeURIComponent(id) + '&maxh=' + maxH(), function (d) {
      if (!d || !d.video) return noty('VK: нет подходящего MP4');
      var data = { url: d.video, title: title || d.title || 'VK Видео', headers: d.headers || {}, home_player: true, home_timeline_only: true, home_vk_id: d.id, home_base: BASE };
      function launch(progress) {
        if (window.HomeUser && window.Lampa && Lampa.Utils && Lampa.Utils.hash) {
          var hash = String(Lampa.Utils.hash('vk:' + d.id));
          data.audio_key = 'vk_' + d.id;
          data.tl_meta = JSON.stringify([{ id: 'vk-' + d.id, h: hash, c: 'vk_' + d.id }]);
          data.timeline = { hash: hash, time: (progress && progress.position) || 0, duration: d.duration || (progress && progress.duration) || 0, percent: (progress && progress.percent) || 0 };
        }
        try { Lampa.Player.play(data); } catch (e) { noty('VK: не удалось запустить плеер'); }
        setTimeout(function () { try { Lampa.Controller.toggle(back); } catch (e) {} }, 700);
      }
      if (window.HomeUser) api('/vk/progress?id=' + encodeURIComponent(d.id), launch, function () { launch(null); });
      else launch(null);
    }, function (x) {
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
  function feed() { openCards('Мой VK', { mode: 'feed' }); }

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

  function addByLink() {
    Lampa.Input.edit({ title: 'Ссылка на плейлист', value: '', nosave: true, onBack: function () { Lampa.Controller.toggle(ctrl()); } }, function (v) {
      if (!v) return;
      $.ajax({ url: BASE + '/vk/subs/add', method: 'POST', contentType: 'application/json', data: JSON.stringify({ url: v }), timeout: 20000,
        success: function () { noty('Плейлист добавлен'); openAlbums(); },
        error: function () { noty('Плейлист не добавлен'); }
      });
    });
  }

  function search() {
    Lampa.Input.edit({ title: 'Поиск VK', value: '', nosave: true, onBack: function () { Lampa.Controller.toggle(ctrl()); } }, function (q) {
      if (!q) return;
      api('/vk/search?q=' + encodeURIComponent(q), function (d) {
        var items = (d.videos || []).map(function (v) { return { title: v.title, subtitle: fmt(v.duration), v: v }; });
        (d.albums || []).forEach(function (a) { items.push({ title: 'Плейлист: ' + (a.title || a.id), subtitle: 'подписать', album: a }); });
        show('VK · ' + q, items, function (a) {
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
    return { id: 'vk' + String(v.id).replace('-', 'm'), vk_id: v.id, seen: !!v.seen, title: v.title || 'VK', name: v.title || 'VK', overview: v.album_title || '', img: poster(v.thumb), quality: v.duration ? fmt(v.duration) : '', source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } };
  }
  function albumCard(a) {
    return { id: 'vka' + String(a.id).replace('-', 'm'), vk_album: a.id, title: a.title || a.id, name: a.title || 'Плейлист', overview: '', img: poster(a.thumb), source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } };
  }

  function vkComponent(object) {
    var comp = new Lampa.InteractionCategory(object);
    comp.create = function () {
      var self = this, vk = object.vk || {};
      this.activity.loader(true);
      var path = vk.mode === 'album' ? '/vk/album?id=' + encodeURIComponent(vk.id) : vk.mode === 'feed' ? '/vk/feed' : '/vk/subs';
      api(path, function (d) {
        var results = vk.mode === 'albums'
          ? (d.albums || []).map(albumCard)
          : (d.results || []).map(cardOf);
        if (vk.mode === 'albums') results.push({ id: 'vkadd', vk_add: true, title: 'Добавить плейлист', name: 'Добавить плейлист', overview: 'по ссылке', source: 'vk', poster_path: '', backdrop_path: '', vote_average: 0, params: { style: { name: 'wide' } } });
        if (!results.length) { self.empty(); return; }
        self.build({ results: results, page: 1, total_pages: 1, total_results: results.length });
        try { self.render().addClass('yt-wide'); } catch (e) {}
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
    comp.cardRender = function (obj, element, card) {
      paintSeen(card, element);
      var keepFocus = card.onFocus;
      card.onFocus = function () { paintSeen(card, element); if (typeof keepFocus === 'function') keepFocus.apply(card, arguments); };
      card.onEnter = function () {
        if (element.vk_add) return addByLink();
        if (element.vk_id) return play(element.vk_id, element.title);
        if (element.vk_album) openAlbum(element.vk_album, element.title);
      };
      card.onMenu = function () {
        if (!element.vk_id) return;
        var back = ctrl();
        show(element.title || 'Ролик', [
          { title: element.seen ? 'Снять отметку' : 'Просмотрено', mark: true }
        ], function () {
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
  window.__vk = { play: play, feed: feed, search: search, openAlbum: openAlbum, open: function () { openAlbums(); } };
  var seenStyle = document.createElement('style');
  seenStyle.textContent = '.vk-seen{position:absolute;top:.4em;left:.4em;background:#2ecc71;color:#fff;font-weight:700;border-radius:.3em;padding:.15em .4em;z-index:2}';
  document.head.appendChild(seenStyle);
  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
