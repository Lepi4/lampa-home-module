(function () {
  'use strict';
  if (window.plugin_apps_ready) return;
  window.plugin_apps_ready = true;

  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M4 4h6v6H4V4zm10 0h6v6h-6V4zM4 14h6v6H4v-6zm10 0h6v6h-6v-6z"/></svg>';
  var PIN_KEY = 'apps_pinned_v1';

  var baseName = 'content';
  function ctrl() {
    try {
      var n = Lampa.Controller.enabled().name;
      if (n && ['select', 'modal', 'input', 'settings_component'].indexOf(n) < 0) baseName = n;
    } catch (e) {}
    return baseName;
  }

  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }
  function native() { return window.AndroidJS && AndroidJS.getInstalledApps && AndroidJS.launchApp; }

  function pinned() { try { return JSON.parse(localStorage.getItem(PIN_KEY) || '[]'); } catch (e) { return []; } }
  function savePinned(a) { try { localStorage.setItem(PIN_KEY, JSON.stringify(a)); } catch (e) {} }
  function isPinned(p) { return pinned().some(function (x) { return x.p === p; }); }

  function installed() {
    try { return JSON.parse(AndroidJS.getInstalledApps()); } catch (e) { return []; }
  }

  function launch(a) {
    try { if (!AndroidJS.launchApp(a.p)) noty('Не удалось открыть ' + a.n); }
    catch (e) { noty('Не удалось открыть ' + a.n); }
  }

  // ---------- размер постеров на главной ----------
  var SCALE_KEY = 'poster_scale_v1';
  // Lampa подгружает в строку около 7 карточек: подбираем размер так, чтобы они занимали всю ширину строки
  function autoScale() {
    try {
      var em = parseFloat(getComputedStyle(document.body).fontSize) || 10;
      var line = document.querySelector('.items-line');
      var rowW = (line && line.clientWidth > 200) ? line.clientWidth : window.innerWidth * 0.84;
      var cardW = rowW / 7.2;                         // 7 карточек целиком и край восьмой — видно, что строка продолжается
      var s = (cardW - 1.0 * em) / (12.75 * em);
      return Math.max(0.5, Math.min(0.65, Math.round(s * 100) / 100));   // не крупнее 0.65: иначе на экран не помещаются три строки
    } catch (e) { return 0.65; }
  }
  function getScale() {
    try {
      var raw = localStorage.getItem(SCALE_KEY);
      if (raw !== null && raw !== '') { var v = parseFloat(raw); if (!isNaN(v)) return v; }
    } catch (e) {}
    return autoScale();
  }
  var COMPACT_KEY = 'compact_v1';
  function getCompact() { try { return localStorage.getItem(COMPACT_KEY) !== '0'; } catch (e) { return true; } }

  function applyScale() {
    var s = getScale();
    var st = document.getElementById('poster-scale-css');
    if (!st) { st = document.createElement('style'); st.id = 'poster-scale-css'; document.head.appendChild(st); }
    var tiny = s < 0.5;   // режим «5 строк»: подписи только у выбранной карточки
    var css = '.apps-card{width:' + Math.max(2.8, 10.4 * s * 0.85).toFixed(2) + 'em !important;margin-right:.8em !important}' +   // значки приложений на 15% меньше
      '.apps-card .card__title{font-size:' + (s <= 0.5 ? 0.85 : 1.0) + 'em !important}';
    // компактная главная: меньше заголовки строк и расстояние между строками
    if (getCompact()) {
      css += '.items-line{padding-bottom:' + (tiny ? 0.6 : 0.8) + 'em !important}' +
        '.items-line__head{margin-bottom:' + (tiny ? 0.3 : 0.35) + 'em !important}' +
        '.items-line__title{font-size:' + (tiny ? 1.0 : 1.05) + 'em !important}' +
        '.items-line .card:not(.apps-card) .card__age{font-size:.85em !important;margin-top:.3em !important}';
    }
    if (s >= 1) { st.textContent = css; return; }
    var title = tiny ? 0.8 : (s <= 0.5 ? 0.95 : 1.1);
    st.textContent = css + '.items-line .card:not(.apps-card):not(.card--wide):not(.card--collection){width:' + (12.75 * s).toFixed(2) + 'em !important}' +
      // название переносится на следующие строки (до трёх), а не обрывается многоточием
      '.items-line .card:not(.apps-card) .card__title{font-size:' + title + 'em !important;white-space:normal !important;display:-webkit-box !important;-webkit-box-orient:vertical;-webkit-line-clamp:3;overflow:hidden;text-overflow:ellipsis;line-height:1.2 !important;max-height:3.6em}' +
      (tiny ? '.items-line .card:not(.apps-card) .card__age{display:none !important}' +
        '.items-line .card:not(.apps-card){position:relative}' +
        '.items-line .card:not(.apps-card) .card__title{position:absolute !important;left:0;right:0;top:100%;opacity:0;z-index:6;background:rgba(0,0,0,.75);padding:.15em .3em;border-radius:.3em;white-space:normal !important}' +
        '.items-line .card.focus:not(.apps-card) .card__title{opacity:1}' : '');
  }
  applyScale();
  document.addEventListener('keydown', function () { window.__lastKey = Date.now(); }, true);
  try {
    Lampa.Listener.follow('activity', function (e) {
      if (e && e.component === 'main' && (e.type === 'start' || e.type === 'archive' || e.type === 'create')) {
        setTimeout(applyScale, 400); setTimeout(applyScale, 1500);
      }
    });
  } catch (e) {}
  window.addEventListener('resize', function () { setTimeout(applyScale, 300); });

  // ---------- пункты меню для закреплённых программ ----------
  function actionOf(p) { return 'app_' + p.replace(/[^A-Za-z0-9]/g, '_'); }

  function addPinnedItem(a) {
    var act = actionOf(a.p);
    if ($('.menu .menu__list [data-action="' + act + '"]').length) return;
    var icon = '';
    try { icon = AndroidJS.getAppIcon(a.p); } catch (e) {}
    var ico = icon ? '<img src="' + icon + '" style="width:1.7em;height:1.7em;border-radius:.3em;object-fit:cover"/>' : ICON;
    var btn = $('<li class="menu__item selector" data-action="' + act + '"><div class="menu__ico">' + ico + '</div><div class="menu__text"></div></li>');
    btn.find('.menu__text').text(a.n);
    btn.on('hover:enter', function () { launch(a); });
    $('.menu .menu__list').eq(0).append(btn);
  }

  function removePinnedItem(p) { $('.menu .menu__list [data-action="' + actionOf(p) + '"]').remove(); }

  function placeAll() { if (window.__kp_placeMenu) window.__kp_placeMenu(); }

  function addPinnedAll() {
    var have = {};
    installed().forEach(function (x) { have[x.p] = true; });
    pinned().forEach(function (a) { if (have[a.p]) addPinnedItem(a); });
    placeAll();
  }

  // ---------- окно «Приложения» ----------
  var mode = 'launch'; // launch | pin

  function showApps(from) {
    var list = installed().sort(function (a, b) { return a.n.toLowerCase() < b.n.toLowerCase() ? -1 : 1; });
    var items = [
      { title: 'Нажатие на программу: ' + (mode === 'launch' ? 'запустить' : 'закрепить / открепить в меню'), subtitle: 'переключить режим', act: 'mode' },
      { title: 'Размер постеров на главной: ' + Math.round(getScale() * 100) + '%', subtitle: 'нажмите, чтобы переключить 100% / 75% / 50% / 30% (до 5 строк на экране)', act: 'scale' },
      { title: 'Плотность главной: ' + (getCompact() ? 'компактно' : 'стандартно'), subtitle: 'размер заголовков строк и расстояние между строками', act: 'compact' }
    ];
    list.forEach(function (a, i) {
      items.push({ title: (isPinned(a.p) ? '★ ' : '') + a.n, subtitle: a.p, app: a, selected: i === (from || 0) });
    });
    Lampa.Select.show({
      title: 'Приложения',
      items: items,
      onBack: function () { Lampa.Controller.toggle(ctrl()); },
      onSelect: function (a) {
        setTimeout(function () {
          if (a.act === 'mode') { mode = mode === 'launch' ? 'pin' : 'launch'; return showApps(0); }
          if (a.act === 'compact') {
            try { localStorage.setItem(COMPACT_KEY, getCompact() ? '0' : '1'); } catch (e) {}
            applyScale();
            noty('Главная: ' + (getCompact() ? 'компактно' : 'стандартно'));
            return showApps(0);
          }
          if (a.act === 'scale') {
            var cur = getScale(), nxt = cur >= 1 ? 0.75 : cur >= 0.75 ? 0.5 : cur >= 0.5 ? 0.3 : 1;
            try { localStorage.setItem(SCALE_KEY, String(nxt)); } catch (e) {}
            applyScale();
            noty('Постеры: ' + Math.round(nxt * 100) + '%');
            return showApps(0);
          }
          var idx = list.indexOf(a.app);
          if (mode === 'launch') { Lampa.Controller.toggle(ctrl()); return launch(a.app); }
          var cur = pinned(), k = -1;
          cur.forEach(function (x, i) { if (x.p === a.app.p) k = i; });
          if (k >= 0) { cur.splice(k, 1); removePinnedItem(a.app.p); noty('Убрано из меню: ' + a.app.n); }
          else { cur.push({ p: a.app.p, n: a.app.n }); addPinnedItem(a.app); noty('В меню: ' + a.app.n); placeAll(); }
          savePinned(cur);
          showApps(idx);
        }, 300);
      }
    });
  }

  function openApps() {
    ctrl();
    if (!native()) return noty('Запуск программ работает в приложении Lampa Home на Android');
    showApps(0);
  }

  // ---------- строка «Приложения» на главной (самая верхняя) ----------
  var ADD_ICON = 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96"><rect width="96" height="96" rx="18" fill="#2a2f55"/><path d="M48 24v48M24 48h48" stroke="#F9A825" stroke-width="9" stroke-linecap="round"/></svg>');

  var TV_ICON = 'data:image/svg+xml;utf8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96"><rect width="96" height="96" rx="18" fill="#232a5c"/><rect x="18" y="26" width="60" height="38" rx="6" fill="none" stroke="#F9A825" stroke-width="6"/><path d="M36 74h24M48 64v10" stroke="#F9A825" stroke-width="6" stroke-linecap="round"/><path d="M43 36v18l16-9z" fill="#F9A825"/></svg>');

  function appCards() {
    var cards = [];
    // первым — значок «ТВ»: сразу включает телевидение (последний канал)
    if (window.__tv && window.__tv.quickPlay) cards.push({ id: 'apps:tv', title: 'ТВ', original_title: 'ТВ', img: TV_ICON, release_date: '', vote_average: 0, source: 'apps', tv: true });
    var have = {};
    installed().forEach(function (x) { have[x.p] = true; });
    pinned().forEach(function (a) {
      if (!have[a.p]) return;
      var icon = '';
      try { icon = AndroidJS.getAppIcon(a.p); } catch (e) {}
      cards.push({ id: 'apps:' + a.p, title: a.n, original_title: a.n, img: icon, release_date: '', vote_average: 0, source: 'apps', pkg: a.p, name_app: a.n });
    });
    cards.push({ id: 'apps:add', title: 'Добавить', original_title: 'Добавить', img: ADD_ICON, release_date: '', vote_average: 0, source: 'apps', add: true });
    return cards;
  }

  function registerRow() {
    if (!native() || window.__apps_row) return;
    window.__apps_row = true;
    Lampa.ContentRows.add({
      name: 'apps_row',
      title: 'Приложения',
      index: 0,
      screen: ['main'],
      call: function () {
        return function (callback) {
          callback({
            results: appCards(),
            title: 'Приложения',
            small: true,
            align_left: true,
            nomore: true,
            card_events: {
              onEnter: function (target, card) {
                if (!card) return;
                if (card.add) { mode = 'pin'; return showApps(0); }
                launch({ p: card.pkg, n: card.name_app });
              }
            }
          });
        };
      }
    });
  }

  // при медленном старте (после перезагрузки) главная успевает построиться раньше, чем зарегистрирована строка:
  // тогда один раз пересоздаём главную, чтобы строка «Приложения» появилась
  function ensureRow() {
    if (!native() || window.__apps_row_fixed) return;
    var tries = 0;
    var t = setInterval(function () {
      tries++;
      if (tries > 90 || window.__apps_row_fixed) return clearInterval(t);
      try {
        if (!window.__apps_row) return;
        var a = Lampa.Activity.active();
        if (!a || a.component !== 'main') return;
        if (document.querySelector('.apps-card')) { window.__apps_row_fixed = true; return clearInterval(t); }
        if (!document.querySelector('.items-line')) return;   // главная ещё грузится
        window.__apps_row_fixed = true; clearInterval(t);
        Lampa.Activity.replace({ title: a.title, component: 'main', source: a.source, page: 1 });
      } catch (e) {}
    }, 1000);
  }

  var css = document.createElement('style');
  css.textContent = '.apps-line .items-line__head{display:none !important}' +   // подписи «Приложения» над строкой нет
    '.apps-line{margin-top:-1.4em !important}' +                                    // строка приложений выше
    '.apps-card .card__view{padding-bottom:100% !important;height:auto !important}' +
    '.apps-card .card__img{object-fit:contain !important;background:rgba(255,255,255,.08)}' +
    '.apps-card .card__title{font-size:1.05em !important;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.apps-card .card__age,.apps-card .card__vote{display:none !important}';
  document.head.appendChild(css);

  // помечаем карточки строки «Приложения» (CSS выше делает их маленькими)
  setInterval(function () {
    var cards = document.querySelectorAll('.card:not(.apps-card)');
    for (var i = 0; i < cards.length; i++) {
      var d = cards[i].card_data;
      if (d && d.source === 'apps') cards[i].classList.add('apps-card');
    }
    // остальные строки: Lampa подгружает по 7 карточек, остальные — при прокрутке. Достраиваем сразу (один раз на строку),
    // чтобы ряд занимал экран: «фокус» на 5-й карточке заставляет Lampa подгрузить следующую порцию
    var lns = document.querySelectorAll('.items-line');
    var idle = Date.now() - (window.__lastKey || 0) > 4000;   // не трогаем фокус, пока пользователь нажимает кнопки
    var nudged = false;
    for (var li = 0; idle && li < lns.length; li++) {
      var ln = lns[li];
      if (ln.__filled || ln.querySelector('.apps-card')) continue;
      var rc = ln.getBoundingClientRect();
      if (rc.bottom <= 0 || rc.top >= window.innerHeight) continue;   // только видимые строки: без прокрутки страницы
      var cs = ln.querySelectorAll('.card');
      if (cs.length >= 6 && cs.length < 12) {
        ln.__filled = true;
        nudged = true;
        (function (cards) {
          try {
            cards[4].dispatchEvent(new CustomEvent('hover:focus', { bubbles: true }));
            setTimeout(function () { try { cards[0].dispatchEvent(new CustomEvent('hover:focus', { bubbles: true })); } catch (e) {} }, 250);
          } catch (e) {}
        })(cs);
      } else if (cs.length >= 12) ln.__filled = true;
    }
    // после подкрутки возвращаем страницу и фокус на самую первую карточку (как при открытии главной)
    if (nudged) {
      setTimeout(function () {
        try {
          if (Date.now() - (window.__lastKey || 0) < 4000) return;
          var first = document.querySelector('.items-line .card');
          if (first) first.dispatchEvent(new CustomEvent('hover:focus', { bubbles: true }));
        } catch (e) {}
      }, 700);
    }
    // Lampa дорисовывает карточки строки только по мере движения вправо (по 6–7 штук):
    // чтобы все закреплённые программы были видны сразу, «подталкиваем» строку один раз
    var ac = document.querySelectorAll('.apps-card');
    if (ac.length) { var l0 = ac[0].closest ? ac[0].closest('.items-line') : null; if (l0) l0.classList.add('apps-line'); }
    if (ac.length >= 6) {
      var line = $(ac[0]).closest('.items-line')[0];
      if (line && !line.__apps_expanded && ac.length < appCards().length) {
        line.__apps_expanded = true;
        try {
          ac[Math.min(4, ac.length - 1)].dispatchEvent(new CustomEvent('hover:focus', { bubbles: true }));
          setTimeout(function () { try { ac[0].dispatchEvent(new CustomEvent('hover:focus', { bubbles: true })); } catch (e) {} }, 150);
        } catch (e) {}
      }
    }
  }, 600);

  // строка должна быть выше остальных: регистрируем после строк kino.pub (они вставляются с индексом 0 раньше)
  function registerRowLater() {
    var tries = 0;
    var t = setInterval(function () {
      tries++;
      if (window.__kp_rows_registered || tries > 40) { clearInterval(t); registerRow(); }
    }, 150);
  }

  document.addEventListener('hover:enter', function (e) {
    try {
      var el = e.target && e.target.closest ? e.target.closest('.card') : null;
      if (!el || !el.card_data || el.card_data.source !== 'apps') return;
      e.stopImmediatePropagation(); e.preventDefault();
      var c = el.card_data;
      if (c.tv) { try { window.__tv.quickPlay(); } catch (z) {} }
      else if (c.add) { ctrl(); mode = 'pin'; showApps(0); }
      else launch({ p: c.pkg, n: c.name_app });
    } catch (x) {}
  }, true);

  // долгое нажатие на значок: убрать с главной
  document.addEventListener('hover:long', function (e) {
    try {
      var el = e.target && e.target.closest ? e.target.closest('.card') : null;
      if (!el || !el.card_data || el.card_data.source !== 'apps' || el.card_data.add || el.card_data.tv) return;
      e.stopImmediatePropagation(); e.preventDefault();
      var c = el.card_data, prev = ctrl();
      Lampa.Select.show({
        title: c.name_app,
        items: [{ title: 'Убрать с главной и из меню', act: 'rm' }, { title: 'Запустить', act: 'go' }],
        onBack: function () { Lampa.Controller.toggle(prev); },
        onSelect: function (a) {
          setTimeout(function () {
            if (a.act === 'go') { Lampa.Controller.toggle(prev); return launch({ p: c.pkg, n: c.name_app }); }
            var cur = pinned().filter(function (x) { return x.p !== c.pkg; });
            savePinned(cur); removePinnedItem(c.pkg);
            $(el).remove();
            noty('Убрано: ' + c.name_app);
            Lampa.Controller.toggle(prev);
          }, 300);
        }
      });
    } catch (x) {}
  }, true);

  function addMenu() {
    if ($('.menu .menu__list [data-action="apps"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="apps"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Приложения</div></li>');
    btn.on('hover:enter', openApps);
    $('.menu .menu__list').eq(0).append(btn);
    if (native()) addPinnedAll(); else placeAll();
  }

  function start() {
    // Лента приложений находится в лаунчере: в веб-Lampa не регистрируем её и не пересоздаём ради неё главную.
    addMenu();
    setTimeout(addMenu, 2000);
    setTimeout(function () { if (native()) addPinnedAll(); }, 4000);
    setTimeout(addMenu, 6000);
  }

  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
