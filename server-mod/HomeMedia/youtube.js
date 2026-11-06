(function () {
  'use strict';
  if(window.HomeUser){var yc=HomeUser.owner.profile.settings||{};if(yc.youtube_mode==='off'||yc.sources&&yc.sources.youtube===false)return;}
  if (window.plugin_youtube_ready) return;
  window.plugin_youtube_ready = true;

  var BASE = '{localhost}';
  if (BASE.indexOf('{') === 0) BASE = location.origin;

  var ICON = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M21.6 7.2a2.5 2.5 0 0 0-1.8-1.8C18.2 5 12 5 12 5s-6.2 0-7.8.4A2.5 2.5 0 0 0 2.4 7.2C2 8.8 2 12 2 12s0 3.2.4 4.8a2.5 2.5 0 0 0 1.8 1.8C5.8 19 12 19 12 19s6.2 0 7.8-.4a2.5 2.5 0 0 0 1.8-1.8c.4-1.6.4-4.8.4-4.8s0-3.2-.4-4.8zM10 15V9l5.2 3L10 15z"/></svg>';

  function noty(t) { try { Lampa.Noty.show(t); } catch (e) {} }

  // Базовый контроллер экрана: в него возвращаемся, когда наше окно закрылось
  var baseName = 'content';
  function ctrl() {
    try {
      var n = Lampa.Controller.enabled().name;
      if (n === 'menu') n = 'content';   // боковое меню после выбора пункта скрывается: возвращаться надо на страницу
      if (n && ['select', 'modal', 'input', 'settings_component'].indexOf(n) < 0) baseName = n;
    } catch (e) {}
    return baseName;
  }

  function api(path, ok, fail, timeout) {
    return $.ajax({ url: BASE + path, dataType: 'json', timeout: timeout || 70000, cache: false, success: ok, error: function (x) { if (fail) fail(x); } });
  }

  function fmt(sec) {
    sec = Math.max(0, Math.floor(sec || 0));
    var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    return (h ? h + ':' + (m < 10 ? '0' : '') : '') + m + ':' + (s < 10 ? '0' : '') + s;
  }

  // предел качества профиля устройства (тот же, что у фильмов и сериалов); 0 — максимум
  function maxH() { try { var v = parseInt(localStorage.getItem('home_maxh_v1'), 10); return isNaN(v) ? 0 : v; } catch (e) { return 0; } }

  // ---------- воспроизведение ----------
  var pendingPlay = null;
  function play(id, title, remember) {
    if (pendingPlay) pendingPlay();
    var navigation=window.__mc_nav_id, back=ctrl(), stopped=false, request=null, timer=null, watch=null;
    var mh = maxH();
    var loading=$('<div style="padding:1em;text-align:center">Подготавливаю видео…<br><small>Проверяю доступные потоки. Назад — отмена.</small></div>');
    function current(){return !stopped && navigation===window.__mc_nav_id;}
    function stop(){
      if(stopped)return;stopped=true;
      clearTimeout(timer);clearInterval(watch);
      if(request)request.abort();
      if(pendingPlay===stop){pendingPlay=null;try{if(loading[0]&&document.body.contains(loading[0])){Lampa.Modal.close();if(Lampa.Controller.enabled().name==='modal')Lampa.Controller.toggle(back);}}catch(e){}}
    }
    pendingPlay=stop;
    Lampa.Modal.open({title:'YouTube',html:loading,size:'medium',onBack:function(){stop();Lampa.Controller.toggle(back);}});
    watch=setInterval(function(){if(!current())stop();},250);
    function resolve(attempt){
    request=api('/yt/video?id=' + encodeURIComponent(id) + '&maxh=' + mh + '&vp9=' + (mh === 0 || mh > 1080), function (d) {
      if(!current())return;
      if (!d || !d.video){stop();Lampa.Controller.toggle(back);return noty('YouTube: видео недоступно');}
      var data = {
        url: d.video,
        title: title || d.title || 'YouTube',
        home_player: true,
        home_timeline_only: true // ролик/трейлер не относится к просмотру открытой карточки фильма
      };
      data.home_youtube_id=d.id;data.home_base=BASE;
      if (d.audio) data.yt_audio = d.audio;
      if (d.quals && d.quals.length) data.yt_quals = JSON.stringify(d.quals);
      else if (d.height > 0) data.yt_quals = JSON.stringify([{h:d.height,v:d.vcodec,url:d.video}]);
      if (d.sponsor && d.sponsor.length) data.yt_skip = JSON.stringify(d.sponsor);
      function launch(progress){
        if(!current())return;
        stop();
        if(window.HomeUser&&remember!==false){
          var hash=String(Lampa.Utils.hash('yt:'+d.id));data.home_base=BASE;data.audio_key='yt_'+d.id;
          data.tl_meta=JSON.stringify([{id:'yt-'+d.id,h:hash,c:'yt_'+d.id}]);
          data.timeline={hash:hash,time:(progress&&progress.position)||0,duration:d.duration||0,percent:(progress&&progress.percent)||0};
        }
        try { Lampa.Select.close(); } catch (e) {}
        try { Lampa.Player.play(data); } catch (e) { noty('YouTube: не удалось запустить плеер'); }
        setTimeout(function () { if(navigation!==window.__mc_nav_id)return;try { Lampa.Select.close(); } catch (e) {} try { Lampa.Controller.toggle(back); } catch (e) {} }, 700);
      }
      if(window.HomeUser&&remember!==false)request=api('/home/youtube/progress?id='+encodeURIComponent(d.id),launch,function(){launch(null);},5000);else launch(null);
    }, function (x) {
      if(!current())return;
      var d=x&&x.responseJSON||{};
      if(attempt===0&&(d.error==='network_error'||d.error==='extract_timeout'||d.error==='busy')){timer=setTimeout(function(){if(current())resolve(1);},15000);return;}
      stop();Lampa.Controller.toggle(back);
      noty('YouTube: '+(d.message||'не удалось связаться с сервером. Попробуйте ещё раз.'));
    }, 85000);
    }
    resolve(0);
  }

  // ---------- профиль (набор каналов) ----------
  function boundPack() {
    if (window.HomeUser) {
      // Профиль без явного набора: пустая строка — сервер (ResolvePack) сам вернёт собственный
      // набор профиля. Раньше здесь был 'main', и первый запрос после смены профиля уходил в
      // чужой набор и давал ошибку до повторного открытия.
      try { var s=HomeUser.owner.profile.settings||{}; if (s.youtube_profile) return s.youtube_profile; } catch (e) {}
      return '';
    }
    try { return localStorage.getItem('yt_profile') || 'main'; } catch (e2) { return 'main'; }
  }
  function profile() { return boundPack(); }
  function mcDev() { try { return (window.AndroidJS && typeof AndroidJS.mcDeviceId === 'function' && AndroidJS.mcDeviceId()) || ''; } catch (e) { return ''; } }
  function profileLabel() {
    try { var s = window.HomeUser && HomeUser.owner.profile.settings && HomeUser.owner.profile.settings.youtube_profile; if (s) return localStorage.getItem('yt_profile_label') || s; } catch (e) {}
    try { return localStorage.getItem('yt_profile_label') || profile(); } catch (e2) { return profile(); }
  }
  function setProfile(p, label) {
    try { localStorage.setItem('yt_profile', p); localStorage.setItem('yt_profile_label', label || p); } catch (e) {}
    if (window.HomeUser) {
      try { HomeUser.owner.profile.settings.youtube_profile = p; } catch (e2) {}
      $.ajax({ url: BASE + '/home/profiles/me/settings', type: 'POST', contentType: 'application/json', data: JSON.stringify({ youtube_profile: p }), timeout: 8000 });
      return;
    }
    var d = mcDev(); if (d) { try { var x = new XMLHttpRequest(); x.open('GET', BASE + '/home/dev/set?dev=' + encodeURIComponent(d) + '&k=ytprof&v=' + encodeURIComponent(p), true); x.send(); } catch (e3) {} }
  }
  function syncProfile() {
    if (window.HomeUser) {
      try { var p = HomeUser.owner.profile.settings && HomeUser.owner.profile.settings.youtube_profile; if (p) localStorage.setItem('yt_profile', p); } catch (e) {}
      return;
    }
    var d = mcDev(); if (!d) return;
    try {
      var x = new XMLHttpRequest(); x.open('GET', BASE + '/home/shell/config?dev=' + encodeURIComponent(d), true); x.timeout = 8000;
      x.onload = function () { try { var p = JSON.parse(x.responseText).dev.ytprof; if (p) localStorage.setItem('yt_profile', p); } catch (e) {} };
      x.send();
    } catch (e) {}
  }
  syncProfile(); setInterval(syncProfile, 60000);
  try { Lampa.Listener.follow('activity', function (e) { if (e && e.type === 'start') syncProfile(); }); } catch (e) {}
  function q(p) { return encodeURIComponent(p); }

  // ---------- окна выбора ----------
  var openList = null;        // результаты в открытом окне: долгое нажатие на строке открывает меню канала
  function showSelect(title, items, back, onSelect) {
    Lampa.Select.show({
      title: title,
      items: items,
      onBack: function () { openList = null; (back || function () { Lampa.Controller.toggle(ctrl()); })(); },
      onSelect: function (a) { openList = null; setTimeout(function () { onSelect(a); }, 300); }
    });
  }

  function showResults(title, results, back) {
    if (!results || !results.length) { noty('Ничего не найдено'); return Lampa.Controller.toggle(ctrl()); }
    var items = results.map(function (r) {
      var sub = (r.channel || '') + (r.duration ? ' · ' + fmt(r.duration) : '') + (r.published ? ' · ' + String(r.published).slice(0, 10) : '');
      return { title: r.title, subtitle: sub + '   (удерживайте — канал)', r: r };
    });
    var again = function () { showResults(title, results, back); };
    showSelect(title, items, back, function (a) { play(a.r.id, a.r.title); });
    openList = { results: results, again: again, back: back };
  }

  // долгое нажатие на видео: канал, подписка
  document.addEventListener('hover:long', function (e) {
    if (!openList) return;
    var el = $(e.target).closest('.selectbox-item');
    if (!el.length) return;
    var idx = $('.selectbox-item').index(el);
    var r = openList.results[idx];
    if (!r || !r.channel_id) return;
    var ol = openList;
    openList = null;
    channelMenu(r, ol.again);
  }, true);

  function channelMenu(r, back) {
    api('/yt/subs?profile=' + q(profile()), function (d) {
      var subscribed = (d.channels || []).some(function (c) { return c.id === r.channel_id; });
      showSelect(r.channel || 'Канал', [
        { title: 'Видео канала', act: 'videos' },
        { title: subscribed ? 'Отписаться' : 'Подписаться', subtitle: 'профиль: ' + profileLabel(), act: 'sub' }
      ], back, function (a) {
        if (a.act === 'videos') openChannel(r.channel_id, r.channel);
        else if (subscribed) api('/yt/subs/remove?profile=' + q(profile()) + '&ch=' + q(r.channel_id), function () { noty('Отписка: ' + r.channel); Lampa.Controller.toggle(ctrl()); });
        else api('/yt/subs/add?profile=' + q(profile()) + '&channel=' + q(r.channel_id), function () { noty('Подписка: ' + r.channel); Lampa.Controller.toggle(ctrl()); }, function () { noty('Не удалось подписаться'); });
      });
    }, function () { noty('Нет связи с сервером'); });
  }

  // ---------- страницы с превью (вместо текстовых списков) ----------
  function channelCard(c) {
    return {
      id: 'ytc' + c.id, yt_ch: c.id, yt_channel_id: c.id, yt_channel: c.title, title: c.title || c.id,
      original_title: '', img: c.thumb || (BASE + '/yt/avatar.svg'), poster_path: '', backdrop_path: '',
      overview: '', vote_average: 0, source: 'youtube'
    };
  }

  function nf(n) {
    n = parseInt(n, 10) || 0;
    if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + ' млн';
    if (n >= 1e3) return Math.round(n / 1e3) + ' тыс.';
    return String(n);
  }

  (function () {
    var st = document.createElement('style');
    st.textContent =
      '.yt-wide .card--category{width:19% !important}' +
      '.yt-wide .card--category .card__view{padding-bottom:56.25% !important}' +
      '.yt-wide .card--category .card__img{object-fit:cover !important}' +
      '.yt-sq .card--category{width:15% !important}' +
      '.yt-sq .card--category .card__view{padding-bottom:100% !important;background:#1b1b1b}' +
      '.yt-sq .card--category .card__img{object-fit:contain !important}' +
      '.yt-wide .card--category.yt-headcard{width:100% !important;margin-bottom:.6em}' +
      '.yt-headcard .yt-chead{display:flex;gap:1.4em;align-items:flex-start;padding:.5em 1em;border-radius:.6em}' +
      '.yt-headcard .yt-chead__ava{width:7em;height:7em;border-radius:50%;object-fit:cover;background:#222;flex:none}' +
      '.yt-headcard .yt-chead__box{flex:1;min-width:0}' +
      '.yt-headcard .yt-chead__name{font-size:1.7em;font-weight:700}' +
      '.yt-headcard .yt-chead__meta{opacity:.7;margin-top:.2em}' +
      '.yt-headcard .yt-chead__descr{margin-top:.5em;opacity:.85;line-height:1.4;max-height:6em;overflow:hidden;white-space:pre-line}' +
      '.yt-headcard .yt-chead__btn{display:inline-block;margin-top:.7em;padding:.45em 1.1em;border-radius:.4em;background:rgba(255,255,255,.14)}' +
      '.yt-headcard.focus .yt-chead__btn{background:#fff;color:#000}' +
      '.yt-headcard.focus .yt-chead{background:rgba(0,0,0,.45)}';
    document.head.appendChild(st);
  })();

  // плашка с датой в левом верхнем углу превью: ставится на любую карточку с yt_date (сетка, строка на главной)
  (function () {
    var st = document.createElement('style');
    st.textContent = '.yt-date{position:absolute;left:.5em;top:.5em;z-index:3;padding:.15em .55em;border-radius:.3em;background:rgba(0,0,0,.72);color:#fff;font-size:.9em;font-weight:600;pointer-events:none}';
    document.head.appendChild(st);
    var pending = false;
    function mark() {
      pending = false;
      var cards = document.querySelectorAll('.card:not(.yt-dated)');
      for (var i = 0; i < cards.length; i++) {
        var c = cards[i], d = c.card_data;
        if (!d || !d.yt_date) continue;
        var view = c.querySelector('.card__view');
        if (!view) continue;
        c.classList.add('yt-dated');
        var b = document.createElement('div');
        b.className = 'yt-date';
        b.textContent = d.yt_date;
        view.appendChild(b);
      }
    }
    new MutationObserver(function () { if (!pending) { pending = true; setTimeout(mark, 150); } }).observe(document.body, { childList: true, subtree: true });
  })();

  function ytComponent(object) {
    var feedSession=Date.now().toString(36)+'-'+Math.random().toString(36).slice(2);
    var comp = new Lampa.InteractionCategory(object);
    var info = null;
    var feed = object.yt && object.yt.mode === 'feed';
    var feedEnded=false,shown=0,pumping=false,busy=false,destroyed=false,built=false;
    var cards=[],focused=-1,nextPage=1,seen=Object.create(null),retryTimer=null,failures=0,request=null,pumpTimer=null;
    var ageTries=0,ageTimer=null,ageWait=[2000,4000,8000,8000];
    var originalDestroy=comp.destroy;
    comp.destroy=function(){destroyed=true;clearTimeout(retryTimer);clearTimeout(pumpTimer);if(ageTimer!==null){clearTimeout(ageTimer);ageTimer=null;}if(request&&request.abort)request.abort();cards=[];return originalDestroy&&originalDestroy.apply(comp,arguments);};
    // The core's 12-card window is too small for five wide thumbnails per row.
    // Keep navigation and rendered layers around the actual focused card.
    var nativeLimit=comp.limit;
    comp.limit=function(){
      if(destroyed)return;
      // Navigator is private to the core. Keep its working navigation via the
      // native limit, then widen only the rendered image window.
      if(Lampa.Controller.own(comp))nativeLimit.call(comp);
      var at=Math.max(0,focused);
      cards.forEach(function(c,i){var node=c.render(true);if(node&&node.classList)node.classList.toggle('layer--render',i>=at-30&&i<=at+30);});
      Lampa.Layer.visible(comp.render(true));
    };

    function load(pg, done, fail) {
      var y = object.yt || {};
      var pack = function (list,d) { return { results: list,has_more:d&&d.has_more===true,retry_page:d&&d.retry_page===true,page:pg }; };
      if(feed)return api('/yt/feed?page='+pg+'&n=24&profile='+q(profile())+'&session='+q(feedSession),function(d){done(pack((d.results||[]).map(cardOf),d));},fail);
      if (y.mode === 'search') return api('/yt/search?n=40&page='+pg+'&q=' + q(y.q), function (d) { done(pack((d.results || []).map(cardOf),d)); }, fail,90000);
      else if (y.mode === 'subs') return api('/yt/subs?profile=' + q(profile()), function (d) { done(pack((d.channels || []).map(channelCard))); }, fail);
      else if (y.mode === 'channel') return api('/yt/channel?n=40&page='+pg+'&id=' + q(y.id), function (d) { info = d.info || {};var list=(d.results||[]).map(cardOf);if(pg===1)list.unshift({id:'ythead',yt_head:true,title:info.title||'',img:info.avatar||'',poster_path:'',backdrop_path:'',vote_average:0,source:'youtube'});done(pack(list,d)); }, fail, 90000);
      else fail();
    }

    function pump() {
      if(destroyed||feedEnded||pumping||busy||retryTimer!==null||ageTimer!==null)return;
      if(shown>=36&&!(focused>=0&&cards.length-focused<=12))return;
      pumping=true;pumpTimer=setTimeout(function(){pumping=false;if(!destroyed)comp.next();},40);
    }
    function retry(){busy=false;if(destroyed)return;if(++failures===1)noty('YouTube: повторяем загрузку страницы…');retryTimer=setTimeout(function(){retryTimer=null;comp.next();},Math.min(15000,1000*Math.pow(2,Math.min(failures-1,4))));}
    function retryAge(){
      busy=false;
      if(destroyed)return;
      if(ageTries>=ageWait.length){
        try{comp.activity.loader(false);}catch(e){}
        noty('Возраст ещё проверяется');
        return;
      }
      var wait=ageWait[ageTries++];
      ageTimer=setTimeout(function(){ageTimer=null;comp.next();},wait);
    }
    comp.create=function(){this.activity.loader(true);this.next();};
    comp.next=function(){
      if(destroyed||feedEnded||busy||retryTimer!==null||ageTimer!==null)return;
      busy=true;var pg=nextPage;
      request=load(pg,function(res){
        if(destroyed)return;
        if(res.retry_page===true){failures=0;retryAge();return;}
        busy=false;failures=0;ageTries=0;object.page=pg;nextPage=pg+1;feedEnded=!res.has_more;
        res.results=res.results.filter(function(c){var id=String(c.id||c.yt_id||c.yt_ch);if(seen[id])return false;seen[id]=true;return true;});
        shown+=res.results.length;res.total_pages=feedEnded?pg:Math.max(500,pg+1);
        if(!built){if(res.results.length||feedEnded){comp.build(res);built=true;try{comp.render().addClass(object.yt.mode==='subs'?'yt-sq':'yt-wide');}catch(e){}}}
        else{comp.append(res,true);comp.limit();}
        pump();
      },retry);
    };

    // содержимое шапки канала: логотип, название, подписчики, описание и кнопка подписки (нажатие на шапку = подписаться/отписаться)
    function fillHeader(card) {
      var i = info || {};
      var el = $(card.render());
      el.addClass('yt-headcard');
      var bits = [];
      if (i.followers) bits.push(nf(i.followers) + ' подписчиков');
      var h = $('<div class="yt-chead"></div>');
      h.append($('<img class="yt-chead__ava">').attr('src', i.avatar || (BASE + '/yt/avatar.svg')));
      var box = $('<div class="yt-chead__box"></div>');
      box.append($('<div class="yt-chead__name"></div>').text(i.title || object.title || ''));
      if (bits.length) box.append($('<div class="yt-chead__meta"></div>').text(bits.join(' · ')));
      if (i.description) box.append($('<div class="yt-chead__descr"></div>').text(i.description));
      var btn = $('<div class="yt-chead__btn">Подписаться</div>');
      box.append(btn);
      h.append(box);
      el.children().hide();   // не удалять: Lampa обращается к служебным элементам карточки (иконки избранного и т.п.)
      el.append(h);
      api('/yt/subs?profile=' + q(profile()), function (d) {
        var on = (d.channels || []).some(function (x) { return x.id === i.id; });
        btn.text(on ? 'Отписаться' : 'Подписаться'); btn.data('on', on);
      });
      card.onEnter = function () {
        var on = !!btn.data('on');
        api(on ? '/yt/subs/remove?profile=' + q(profile()) + '&ch=' + q(i.id) : '/yt/subs/add?profile=' + q(profile()) + '&channel=' + q(i.id), function () {
          btn.data('on', !on); btn.text(on ? 'Подписаться' : 'Отписаться'); noty(on ? 'Отписка' : 'Подписка: ' + (i.title || ''));
        });
      };
      card.onMenu = function () {};
    }

    comp.cardRender = function (obj, element, card) {
      var index=cards.length;cards.push(card);
      var keepFocus = card.onFocus;
      card.onFocus = function () { focused=index;if (typeof keepFocus === 'function') keepFocus.apply(card, arguments); pump(); };
      if (element.yt_head) return fillHeader(card);
      card.onEnter = function () {
        if (element.yt_id) play(element.yt_id, element.title);
        else if (element.yt_ch) openChannel(element.yt_ch, element.title);
      };
      card.onMenu = function () {
        var cid = element.yt_channel_id || element.yt_ch;
        if (cid) channelMenu({ channel_id: cid, channel: element.yt_channel || element.title }, function () { Lampa.Controller.toggle(ctrl()); });
      };
    };
    return comp;
  }
  Lampa.Component.add('yt_list', ytComponent);

  // Lampa при запуске восстанавливает последнюю страницу раньше, чем загрузился этот плагин, и она выходит пустой: перестраиваем её, когда компонент уже есть
  try {
    var cur = Lampa.Activity.active();
    if (cur && cur.component === 'yt_list' && cur.yt) {
      setTimeout(function () { try { Lampa.Activity.replace({ url: '', title: cur.title, component: 'yt_list', page: 1, yt: cur.yt }); } catch (e) {} }, 400);
    }
  } catch (e) {}

  function openPage(title, yt) { Lampa.Activity.push({ url: '', title: title, component: 'yt_list', page: 1, yt: yt }); }
  function openChannel(id, name) { openPage(name || 'Канал', { mode: 'channel', id: id }); }

  function search() {
    ctrl();
    // раскладка «clarify»: есть и микрофон (голосовой ввод через системное окно приложения), и кнопка «Поиск»
    Lampa.Input.edit({ title: 'Поиск на YouTube', value: '', free: true, nosave: true, layout: 'clarify' }, function (val) {
      if (!val) return Lampa.Controller.toggle(ctrl());
      openPage('YouTube: ' + val, { mode: 'search', q: val });
    });
  }

  // ---------- подписки ----------
  function feed() { openPage('Подписки · ' + profileLabel(), { mode: 'feed' }); }
  function channelsList() { openPage('Каналы · ' + profileLabel(), { mode: 'subs' }); }

  function addChannels() {
    ctrl();
    Lampa.Input.edit({ title: 'Ссылки на каналы, @имена (через пробел или запятую)', value: '', free: true, nosave: true }, function (val) {
      if (!val) return openMenu();
      noty('Добавляю…');
      $.ajax({
        url: BASE + '/yt/subs/addmany?profile=' + q(profile()), type: 'POST', contentType: 'text/plain; charset=utf-8', data: val.replace(/\s+/g, '\n'), dataType: 'json', timeout: 120000,
        success: function (d) {
          noty('Добавлено: ' + (d.added || []).length + ((d.failed || []).length ? ', не найдено: ' + d.failed.length : ''));
          openMenu();
        },
        error: function () { noty('Не удалось добавить'); openMenu(); }
      });
    });
  }

  function profiles() {
    api('/yt/profiles', function (d) {
      var items = (d.profiles || []).map(function (p) { return { title: (p.name === profile() ? '✓ ' : '') + (p.label || p.name), subtitle: p.subs + ' каналов', name: p.name, label: p.label || p.name }; });
      items.push({ title: '＋ Новый профиль', act: 'new' });
      showSelect('Профиль', items, openMenu, function (a) {
        if (a.act === 'new') {
          Lampa.Input.edit({ title: 'Имя профиля', value: '', free: true, nosave: true }, function (v) {
            v = (v || '').replace(/\s+/g, ' ').trim();
            if (!v) return openMenu();
            api('/yt/profiles?add=' + q(v), function (d2) {
              var created = ((d2 && d2.profiles) || []).filter(function (p) { return (p.label || p.name) === v; })[0];
              if (!created) { noty('Имя: 1–32 символа, без / ? & #'); return openMenu(); }
              setProfile(created.name, created.label); noty('Профиль: ' + created.label); openMenu();
            }, function () { noty('Не удалось создать'); openMenu(); });
          });
        } else { setProfile(a.name, a.label); noty('Профиль: ' + (a.label || a.name)); openMenu(); }
      });
    });
  }

  function openMenu() {
    ctrl();
    var items=[{title:'Лента подписок',act:'feed'},{title:'Поиск видео',act:'search'},{title:'Мои каналы',act:'list'}];
    if(window.HomeUser)items.splice(1,0,{title:'Недавно смотрели',act:'history'});
    if(!window.HomeUser||!HomeUser.owner.profile.is_child||(HomeUser.owner.profile.settings||{}).youtube_mode==='normal')items.push({title:'Добавить каналы по ссылке',act:'add'});
    var childChannels=window.HomeUser&&HomeUser.owner.profile.is_child&&(HomeUser.owner.profile.settings||{}).youtube_mode==='channels';
    if(!childChannels)items.push({title:'Профили каналов',subtitle:profileLabel(),act:'prof'});
    showSelect('YouTube · '+(window.HomeUser?HomeUser.owner.profile.name:profile()),items,null,function(a){
      if(a.act==='history')return api('/yt/history',function(d){showResults('Недавно смотрели',d.results,openMenu);});
      ({feed:feed,search:search,list:channelsList,add:addChannels,prof:profiles})[a.act]();
    });
  }

  // ---------- трейлер в карточке ----------
  function hookTrailer() {
    Lampa.Listener.follow('full', function (e) {
      if (e.type !== 'complite' || !e.data || !e.data.movie) return;
      var movie = e.data.movie;
      var media = (e.object && e.object.method) || (movie.name ? 'tv' : 'movie');
      var id = parseInt(movie.id, 10);
      if (!id || String(movie.id) !== String(id)) return;
      var render = e.object.activity.render();
      var title = movie.title || movie.name || '';
      var year = String(movie.release_date || movie.first_air_date || '').slice(0, 4);
      var n = 0;
      // нужно дождаться, пока Lampa нарисует ряд кнопок
      var t = setInterval(function () {
        n++;
        var cont = render.find('.full-start-new__buttons').first();
        if (!cont.length) cont = render.find('.full-start__buttons').first();
        if (cont.length || n > 40) clearInterval(t);
        if (!cont.length || render.find('.view--yttrailer').length) return;
        // встроенная кнопка «Трейлеры» Lampa берёт ролики с YouTube напрямую и в России не работает — заменяем своей
        render.find('.view--trailer, .button--trailer').remove();
        var btn = $('<div class="full-start__button selector view--yttrailer">' + ICON.replace('width="24" height="24"', 'width="24" height="24" style="vertical-align:middle;margin-right:.45em"') + '<span style="vertical-align:middle">Трейлер</span></div>');
        btn.on('hover:enter', function () {
          noty('Ищу трейлер…');
          api('/yt/trailer?media=' + media + '&tmdb=' + id + '&year=' + encodeURIComponent(year) + '&title=' + encodeURIComponent(title), function (d) {
            if (!d || !d.id) return noty('Трейлер не найден');
            play(d.id, 'Трейлер · ' + title, false);
          }, function () { noty('Трейлер не найден'); });
        });
        cont.append(btn);
        // Кнопка появляется после построения навигации карточки. DOM-класса selector недостаточно.
        var active = Lampa.Activity.active();
        var currentId = active && (active.id || (active.card && active.card.id) || (active.movie && active.movie.id));
        if (String(currentId) === String(id) && render[0] && document.documentElement.contains(render[0])) {
          var cn = Lampa.Controller.enabled().name;
          if (cn === 'full_start' || (cn === 'content' && !render.find('.full-start-new__buttons').length))
            Lampa.Controller.collectionAppend(btn);
        }
      }, 250);
    });
  }

  // ---------- строка «YouTube · подписки» на главной ----------
  var MON = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
  // подпись даты на карточке: «сегодня», «вчера», «3 дн. назад», дальше — «12 октября» / «12 октября 2025»
  function dateLabel(iso) {
    var t = Date.parse(iso);
    if (!t) return '';
    var d = new Date(t), now = new Date();
    var days = Math.floor((new Date(now.getFullYear(), now.getMonth(), now.getDate()) - new Date(d.getFullYear(), d.getMonth(), d.getDate())) / 86400000);
    if (days <= 0) return 'сегодня';
    if (days === 1) return 'вчера';
    if (days < 7) return days + ' дн. назад';
    return d.getDate() + ' ' + MON[d.getMonth()] + (d.getFullYear() !== now.getFullYear() ? ' ' + d.getFullYear() : '');
  }

  function cardOf(v) {
    return {
      id: 'yt' + v.id,
      yt_id: v.id,
      yt_channel_id: v.channel_id,
      yt_channel: v.channel,
      title: v.title,
      original_title: v.channel || '',
      img: v.thumb,
      poster_path: '',
      backdrop_path: '',
      overview: (v.channel || '') + ((v.published || v.ago) ? ' · опубликовано ' + (v.published ? dateLabel(v.published) : v.ago) : ''),
      quality: v.duration ? fmt(v.duration) : '',                                  // плашка снизу слева: длительность
      yt_date: v.published ? dateLabel(v.published) : (v.ago || ''),               // плашка сверху слева: дата выхода
      release_date: String(v.published || '').slice(0, 10),
      vote_average: 0,
      source: 'youtube',
      params: { style: { name: 'wide' } }
    };
  }

  function addRow() {
    if (!Lampa.ContentRows || window.__yt_row) return;
    window.__yt_row = true;
    Lampa.ContentRows.add({
      name: 'yt_row_feed',
      title: 'YouTube · подписки',
      index: 7.95,      // над «Мои фильмы» (8), под «Мультфильмы» (7.9)
      screen: ['main'],
      call: function () {
        return function (callback) {
          api('/yt/feed?n=30&profile=' + q(profile()), function (d) {
            var items = (d.results || []).map(cardOf);
            if (!items.length) return callback({ results: [] });
            callback({
              results: items,
              title: 'YouTube · подписки',
              nomore: true,
              params: { items: { view: 4 } },
              card_events: {
                onEnter: function (target, card) { if (card && card.yt_id) play(card.yt_id, card.title); },
                onMenu: function (target, card) {
                  if (card && card.yt_channel_id) channelMenu({ channel_id: card.yt_channel_id, channel: card.yt_channel }, function () { Lampa.Controller.toggle(ctrl()); });
                }
              }
            });
          }, function () { callback({ results: [] }); }, 60000);
        };
      }
    });
  }

  // ---------- меню ----------
  function addMenu() {
    if ($('.menu .menu__list [data-action="youtube"]').length) return;
    var btn = $('<li class="menu__item selector" data-action="youtube"><div class="menu__ico">' + ICON + '</div><div class="menu__text">YouTube</div></li>');
    btn.on('hover:enter', openMenu);
    $('.menu .menu__list').eq(0).append(btn);
    if (window.__kp_placeMenu) window.__kp_placeMenu();
  }

  function start() {
    hookTrailer();
    addRow();
    addMenu();
    setTimeout(addMenu, 2000);
    setTimeout(addMenu, 6000);
  }

  window.__yt = { play: play, search: search, openMenu: openMenu, openChannel: openChannel, channels: function () { channelsList(); if (window.__mc_ret) { window.__mc_base = Lampa.Activity.all().length; window.__mc_ret = 2; } }, feed: function () { openPage('YouTube · подписки', { mode: 'feed' }); if (window.__mc_ret) { window.__mc_base = Lampa.Activity.all().length; window.__mc_ret = 2; } } };

  if (window.appready) start();
  else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
