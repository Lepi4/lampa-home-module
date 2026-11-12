// Called inside the success callback of the existing __home_prepare_progress.
window.__home_lift_before_native=function(data,next,fail){
  if(data.home_source!=='lift')return next();
  function valid(){return window.HomeLift&&HomeLift.valid(data);}
  if(!valid())return;
  var meta=JSON.parse(data.tl_meta)[0],r=meta.r,parts=String(meta.c).split('_');
  var target={media:parts[1],tmdb:Number(parts[0]),season:r.season||0,episode:r.ep||0,
    binding_revision:r.binding_revision};
  var sent=false;
  function post(path,body,success){
    var xhr=$.ajax({url:data.home_base+path,type:'POST',data:JSON.stringify(body),
      contentType:'application/json',dataType:'json',timeout:45000,cache:false,
      // The common profile XHR wrapper supplies the session exactly once.
      success:function(result){if(valid())success(result);},
      error:function(){if(valid())fail('Не удалось подготовить видео Lift');}});
    HomeLift.track(xhr);
  }
  post('/home/lift/position-context',target,function(context){
      if(!valid())return;
      function finish(decision){
        if(!valid()||sent)return;sent=true;
        var args=Object.assign({},target,{voice_name:r.voice_name});
        // Refresh the signed MPD after the user dialog. Keep the resolved progress decision.
        post('/home/lift/resolve',args,function(fresh){
          var check={profileId:HomeUser.owner.profile.id,identity:meta.id,cardKey:meta.c,
            card:data.card,base:data.home_base,season:r.season,episode:r.ep};
          var packet;try{packet=LiftFlow.packet(check,fresh,fresh.selected_voice);}catch(e){return fail('Lift: данные видео изменились');}
          data.url=packet.url;data.headers=packet.headers;
          data.playlist[0].url=packet.url;
          data.lift_voice_rep=packet.lift_voice_rep;data.lift_audio_map=packet.lift_audio_map;
          data.lift_video_formats=packet.lift_video_formats;
          data.home_clock_ms=fresh.now;
          // A reset or a new position may arrive from another device while the dialog is open.
          window.__home_prepare_progress(data,function(){
            if(!valid())return;
            LiftFlow.position(data,decision);next();
          },fail);
        });
      }
      if(!(data.timeline.time>0))return finish('start_zero');
      var time=Math.floor(data.timeline.time),label=Math.floor(time/60)+':'+('0'+time%60).slice(-2);
      Lampa.Select.show({title:context.compatible?'Lift · Продолжение':'Lift · Другая версия видео',
        items:[{title:'Продолжить с '+label,decision:'resume'},{title:'С начала',decision:'start_zero'}],
        onSelect:function(it){finish(it.decision);},
        onBack:function(){if(valid())HomeLift.cancel();}});
  });
};
