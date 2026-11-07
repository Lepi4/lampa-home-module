(function () {
  'use strict';
  // Lampa Media Center — оболочка домашнего экрана. Ни одной библиотеки: страница лежит в APK и рисуется мгновенно.
  var MC = window.MC || { server: function () { return localStorage.getItem('mc_server') || ''; }, setServer: function (u) { localStorage.setItem('mc_server', u); },
    deviceId: function () { return 'web'; }, deviceName: function () { return 'browser'; }, discover: function () { setTimeout(function () { window.__mc.onScanDone(0); }, 500); },
    apps: function () { return '[]'; }, tv: function () {}, warm: function () {}, voice: function () {}, restartLampa: function () {},  icon: function () { return ''; }, launch: function () {}, settings: function () {}, lampa: function (c) { console.log('lampa', c); }, lampaReady: function () { return true; }, log: function () {} };

  var state = { server: '', profile: 'main', cfg: null, nav: [], r: 0, c: 0, mem: {}, setupOpen: false, setupItems: [], setupIdx: 0, firstRun: false, lastLoad: 0 };
  var userContext = null, userGeneration = 0;
  function clearMetadataQueues(){clearTimeout(kpT);clearTimeout(qT);kpT=null;qT=null;kpQ={};qQ={};}
  function userKey(k) { return userContext && userContext.profiles_enabled && !/^(mc_server|mc_setup|mc_profile|mc_seeded|mc_smode)$/.test(k) ? 'home_user:' + userContext.server_id + ':' + userContext.profile.id + ':' + (/^(mc_row_|mc_cfg)/.test(k)?'policy:'+(userContext.profile.policy_rev||1)+':':'') + k : k; }
  function ownerHeader(x, url) { if (userContext && userContext.token && url.indexOf(state.server + '/') === 0) x.setRequestHeader('X-Home-Session', userContext.token); }
  function writeApi(path, value, done) {
    var x = new XMLHttpRequest(), g=userGeneration; x.open('POST',state.server+path,true); ownerHeader(x,state.server+path); x.setRequestHeader('Content-Type','application/json');
    x.onload=function(){if(g!==userGeneration)return;var d;try{d=JSON.parse(x.responseText);}catch(e){d={error:'response_invalid'};}if(done)done(d);}; x.onerror=function(){toast('Нет связи с сервером');};x.send(JSON.stringify(value));
  }
  function pinPad(title, accept, cancel) {
    var digits='';
    function paint(){var entries=[];for(var i=0;i<10;i++)(function(n){entries.push([String(n),null,function(){if(digits.length<8)digits+=n;paint();}]);})(i);
      entries.push(['Подтвердить',null,function(){accept(digits);}]);entries.push(['Стереть',null,function(){digits=digits.slice(0,-1);paint();}]);entries.push(['Отмена',null,cancel||menuClose]);menuShow(title+' '+Array(digits.length+1).join('•'),entries);}
    paint();
  }
  function refreshUserPolicy(){
    if(!userContext||!MC.profileRefresh)return false;
    if(MC.profileRefreshAsync){MC.profileRefreshAsync(userGeneration);return false;}
    var next;try{next=JSON.parse(MC.profileRefresh());}catch(e){return false;}
    return applyUserPolicy(next);
  }
  function applyUserPolicy(next){
    var previous=userContext;if(!previous)return false;
    if(!next.ready||!next.profile||next.profile.id!==previous.profile.id||next.profile.policy_rev!==previous.profile.policy_rev){
      userGeneration++;clearMetadataQueues();try{if(window.__homeClearEpisodeMarks)window.__homeClearEpisodeMarks();}catch(e){}state.mem={};state.cfg=null;userContext=next.ready?next:null;try{MC.restartLampa();}catch(e){}startHome(true);return true;
    }
    userContext=next;return false;
  }
  function switchUsers() {
    api('/home/profiles',function(d){menuShow('Профиль',d.profiles.map(function(p){return [p.name+(p.is_child?' · детский':''),null,function(){
      function change(pin){var result;try{result=JSON.parse(MC.profileSwitch(p.id,pin));}catch(e){toast('Не удалось сменить профиль');return;}
        if(!result.token){toast('Проверьте ПИН-код');return;}
        userGeneration++;clearMetadataQueues();try{if(window.__homeClearEpisodeMarks)window.__homeClearEpisodeMarks();}catch(e){}userContext=JSON.parse(MC.profileContext());state.mem={};state.cfg=null;state.lastLoad=0;state.nav=[];releasePosters($('rows'));$('rows').innerHTML='';menuClose();startHome(true);}
      if(p.has_pin&&!p.is_child)pinPad(p.name,change,switchUsers);else change('');
    }];}));});
  }
  function ensureUser() {
    if(!MC.profileBoot)return true;
    var result;try{result=JSON.parse(MC.profileBoot(''));}catch(e){toast('Не удалось загрузить профили');return false;}
    if(result.ready){if(userContext&&userContext.token!==result.token){userGeneration++;clearMetadataQueues();state.mem={};}userContext=result;return true;}
    if(result.locked)pinPad('ПИН: '+result.profile.name,function(pin){var r=JSON.parse(MC.profileBoot(pin));if(r.ready){userContext=r;menuClose();startHome(true);}else toast('Проверьте ПИН-код');},function(){ensureUser();});
    else menuShow('Сервер недоступен', [['Повторить',null,function(){menuClose();startHome(true);}]]);
    return false;
  }
  function autoTorrentSettings() {
    api('/home/device/playback', function (d) {
      if (d.error) { toast(d.error); return; }
      var play = d.playback || {};
      function save(obj) {
        writeApi('/home/device/playback', obj, function (r) {
          if (r.error) { toast(r.error); return; }
          play = obj;
          paint();
        });
      }
      function label(v) { return v === 'only' ? 'только' : v === 'no' ? 'нет' : 'любое'; }
      function nextMode(v) { return v === 'any' ? 'only' : v === 'only' ? 'no' : 'any'; }
      function paint() {
        var auto = play.auto === true, mh = +play.max_height || 1080, seeds = +play.min_seeds || 5;
        var hevc = play.hevc || 'any', hdr = play.hdr || 'no';
        menuShow('Торренты: ' + userContext.profile.name, [
          ['Автовыбор: ' + (auto ? 'включён' : 'выключен'), null, function () { play.auto = !auto; save(play); }],
          ['Разрешение: до ' + mh + 'p', null, function () { var a = [0, 720, 1080, 2160], i = a.indexOf(mh); play.max_height = a[(i + 1) % a.length]; save(play); }],
          ['Минимум сидов: ' + seeds, null, function () { var a = [3, 5, 10, 20], i = a.indexOf(seeds); play.min_seeds = a[(Math.max(0, i) + 1) % a.length]; save(play); }],
          ['HEVC: ' + label(hevc), null, function () { play.hevc = nextMode(hevc); save(play); }],
          ['HDR: ' + label(hdr), null, function () { play.hdr = nextMode(hdr); save(play); }],
          ['Назад', null, openSettings]
        ]);
      }
      paint();
    });
  }
  function profileRowsSettings() {
    api('/home/profiles/me/rows',function(d){var saved={};(d.rows||[]).forEach(function(r){saved[r.id]=r;});
      var rows=(state.allRows||state.cfg||[]).map(function(r,i){return {id:r.id,title:r.title,enabled:!saved[r.id]||saved[r.id].enabled,order:saved[r.id]?saved[r.id].order:i};}).sort(function(a,b){return a.order-b.order;});
      function paint(){var entries=rows.map(function(r,i){return [(r.enabled?'☑ ':'☐ ')+r.title,null,function(){menuShow(r.title,[['Показывать / скрыть',null,function(){r.enabled=!r.enabled;paint();}],['Выше',null,function(){if(i){var a=rows[i-1];rows[i-1]=r;rows[i]=a;}paint();}],['Ниже',null,function(){if(i<rows.length-1){var a=rows[i+1];rows[i+1]=r;rows[i]=a;}paint();}],['Назад',null,paint]]);}];});
        entries.push(['Сохранить',null,function(){writeApi('/home/profiles/me/rows',{rows:rows.map(function(r,i){return {id:r.id,enabled:r.enabled,order:i};})},function(d){if(d.error){toast(d.error);return;}userContext=JSON.parse(MC.profileRefresh());try{MC.restartLampa();}catch(e){}menuClose();startHome(true);});}]);menuShow('Ленты профиля',entries);}
      paint();
    });
  }
  var $ = function (id) { return document.getElementById(id); };
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function log(m) { try { MC.log(String(m)); } catch (e) {} }

  // открыть Lampa: если она ещё догружается — ждём (показываем своё сообщение), чтобы не видеть её заставку
  function showWait(t) { var w = document.getElementById('wait'); if (!w) { w = document.createElement('div'); w.id = 'wait'; document.body.appendChild(w); } w.textContent = t; w.className = 'on'; }
  function hideWait() { var w = document.getElementById('wait'); if (w) w.className = ''; }
  function lampaGo(cmd) {
    var ready = true; try { ready = MC.lampaReady(); } catch (e) {}
    if (ready) { if(cmd!=='{}'){state.waiting=true;showWait('Открываю… (Назад — отмена)');} MC.lampa(cmd); return; }
    try { MC.warm(); } catch (e) {}
    showWait('Lampa прогревается… (Назад — отмена)');
    var n = 0; state.waiting = true;
    var t = setInterval(function () {
      n++; var r = true; try { r = MC.lampaReady(); } catch (e) {}
      if (!state.waiting) { clearInterval(t); hideWait(); return; }                 // отменено кнопкой «Назад»
      if (r || n > 60) { clearInterval(t); state.waiting = false; hideWait(); MC.lampa(cmd); }   // не дольше 18 с: дальше открываем как есть
    }, 300);
  }

  // Старый WebView (Chromium 83 на части ТВ): нет `gap` для flex — заменяем отступами между детьми
  (function flexGapFallback() {
    try {
      var t = document.createElement('div'); t.style.cssText = 'display:flex;flex-direction:column;gap:1px;position:absolute;visibility:hidden';
      t.appendChild(document.createElement('i')); t.appendChild(document.createElement('i')); t.firstChild.style.height = t.lastChild.style.height = '1px';
      document.body.appendChild(t); var ok = t.scrollHeight === 3; document.body.removeChild(t);
      if (ok) return;
      var out = '';
      for (var i = 0; i < document.styleSheets.length; i++) {
        var rules; try { rules = document.styleSheets[i].cssRules; } catch (e) { continue; }
        for (var j = 0; j < rules.length; j++) {
          var r = rules[j]; if (!r.style || !r.style.gap || r.style.display !== 'flex' && r.style.display !== '') continue;
          var g = r.style.gap.split(' ')[0], col = r.style.flexDirection === 'column';
          if (col) out += r.selectorText + '>*+*{margin-top:' + g + '}';
          else if (r.style.flexWrap === 'wrap') out += r.selectorText + '>*{margin:0 ' + g + ' ' + g + ' 0}';
          else out += r.selectorText + '>*+*{margin-left:' + g + '}';
        }
      }
      var st = document.createElement('style'); st.textContent = out; document.head.appendChild(st);
    } catch (e) {}
  })();

  var toastT;
  function toast(t) { var n = $('toast'); n.textContent = t; n.classList.add('on'); clearTimeout(toastT); toastT = setTimeout(function () { n.classList.remove('on'); }, 2600); }

  function api(path, ok, fail, timeout) {
    var x = new XMLHttpRequest();
    var generation=userGeneration; x.open('GET', state.server + path, true); ownerHeader(x,state.server+path);
    x.timeout = timeout || 30000;
    x.onload = function () { if(generation!==userGeneration)return; if (x.status === 200) { try { ok(JSON.parse(x.responseText)); } catch (e) { if (fail) fail(e); } } else if (fail) fail(x.status); };
    x.onerror = x.ontimeout = function () { if (generation===userGeneration&&fail) fail('net'); };
    x.send();
  }
  function getJson(url, ok, fail, timeout) {
    var x = new XMLHttpRequest();
    var generation=userGeneration; x.open('GET', url, true); ownerHeader(x,url); x.timeout = timeout || 6000;
    x.onload = function () { if(generation!==userGeneration)return; if (x.status === 200) { try { ok(JSON.parse(x.responseText)); } catch (e) { if (fail) fail(e); } } else if (fail) fail(x.status); };
    x.onerror = x.ontimeout = function () { if (generation===userGeneration&&fail) fail('net'); };
    x.send();
  }
  function b64url(s) { return btoa(unescape(encodeURIComponent(s))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
  // значения всегда пишутся как JSON; строки, сохранённые прежними версиями без кавычек, читаются как есть (только для известных ключей)
  var STRING_KEYS = { mc_server: 1, mc_profile: 1, mc_seeded: 1, mc_smode: 1, mc_wall_checked: 1, mc_setup: 1 };
  function save(k, v) { try { localStorage.setItem(userKey(k), JSON.stringify(v)); } catch (e) {} }
  function load(k, def) {
    try {
      var v = localStorage.getItem(userKey(k));
      if (v == null) return def;
      try { return JSON.parse(v); } catch (pe) { return STRING_KEYS[k] ? v : def; }
    } catch (e) { return def; }
  }

  // ---------- часы ----------
  function clockCacheKey() { return 'home_device_cfg_v2|' + MC.server() + '|' + MC.deviceId() + '|mc_clock'; }
  var clockTimer = null;
  function applyClock() {
    var clock = $('clock');
    if (!clock) return;
    var cached = '';
    try { cached = localStorage.getItem(clockCacheKey()) || ''; } catch (e) {}
    var raw = (state.dev || {}).mc_clock;
    var show = String(raw == null ? (cached === '0' ? '0' : '1') : raw) !== '0';
    clock.style.display = show ? '' : 'none';
    if (!show) { if (clockTimer !== null) clearInterval(clockTimer); clockTimer = null; return; }
    function paint() {
      var d = new Date();
      clock.textContent = ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2);
    }
    paint();
    if (clockTimer === null) clockTimer = setInterval(paint, 10000);
  }
  try { applyClock(); } catch (e) {}
  // остаток подписки Кинопаба: нет аккаунта — не показываем; 0 дней — «истекла»
  function dayWord(n) { var m = n % 10, h = n % 100; return (h > 10 && h < 20) ? 'дней' : m === 1 ? 'день' : (m >= 2 && m <= 4) ? 'дня' : 'дней'; }
  // Цвет остатка: привычный светлый выше 100 дней. Дальше каждый день сдвигает оттенок на 1:
  // зелёный 50–100, синий 15–49, красный ниже 15.
  function subColor(days) {
    days = Math.max(0, days | 0);
    if (days > 100) return '';
    var step, r, g, b;
    if (days >= 50) { step = days - 50; r = 24 + step; g = 110 + step; b = 48 + step; }
    else if (days >= 15) { step = days - 15; r = 24 + step; g = 72 + step; b = 150 + step; }
    else { step = days; r = 168 + step; g = 32 + step; b = 32 + step; }
    return 'rgb(' + r + ',' + g + ',' + b + ')';
  }
  function loadSub() {
    var b = $('sub');
    if (!b) return;
    if (!state.server || !state.profile) { setTimeout(loadSub, 4000); return; }
    api('/home/kp/sub?profile=' + encodeURIComponent(state.profile), function (d) {
      if (!b) return;
      if (!d || !d.has) { b.style.display = 'none'; return; }
      var days = d.days | 0, expired = !!(d.expired || days <= 0);
      b.style.display = 'inline-flex';
      b.innerHTML = 'Кинопаб <b>' + (expired ? 'истекла' : days + ' ' + dayWord(days)) + '</b>';
      b.style.background = subColor(expired ? 0 : days) || 'rgba(255,255,255,.12)';
      b.style.color = '#fff';
    }, function () { if (b) b.style.display = 'none'; });
  }
  setTimeout(loadSub, 3000); setInterval(loadSub, 30 * 60 * 1000);

  // ---------- описание под выбранным постером ----------
  var GENRES = { 28: 'Боевик', 12: 'Приключения', 16: 'Мультфильм', 35: 'Комедия', 80: 'Криминал', 99: 'Документальный', 18: 'Драма', 10751: 'Семейный', 14: 'Фэнтези', 36: 'История', 27: 'Ужасы', 10402: 'Музыка', 9648: 'Детектив', 10749: 'Мелодрама', 878: 'Фантастика', 10770: 'ТВ-фильм', 53: 'Триллер', 10752: 'Военный', 37: 'Вестерн', 10759: 'Боевик и приключения', 10762: 'Детский', 10763: 'Новости', 10764: 'Реалити-шоу', 10765: 'НФ и фэнтези', 10766: 'Мыльная опера', 10767: 'Ток-шоу', 10768: 'Война и политика' };
  var COUNTRY = { US: 'США', RU: 'Россия', GB: 'Великобритания', JP: 'Япония', KR: 'Южная Корея', FR: 'Франция', DE: 'Германия', IN: 'Индия', CN: 'Китай', TR: 'Турция', IT: 'Италия', ES: 'Испания', CA: 'Канада', UA: 'Украина', BR: 'Бразилия', SE: 'Швеция', AU: 'Австралия', DK: 'Дания', NO: 'Норвегия', PL: 'Польша', MX: 'Мексика', HK: 'Гонконг', TW: 'Тайвань', TH: 'Таиланд', SU: 'СССР' };
  function infoOn() { return String((state.dev || {}).mc_info != null ? state.dev.mc_info : '1') !== '0'; }
  var infoT = null, infoItem = null;
  function showInfo(item) {
    infoItem = item;
    var box = $('info');
    clearTimeout(infoT);
    document.body.classList.toggle('info-on', !!(item && infoOn()));
    if (!item || !infoOn()) { box.classList.add('hidden'); return; }
    infoT = setTimeout(function () {                       // небольшая задержка: при быстрой прокрутке описание не мигает
      var yt = !!item.published || item.channel_id !== undefined || (item.thumb && !item.poster_path);
      var h = '', meta = [];
      var title = item.title || item.name || '';
      h += '<div class="it">' + esc(title) + '</div>';
      if (yt) {
        if (item.channel) meta.push(esc(item.channel));
        var dd = item.published ? dateLabel(item.published) : (item.ago || ''); if (dd) meta.push(dd);
        var du = fmtDur(item.duration); if (du) meta.push(du);
        if (item.views) meta.push(Math.round(item.views / 1000) + ' тыс. просмотров');
      } else {
        var orig = item.original_title || item.original_name || '';
        if (orig && orig !== title) h += '<div class="io">' + esc(orig) + '</div>';
        var yr = String(item.release_date || item.first_air_date || '').slice(0, 4); if (yr) meta.push(yr);
        var tv = item.wrestling || item.media_type === 'tv' || !!item.name || !!item.first_air_date; meta.push(tv ? 'Сериал' : 'Фильм');
        if (item.origin_country && item.origin_country.length) meta.push(item.origin_country.map(function (c) { return COUNTRY[c] || c; }).join(', '));
        else if (item.countries_ru && item.countries_ru.length) meta.push(item.countries_ru.slice(0, 2).join(', '));
        if (item.vote_average > 0) meta.push('★ ' + item.vote_average.toFixed(1));
        var q = item.release_quality || item.cp_quality || qcache(item); if (q) meta.push(esc(q));
        if (item.kp_new > 0) meta.push('новых серий: ' + item.kp_new);
      }
      if (meta.length) h += '<div class="im">' + meta.join(' · ') + '</div>';
      var gs = item.genres_ru && item.genres_ru.length ? item.genres_ru : (item.genre_ids || []).map(function (g) { return GENRES[g]; }).filter(Boolean);
      if (gs.length) h += '<div class="ig">' + esc(gs.join(' · ')) + '</div>';
      if (item.wrestling && !item.overview) h += '<div class="io2">Raw, SmackDown и PPV по порядку выхода' + (item.wr_new ? ' · не просмотрено: ' + item.wr_new : '') + '</div>';
      if (item.overview) h += '<div class="io2">' + esc(item.overview) + '</div>';
      box.innerHTML = h; box.classList.remove('hidden');
    }, 160);
  }
  function esc(t) { return String(t == null ? '' : t).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  // ---------- фокус и навигация ----------
  function focusables() { return state.nav; }
  function clearFocus() { var f = document.querySelector('.focus'); if (f) f.classList.remove('focus'); }

  function setFocus(r, c, soft) {
    var nav = state.nav;
    if (!nav.length) return;
    r = Math.max(0, Math.min(nav.length - 1, r));
    var items = nav[r].items;
    if (!items.length) return;
    c = Math.max(0, Math.min(items.length - 1, c));
    clearFocus();
    // ряд, из которого ушёл фокус, показываем с начала: иначе прокрученная лента прячет первые карточки (например, Wrestling)
    var prev = state.r;
    if (prev !== r && nav[prev] && nav[prev].strip) nav[prev].strip.scrollLeft = 0;
    state.r = r; state.c = c; state.mem[nav[r].id] = c;
    var e = items[c];
    e.classList.add('focus');
    for (var near = Math.max(0, c - 1); near <= Math.min(items.length - 1, c + 2); near++) loadCardPoster(items[near]);
    showInfo(e._item || null);
    var strip = nav[r].strip;
    if (strip && strip.scrollWidth > strip.clientWidth) {
      var want = e.offsetLeft - strip.clientWidth * 0.18;
      strip.scrollLeft = Math.max(0, want);
    }
    var sc = $('scroll'), row = nav[r].box;
    if (row && !soft) sc.scrollTop = Math.max(0, row.offsetTop - $('apps').offsetTop - (r > 1 ? 10 : 0));
    if (nav[r].id === 'bar' || nav[r].id === 'apps') sc.scrollTop = 0;
  }

  function move(dr, dc) {
    var nav = state.nav;
    if (dc) { setFocus(state.r, state.c + dc); return; }
    var r = state.r + dr;
    while (r >= 0 && r < nav.length && !nav[r].items.length) r += dr;
    if (r < 0 || r >= nav.length) return;
    var cur = nav[state.r].items[state.c], cx = 0;
    if (cur) { var b = cur.getBoundingClientRect(); cx = b.left + b.width / 2; }
    // тот же ряд, который уже посещали: вернуться на запомненную позицию, иначе — ближайший по горизонтали
    var mem = state.mem[nav[r].id];
    var best = typeof mem === 'number' ? mem : 0;
    if (typeof mem !== 'number') {
      var bd = 1e9, items = nav[r].items;
      for (var i = 0; i < items.length; i++) { var bb = items[i].getBoundingClientRect(); var d = Math.abs(bb.left + bb.width / 2 - cx); if (d < bd) { bd = d; best = i; } }
    }
    setFocus(r, best);
  }

  function enter() {
    var nav = state.nav, e = nav[state.r] && nav[state.r].items[state.c];
    if (e && e._act) e._act();
  }

  var lp = { t: null, fired: false, down: false, at: 0 };
  document.addEventListener('keydown', function (ev) {
    var k = ev.keyCode;
    // Окно открыто этим удержанием: повторы не должны нажать его первый пункт.
    if ((k === 13 || k === 23) && lp.down && lp.fired) { ev.preventDefault(); return; }
    if (state.setupOpen) { setupKey(ev); return; }
    if (state.move) { moveKey(ev); return; }
    if (k === 13 || k === 23) {
      // на пультах удержание приходит повторяющимися keydown (иногда без «чистого» первого): стартуем и от повтора,
      // и срабатываем по времени удержания, не полагаясь только на таймер
      var now = Date.now();
      if (!lp.down) { lp.down = true; lp.fired = false; lp.at = now; clearTimeout(lp.t); lp.t = setTimeout(function () { if (lp.down && !lp.fired && longPress()) lp.fired = true; }, 600); }
      else if (!lp.fired && now - lp.at > 600) { clearTimeout(lp.t); if (longPress()) lp.fired = true; }
      ev.preventDefault(); return;
    }
    if (k === 37) move(0, -1); else if (k === 39) move(0, 1); else if (k === 38) move(-1, 0); else if (k === 40) move(1, 0); else return;
    ev.preventDefault();
  });
  document.addEventListener('keyup', function (ev) {
    var k = ev.keyCode;
    if (k !== 13 && k !== 23) return;
    if (!lp.down) return;                       // нажатие началось не на главном экране (например, в окне) — не реагируем
    lp.down = false; clearTimeout(lp.t);
    if (!lp.fired && !state.setupOpen && !state.move && Date.now() - lp.at > 550 && longPress()) { lp.fired = true; ev.preventDefault(); return; }
    if (state.move) { ev.preventDefault(); return; }
    if (!lp.fired && !state.setupOpen) enter();
    ev.preventDefault();
  });
  function cancelHeldOk() { clearTimeout(lp.t); lp.down = false; lp.fired = true; }
  window.addEventListener('blur', cancelHeldOk);
  document.addEventListener('visibilitychange', function () { if (document.hidden) cancelHeldOk(); });

  // ---------- долгое нажатие на карточке: «Мои сериалы / фильмы / аниме», «Продолжить просмотр» ----------
  function mediaOf(it) { return it.media_type === 'tv' || it.name || it.first_air_date ? 'tv' : 'movie'; }
  function isAnime(it) { return it.home_kind ? it.home_kind.indexOf('anime_') === 0 : (it.genre_ids || []).indexOf(16) >= 0 && it.original_language === 'ja'; }
  function listName(it) {
    var k=it.home_kind;if(k==='cartoon_movie')return 'Мои мультфильмы';if(k==='cartoon_series')return 'Мои мультсериалы';if(k&&k.indexOf('anime_')===0)return 'Мои аниме';
    return mediaOf(it)==='tv'?(isAnime(it)?'Мои аниме':'Мои сериалы'):'Мои фильмы';
  }
  function longPress() {
    if (state.setupOpen) return false;
    var nav = state.nav[state.r], e = nav && nav.items[state.c];
    if (!e) return false;
    if (e._pkg) { startMove(e); return true; }
    if (e._item) { cardMenu(e, nav.id); return true; }
    return false;
  }


  function menuShow(title, entries) {
    var box = $('setup'); box.innerHTML = ''; box.className = '';
    box.appendChild(el('h1', null, title));
    var list = el('div', 'list'); box.appendChild(list);
    var items = [];
    entries.forEach(function (en) { var t = tile(en[0], en[1] || null, en[2]); list.appendChild(t); items.push(t); });
    setupShow(true); setupItems(items);
  }
  function menuClose() { setupShow(false); rebuildNav(); }

  function dropCard(e, rowId) {
    // убрать карточку из ряда сразу, не дожидаясь обновления
    var nav = state.nav[state.r]; var idx = nav ? nav.items.indexOf(e) : -1;
    releasePosters(e); if (e.parentNode) e.parentNode.removeChild(e);
    var r = (state.cfg || []).filter(function (x) { return x.id === rowId; })[0]; if (r) { r._sig = ''; if (r._strip && !r._strip.querySelector('.card:not(.all)')) { r._strip.innerHTML = ''; r._box.classList.add('hidden'); save(rowKey(r.id), []); } }
    rebuildNav(); setFocus(state.r, Math.max(0, idx - (idx > 0 ? 1 : 0)), true);
  }

  function ytPackName() {
    var s = userContext && userContext.profile && userContext.profile.settings;
    return (s && s.youtube_profile) || (state.dev || {}).ytprof || 'main';
  }

  function cardMenu(e, rowId) {
    var it = e._item, title = it.title || it.name || '';
    if(it.history_card){
      menuShow(title,[['Открыть',null,function(){menuClose();e._act();}],['Убрать из истории',null,function(){
        writeApi('/home/history/hide',{card:it.history_card},function(d){if(!d.ok){toast('Не удалось изменить историю');return;}
          menuClose();dropCard(e,rowId);state.mem={};state.lastLoad=0;loadRows();});
      }],['Назад',null,menuClose]]);return;
    }
    if (rowId === 'youtube') {
      var cid = it.channel_id, ent = [['Открыть видео', null, function () { menuClose(); e._act(); }]];
      if (cid) {
        api('/yt/subs?profile=' + encodeURIComponent(ytPackName()), function (d) {
          var on = (d.channels || []).some(function (c) { return c.id === cid; });
          ent.push([on ? 'Отписаться от канала' : 'Подписаться на канал', it.channel || '', function () {
            var pf = encodeURIComponent(ytPackName());
            api(on ? '/yt/subs/remove?profile=' + pf + '&ch=' + encodeURIComponent(cid) : '/yt/subs/add?profile=' + pf + '&channel=' + encodeURIComponent(cid), function () { toast(on ? 'Отписка' : 'Подписка: ' + (it.channel || '')); menuClose(); state.lastLoad = 0; });
          }]);
          ent.push(['Закрыть', null, menuClose]); menuShow(title, ent);
        }, function () { ent.push(['Закрыть', null, menuClose]); menuShow(title, ent); });
      } else { ent.push(['Закрыть', null, menuClose]); menuShow(title, ent); }
      return;
    }
    var type = mediaOf(it), prof = encodeURIComponent(state.profile), name = listName(it);
    api('/kp/watchlist/state?type=' + type + '&tmdb=' + it.id + '&profile=' + prof, function (d) {
      if(d.kind)it.home_kind=d.kind;name=listName(it);
      var on = !!d.on, ent = [];
      ent.push([on ? 'Убрать из «' + name + '»' : 'Добавить в «' + name + '»', null, function () {
        // желаемое состояние фиксируется при нажатии и уходит в запросе как есть: повтор после потерянного ответа не отменит уже применённое действие
        var wantUrl = '/kp/watchlist/toggle?type=' + type + '&tmdb=' + it.id + '&profile=' + prof + '&on=' + (on ? 0 : 1);
        var done = function (r) {
          toast(r.on ? 'Добавлено в «' + name + '»' : 'Убрано из «' + name + '»'); menuClose();
          if (!r.on && (rowId === 'watching' || rowId === 'watching_anime' || rowId === 'my_movies')) dropCard(e, rowId);
          state.lastLoad = 0; loadRows();
        };
        api(wantUrl, done, function () {
          api(wantUrl, done, function () { toast('Не удалось изменить'); menuClose(); });
        });
      }]);
      if (rowId === 'continue') ent.push(['Убрать из «Продолжить просмотр»', null, function () {
        api('/kp/continue/hide?type=' + type + '&tmdb=' + it.id + '&profile=' + prof, function () { toast('Убрано из «Продолжить просмотр»'); menuClose(); dropCard(e, rowId); }, function () { toast('Не удалось убрать'); menuClose(); });
      }]);
      ent.push(['Открыть карточку', null, function () { menuClose(); e._act(); }]);
      ent.push(['Закрыть', null, menuClose]);
      menuShow(title, ent);
    }, function () { menuShow(title, [['Нет связи с сервером', null, menuClose]]); });
  }

  // Режим перемещения: удержание на приложении «поднимает» плитку; ←/→ двигают её по ряду, ОК — фиксирует, ↑ — убрать из ряда, Назад — отмена
  function startMove(e) {
    var strip = state.nav[state.r].strip;
    state.move = { e: e, strip: strip, orig: pinnedList().slice() };
    e.classList.add('moving');
    toast('Влево/вправо — переместить · ОК — готово · ↑ — убрать из ряда');
  }
  function moveCommit() { var m = state.move; if (!m) return; m.e.classList.remove('moving'); state.move = null; toast('Порядок сохранён'); }
  function moveCancel() { var m = state.move; if (!m) return; m.e.classList.remove('moving'); state.move = null; savePinned(m.orig); buildApps(); rebuildNav(); setFocus(1, 0, true); }
  function moveKey(ev) {
    var k = ev.keyCode, m = state.move, e = m.e; ev.preventDefault();
    if (k === 13 || k === 23) { if (lp.down) return; moveCommit(); return; }
    if (k === 38) { var p = pinnedList().filter(function (x) { return x !== e._pkg; }); savePinned(p); state.move = null; buildApps(); rebuildNav(); setFocus(1, 0, true); toast('Убрано из ряда'); return; }
    if (k !== 37 && k !== 39) return;
    var apps = [].slice.call(m.strip.querySelectorAll('.app')).filter(function (x) { return x._pkg; });
    var i = apps.indexOf(e), j = i + (k === 37 ? -1 : 1);
    if (j < 0 || j >= apps.length) return;
    var other = apps[j];
    if (k === 37) m.strip.insertBefore(e, other); else m.strip.insertBefore(e, other.nextSibling);
    var order = [].slice.call(m.strip.querySelectorAll('.app')).filter(function (x) { return x._pkg; }).map(function (x) { return x._pkg; });
    savePinned(order); rebuildNav();
    var idx = state.nav[state.r].items.indexOf(e); if (idx >= 0) setFocus(state.r, idx, true);
  }

  // долгое нажатие на приложении в ряду: убрать из ряда
  function appMenu(e) {
    function moveBy(d) {
      var p = pinnedList().slice(), i = p.indexOf(e._pkg), j = i + d;
      if (i < 0 || j < 0 || j >= p.length) { toast(d < 0 ? 'Уже первое' : 'Уже последнее'); return; }
      var t = p[i]; p[i] = p[j]; p[j] = t; savePinned(p);
      menuClose(); buildApps(); rebuildNav(); setFocus(1, j + 1, true);        // +1: перед закреплёнными стоит «ТВ»
      var ne = state.nav[1] && state.nav[1].items[j + 1]; if (ne && ne._pkg) setTimeout(function () { appMenu(ne); }, 120);
    }
    menuShow(e._label || 'Приложение', [
      ['Запустить', null, function () { menuClose(); e._act(); }],
      ['← Переместить влево', null, function () { moveBy(-1); }],
      ['Переместить вправо →', null, function () { moveBy(1); }],
      ['Убрать из ряда', null, function () { var p = pinnedList().filter(function (x) { return x !== e._pkg; }); savePinned(p); menuClose(); buildApps(); rebuildNav(); setFocus(1, 0, true); }],
      ['Закрыть', null, menuClose]
    ]);
  }

  // ---------- первая настройка: поиск сервера и профиль воспроизведения ----------
  function setupShow(open) { state.setupOpen = open; if (open && !state.searchOpen) $('setup').className = ''; $('setup').classList.toggle('hidden', !open); if (!open) { state.setupItems = []; } }
  function setupItems(items) { state.setupItems = items; state.setupIdx = 0; setupFocus(); }
  function setupFocus() {
    var it = state.setupItems; if (!it.length) return;
    for (var i = 0; i < it.length; i++) it[i].classList.remove('focus');
    var e = it[state.setupIdx]; e.classList.add('focus');
    try { e.scrollIntoView({ block: 'nearest' }); } catch (e3) {}
    if (e.tagName === 'INPUT') e.focus(); else { if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); }
  }
  function setupKey(ev) {
    var k = ev.keyCode, it = state.setupItems, e = it[state.setupIdx];
    if (k === 13 || k === 23) { if (e && e._act) { e._act(); ev.preventDefault(); } return; }
    if (e && e.tagName === 'INPUT' && (k === 37 || k === 39)) {            // стрелки двигают курсор, а у края текста — уходят на соседние кнопки
      var cp = e.selectionStart, atEnd = cp === e.value.length && e.selectionEnd === cp, atStart = cp === 0;
      if (!((k === 39 && atEnd) || (k === 37 && atStart))) return;
    }
    if (e && e.classList.contains('srow') && (k === 37 || k === 39)) { if (k === 37 && e._left) e._left(); if (k === 39 && e._right) e._right(); ev.preventDefault(); return; }
    if (e && e._left && k === 37) { e._left(); ev.preventDefault(); return; }
    if (e && e._right && k === 39) { e._right(); ev.preventDefault(); return; }
    if ((k === 38 || k === 40) && e && !e.classList.contains('gc') && document.querySelector('#setup .sline')) {
      var b0 = e.getBoundingClientRect(), bst = -1, bdd = 1e9;
      for (var j = 0; j < it.length; j++) { if (j === state.setupIdx) continue; var r0 = it[j].getBoundingClientRect(); var dy0 = r0.top - b0.top; if ((k === 40 && dy0 > 5) || (k === 38 && dy0 < -5)) { var d0 = Math.abs(dy0) * 3 + Math.abs(r0.left - b0.left); if (d0 < bdd) { bdd = d0; bst = j; } } }
      if (bst >= 0) { state.setupIdx = bst; setupFocus(); }
      ev.preventDefault(); return;
    }
    var grid = document.querySelector('#setup .picker');
    if (grid && (k === 38 || k === 40) && e && e.classList.contains('gc')) {
      // сетка приложений: ближайшая плитка выше/ниже по горизонтали
      var b = e.getBoundingClientRect(), best = -1, bd = 1e9;
      for (var i = 0; i < it.length; i++) { if (i === state.setupIdx || !it[i].classList.contains('gc')) continue; var r = it[i].getBoundingClientRect(); var dy = r.top - b.top; if ((k === 40 && dy > 5) || (k === 38 && dy < -5)) { var d = Math.abs(dy) * 3 + Math.abs(r.left - b.left); if (d < bd) { bd = d; best = i; } } }
      if (best < 0 && k === 38) best = 0;                // выше первого ряда — к полю ввода
      if (best < 0 && k === 40) { for (var q = 0; q < it.length; q++) if (!it[q].classList.contains('gc')) best = q; }   // ниже последней плитки — к кнопке «Готово»
      if (best >= 0) { state.setupIdx = best; setupFocus(); try { it[best].scrollIntoView({ block: 'nearest' }); } catch (e2) {} }
      ev.preventDefault(); return;
    }
    if (k === 37 || k === 38) { state.setupIdx = Math.max(0, state.setupIdx - 1); setupFocus(); ev.preventDefault(); }
    else if (k === 39 || k === 40) { state.setupIdx = Math.min(it.length - 1, state.setupIdx + 1); setupFocus(); ev.preventDefault(); }
  }
  function tile(title, sub, act) { var t = el('div', 'tile', title); if (sub) t.appendChild(el('small', null, sub)); t._act = act; return t; }

  function adoptServer(url) {
    getJson(url + '/home/whoami', function (w) {
      var list = w.profiles || [], saved = load('mc_profile', '');
      var chosen = saved && list.filter(function (pr) { return pr.id === saved; })[0];
      if (!chosen) {
        var auth = list.filter(function (pr) { return pr.authorized; });
        chosen = auth.length === 1 ? auth[0] : (list.filter(function (pr) { return pr.id === 'main'; })[0] || list[0]);
      }
      setupFinish(url, chosen ? chosen.id : 'main', chosen && chosen.maxh ? chosen.maxh : 1080);
    }, function () { setupFinish(url, load('mc_profile', '') || 'main', 1080); });
  }

  function setupFind(ask, attempt) {
    attempt = attempt || 0;
    var box = $('setup'); box.innerHTML = '';
    box.appendChild(el('h1', null, 'Lampa Media Center'));
    var p = el('p', null, 'Ищу сервер в локальной сети…'); box.appendChild(p);
    var list = el('div', 'list'); box.appendChild(list);
    setupShow(true);
    var found = [];
    function showChoices() {
      list.innerHTML = '';
      var items = [];
      found.forEach(function (o) {
        var t = tile(o.name || 'Lampa Media Center', o.url, function () { if (ask) setupProfile(o.url); else adoptServer(o.url); });
        list.appendChild(t); items.push(t);
      });
      var manual = tile('Ввести адрес вручную', null, function () { setupManual(); });
      list.appendChild(manual); items.push(manual);
      setupItems(items);
    }
    if (ask) showChoices();
    window.__mc.onFound = function (o) {
      found.push(o);
      if (ask) { p.textContent = 'Найден сервер. Выберите:'; showChoices(); }
      else p.textContent = found.length === 1 ? 'Сервер найден, подключаюсь…' : 'Найдено серверов: ' + found.length;
    };
    window.__mc.onScanDone = function () {
      if (!ask && found.length === 1) { adoptServer(found[0].url); return; }
      if (!found.length && attempt < 1 && document.body.contains(p)) { p.textContent = 'Сеть ещё поднимается, ищу сервер ещё раз…'; setTimeout(function () { if (document.body.contains(p)) setupFind(ask, attempt + 1); }, 1500); return; }
      if (!found.length) p.textContent = 'Сервер не найден. Введите адрес вручную (например http://192.168.1.80:9118).';
      else p.textContent = 'Найден сервер. Выберите:';
      showChoices();
    };
    var hints = []; var last = MC.server(); if (last) hints.push(last);
    MC.discover(JSON.stringify(hints));
  }

  function setupManual() {
    var box = $('setup'); box.innerHTML = '';
    box.appendChild(el('h1', null, 'Адрес сервера'));
    box.appendChild(el('p', null, 'Введите адрес Lampa Media Center:'));
    var inp = document.createElement('input'); inp.type = 'text'; inp.value = MC.server() || 'http://192.168.1.80:9118'; box.appendChild(inp);
    var go = tile('Подключиться', null, function () { var u = inp.value.trim(); if (!/^https?:\/\//.test(u)) u = 'http://' + u; if (!/:\d+$/.test(u.replace(/^https?:\/\//, ''))) u += ':9118'; setupProfile(u.replace(/\/$/, '')); });
    var back = tile('Назад к поиску', null, setupFind);
    inp._act = function () { go._act(); };
    box.appendChild(go); box.appendChild(back);
    setupItems([inp, go, back]);
  }

  function setupProfile(url) {
    var box = $('setup'); box.innerHTML = '';
    box.appendChild(el('h1', null, 'Профиль воспроизведения'));
    var p = el('p', null, 'Подключаюсь к ' + url + '…'); box.appendChild(p);
    setupItems([]);
    getJson(url + '/home/whoami', function (w) {
      p.textContent = 'Выберите профиль: от него зависит аккаунт Кинопаба и предельное качество.';
      var list = el('div', 'list'); box.appendChild(list);
      var items = [];
      (w.profiles || []).forEach(function (pr) {
        var t = tile(pr.label || pr.id, pr.authorized ? 'аккаунт подключён · до ' + pr.maxh + 'p' : 'аккаунт не подключён · до ' + pr.maxh + 'p', function () { setupFinish(url, pr.id, pr.maxh); });
        list.appendChild(t); items.push(t);
      });
      var none = tile('Без аккаунта Кинопаба', 'ленты из TMDB и торрентов', function () { setupFinish(url, 'main', 1080); });
      list.appendChild(none); items.push(none);
      var back = tile('Назад', null, setupFind); box.appendChild(back); items.push(back);
      setupItems(items);
    }, function () {
      p.textContent = 'Сервер не отвечает. Проверьте адрес.';
      var b = tile('Назад', null, setupManual); box.appendChild(b); setupItems([b]);
    });
  }

  function setupFinish(url, prof, maxh) {
    state.server = url; state.profile = prof;
    MC.setServer(url); save('mc_profile', prof); save('mc_setup', '1');
    var dev = MC.deviceId(), name = MC.deviceName();
    // запись устройства на сервере: профиль воспроизведения и предел качества
    var init = encodeURIComponent(JSON.stringify({ kpprof: prof, maxh: String(maxh), auto: '0', mc_auto: '0', engine: 'exo', buf: '1' }));
    api('/home/dev/hello?dev=' + encodeURIComponent(dev) + '&name=' + encodeURIComponent(name) + '&init=' + init, function () {
      api('/home/dev/set?dev=' + encodeURIComponent(dev) + '&k=kpprof&v=' + encodeURIComponent(prof), function () {});
      api('/home/dev/set?dev=' + encodeURIComponent(dev) + '&k=maxh&v=' + encodeURIComponent(String(maxh)), function () {});
    }, function () {});
    setupShow(false);
    seedLampa(function () { startHome(true); });
  }

  // ---------- посев настроек Lampa: язык и профиль, чтобы она не показывала свой мастер первого запуска ----------
  function seedLampa(done) {
    if (load('mc_seeded', '') === state.server + '|' + state.profile) { if (done) done(); return; }
    var f = document.createElement('iframe'); f.style.cssText = 'position:fixed;width:1px;height:1px;left:-9px;top:-9px;border:0;opacity:0';
    var fin = false;
    function end() { if (fin) return; fin = true; window.removeEventListener('message', on); try { f.remove(); } catch (e) {} save('mc_seeded', state.server + '|' + state.profile); if (done) done(); }
    function on(ev) { if (ev.data === 'mc-seed-done') end(); }
    window.addEventListener('message', on);
    f.src = state.server + '/mc/seed?lang=ru&prof=' + encodeURIComponent(state.profile);
    document.body.appendChild(f);
    setTimeout(end, 4000);
  }

  // ---------- главный экран ----------
  var DEFAULT_ROWS = [
    { id: 'continue', title: 'Продолжить просмотр', style: 'wide' }, { id: 'history', title: 'История', style: 'wide' }, { id: 'watching', title: 'Мои сериалы', style: 'poster' },
    { id: 'new_movie', title: 'Новые фильмы', style: 'poster' }, { id: 'new_serial', title: 'Новые сериалы', style: 'poster' },
    { id: 'watching_anime', title: 'Мои аниме', style: 'poster' }, { id: 'anime', title: 'Аниме', style: 'poster' },
    { id: 'ru_serial', title: 'Отечественные сериалы', style: 'poster' }, { id: 'ru_movie', title: 'Отечественные фильмы', style: 'poster' },
    { id: 'new_cartoon', title: 'Мультфильмы', style: 'poster' }, { id: 'youtube', title: 'YouTube · подписки', style: 'wide' }, { id: 'my_movies', title: 'Мои фильмы', style: 'poster' }
  ];

  var MON = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
  function dateLabel(iso) {
    var t = Date.parse(iso); if (!t) return '';
    var d = new Date(t), n = new Date();
    var days = Math.floor((new Date(n.getFullYear(), n.getMonth(), n.getDate()) - new Date(d.getFullYear(), d.getMonth(), d.getDate())) / 86400000);
    if (days <= 0) return 'сегодня'; if (days === 1) return 'вчера'; if (days < 7) return days + ' дн. назад';
    return d.getDate() + ' ' + MON[d.getMonth()] + (d.getFullYear() !== n.getFullYear() ? ' ' + d.getFullYear() : '');
  }
  function fmtDur(s) { s = Math.floor(s || 0); if (!s) return ''; var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), c = s % 60; return (h ? h + ':' + ('0' + m).slice(-2) : m) + ':' + ('0' + c).slice(-2); }

  function picOf(c) { return c.querySelector('.pic') || c; }

  var posterObserver = null;
  function loadPoster(img) {
    var src = img && img.getAttribute('data-poster-src');
    if (!src) return;
    img.removeAttribute('data-poster-src');
    img.src = src;
    if (posterObserver) posterObserver.unobserve(img);
  }
  function loadCardPoster(card) { if (card) loadPoster(card.querySelector('img')); }
  function deferPoster(img, src) {
    if (!src) return;
    if (!window.IntersectionObserver) { img.src = src; return; }
    if (!posterObserver) posterObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) { if (entry.isIntersecting) loadPoster(entry.target); });
    }, { rootMargin: '250px 150px' });
    img.setAttribute('data-poster-src', src);
    posterObserver.observe(img);
  }
  function releasePosters(container) {
    if (!posterObserver || !container) return;
    [].forEach.call(container.querySelectorAll('img[data-poster-src]'), function (img) { posterObserver.unobserve(img); });
  }

  function cardEl(item, row) {
    var yt = row.id === 'youtube' || item.provider === 'youtube';
    var c = el('div', 'card ' + row.style);
    var img = document.createElement('img'); img.alt = ''; img.decoding = 'async';
    var src = '';
    if (yt) src = item.thumb ? state.server + '/yt/p?u=' + b64url(item.thumb) : '';
    else if (item.wrestling || item.kp_legacy) src = item.img || '';
    else if (row.style === 'wide') { var bp = item.backdrop_path || item.poster_path; src = bp ? state.server + '/tmdb/img/t/p/w500' + bp : ''; }
    else src = item.poster_path ? state.server + '/tmdb/img/t/p/w342' + item.poster_path : '';
    deferPoster(img, src);
    var pic = el('div', 'pic'); pic.appendChild(img); c.appendChild(pic);
    var title = item.title || item.name || '';
    c.appendChild(el('div', 'ttl', title));
    if(item.history_episode)pic.appendChild(el('div','hist',item.history_episode));
    if (yt) {
      var d = item.published ? dateLabel(item.published) : (item.ago || '');
      if (d) pic.appendChild(el('div', 'dt', d));
      var dur = fmtDur(item.duration); if (dur) pic.appendChild(el('div', 'dur', dur));
    } else {
      var isTv = item.wrestling || item.media_type === 'tv' || !!item.name || !!item.first_air_date;
      if (isTv) pic.appendChild(el('div', 'tvl', 'TV'));
      if (item.wrestling) { if (item.wr_new) pic.appendChild(el('div', 'kpb', String(item.wr_new))); }
      else if (item.kp_id) pic.appendChild(el('div', 'kpb', item.kp_new > 0 ? 'KP +' + item.kp_new : 'KP'));
      var q = item.release_quality || item.cp_quality || qcache(item);
      if (q) pic.appendChild(el('div', 'bdg', q));
      if (item.vote_average > 0) pic.appendChild(el('div', 'rt', (item.vote_average).toFixed(1)));
      c._tv = isTv;
      if (!item.kp_id && !item.wrestling) needKp(item, c);
      if (!q && !item.wrestling && !item.kp_legacy) needQuality(item, c, isTv);
    }
    c._act = function () {
      if (yt) {
        var fn = '(function(a,b){var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__yt){clearInterval(t);window.__yt.play(a,b)}else if(++n<=80)t=setTimeout(run,100)}run()})';
        toast('Запускаю ролик…');
        lampaGo(JSON.stringify({ js: fn, arg: JSON.stringify(item.id) + ',' + JSON.stringify(title), keep: true }));
      } else if (item.kp_legacy) {
        // тайтл без карточки TMDB: открывается страницей самого Кинопаба
        lampaGo(JSON.stringify({ js: '(function(a){window.__mc_ret=1;var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__kp_open){clearInterval(t);window.__kp_open(a)}else if(++n<=80)t=setTimeout(run,100)}run()})', arg: String(item.kp_id) }));
      } else if (item.wrestling) {
        lampaGo(JSON.stringify({ js: "(function(){window.__mc_ret=1;Lampa.Activity.push({url:'',title:'Wrestling',component:'wrestling',page:1})})", arg: '' }));
      } else {
        lampaGo(JSON.stringify({ id: item.id, media: item.media_type || (item.name || item.first_air_date ? 'tv' : 'movie'), source: 'tmdb' }));
      }
    };
    return c;
  }

  // ---------- «KP» и качество для карточек, у которых сервер их не передал (пачками, с кэшем) ----------
  var kpQ = {}, kpT = null, qQ = {}, qT = null;
  // кэши значков и качества привязаны к серверу и профилю: чужой сервер/профиль не показывает чужие значки
  function scopedKey(base) { var n = String(state.server || '') + '|' + String(state.profile || ''), h = 0; for (var i = 0; i < n.length; i++) h = (h * 31 + n.charCodeAt(i)) | 0; return base + '_' + (h >>> 0).toString(36); }
  function keyOf(item, tv) { return (tv ? 'tv:' : 'movie:') + item.id; }
  // запись кэша: {v, at}; прежние голые значения читаются как «без срока»: положительные годятся, пустые/нулевые считаются неизвестными и перезапрашиваются
  var CACHE_MAX = 800, TTL_POS = 14 * 86400000, TTL_NEG = 86400000;
  function cget(m, k) {
    var e = m[k]; if (e === undefined || e === null) return undefined;
    if (typeof e === 'object') { var age = Date.now() - (e.at || 0); return age < (e.v ? TTL_POS : TTL_NEG) ? e.v : undefined; }
    return e ? e : undefined;
  }
  function cput(m, k, v) { m[k] = { v: v, at: Date.now() }; }
  function ctrim(m) {
    var ks = Object.keys(m); if (ks.length <= CACHE_MAX) return m;
    ks.sort(function (a, b) { return ((m[a] && m[a].at) || 0) - ((m[b] && m[b].at) || 0); });
    ks.slice(0, ks.length - CACHE_MAX).forEach(function (k) { delete m[k]; });
    return m;
  }
  // контекст запроса фиксируется до отправки: ответ, пришедший после смены профиля/сервера, пишется под ключ СВОЕГО контекста и не рисуется на чужом экране
  function ctxOf() { return { server: state.server, profile: state.profile, generation:userGeneration }; }
  function sameCtx(c) { return c.server === state.server && c.profile === state.profile && c.generation === userGeneration; }
  function scopedFor(base, c) { var n = String(c.server || '') + '|' + String(c.profile || ''), h = 0; for (var i = 0; i < n.length; i++) h = (h * 31 + n.charCodeAt(i)) | 0; return base + '_' + (h >>> 0).toString(36); }
  function qualityKey(item) { return (item.media_type === 'tv' || item.name || item.first_air_date ? 't' : 'm') + item.id; }
  function qcache(item) { var m = load(scopedKey('mc_q_v2'), {}); return cget(m, qualityKey(item)) || ''; }
  function refreshInfoQuality(keys) { if (infoItem && keys.indexOf(qualityKey(infoItem)) >= 0) showInfo(infoItem); }
  function needKp(item, card) {
    var tv = item.media_type === 'tv' || !!item.name, k = keyOf(item, tv);
    var m = load(scopedKey('mc_kp'), {}), v = cget(m, k); if (v !== undefined) { if (v) picOf(card).appendChild(el('div', 'kpb', 'KP')); return; }
    (kpQ[k] = kpQ[k] || []).push(card);
    if (!kpT) kpT = setTimeout(flushKp, 500);
  }
  function flushKp() {
    kpT = null; var keys = Object.keys(kpQ).slice(0, 40); if (!keys.length) return;
    var batch = {}; keys.forEach(function (k) { batch[k] = kpQ[k]; delete kpQ[k]; });
    var cx = ctxOf(), ck = scopedFor('mc_kp', cx);
    api('/kp/has?keys=' + encodeURIComponent(keys.join(',')) + '&profile=' + encodeURIComponent(cx.profile), function (d) {
      var m = load(ck, {}), here = sameCtx(cx);
      keys.forEach(function (k) { cput(m, k, d[k] || 0); if (here && d[k]) (batch[k] || []).forEach(function (c) { if (!c.querySelector('.kpb')) picOf(c).appendChild(el('div', 'kpb', 'KP')); }); });
      save(ck, ctrim(m)); if (Object.keys(kpQ).length) kpT = setTimeout(flushKp, 300);
    }, function () { if (Object.keys(kpQ).length) kpT = setTimeout(flushKp, 5000); });
  }
  function needQuality(item, card, tv) {
    var yr = String(item.release_date || item.first_air_date || '').slice(0, 4), title = item.title || item.name;
    if (!title || !yr) return;
    var k = (tv ? 't' : 'm') + item.id;
    (qQ[k] = qQ[k] || { item: { id: item.id, title: title, year: yr, tv: tv }, cards: [] }).cards.push(card);
    if (!qT) qT = setTimeout(flushQuality, 900);
  }
  function flushQuality() {
    qT = null; var keys = Object.keys(qQ).slice(0, 15); if (!keys.length) return;
    var batch = {}, items = []; keys.forEach(function (k) { batch[k] = qQ[k]; items.push(qQ[k].item); delete qQ[k]; });
    var cx = ctxOf(), ck = scopedFor('mc_q_v2', cx);
    var again = function (ms) { if (Object.keys(qQ).length) qT = setTimeout(flushQuality, ms); };
    function retryBatch(){if(!sameCtx(cx))return;keys.forEach(function(k){if(qQ[k])qQ[k].cards=qQ[k].cards.concat(batch[k].cards);else qQ[k]=batch[k];});again(8000);}
    var x = new XMLHttpRequest(); x.open('POST', cx.server + '/kp/quality', true); ownerHeader(x,cx.server+'/kp/quality'); x.timeout = 60000; x.setRequestHeader('Content-Type', 'application/json');
    x.onload = function () {
      if(!sameCtx(cx))return;
      if(x.status!==200){retryBatch();return;}
      try {
        var d = JSON.parse(x.responseText), m = load(ck, {}), here = sameCtx(cx);if(!d||d.error){retryBatch();return;}
        keys.forEach(function (k) { cput(m, k, d[k] || ''); if (here && d[k]) batch[k].cards.forEach(function (c) { if (!c.querySelector('.bdg')) picOf(c).appendChild(el('div', 'bdg', d[k])); }); });
        save(ck, ctrim(m));
        refreshInfoQuality(keys);
      } catch (e) {retryBatch();return;}
      again(1500);
    };
    // сбой запроса не оставляет остаток очереди без таймера
    x.onerror = x.ontimeout = retryBatch;
    x.send(JSON.stringify(items));
  }

  // ключ карточки — тип:id (в смешанных рядах фильм и сериал могут иметь один числовой id); подпись включает всё, что рисуется на карточке:
  // иначе при неизменном наборе id счётчик новых серий, качество и рейтинг навсегда оставались прежними
  function strHash(s) { var h = 0; s = String(s); for (var i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return (h >>> 0).toString(36) + ':' + s.length; }
  function cardKey(x) { return (x.media_type || (x.name || x.first_air_date ? 'tv' : 'movie')) + ':' + x.id; }
  function sig(list) {
    return (list || []).map(function (x) {
      return [cardKey(x), x.kp_new, x.release_quality || x.cp_quality || '', x.vote_average || '', x.poster_path || x.img || x.thumb || '', x.title || x.name || '', x.wr_new || '', x.watch_pct || '', x.kp_id || '', x.backdrop_path || '', strHash(x.overview || ''), x.history_episode || '', x.published || '', x.ago || '', x.duration || '', x.channel || '', x.release_date || x.first_air_date || ''].join('~');
    }).join('|');
  }

  // Последняя плитка ленты: открывает страницу Lampa этой категории, где прокрутка уже бесконечная (лента в лаунчере — только первые карточки)
  function allEl(row) {
    var c = el('div', 'card all ' + row.style);
    var pic = el('div', 'pic'); pic.appendChild(el('div', 'allarrow', '›')); c.appendChild(pic);
    c.appendChild(el('div', 'ttl', 'Смотреть все'));
    c._all = true;
    c._act = function () {
      var yt = row.id === 'youtube';
      var fn = yt
        ? '(function(){window.__mc_ret=1;var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__yt&&window.__yt.feed){clearInterval(t);window.__yt.feed()}else if(++n<=80)t=setTimeout(run,100)}run()})'
        : '(function(a,b){window.__mc_ret=1;var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__kp_section){clearInterval(t);window.__kp_section(a,b)}else if(++n<=80)t=setTimeout(run,100)}run()})';
      lampaGo(JSON.stringify({ js: fn, arg: yt ? '' : JSON.stringify(row.title) + ',' + JSON.stringify(row.id), keep: false }));
    };
    return c;
  }

  function fillRow(row, items, fromCache) {
    var box = row._box, strip = row._strip;
    if (!items || !items.length) {
      if (!fromCache || row.id === 'youtube') releasePosters(strip);
      if (row.id === 'youtube') { strip.innerHTML = ''; strip.appendChild(el('div', 'empty', 'Каналов пока нет: добавьте их в веб-панели сервера (раздел YouTube)')); box.classList.remove('hidden'); }
      else if (!fromCache) { strip.innerHTML = ''; box.classList.add('hidden'); }
      row._sig = ''; rebuildNav(); return;
    }
    var s = sig(items);
    if (s === row._sig) return;                        // тот же состав — ничего не перерисовываем
    var focusedId = null, cur = state.nav[state.r];
    var focusedAll = false;
    if (cur && cur.id === row.id) { var fe = cur.items[state.c]; focusedId = fe && fe._item && cardKey(fe._item); focusedAll = !!(fe && fe._all); }
    row._sig = s;
    releasePosters(strip);
    strip.innerHTML = '';
    items.forEach(function (it) { var c = cardEl(it, row); c._item = it; strip.appendChild(c); });
    strip.appendChild(allEl(row));
    box.classList.remove('hidden');
    rebuildNav();
    if (focusedId != null) { var idx = items.map(function (x) { return cardKey(x); }).indexOf(focusedId); if (idx >= 0) setFocus(state.r, idx, true); }
    else if (focusedAll) setFocus(state.r, items.length, true);          // фокус был на плитке «Смотреть все» — остаётся на ней после обновления ленты
  }

  function rebuildNav() {
    var old = state.nav[state.r] && state.nav[state.r].id, oc = state.c;
    var nav = [];
    nav.push({ id: 'bar', box: $('top'), strip: $('bar'), items: [].slice.call($('bar').children) });
    nav.push({ id: 'apps', box: $('apps'), strip: $('apps').querySelector('.strip'), items: [].slice.call(($('apps').querySelector('.strip') || { children: [] }).children) });
    (state.cfg || []).forEach(function (r) { if (r._box && !r._box.classList.contains('hidden')) nav.push({ id: r.id, box: r._box, strip: r._strip, items: [].slice.call(r._strip.querySelectorAll('.card')) }); });
    state.nav = nav;
    // первый показ: фокус на первую ленту, а не на кнопку «Lampa»
    if (document.querySelector('.focus') == null && !state.setupOpen) { var first = nav.length > 1 ? 1 : 0; if (nav[first] && nav[first].items.length) { state.r = first; state.c = 0; nav[first].items[0].classList.add('focus'); return; } }
    if (old) { for (var i = 0; i < nav.length; i++) if (nav[i].id === old) { state.r = i; state.c = Math.min(oc, Math.max(0, nav[i].items.length - 1)); if (document.querySelector('.focus') == null) setFocus(i, state.c, true); return; } }
  }

  function buildBar() {
    var bar = $('bar');
    var sub = $('sub');
    if (sub && sub.parentNode) sub.parentNode.removeChild(sub);
    bar.innerHTML = '';
    if (sub) bar.appendChild(sub);
    function btn(t, f) { var e = el('div', 'btn', t); e._act = f; bar.appendChild(e); }
    btn('Поиск', function () { openSearch(); });
    btn('Настройки', function () { openSettings(); });
    if (!(userContext && userContext.profile.is_child)) btn('Android', function () { MC.settings(); });
    btn('Lampa', function () { lampaGo('{}'); });
    if(userContext&&userContext.profiles_enabled)btn(userContext.profile.name,switchUsers);
  }

  var ALL_APPS = [];
  function pinnedList() {
    var p = load('mc_pinned', null);
    if (p) return p;
    // по умолчанию в ряду только «ТВ», «Приложения», «Настройки», «Добавить»; остальное закрепляется вручную
    return [];
  }
  function savePinned(list) { save('mc_pinned', list); }

  function buildApps() {
    var box = $('apps'); box.innerHTML = '';
    box.appendChild(el('h2', null, 'Приложения'));
    var strip = el('div', 'strip'); box.appendChild(strip);
    try { ALL_APPS = JSON.parse(MC.apps()); } catch (e) { ALL_APPS = []; }
    var byPkg = {}; ALL_APPS.forEach(function (a) { byPkg[a.p] = a; });
    function sys(label, glyph, act) {
      var t = el('div', 'app sys'); t.appendChild(el('div', 'glyph', glyph)); t.appendChild(el('span', null, label)); t._act = act; strip.appendChild(t);
      return t;
    }
    sys('ТВ', '📺', function () { MC.tv(); });
    pinnedList().forEach(function (pk) {
      var a = byPkg[pk]; if (!a) return;
      var t = el('div', 'app'); var im = document.createElement('img');
      var ic = MC.icon(a.p); if (ic) im.src = 'data:image/png;base64,' + ic; t.appendChild(im);
      t.appendChild(el('span', null, a.l)); t._act = function () { MC.launch(a.p); }; t._pkg = a.p; t._label = a.l; strip.appendChild(t);
    });
    if ((state.dev || {}).mc_ym === '1') sys('Яндекс Музыка', '♫', function () {
      lampaGo(JSON.stringify({ js: '(function(){window.__mc_ret=1;var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__ym){clearInterval(t);try{window.__ym.openMenu()}catch(e){}}else if(++n<=80)t=setTimeout(run,100)}run()})', arg: '' }));
    });
    sys('Приложения', '▦', function () { openPicker(false); }).classList.add('app-picker');
    if (!(userContext && userContext.profile.is_child)) sys('Настройки', '⚙', function () { MC.settings(); });
    sys('Добавить', '＋', function () { openPicker(true); });
    refreshMyChannels();
  }

  function refreshMyChannels() {
    var box=$('apps'), strip=box&&box.querySelector('.strip');
    if(!strip)return;
    var cfg=userContext&&userContext.profile.settings||{};
    var enabled=cfg.youtube_mode!=='off'&&(!cfg.sources||cfg.sources.youtube!==false)&&(!state.sources||state.sources.youtube!==false);
    var request=strip._channelsRequest=(strip._channelsRequest||0)+1;
    function paint(hasChannels){
      if(strip!==$('apps').querySelector('.strip')||request!==strip._channelsRequest)return;
      var focused=document.querySelector('#apps .app.focus');
      function nav(){rebuildNav();if(focused)for(var i=0;i<state.nav.length;i++)if(state.nav[i].id==='apps'){var at=state.nav[i].items.indexOf(focused);if(at>=0){state.r=i;state.c=at;}else setFocus(i,Math.min(state.c,state.nav[i].items.length-1),true);}}
      var tile=strip.querySelector('.my-channels');
      if(!hasChannels){if(tile){strip.removeChild(tile);nav();}return;}
      if(tile)return;
      tile=el('div','app sys my-channels');
      tile.appendChild(el('div','glyph','▶'));
      tile.appendChild(el('span',null,'Мои каналы'));
      tile._act=function(){
        lampaGo(JSON.stringify({js:'(function(){window.__mc_ret=1;var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__yt&&window.__yt.channels){clearInterval(t);window.__yt.channels()}else if(++n<=80)t=setTimeout(run,100)}run()})',arg:'',keep:false}));
      };
      var anchor=strip.querySelector('.app-picker');
      strip.insertBefore(tile,anchor?anchor.nextSibling:null);
      nav();
    }
    if(!enabled){paint(false);return;}
    var pack=ytPackName();
    api('/yt/subs?profile='+encodeURIComponent(pack),function(d){paint((d.channels||[]).length>0);},function(){},10000);
  }

  // окно выбора: все приложения (запуск) или закрепление в ряду (Enter включает/выключает)
  function openPicker(pinMode) {
    var box = $('setup'); box.innerHTML = '';
    box.appendChild(el('h1', null, pinMode ? 'Добавить в ряд приложений' : 'Все приложения'));
    box.appendChild(el('p', null, pinMode ? 'Нажмите на приложение, чтобы закрепить или убрать его из ряда.' : 'Выберите приложение для запуска.'));
    var list = el('div', 'list picker'); box.appendChild(list);
    var pins = pinnedList().slice(), items = [];
    var sorted = ALL_APPS.slice().sort(function (a, b) { return a.l.localeCompare(b.l, 'ru'); });
    sorted.forEach(function (a) {
      var t = el('div', 'tile app2 gc'); var im = document.createElement('img'); var ic = MC.icon(a.p); if (ic) im.src = 'data:image/png;base64,' + ic; t.appendChild(im);
      var lab = el('span', null, (pinMode && pins.indexOf(a.p) >= 0 ? '✓ ' : '') + a.l); t.appendChild(lab);
      t._act = function () {
        if (!pinMode) { setupShow(false); MC.launch(a.p); return; }
        var i = pins.indexOf(a.p); if (i >= 0) pins.splice(i, 1); else pins.push(a.p);
        lab.textContent = (pins.indexOf(a.p) >= 0 ? '✓ ' : '') + a.l; savePinned(pins);
      };
      list.appendChild(t); items.push(t);
    });
    var close = tile('Готово', null, function () { setupShow(false); buildApps(); rebuildNav(); setFocus(1, 0, true); });
    box.appendChild(close); items.push(close);
    setupShow(true); setupItems(items);
    state.firstRun = false;
  }

  function buildRows(cfg) {
    var wrap = $('rows'); releasePosters(wrap); wrap.innerHTML = '';
    cfg.forEach(function (r) {
      var box = el('div', 'row'); box.appendChild(el('h2', null, r.title));
      var strip = el('div', 'strip'); box.appendChild(strip); wrap.appendChild(box);
      r._box = box; r._strip = strip; r._sig = '';
    });
    state.cfg = cfg;
  }

  // «Wrestling» — сериал из папки на сервере: первая карточка строки «Мои сериалы» (как в Lampa); выключается настройкой устройства mc_wr
  function withWrestling(row, items, done) {
    if ((userContext&&userContext.profile.is_child) || row.id !== 'watching' || (state.dev || {}).mc_wr === '0') return done(items);
    api('/wrestling/list', function (d) {
      var n = (d.items || []).length;
      if (d.disabled || d.watching === false || !n) return done(items);
      var card = { id: 'wr-series', wrestling: true, title: 'Wrestling', img: state.server + '/wrestling/poster.svg', first_air_date: '' };
      api('/wrestling/unseen', function (u) { if (u && u.unseen === 0) return done(items); if (u && u.unseen > 0) card.wr_new = u.unseen; done([card].concat(items)); },
        function () { var old = (load(rowKey('watching'), []) || []).filter(function (x) { return x && x.wrestling; })[0]; if (old) card.wr_new = old.wr_new; done([card].concat(items)); }, 8000);
    }, function () {
      // сервер не ответил (перезапуск, сеть): не убираем карточку, берём прошлую из кэша; убирает её только явный ответ «папки нет / выключено»
      var old = (load(rowKey('watching'), []) || []).filter(function (x) { return x && x.wrestling; })[0];
      done(old ? [old].concat(items) : items);
    }, 15000);
  }

  // кэш рядов привязан к серверу и профилю: смена любого из них не показывает чужие карточки
  function rowKey(id, sv, pf) { var n = String(sv !== undefined ? sv : state.server || '') + '|' + String(pf !== undefined ? pf : state.profile || ''), h = 0; for (var i = 0; i < n.length; i++) h = (h * 31 + n.charCodeAt(i)) | 0; return 'mc_row_' + (h >>> 0).toString(36) + '_' + id; }

  function loadRows(fresh) {
    var cfg = state.cfg || [];
    cfg.forEach(function (r) { r._emptyTries = 0; });
    var queue = cfg.slice(), active = 0;
    var gen = state.gen = (state.gen || 0) + 1;       // поколение запроса: ответ устаревшего запуска не затирает более новый
    var sv = String(state.server || ''), pf = String(state.profile || '');     // сервер и профиль запуска: результат ложится в кэш того, для кого запрошен
    fresh = !!fresh;                                  // «мимо серверного кэша» — для всех рядов этого запуска, а не только первых трёх
    function next() {
      while (active < 3 && queue.length) {
        (function (r) {
          active++;
          api(r.url + (fresh && /continue|watching|my_movies/.test(r.id) ? '&fresh=1' : ''), function (d) {
            active--; if (gen !== state.gen) return; var items = (d.results || []).slice(0, 30);
            withWrestling(r, items, function (all) {
              if (gen !== state.gen) return;           // ответ вложенного запроса устарел: ни кэш, ни экран не трогаем
              var more = d.has_more === true;
              if (!all.length && more) {
                r._emptyTries = (r._emptyTries || 0) + 1;
                if (r._emptyTries <= 4) {
                  var wait = [2000, 4000, 8000, 8000][r._emptyTries - 1];
                  setTimeout(function () {
                    if (gen !== state.gen) return;
                    queue.push(r);
                    next();
                  }, wait);
                }
                return;
              }
              r._emptyTries = 0;
              save(rowKey(r.id, sv, pf), all.slice(0, 24)); fillRow(r, all, false); });
            next(); },
            function () { active--; if (gen === state.gen) next(); }, 90000);
        })(queue.shift());
      }
    }
    next();
  }

  function startHome(fresh) {
    state.server = MC.server(); state.profile = load('mc_profile', 'main');
    if (!state.server) { setupFind(); return; }
    if (!ensureUser()) return;
    seedLampa();
    state.dev = load('mc_dev', {}); syncAfr(); applyWall();
    buildBar(); buildApps();
    var cached = load('mc_cfg', null) || DEFAULT_ROWS.map(function (r) { return { id: r.id, title: r.title, style: r.style, url: '' }; });
    var local = cached.map(function (r) { return { id: r.id, title: r.title, style: r.style, url: r.url }; });
    buildRows(local);
    local.forEach(function (r) { var c = load(rowKey(r.id), null); if (c) fillRow(r, c, true); else r._box.classList.add('hidden'); });
    rebuildNav(); setFocus(state.nav.length > 1 ? 1 : 0, 0, true);
    var dev = MC.deviceId();
    api('/home/shell/config?dev=' + encodeURIComponent(dev) + '&profile=' + encodeURIComponent(state.profile), function (c) {
      state.dev = c.dev || {}; save('mc_dev', state.dev); try { localStorage.setItem(clockCacheKey(), String((state.dev || {}).mc_clock != null ? state.dev.mc_clock : '1')); } catch (e) {}
      syncAfr(); applyWall(); inheritWall(); applyClock();
      state.sources=c.sources||{};state.allRows=c.all_rows||c.rows||[];
      refreshMyChannels();
      var rows = (c.rows || []).map(function (r) { return { id: r.id, title: r.title, style: r.style, url: r.url }; });
      save('mc_cfg', rows);
      var same = sig(rows) === sig(state.cfg);
      if (!same) {
        // состав рядов поменялся на сервере: пересобираем, подставляя кэш
        buildRows(rows);
        rows.forEach(function (r) { var cc = load(rowKey(r.id), null); if (cc) fillRow(r, cc, true); else r._box.classList.add('hidden'); });
      } else { state.cfg.forEach(function (r, i) { r.url = rows[i].url; }); }
      rebuildNav(); state.lastLoad = Date.now(); loadRows();
      setTimeout(function () { try { MC.warm(); } catch (e) {} }, 2500);
      if (!autoDone) { autoDone = true; setTimeout(function () { maybeAutoTv(0); }, 3500); }   // Lampa прогревается в фоне, пока вы смотрите главную
    }, function () { toast('Нет связи с сервером — показан сохранённый экран'); });
  }

  // обновления: периодически и при возвращении на экран; ленты меняются по месту
  setInterval(function () { if(!state.setupOpen&&refreshUserPolicy())return; if(!state.setupOpen&&state.server)refreshMyChannels(); if (!state.setupOpen && state.server && Date.now() - state.lastLoad > 5 * 60000) { state.lastLoad = Date.now(); loadRows(); } }, 60000);

  // ---------- настройки устройства (хранятся на сервере в записи устройства) ----------
  function applyWall() {
    var w = (state.dev || {}).wall, bg = $('bg');
    if (w) { bg.style.backgroundImage = 'url("' + state.server + '/home/wallpaper?name=' + encodeURIComponent(w) + '")'; bg.classList.add('img'); }
    else { bg.style.backgroundImage = ''; bg.classList.remove('img'); }
  }
  function inheritWall() {
    if ((state.dev || {}).wall || load('mc_wall_checked', '')) return;
    save('mc_wall_checked', '1');
    api('/home/dev/list', function (d) {
      var best = null;
      (d.devices || []).forEach(function (x) { if (x.cfg && x.cfg.wall && (!best || x.seen > best.seen)) best = x; });
      if (best) devSet('wall', best.cfg.wall, applyWall);
    });
  }
  function devSet(k, v, cb) {
    var prev = JSON.parse(JSON.stringify(state.dev || {}));
    state.dev = state.dev || {}; if (v === '' || v == null) delete state.dev[k]; else state.dev[k] = String(v);
    save('mc_dev', state.dev);
    if (k === 'afr') syncAfr();
    api('/home/dev/set?dev=' + encodeURIComponent(MC.deviceId()) + '&k=' + encodeURIComponent(k) + (v === '' || v == null ? '' : '&v=' + encodeURIComponent(v)), function () { if (cb) cb(); }, function () {
      state.dev = prev; save('mc_dev', state.dev); applyClock(); toast('Не удалось сохранить на сервере');
    });
  }
  function syncAfr() { try { if(MC.syncAfrPending)MC.syncAfrPending();if (MC.setAfrMode && state.dev && state.dev.afr != null) MC.setAfrMode(String(state.dev.afr) !== '0'); } catch (e) {} }
  var OPT = {
    mc_auto: { t: 'Автозапуск телевидения', d: '0', o: [['0', 'выключен'], ['1', 'включён: сразу включать последний канал']], hint: 'при включении приставки и после долгого сна' },
    engine: { t: 'Движок эфира', d: 'exo', o: [['exo', 'ExoPlayer'], ['mpv', 'mpv · копирование'], ['mpv_embed', 'mpv · прямой вывод'], ['mpv_hw', 'mpv · аппаратный'], ['mpv_sw', 'mpv · программный']] },
    buf: { t: 'Буфер видео', d: '1', o: [['0', 'без буфера'], ['1', '1 секунда'], ['3', '3 секунды'], ['5', '5 секунд'], ['10', '10 секунд']], hint: 'больше — реже подвисания, меньше — быстрее переключение' },
    font: { t: 'Шрифт списка каналов', d: '0', o: [['0', 'мелкий'], ['1', 'средний'], ['2', 'крупный']] },
    alpha: { t: 'Прозрачность списка каналов', d: '70', o: [['100', '100%'], ['85', '85%'], ['70', '70%'], ['55', '55%'], ['40', '40%']] },
    afr: { t: 'Авто-частота кадров', d: '1', o: [['1', 'включена'], ['0', 'выключена']] },
    vodbuf: { t: 'Буфер фильмов', d: '0', o: [['0', 'стандартный (до 2 мин, ~140 МБ)'], ['250', 'большой (250 МБ)'], ['500', 'максимальный (500 МБ)']], hint: 'только фильмы и сериалы, ТВ-эфир не затрагивает; при нехватке памяти ограничивается сам' },
    maxh: { t: 'Предел качества фильмов', d: '0', o: [['0', 'без ограничения (4K)'], ['1080', 'до 1080p'], ['720', 'до 720p']] },
    mc_ym: { t: 'Яндекс Музыка', d: '0', o: [['0', 'скрыть'], ['1', 'плитка в ряду приложений']], hint: 'подключаемая опция: плитка открывает Яндекс Музыку в Lampa' },
    mc_top: { t: 'Лучшие новинки', d: '1', o: [['1', 'показывать'], ['0', 'скрыть']], hint: 'рейтинговые новинки, все жанры вместе' },
    mc_topnew: { t: 'Лучшие новинки: за период', d: '3', o: [['1', 'месяц'], ['3', '3 месяца'], ['12', 'год'], ['0', 'всё время']] },
    mc_toprate: { t: 'Лучшие новинки: рейтинг от', d: '7.9', o: [['7.0', '7.0'], ['7.5', '7.5'], ['7.9', '7.9'], ['8.2', '8.2'], ['8.5', '8.5']], hint: 'у российского производства порог на 0,9 ниже' },
    mc_yt: { t: 'Ряд YouTube', d: '1', o: [['1', 'показывать'], ['0', 'скрыть']] },
    mc_info: { t: 'Описание под постером', d: '1', o: [['1', 'показывать'], ['0', 'скрыть']], hint: 'название, год, жанры, сюжет выбранной карточки' },
    mc_clock: { t: 'Часы лаунчера', d: '1', o: [['1', 'показывать'], ['0', 'скрыть']] },
    saver: { t: 'Заставка Lampa', d: '0', o: [['0', 'выключена'], ['1', 'включена']] }
  };
  function optIndex(key, list) { var v = String((state.dev || {})[key] != null ? state.dev[key] : OPT[key] && OPT[key].d); for (var i = 0; i < list.length; i++) if (list[i][0] === v) return i; return 0; }

  function openSettings() {
    var box = $('setup'); box.innerHTML = '';
    box.appendChild(el('h1', null, 'Настройки'));
    var appv = '';
    try { appv = (window.MC && typeof MC.appVersion === 'function') ? MC.appVersion() : ((window.AndroidJS && typeof AndroidJS.appVersion === 'function') ? AndroidJS.appVersion() : ''); } catch (e) {}
    if (appv) { var buildRow = el('div', 'srow'); buildRow.appendChild(el('div', 'sl', 'Сборка')); buildRow.appendChild(el('div', 'sv', appv)); box.appendChild(buildRow); }
    var scroll = el('div', 'slist'); box.appendChild(scroll);
    var items = [];
    function group(t) { scroll.appendChild(el('h3', null, t)); }
    function row(title, hint, getVal, onChange, onEnter) {
      var r = el('div', 'srow'); var l = el('div', 'sl', title); var v = el('div', 'sv'); r.appendChild(l); r.appendChild(v);
      if (hint) l.appendChild(el('small', null, hint));
      function paint() { v.textContent = getVal(); }
      paint(); r._repaint = paint; r._act = function () { if (onEnter) onEnter(paint); else onChange(1, paint); };
      r._left = function () { if (onChange && !onEnter) onChange(-1, paint); }; r._right = function () { if (onChange && !onEnter) onChange(1, paint); };
      scroll.appendChild(r); items.push(r); return r;
    }
    function opt(key, extra) {
      var def = OPT[key], list = def.o;
      function current() { if(key==='afr'&&MC.afrMode)return MC.afrMode()?0:1;return optIndex(key,list); }
      row(def.t, def.hint, function () { return list[current()][1]; }, function (dir, paint) {
        var i = (current() + dir + list.length) % list.length;if(key==='afr'&&MC.chooseAfrMode)MC.chooseAfrMode(list[i][0]!=='0');devSet(key, list[i][0], function () { paint(); if (extra) extra(); });
      });
    }
    function ytLabel() {
      var n = userContext && userContext.profile && userContext.profile.settings && userContext.profile.settings.youtube_profile;
      if (!n || n.indexOf('usr_') === 0) return 'свой';
      var parts = String(n).split(':'); return parts[parts.length - 1];
    }
    function saveYoutubeProfile(name, paint) {
      writeApi('/home/profiles/me/settings', { youtube_profile: name }, function (r) {
        if (r.error) { toast(r.error === 'bad_youtube_profile' ? 'Такого профиля YouTube нет' : 'Не удалось сохранить'); return; }
        try { userContext = JSON.parse(MC.profileRefresh()); } catch (e) {}
        if (paint) paint();
        toast('YouTube: ' + ytLabel());
        state.lastLoad = 0; startHome(true);
      });
    }
    function cycleYoutubeProfile(paint) {
      api('/yt/profiles', function (d) {
        var ps = d.profiles || []; if (!ps.length) { toast('Профилей YouTube пока нет'); return; }
        var cur = (userContext.profile.settings || {}).youtube_profile || '', i = -1;
        ps.forEach(function (p, n) { if (p.name === cur) i = n; });
        var nx = ps[(i + 1) % ps.length];
        saveYoutubeProfile(nx.name, paint);
      });
    }
    if (userContext && userContext.profile.is_child) {
      row('Ленты', 'Что показывать и в каком порядке', function () { return '›'; }, null, profileRowsSettings);
      row('Профиль YouTube', 'Назначается во взрослом профиле', ytLabel, null, null);
      var ytOn = true, ytOrder = 50, ytRow = row('Ряд YouTube', 'Только на этом профиле', function () { return ytOn ? 'показывать' : 'скрыть'; }, function (dir, paint) {
        ytOn = !ytOn;
        writeApi('/home/profiles/me/rows', { rows: [{ id: 'youtube', enabled: ytOn, order: ytOrder }] }, function (r) {
          if (r.error) { ytOn = !ytOn; toast('Не удалось сохранить'); paint(); return; }
          paint(); state.lastLoad = 0; startHome(true);
        });
      });
      api('/home/profiles/me/rows', function (d) {
        (d.rows || []).forEach(function (r) { if (r.id === 'youtube') { ytOn = r.enabled !== false; ytOrder = r.order; } });
        if (ytRow._repaint) ytRow._repaint();
      });
      var closeChild = tile('Закрыть', null, function () { setupShow(false); rebuildNav(); if (Date.now() - state.lastLoad > 1500) { state.lastLoad = Date.now(); startHome(false); } });
      box.appendChild(closeChild); items.push(closeChild);
      setupShow(true); setupItems(items);
      state.firstRun = false;
      return;
    }
    row('Полные настройки Lampa', 'интерфейс, плеер, парсер, плагины (откроется Lampa)', function () { return '›'; }, null, function () {
      setupShow(false);
      lampaGo(JSON.stringify({ js: '(function(){var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.Lampa&&Lampa.Settings){clearInterval(t);Lampa.Settings.open()}else if(++n>60)clearInterval(t)},500)})', arg: '' }));
    });
    row('Настройки Android', 'сеть, экран, звук, приложения', function () { return '›'; }, null, function () { setupShow(false); MC.settings(); });
    if(userContext&&userContext.profiles_enabled){row('Ленты профиля','Порядок и видимость',function(){return '›';},null,profileRowsSettings);if(!userContext.profile.is_child)row('Автовыбор торрентов','Выключен по умолчанию; удержание открывает ручной выбор',function(){return '›';},null,autoTorrentSettings);}
    group('Звук — все источники');
    function audioPassthrough() { try { return !!MC.audioPassthrough(); } catch (e) { return false; } }
    var drcRow;
    row('Проброс звука', 'Общий для всех профилей приставки · поддерживаемый звук через HDMI/S/PDIF · при включении DRC не применяется · со следующего запуска плеера', function () { return audioPassthrough() ? 'Вкл' : 'Выкл'; }, function (dir, paint) {
      MC.setAudioPassthrough(!audioPassthrough()); paint();
      if (drcRow && drcRow._repaint) drcRow._repaint();
    });
    var drcLabels = ['Выключена', 'Мягкая', 'Ночная'];
    function drcMode() { return Math.max(0, Math.min(2, Number(MC.drcMode()) || 0)); }
    drcRow = row('Динамика звука (DRC)', 'ТВ, кино, сериалы, аниме, YouTube и рестлинг · ExoPlayer и mpv · со следующего запуска плеера', function () { return drcLabels[drcMode()] + (audioPassthrough() ? ' · не применяется' : ''); }, function (dir, paint) {
      MC.setDrcMode((drcMode() + dir + 3) % 3); paint();
    });
    group('Экран — все источники');
    opt('afr');
    row('Туннелирование A/V (ExoPlayer)', 'Аппаратная синхронизация видео и звука · отдельная настройка от проброса · не применяется при действующей DRC · со следующего запуска плеера', function () { try{return MC.tunneling()?'Вкл':'Выкл';}catch(e){return 'Выкл';} }, function (dir, paint) { MC.setTunneling(!MC.tunneling());paint(); });
    group('Телевидение');
    opt('mc_auto'); opt('engine'); opt('buf'); opt('font'); opt('alpha');
    group('Кино и сериалы');
    opt('maxh'); opt('vodbuf');
    var kpRow = row('Профиль Кинопаба', 'аккаунт и качество воспроизведения', function () { return (state.dev || {}).kpprof || state.profile || 'main'; }, null, function (paint) {
      api('/kp/profiles', function (d) {
        var ps = d.profiles || [], cur = (state.dev || {}).kpprof || 'main', i = 0; ps.forEach(function (p, n) { if (p.id === cur) i = n; });
        var nx = ps[(i + 1) % ps.length]; if (!nx) return;
        devSet('kpprof', nx.id, function () { state.profile = nx.id; save('mc_profile', nx.id); paint(); toast('Профиль: ' + (nx.label || nx.id)); state.lastLoad = 0; });
      });
    });
    group('Главный экран');
    opt('mc_clock', applyClock);
    opt('mc_info', function () { showInfo(null); });
    opt('mc_ym', function () { buildApps(); rebuildNav(); });
    opt('mc_top', function () { state.lastLoad = 0; });
    row('Период новинок', 'Для всех профилей', function () { return '3 года; лучшие — 1 год'; }, null, function () {});
    opt('mc_toprate', function () { state.lastLoad = 0; });
    opt('mc_yt', function () { toast('Ряд обновится при возврате на главную'); state.lastLoad = 0; });
    row('Профиль YouTube', 'Набор каналов этого профиля, не приставки', ytLabel, null, cycleYoutubeProfile);
    row('Обои', 'картинка с сервера на фоне', function () { return (state.dev || {}).wall || 'по умолчанию'; }, null, function (paint) {
      api('/home/wallpapers', function (d) {
        var fs = ['' ].concat(d.files || []), cur = (state.dev || {}).wall || '', i = Math.max(0, fs.indexOf(cur));
        var nx = fs[(i + 1) % fs.length];
        devSet('wall', nx, function () { applyWall(); paint(); });
      });
    });
    opt('saver');
    group('Службы');
    var diagHolder = el('div');
    scroll.appendChild(diagHolder);
    function paintDiag(d) {
      diagHolder.innerHTML = '';
      var list = (d && d.services) || [];
      if (!list.length) { diagHolder.appendChild(el('div', 'srow', 'Нет данных')); return; }
      list.forEach(function (s) {
        var r = el('div', 'srow');
        var l = el('div', 'sl', s.name || '');
        if (s.detail) l.appendChild(el('small', null, s.detail));
        var v = el('div', 'sv', s.ok ? 'доступен' : (s.error || 'ошибка'));
        v.style.color = s.ok ? '#7dffa8' : '#ff8a80';
        r.appendChild(l); r.appendChild(v);
        diagHolder.appendChild(r);
      });
    }
    function loadDiag() {
      diagHolder.innerHTML = '';
      diagHolder.appendChild(el('div', 'srow', 'Проверяю службы…'));
      api('/home/diag', paintDiag, function () { paintDiag({ services: [{ name: 'Диагностика', ok: false, error: 'нет связи' }] }); }, 15000);
    }
    row('Обновить состояние служб', 'TorrServer, JacRed, аниме, телевидение', function () { return '›'; }, null, loadDiag);
    loadDiag();
    group('Система');
    row('Очистить историю профиля','Закладки и тайминг сохранятся',function(){return '›';},null,function(){
      menuShow('Очистить историю текущего профиля?',[['Очистить',null,function(){writeApi('/home/history/clear',{},function(d){
        if(!d.ok){toast('Не удалось очистить историю');return;}state.mem={};save(rowKey('history'),[]);menuClose();startHome(true);
      });}],['Отмена',null,openSettings]]);
    });
    row('Перезапуск Lampa', 'закрывает страницу Lampa и открывает заново; оболочка не перезапускается', function () { return '›'; }, null, function () {
      menuShow('Перезапустить Lampa?', [
        ['Да, перезапустить', 'страница Lampa загрузится заново', function () { menuClose(); try { MC.restartLampa(); } catch (e) {} toast('Lampa перезапускается…'); }],
        ['Отмена', null, menuClose]
      ]);
    });
    row('Сервер и профиль воспроизведения', MC.server(), function () { return '›'; }, null, function () { state.firstRun = false; setupFind(true); });
    var close = tile('Закрыть', null, function () { setupShow(false); rebuildNav(); if (Date.now() - state.lastLoad > 1500) { state.lastLoad = Date.now(); startHome(false); } });
    box.appendChild(close); items.push(close);
    setupShow(true); setupItems(items);
    state.firstRun = false;
  }

  // ---------- поиск: своя страница, Lampa не открывается; открывается только карточка выбранного ----------
  var lastQuery = '';
  var SEARCH_CATEGORIES = [['movie', 'Фильмы'], ['cartoon_movie', 'Мультфильмы'], ['person', 'Люди'], ['tv', 'Сериалы'], ['cartoon_tv', 'Мультсериалы'], ['anime_movie', 'Аниме'], ['anime_tv', 'Аниме-сериалы'], ['yc', 'YouTube каналы'], ['yv', 'YouTube видео']];
  var PERIODS = [['', 'любой'], ['y1', 'этот год'], ['y3', 'последние 3 года'], ['y10', 'последние 10 лет'], ['d2020', '2020-е'], ['d2010', '2010-е'], ['d2000', '2000-е'], ['old', 'до 2000']];
  var COUNTRIES = [['all', 'все (без Индии и Турции)'], ['foreign', 'зарубежные'], ['ru', 'отечественные'], ['any', 'все, без исключений']];
  var RATINGS = [[0, 'любой'], [5, 'от 5'], [6, 'от 6'], [7, 'от 7'], [8, 'от 8']];
  function sfGet() { return load('mc_sf', { p: '', c: 'all', r: 0 }); }
  function periodParams(p) { var y = new Date().getFullYear(); return { y1: [y, y], y3: [y - 2, y], y10: [y - 9, y], d2020: [2020, 2029], d2010: [2010, 2019], d2000: [2000, 2009], old: [1800, 1999] }[p] || [0, 0]; }
  function searchAlternative(q) { return q.replace(/[эЭёЁ]/g, function (c) { return { 'э': 'е', 'Э': 'Е', 'ё': 'е', 'Ё': 'Е' }[c]; }).replace(/([А-Яа-я])[-‐‑–—](?=[А-Яа-я])/g, '$1'); }
  function searchWithAlternative(path, q, category, request, ok, fail) {
    request(path, function (d) {
      var alternate = searchAlternative(q);
      if (category !== 'yc' && category !== 'yv' && alternate !== q && d && Array.isArray(d.results) && !d.results.length && !d.next_page && !d.error) {
        var next = path.replace(/([?&])q=[^&]*/, function (_, sep) { return sep + 'q=' + encodeURIComponent(alternate); });
        return request(next, function (result) { ok(result, next); }, fail);
      }
      ok(d, path);
    }, fail);
  }
  function searchCategories() { var cfg=userContext&&userContext.profile.settings||{};var on=cfg.youtube_mode!=='off'&&(!cfg.sources||cfg.sources.youtube!==false)&&(!state.sources||state.sources.youtube!==false);return SEARCH_CATEGORIES.filter(function(c){return on||c[0]!=='yc'&&c[0]!=='yv';}); }
  function searchMode() {
    var v = load('mc_smode', 'movie'), legacy = { everything: 'movie', all: 'movie', anime: 'anime_tv' };
    v = legacy[v] || v;
    var categories=searchCategories();for (var i = 0; i < categories.length; i++) if (categories[i][0] === v) return v;
    return 'movie';
  }
  function groupHead(res, text) { var h = el('h3', 'shead', text); res.appendChild(h); return h; }

  function personCard(it) {
    var c = el('div', 'card poster person');
    var pic = el('div', 'pic'); var img = document.createElement('img'); img.alt = '';
    img.src = state.server + '/tmdb/img/t/p/w342' + it.profile_path; pic.appendChild(img);
    var dep = { Acting: 'Актёр', Directing: 'Режиссёр', Writing: 'Сценарист', Production: 'Продюсер' }[it.department] || it.department || '';
    if (dep) pic.appendChild(el('div', 'tvl', dep));
    c.appendChild(pic); c.appendChild(el('div', 'ttl', it.name));
    c._item = { title: it.name, original_title: it.known_for || '', overview: it.known_for ? 'Известен по: ' + it.known_for : '', media_type: 'person' };
    c._act = function () { openPerson(it.id, it.name); };
    return c;
  }

  function nfs(n) { n = parseInt(n, 10) || 0; return n >= 1e6 ? (n / 1e6).toFixed(1).replace(/\.0$/, '') + ' млн' : n >= 1e3 ? Math.round(n / 1e3) + ' тыс.' : (n ? String(n) : ''); }
  function channelTile(ch) {
    var t = el('div', 'tile chan gc');
    var im = document.createElement('img'); if (ch.thumb) im.src = ch.thumb; t.appendChild(im);
    var box = el('div', 'cbox'); box.appendChild(el('div', 'cn', ch.title || ch.id));
    var sub = nfs(ch.followers); box.appendChild(el('small', null, sub ? sub + ' подписчиков' : 'канал YouTube')); t.appendChild(box);
    t._item = { title: ch.title, overview: ch.description || '', original_title: 'Канал YouTube', channel: ch.title, published: '' , media_type: 'channel', vote_average: 0 };
    t._act = function () {
      setupShow(false); state.searchOpen = false; $('setup').className = 'hidden';
      lampaGo(JSON.stringify({ js: '(function(a,b){window.__mc_ret=1;var n=0;var generation=window.__mc_nav_id;var t=null;function run(){if(generation!==window.__mc_nav_id)return;if(window.__yt&&window.__yt.openChannel){clearInterval(t);window.__yt.openChannel(a,b)}else if(++n<=80)t=setTimeout(run,100)}run()})', arg: JSON.stringify(ch.id) + ',' + JSON.stringify(ch.title) }));
    };
    return t;
  }

  function openSearch(q0) {
    var box = $('setup'); box.innerHTML = '';
    box.className = 'searchbox hidden';
    box.appendChild(el('h1', null, 'Поиск'));
    var line = el('div', 'sline'); box.appendChild(line);
    var inp = document.createElement('input'); inp.type = 'search'; inp.setAttribute('enterkeyhint', 'search'); inp.placeholder = 'Что искать?'; inp.value = q0 || lastQuery || '';
    var mic = tile('🎤 Голос', null, function () { try { MC.voice(); } catch (e) {} });
    var back = tile('Закрыть', null, closeSearch);
    line.appendChild(inp); line.appendChild(mic); line.appendChild(back);

    // девять взаимоисключающих категорий; выбранная подсвечена жёлтым, фокус пульта — белой рамкой
    var catBox = el('div', 'scats'); box.appendChild(catBox);
    var cats = [];
    function paintCats() { cats.forEach(function (c) { c.classList.toggle('sel', c._cat === searchMode()); }); }
    searchCategories().forEach(function (c) {
      var t = tile(c[1], null, function () {
        save('mc_smode', c[0]); paintCats(); applyCategoryUi(); last = ''; run(inp.value);
      });
      t._cat = c[0]; t.classList.add('cat'); cats.push(t); catBox.appendChild(t);
    });

    // фильтры относятся только к фильмам и сериалам
    var fline = el('div', 'sline sfline'); box.appendChild(fline);
    function lab(list, v) { for (var i = 0; i < list.length; i++) if (String(list[i][0]) === String(v)) return list[i][1]; return list[0][1]; }
    function cyc(list, v) { var i = 0; list.forEach(function (x, n) { if (String(x[0]) === String(v)) i = n; }); return list[(i + 1) % list.length][0]; }
    var sf = sfGet();
    var fPer = tile('Период: ' + lab(PERIODS, sf.p), null, function () { var f = sfGet(); f.p = cyc(PERIODS, f.p); save('mc_sf', f); fPer.firstChild.textContent = 'Период: ' + lab(PERIODS, f.p); last = ''; run(inp.value); });
    var fCou = tile('Страна: ' + lab(COUNTRIES, sf.c), null, function () { var f = sfGet(); f.c = cyc(COUNTRIES, f.c); save('mc_sf', f); fCou.firstChild.textContent = 'Страна: ' + lab(COUNTRIES, f.c); last = ''; run(inp.value); });
    var fRat = tile('Рейтинг: ' + lab(RATINGS, sf.r), null, function () { var f = sfGet(); f.r = cyc(RATINGS, f.r); save('mc_sf', f); fRat.firstChild.textContent = 'Рейтинг: ' + lab(RATINGS, f.r); last = ''; run(inp.value); });
    fline.appendChild(fPer); fline.appendChild(fCou); fline.appendChild(fRat);
    function filtersApply() { var c = searchMode(); return c !== 'person' && c !== 'yc' && c !== 'yv'; }
    function applyCategoryUi() { fline.style.display = filtersApply() ? '' : 'none'; }

    var info = el('p', 'sinfo', 'Введите запрос или нажмите «Голос»'); box.appendChild(info);
    var res = el('div', 'list picker sres'); box.appendChild(res);
    var timer = null, last = '', gen = 0, xhr = null, section = null, nextPage = 0;
    function head() {
      var items = [inp, mic, back].concat(cats);
      if (filtersApply()) items.push(fPer, fCou, fRat);
      return items;
    }
    function syncItems() {
      var items = head();
      [].slice.call(res.querySelectorAll('.gc')).forEach(function (n) { items.push(n); });
      state.setupItems = items;
    }
    // запрос с отменой: ответ устаревшего запроса не меняет ни страницу, ни список навигации
    function request(path, ok, fail, timeout) {
      if (xhr) { try { xhr.abort(); } catch (e) {} }
      var my = ++gen, x = new XMLHttpRequest();
      xhr = x; var generation=userGeneration; x.open('GET', state.server + path, true); ownerHeader(x,state.server+path); x.timeout = timeout || 40000;
      x.onload = function () {
        if (my !== gen || generation !== userGeneration) return;
        try { var d = JSON.parse(x.responseText); if (x.status === 200) return ok(d); } catch (e) {}
        fail && fail();
      };
      x.onerror = x.ontimeout = function () { if (my === gen) fail && fail(); };
      x.send();
      return my;
    }
    function render(cat, list, append) {
      if (!append) { res.innerHTML = ''; section = el('div', 'ssec'); res.appendChild(section); }
      var more = section.querySelector('.more'); if (more) more.parentNode.removeChild(more);
      list.forEach(function (it) {
        var c;
        if (cat === 'person') c = personCard(it);
        else if (cat === 'yc') { c = channelTile(it); }
        else if (cat === 'yv') { c = cardEl(cardOfYt(it), { id: 'youtube', style: 'wide' }); c._item = cardOfYt(it); }
        else { c = cardEl(it, { id: 'search', style: 'poster' }); c._item = it; }
        c.classList.add('gc'); section.appendChild(c);
      });
    }
    function run(q) {
      q = (q || '').trim(); if (q === last) return; last = q; lastQuery = q;
      if (xhr) { try { xhr.abort(); } catch (e) {} } gen++;
      res.innerHTML = ''; section = null; nextPage = 0; syncItems();
      if (q.length < 2) { info.textContent = 'Введите запрос или нажмите «Голос»'; return; }
      var cat = searchMode(), name = lab(SEARCH_CATEGORIES, cat);
      info.textContent = name + ': ищу «' + q + '»…';
      var path;
      if (cat === 'yc') path = '/yt/channels?n=18&q=' + encodeURIComponent(q);
      else if (cat === 'yv') path = '/yt/search?n=30&q=' + encodeURIComponent(q);
      else {
        var f = sfGet(), pr = periodParams(f.p);
        path = '/home/search/v2?category=' + cat + '&q=' + encodeURIComponent(q) + '&country=' + f.c + (pr[0] ? '&from=' + pr[0] + '&to=' + pr[1] : '') + (f.r ? '&minr=' + f.r : '');
      }
      searchWithAlternative(path, q, cat, request, function (d, resolvedPath) {
        var list = d.results || [];
        render(cat, list, false);
        nextPage = d.next_page || 0;
        // продолжение определяет next_page, а не непустота порции: сервер мог просмотреть страницы и не найти подходящих
        if (nextPage) { var m = tile('Показать ещё', null, function () { loadMore(cat, resolvedPath); }); m.classList.add('gc', 'more'); section.appendChild(m); }
        info.textContent = name + ': ' + (list.length ? 'найдено ' + list.length + (nextPage ? ' (есть ещё)' : '') : (nextPage ? 'в этой порции ничего нет — нажмите «Показать ещё»' : 'ничего не найдено'));
        syncItems();
      }, function () { last = ''; info.textContent = name + ': источник недоступен, повторите поиск'; });
    }
    function loadMore(cat, path) {
      var np = nextPage; if (!np) return;
      info.textContent = 'Загружаю ещё…';
      request(path + '&page=' + np, function (d) {
        var list = d.results || [];
        render(cat, list, true);
        nextPage = d.next_page || 0;
        if (nextPage) { var m = tile('Показать ещё', null, function () { loadMore(cat, path); }); m.classList.add('gc', 'more'); section.appendChild(m); }
        info.textContent = lab(SEARCH_CATEGORIES, cat) + (nextPage ? ': есть ещё' : '');
        syncItems();
      }, function () {
        // кнопка «Показать ещё» осталась на месте (render её не трогал до ответа), повтор возможен
        info.textContent = 'Не удалось загрузить следующую страницу — нажмите «Показать ещё» ещё раз';
      });
    }
    inp.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(function () { run(inp.value); }, 700); });
    // OK in the text field belongs to WebView: it opens the Android keyboard.
    // The keyboard's Search action submits via the standard search event.
    inp.addEventListener('search', function () { clearTimeout(timer); run(inp.value); try { inp.blur(); } catch (e) {} syncItems(); var first = state.setupItems.filter(function (n) { return n.classList && n.classList.contains('gc'); })[0]; if (first) { state.setupIdx = state.setupItems.indexOf(first); setupFocus(); } });
    window.__mc.onVoice = function (t) { if (t) { inp.value = t; run(t); } };
    state._searchClose = function () { clearTimeout(timer); if (xhr) { try { xhr.abort(); } catch (e) {} } gen++; window.__mc.onVoice = function () {}; };
    state.searchOpen = true;
    box.classList.remove('hidden');
    paintCats(); applyCategoryUi();
    setupShow(true); state.setupItems = head(); state.setupIdx = 0; setupFocus();
    if (inp.value) { last = ''; run(inp.value); }
  }
  function cardOfYt(v) { return { id: v.id, title: v.title, thumb: v.thumb, duration: v.duration, channel: v.channel, channel_id: v.channel_id, published: v.published, views: v.views }; }

  // страница человека: его фильмография
  function openPerson(id, name) {
    var box = $('setup'); box.innerHTML = ''; box.className = 'searchbox'; state.searchOpen = true;
    box.appendChild(el('h1', null, name || 'Человек'));
    var line = el('div', 'sline'); box.appendChild(line);
    var back = tile('← К поиску', null, function () { openSearch(lastQuery); });
    var close = tile('Закрыть', null, closeSearch);
    line.appendChild(back); line.appendChild(close);
    var info = el('p', 'sinfo', 'Загружаю…'); box.appendChild(info);
    var res = el('div', 'list picker sres'); box.appendChild(res);
    state.setupItems = [back, close]; state.setupIdx = 0; setupShow(true); setupFocus();
    api('/home/person?id=' + id, function (d) {
      var p = d.person || {}, r = d.results || [];
      var bits = []; if (p.department) bits.push({ Acting: 'Актёр', Directing: 'Режиссёр', Writing: 'Сценарист', Production: 'Продюсер' }[p.department] || p.department);
      if (p.birthday) bits.push(p.birthday); if (p.place) bits.push(p.place);
      info.textContent = bits.join(' · ') + (r.length ? ' — работ: ' + r.length : ' — работы не найдены');
      var items = [back, close], sec = el('div', 'ssec'); res.appendChild(sec);
      r.forEach(function (it) { var c = cardEl(it, { id: 'person', style: 'poster' }); c.classList.add('gc'); c._item = it; if (it.role) { var rl = el('div', 'dt', it.role); c.querySelector('.pic').appendChild(rl); } sec.appendChild(c); items.push(c); });
      state.setupItems = items;
    }, function () { info.textContent = 'Не удалось загрузить'; });
  }
  function closeSearch() { try { state._searchClose && state._searchClose(); } catch (e) {} state.searchOpen = false; showInfo(null); $('setup').className = 'hidden'; setupShow(false); rebuildNav(); }

  // автозапуск ТВ: при старте оболочки (включение приставки) и при возврате после долгого сна
  var autoDone = false;
  function maybeAutoTv(away) {
    var d = state.dev || load('mc_dev', {});
    if (String(d.mc_auto) !== '1' || state.setupOpen) return;
    if (away > 0 && away < 300) return;                       // короткий уход (плеер, настройки) — не перебиваем
    try { MC.tv(); } catch (e) {}
  }

  window.__mc = {
    profileRefreshed:function(next,generation){if(generation===userGeneration)applyUserPolicy(next);},
    home: function () { if (state.setupOpen) return; state.nav.forEach(function (n) { if (n.strip) n.strip.scrollLeft = 0; }); setFocus(state.nav.length > 1 ? 1 : 0, 0); $('scroll').scrollTop = 0; },
    resume: function (away) {
      syncAfr();
      state.waiting=false;hideWait();
      if(refreshUserPolicy())return;
      if(!state.setupOpen&&state.server)refreshMyChannels();
      // вернулись из плеера/Lampa (≥15 с): ленты «Продолжить» и «Мои сериалы» обновляем сразу, остальное — раз в 2 минуты
      if (!state.setupOpen && state.server && (away >= 3 || Date.now() - state.lastLoad > 120000)) { state.lastLoad = Date.now(); loadRows(away >= 3); }
      if (away > 300) maybeAutoTv(away);
    },
    back: function () { if (state.move) { moveCancel(); return; } if (state.waiting) { try{if(MC.cancelNavigation)MC.cancelNavigation();}catch(e){}state.waiting = false; hideWait(); } else if (state.searchOpen) { closeSearch(); } else if (state.setupOpen && !state.firstRun) { setupShow(false); } else if (!state.setupOpen) setFocus(0, 0); },
    onFound: function () {}, onScanDone: function () {}
  };

  // ---------- старт ----------
  if (MC.server()) {
    if (!load('mc_setup', '')) save('mc_setup', '1');
    startHome(false);
  } else { state.firstRun = true; setupFind(false); }
})();
