(function(root){
  'use strict';
  function packet(ctx,r,voice){
    if(r.source!=='lift'||r.codec!=='vp9'||r.protocol!=='dash')throw Error('lift_format');
    if(r.profile_id!==ctx.profileId||r.identity!==ctx.identity||r.card!==ctx.cardKey)throw Error('lift_scope');
    var h=String(r.hash);if(!/^-?[0-9]{1,15}$/.test(h)||/^-?0+$/.test(h))throw Error('lift_hash');
    if(!voice||!(r.voices||[]).some(function(v){return v.name===voice.name&&v.representation_id===voice.representation_id;}))throw Error('lift_voice');
    if(!/^https:\/\//.test(r.video||''))throw Error('lift_video');
    var resume={src:'lift',item:String(r.item),season:ctx.season,ep:ctx.episode,
      voice_name:voice.name,codec:'vp9',edition:r.edition,binding_revision:r.binding_revision};
    var tl={hash:h,time:0,duration:r.duration||0,percent:0,received:true};
    var item={url:r.video,title:r.title,timeline:tl,hs:resume,season:ctx.season,episode:ctx.episode};
    return {url:r.video,title:r.title,card:ctx.card,hs:resume,timeline:tl,playlist:[item],
      home_player:true,home_base:ctx.base,home_source:'lift',home_mime:'application/dash+xml',max_height:1080,
      home_clock_ms:r.now,headers:r.headers||{},lift_binding_revision:r.binding_revision,
      lift_voice_rep:voice.representation_id,lift_audio_map:JSON.stringify(r.voices),
      lift_video_formats:JSON.stringify(r.representations),
      tl_meta:JSON.stringify([{id:ctx.identity,c:ctx.cardKey,h:h,r:resume}])};
  }
  // continue.js calls this from __home_resume before its generic field whitelist.
  function persistentResume(hs,title){
    if(!hs||hs.src!=='lift')throw Error('lift_resume');
    return {src:'lift',item:hs.item,season:hs.season,ep:hs.ep,voice_name:hs.voice_name,
      codec:'vp9',edition:hs.edition,binding_revision:hs.binding_revision,title:title};
  }
  function position(data,decision){
    if(decision==='start_zero'){
      data.timeline.time=0;data.timeline.percent=0;
      (data.playlist||[]).forEach(function(x){if(String(x.timeline.hash)===String(data.timeline.hash)){x.timeline.time=0;x.timeline.percent=0;}});
    }else if(decision!=='resume')throw Error('lift_decision_required');
  }
  function attempt(manager,owner,activity){
    manager.generation=(manager.generation||0)+1;
    (manager.requests||[]).forEach(function(r){try{r.abort();}catch(e){}});manager.requests=[];
    var generation=manager.generation,savedOwner=JSON.stringify(owner());var savedActivity=activity();
    return {valid:function(){return generation===manager.generation&&savedOwner===JSON.stringify(owner())&&savedActivity===activity();},
      cancel:function(){if(generation!==manager.generation)return;manager.generation++;(manager.requests||[]).forEach(function(r){try{r.abort();}catch(e){}});}};
  }
  root.LiftFlow={packet:packet,persistentResume:persistentResume,position:position,attempt:attempt};
})(typeof window==='undefined'?globalThis:window);
