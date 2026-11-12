(function(){
  'use strict';
  if(window.__home_lift_ready)return;window.__home_lift_ready=true;
  var BASE='{localhost}',manager={generation:0,requests:[]},active=null;
  if(BASE.indexOf('{')===0)BASE=location.origin;
  function owner(){return window.HomeUser&&HomeUser.owner;}
  function current(){return Lampa.Activity.active();}
  function note(s){Lampa.Noty.show(s);}
  function cancel(){if(active)active.attempt.cancel();active=null;}
  function request(c,path,body,ok){
    if(!c.attempt.valid())return;
    var xhr=$.ajax({url:BASE+path,type:body?'POST':'GET',data:body?JSON.stringify(body):undefined,
      contentType:body?'application/json':undefined,dataType:'json',timeout:45000,cache:false,
      // profile-start/profiles-web already attach the frozen page owner's session.
      // Repeating setRequestHeader combines values and makes the session invalid.
      success:function(r){if(c.attempt.valid())ok(r);},
      error:function(x){if(!c.attempt.valid())return;var code=x.responseJSON&&x.responseJSON.error;finish(c,false,code||'Lift не отвечает');}});
    manager.requests.push(xhr);
  }
  function finish(c,ok,message){
    if(!c.attempt.valid()||c.settled)return;c.settled=true;
    if(ok){if(c.ok)c.ok();}
    else {c.attempt.cancel();if(active===c)active=null;if(c.fail)c.fail(message);else note(message);}
  }
  function choose(c,title,items,select){
    if(!c.attempt.valid())return;
    if(!items.length)return finish(c,false,'В Lift нет подтверждённого варианта');
    Lampa.Select.show({title:title,items:items,onSelect:function(it){if(c.attempt.valid())select(it);},
      onBack:function(){if(!c.attempt.valid())return;cancel();Lampa.Controller.toggle(c.previous);}});
  }
  function bind(c){
    var title=c.card.title||c.card.name||c.card.original_title||c.card.original_name;
    request(c,'/home/lift/find',{query:title},function(results){
      choose(c,'Lift · Сверьте название и год',(results.items||[]).map(function(x){
        return {title:x.name+' ('+(x.year||'?')+')',subtitle:x.origin_name||'',candidate:x};
      }),function(choice){
        request(c,'/home/lift/catalog',{media:c.media,tmdb:c.card.id,season:c.media==='tv'?1:0,episode:c.media==='tv'?1:0,item:choice.candidate.id},function(list){
          choose(c,'Lift · Выберите фильм или серию',list.items||[],function(entry){
            var target={media:c.media,tmdb:c.card.id,season:entry.season,episode:entry.episode,
              item:choice.candidate.id,provider_season:entry.season,provider_episode:entry.episode};
            function confirm(){choose(c,'TMDB '+(c.media==='movie'?'фильм':'S'+target.season+' E'+target.episode)+' → Lift S'+entry.season+' E'+entry.episode+'?',
              [{title:'Подтвердить соответствие',confirmed:true},{title:'Отмена'}],function(answer){
                if(!answer.confirmed)return cancel();
                request(c,'/home/lift/bind',target,function(){query(c,target,null);});
              });}
            if(c.media==='movie')return confirm();
            request(c,'/home/lift/episodes',{media:'tv',tmdb:c.card.id,season:1,episode:1},function(meta){
              choose(c,'К какому сезону TMDB относится серия?',(meta.seasons||[]).map(function(s){return {title:s.title||('Сезон '+s.season),season:s.season,count:s.count};}),function(season){
                var episodes=[];for(var i=1;i<=Math.min(9999,season.count);i++)episodes.push({title:'TMDB · Сезон '+season.season+' · серия '+i,episode:i});
                choose(c,'Какая серия в TMDB?',episodes,function(ep){target.season=season.season;target.episode=ep.episode;confirm();});
              });
            });
          });
        });
      });
    });
  }
  function query(c,target,savedVoice){
    var body={media:c.media,tmdb:c.card.id,season:target.season,episode:target.episode};
    request(c,'/home/lift/options',body,function(options){
      function resolve(voice){
        var args=$.extend({},body,{voice_name:voice.name,binding_revision:options.binding_revision});
        request(c,'/home/lift/resolve',args,function(r){
          if(!c.attempt.valid())return;
          var context={profileId:c.owner.profile.id,identity:r.identity,cardKey:String(c.card.id)+'_'+c.media,
            card:c.card,base:BASE,season:target.season,episode:target.episode};
          // Cross-check the canonical identity independently of the descriptor.
          context.identity=c.media==='movie'?'movie-'+c.card.id:'tv-'+c.card.id+'-s'+target.season+'e'+target.episode;
          var data;try{data=LiftFlow.packet(context,r,r.selected_voice);}catch(e){return finish(c,false,'Lift: неверные данные запуска');}
          data.lift_launch_id=manager.generation;
          try{Lampa.Player.play(data);}catch(e){finish(c,false,'Lift: не удалось передать запуск');}
          // Completion is acknowledged by the final common playerExtras bridge hook.
        });
      }
      if(savedVoice){
        var hits=(options.voices||[]).filter(function(v){return v.name===savedVoice;});
        if(hits.length===1)return resolve(hits[0]);
        note('Прежняя озвучка недоступна — выберите другую');
      }
      choose(c,'Lift · Озвучка',(options.voices||[]).map(function(v){return {title:v.name,voice:v};}),function(v){resolve(v.voice);});
    });
  }
  function open(card,media,rec,ok,fail){
    cancel();
    if(!window.__home_lift_enabled)return fail?fail('Источник Lift выключен'):note('Источник Lift выключен');
    if(!window.AndroidJS||typeof AndroidJS.liftVersion!=='function'||AndroidJS.liftVersion()<1)
      return fail?fail('Обновите APK для Lift VP9'):note('Обновите APK для Lift VP9');
    var c={card:card,media:media,owner:JSON.parse(JSON.stringify(owner())),ok:ok,fail:fail,settled:false,
      previous:(Lampa.Controller.enabled()||{}).name||'full_start'};
    c.attempt=LiftFlow.attempt(manager,owner,current);active=c;
    request(c,'/home/lift/item?media='+encodeURIComponent(media)+'&tmdb='+encodeURIComponent(card.id),null,function(d){
      var items=(d.items||[]).map(function(x){var a=String(x.key).split(':');return {title:media==='movie'?'Фильм':'Сезон '+a[2]+' · серия '+a[3],season:Number(a[2]),episode:Number(a[3])};})
        .sort(function(a,b){return a.season-b.season||a.episode-b.episode;});
      var admin=c.owner.profile.is_admin&&!c.owner.profile.is_child;
      if(!items.length)return admin?bind(c):finish(c,false,'Lift: администратор ещё не подтвердил соответствие видео');
      if(media==='movie') {
        if(rec || !admin)return query(c,{season:0,episode:0},rec&&rec.voice_name);
        return choose(c,'Lift',[{title:'Смотреть подтверждённый фильм'},{title:'Найти другой вариант',bind:true}],function(x){if(x.bind)bind(c);else query(c,{season:0,episode:0},null);});
      }
      if(rec&&!rec.done){var hit=items.filter(function(x){return x.season===Number(rec.season)&&x.episode===Number(rec.ep);});if(hit.length===1)return query(c,hit[0],rec.voice_name);}
      if(admin)items.push({title:'Добавить серию / другой вариант',bind:true});
      choose(c,'Lift · Выберите серию',items,function(x){if(x.bind)bind(c);else query(c,x,null);});
    });
  }
  window.HomeLift={open:open,cancel:cancel,
    track:function(xhr){manager.requests.push(xhr);},
    valid:function(data){return !!active&&data.lift_launch_id===manager.generation&&active.attempt.valid();},
    accepted:function(data){if(this.valid(data))finish(active,true);},
    failed:function(data,message){if(this.valid(data))finish(active,false,message);}};
  window.HomeSrc=window.HomeSrc||{};
  HomeSrc.lift={resume:function(rec,card,media,ok,fail){open(card,media,rec,ok,fail);},cancel:cancel};
  function addButton(card,media,render){
    if(!window.__home_lift_enabled||!card||!render||typeof render.find!=='function')return;
    if(typeof media!=='string'||!/^(movie|tv)$/.test(media))media=card.media_type||((card.name||card.original_name)?'tv':'movie');
    if(!/^(movie|tv)$/.test(media)||!/^\d+$/.test(String(card.id))||card.source==='wrestling'||render.find('.view--lift').length)return;
    var button=$('<div class="full-start__button selector view--lift"><svg viewBox="0 0 24 24" width="24" height="24" fill="none" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="3" stroke="currentColor" stroke-width="1.7"/><path d="m10 8 6 4-6 4Z" fill="currentColor"/></svg><span>Lift</span></div>');
    button.on('hover:enter',function(){open(card,media,null);});
    // The source menu reads the container holding Torrent/KinoPub, not the first button bar.
    var anchor=render.find('.view--torrent, .view--kinopub').first();
    if(anchor.length)anchor.after(button);
    else render.find('.buttons--container, .full-start__buttons, .full-start-new__buttons').first().append(button);
    try{if(button.is(':visible'))Lampa.Controller.collectionAppend(button);}catch(ignore){}
  }
  function hook(){
    Lampa.Listener.follow('full',function(e){
      if(e.type!=='complite'||!e.data||!e.data.movie)return;
      var o=e.object;if(o&&o.activity)addButton(e.data.movie,o.method,o.activity.render());
    });
    // A newly installed plugin can arrive after the current full-card completion event.
    var a=current();if(a&&a.component==='full'&&a.activity)addButton(a.card||a.movie,a.method,a.activity.render());
  }
  if(window.appready)hook();else Lampa.Listener.follow('app',function(e){if(e.type==='ready')hook();});
})();
