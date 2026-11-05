(function () {
'use strict';
var owner = window.__home_scope;
if (!owner || !owner.profiles_enabled || !owner.ready) return;
var profile = owner.profile, origin = new URL(owner.server_base).origin;
var prefix = 'home_user:' + owner.server_id + ':' + profile.id + ':';
var shared = /^(torrserver_url|torrserver_url_two|parser_torrent_type|jackett_url|jackett_key|protocol|cub_mirrors|cub_domain|cub_alive|language|interface_size|device|platform|invc_.*|home_device_.*)$/;
var store = window.localStorage, proto = Storage.prototype;
var get = proto.getItem, set = proto.setItem, remove = proto.removeItem, key = proto.key;
function scoped(k) { k=String(k); return shared.test(k) ? k : prefix+k; }
// The legacy origin belongs only to the primary adult. Copy without deleting it, for rollback.
if (profile.id === 'u_default' && !get.call(store, prefix+'__imported')) {
 var keys=[]; for(var i=0;i<store.length;i++) keys.push(key.call(store,i));
 keys.forEach(function(k){ if(k && k.indexOf('home_user:')!==0 && !shared.test(k) && get.call(store,prefix+k)===null) set.call(store,prefix+k,get.call(store,k)); });
 set.call(store,prefix+'__imported','1');
}
// Seed before Lampa starts; the later setup iframe can otherwise lose this race.
if(get.call(store,'language')===null)set.call(store,'language','ru');
proto.getItem=function(k){return get.call(this,this===store?scoped(k):k);};
proto.setItem=function(k,v){return set.call(this,this===store?scoped(k):k,v);};
proto.removeItem=function(k){return remove.call(this,this===store?scoped(k):k);};
proto.clear=function(){if(this!==store){while(this.length)remove.call(this,key.call(this,0));return;}var ks=[];for(var i=0;i<this.length;i++){var k=key.call(this,i);if(k.indexOf(prefix)===0)ks.push(k);}ks.forEach(function(k){remove.call(store,k);});};
// Property access, key enumeration and backup/export must see this profile only as well.
var rawLength=Object.getOwnPropertyDescriptor(Storage.prototype,'length').get;
function visibleKeys(){var out=[];for(var i=0;i<rawLength.call(store);i++){var k=key.call(store,i);if(k.indexOf(prefix)===0)out.push(k.substring(prefix.length));else if(shared.test(k))out.push(k);}return out;}
var facade=new Proxy(store,{
 get:function(target,p){if(p==='length')return visibleKeys().length;if(p==='getItem')return function(k){return get.call(store,scoped(k));};if(p==='setItem')return function(k,v){return set.call(store,scoped(k),v);};if(p==='removeItem')return function(k){return remove.call(store,scoped(k));};if(p==='clear')return function(){visibleKeys().forEach(function(k){if(!shared.test(k))remove.call(store,scoped(k));});};if(p==='key')return function(i){return visibleKeys()[i]||null;};if(typeof p!=='string'||p==='constructor'||p==='toString')return Reflect.get(target,p,target);return get.call(store,scoped(p));},
 set:function(target,p,v){set.call(store,scoped(p),v);return true;},
 deleteProperty:function(target,p){remove.call(store,scoped(p));return true;},
 ownKeys:function(){return visibleKeys();},
 has:function(target,p){return typeof p==='string'&&get.call(store,scoped(p))!==null;},
 getOwnPropertyDescriptor:function(target,p){var v=get.call(store,scoped(p));return v===null?undefined:{value:v,writable:true,enumerable:true,configurable:true};}
});
Object.defineProperty(window,'localStorage',{configurable:true,enumerable:true,get:function(){return facade;}});
// IndexedDB and catalog caches are private too; a stricter policy starts with a fresh result cache.
var resultPrefix=prefix+'policy:'+(profile.policy_rev||1)+':';
var databases=window.indexedDB||window.webkitIndexedDB||window.mozIndexedDB;
if(databases){
 var dbOpen=databases.open,dbDelete=databases.deleteDatabase;
 databases.open=function(name){var args=Array.prototype.slice.call(arguments);args[0]=resultPrefix+name;return dbOpen.apply(this,args);};
 databases.deleteDatabase=function(name){return dbDelete.call(this,resultPrefix+name);};
 if(databases.databases){var dbList=databases.databases;databases.databases=function(){return dbList.call(this).then(function(list){return list.filter(function(d){return d.name.indexOf(resultPrefix)===0;}).map(function(d){return {name:d.name.substring(resultPrefix.length),version:d.version};});});};}
}
// A page owns its credential for its entire lifetime. A late callback never adopts the next user.
var open=XMLHttpRequest.prototype.open,send=XMLHttpRequest.prototype.send;
function homeRequestUrl(value){
 var u=new URL(value,location.href);
 if(!profile.is_child)return u.href;
 if(u.origin===origin&&u.pathname.indexOf('/tmdb/api/3/')===0)return origin+'/home/catalog/'+u.pathname.substring(12)+u.search;
 if(u.hostname==='api.themoviedb.org'&&u.pathname.indexOf('/3/')===0)return origin+'/home/catalog/'+u.pathname.substring(3)+u.search;
 // An asynchronous 403 lets optional analytics/config requests fail normally;
 // a synchronous throw aborted startup before our catalog plugins were loaded.
 if(u.origin!==origin)return origin+'/home/blocked-external';
 return u.href;
}
XMLHttpRequest.prototype.open=function(method,url){arguments[1]=homeRequestUrl(url);this.__homeLocal=new URL(arguments[1]).origin===origin;return open.apply(this,arguments);};
XMLHttpRequest.prototype.send=function(){if(this.__homeLocal)this.setRequestHeader('X-Home-Session',owner.token);return send.apply(this,arguments);};
if(window.fetch){var fetch=window.fetch;window.fetch=function(input,options){
 var value=typeof input==='string'?input:(input.url||input.href),url=homeRequestUrl(value);
 if(new URL(url).origin===origin){options=Object.assign({},options);options.headers=new Headers(options.headers||(input&&input.headers));options.headers.set('X-Home-Session',owner.token);}
 if(typeof input==='string'||input instanceof URL)input=url;
 else if(input.url!==url)input=new Request(url,input);
 return fetch.call(this,input,options);
};}
if(profile.is_child)set.call(store,prefix+'source','tmdb');
window.HomeUser={owner:owner,prefix:prefix};
// Defer core hooks; the storage namespace and request owner above are already installed.
var tries=0,timer=setInterval(function(){if(!window.Lampa||!Lampa.Activity||!Lampa.Torrent){if(++tries>300)clearInterval(timer);return;}clearInterval(timer);init();},200);
function init(){
 // Use the same ordinary poster cache entries as the launcher. Explicit sizes
 // (backdrops, cast photos, etc.) keep their original semantics.
 if(Lampa.Api && Lampa.Api.img && !Lampa.Api.img.__homePoster){
  var originalImg=Lampa.Api.img;
  var canonicalImg=function(path,size){
   var url=originalImg.apply(this,arguments);
   if(!size&&typeof url==='string'){
    try{var u=new URL(url,location.href);if(u.origin===origin&&u.pathname.indexOf('/tmdb/img/t/p/')===0){u.pathname=u.pathname.replace(/\/w\d+\/+/, '/w342/');return u.href;}}catch(ignore){}
   }
   return url;
  };
  canonicalImg.__homePoster=true;Lampa.Api.img=canonicalImg;
 }
 // A held OK must not select the first action in the menu it just opened.
 if(Lampa.Keypad&&Lampa.Keypad.listener&&Lampa.Select.listener){
   var okDown=false,waitFreshOk=false;
   function isOk(e){return [13,23,66].indexOf(e.keyCode||e.which)>=0;}
   window.addEventListener('keydown',function(e){if(!isOk(e))return;
     if(okDown||e.repeat){if(waitFreshOk){e.preventDefault();e.stopImmediatePropagation();}return;}
     okDown=true;waitFreshOk=false;
   },true);
   window.addEventListener('keyup',function(e){if(isOk(e))okDown=false;},true);
   window.addEventListener('blur',function(){okDown=false;waitFreshOk=false;});
   Lampa.Keypad.listener.follow('longdown',function(){waitFreshOk=true;});
   Lampa.Select.listener.follow('preshow',function(e){var p=e.active,original=p.onBeforeClose;
     p.onBeforeClose=function(){return !waitFreshOk&&(!original||original.apply(this,arguments));};
   });
 }
 if(profile.is_child){var originalSet=Lampa.Storage.set;Lampa.Storage.set=function(k,v){if(k==='source')v='tmdb';return originalSet.call(this,k,v);};Lampa.Storage.set('source','tmdb');}

 var cfg=profile.settings||{}, allowSubtitlesOnly=cfg.allow_subtitles_only===true, feedBlock=cfg.feed_block_quality||[], generation=0, pendingAuto=null, pendingRetry=null, manualUntil=0, autoTimer=null, autoPlaybackPending=false;
 function cancelAuto(){generation++;pendingAuto=null;pendingRetry=null;autoPlaybackPending=false;clearTimeout(autoTimer);}
 Lampa.Activity.listener.follow('popstate',cancelAuto);Lampa.Listener.follow('activity',function(e){if(e.type==='init')cancelAuto();});
 if(Lampa.Player&&Lampa.Player.play){var originalPlay=Lampa.Player.play;
   Lampa.Player.play=function(data){
     if(autoPlaybackPending&&pendingRetry){data.home_auto_attempt=pendingRetry.attempt;data.home_auto_profile=profile.id;pendingRetry.at=Date.now();}
     else pendingRetry=null;
     autoPlaybackPending=false;
     return originalPlay.apply(this,arguments);
   };
 }
 var push=Lampa.Activity.push;
 function manual(object){cancelAuto();manualUntil=Date.now()+2000;return push.call(Lampa.Activity,object);}
 function videoFiles(files,movie,target){
   return files.filter(function(f){if(!/\.(mkv|mp4|avi|m4v|ts|m2ts|mov|webm)$/i.test(f.path||''))return false;if(/sample|trailer|extras/i.test(f.path||''))return false;
     if(!target)return true;var info=Lampa.Torserver.parse({movie:movie,files:files,filename:f.path_human||f.path,path:f.path});return +info.season===target.season&&+info.episode===target.episode;
   });
 }
 function chooseTarget(movie,callback){
   if(!(movie.name||movie.number_of_seasons)){callback(null);return;}
   $.ajax({url:owner.server_base+'/home/auto/episode?tmdb='+movie.id,dataType:'json',timeout:12000,success:function(d){if(d.season&&d.episode)callback({season:+d.season,episode:+d.episode});else callback(false);},error:function(){callback(false);}});
 }
 function resolution(e){var t=e.Title||e.title||'';return /2160|4k|uhd/i.test(t)?2160:/1080/i.test(t)?1080:/720/i.test(t)?720:/480|576/i.test(t)?480:0;}
 function sameTitle(e,movie,target){
   function norm(t){return String(t||'').toLowerCase().replace(/[^a-zа-яё0-9\u3040-\u30ff\u3400-\u9fff]+/g,' ').replace(/\s+/g,' ').trim();}
   var release=' '+norm(e.Title||e.title)+' ',names=[movie.title,movie.original_title,movie.name,movie.original_name].map(norm).filter(function(n){return n.length>=3;});
   if(!names.some(function(n){return release.indexOf(' '+n+' ')>=0;}))return false;
   var year=String(movie.release_date||movie.first_air_date||'').slice(0,4),years=String(e.Title||e.title||'').match(/\b(?:19|20)\d{2}\b/g)||[];
   var inTitle=names.join(' ').match(/\b(?:19|20)\d{2}\b/g)||[];
   var alien=years.filter(function(y){return y!==year&&inTitle.indexOf(y)<0;});
   if(year&&alien.length&&years.indexOf(year)<0)return false;
   return true;
 }
 function codecMode(v, fallback){if(v==='any'||v==='only'||v==='no')return v;if(v===false)return 'no';if(v===true)return 'any';return fallback;}
 function allows(mode, hit){if(mode==='any')return true;return mode==='only'?hit:!hit;}
 function eligible(e){var title=e.Title||e.title||'',h=/2160|4k|uhd/i.test(title)?2160:/1080/i.test(title)?1080:/720/i.test(title)?720:/480|576/i.test(title)?480:0;
   var hevc=/hevc|h[ .]?265|x265/i.test(title),hdr=/(?:^|[^a-z0-9])hdr(?:10)?\+?(?![a-z])/i.test(title)||/dolby[ .]?vision|\bdv\b/i.test(title);
   var subsOnly=/\bСТ\b|\[СТ\]|\bSub\b|\bSUB\b|субтитр/i.test(title)&&!/Дубл|дубл|MVO|DVO|AVO|озвуч|Озвуч|Многоголос|Закадров|\bDUB\b|\bRUS\b|\bрус/i.test(title);
   if(subsOnly&&!allowSubtitlesOnly)return false;
   return h>0&&h<=(+cfg.torrent_max_height||1080)&&+(e.Seeders!=null?e.Seeders:e.Seeds||0)>=(+cfg.torrent_min_seeds||5)&&allows(codecMode(cfg.torrent_allow_hevc,'any'),hevc)&&allows(codecMode(cfg.torrent_allow_hdr,'no'),hdr);
 }
 Lampa.Activity.push=function(object){if(!cfg.torrent_auto||object.component!=='torrents'||Date.now()<manualUntil)return push.apply(this,arguments);
   var movie=object.movie;if(!movie||!Lampa.Parser||!Lampa.Torserver)return manual(object);var own=++generation,active=Lampa.Activity.active();
   function fallback(){if(own!==generation||Lampa.Activity.active()!==active)return;manual(object);}
   clearTimeout(autoTimer);autoTimer=setTimeout(fallback,35000);
   Lampa.Noty.show('Выбираю раздачу…');
   chooseTarget(movie,function(target){if(own!==generation||Lampa.Activity.active()!==active)return;if(target===false)return fallback();
     Lampa.Parser.get(object,function(data){var candidates=(data.Results||[]).filter(function(e){return eligible(e)&&sameTitle(e,movie,target);});candidates.sort(function(a,b){return resolution(b)-resolution(a)||+(b.Size||0)-+(a.Size||0);});if(!candidates.length){Lampa.Noty.show('Подходящих раздач нет — ручной выбор');return fallback();}
       // Bound metadata work: compare the actual episode files of the eight largest eligible releases.
       var counts={},pool=candidates.filter(function(e){var h=resolution(e);counts[h]=(counts[h]||0)+1;return counts[h]<=2;}).slice(0,8),best=null,spare=null,deadline=Date.now()+35000;
       function consider(release,file){var size=+(file.length||file.size||0),key=release.MagnetUri||release.Link||release.Title,item={release:release,file:file,size:size,key:key,height:resolution(release)};if(!best||item.height>best.height||(item.height===best.height&&size>best.size)){if(best&&best.key!==key)spare=best;best=item;}else if(key!==best.key&&(!spare||size>spare.size))spare=item;}
       function next(){if(own!==generation||Lampa.Activity.active()!==active)return;if(!pool.length||Date.now()>deadline){if(!best){Lampa.Noty.show('Не удалось проверить файлы — ручной выбор');return fallback();}clearTimeout(autoTimer);pendingAuto={path:best.file.path,movie:movie};pendingRetry={attempt:Date.now()+':'+own+':'+Math.random().toString(36).slice(2),release:spare&&spare.release,file:spare&&spare.file,movie:movie,object:object,at:Date.now(),used:false};Lampa.Torrent.start(best.release,movie);return;}
         var release=pool.shift();Lampa.Torserver.hash({title:release.Title,link:release.MagnetUri||release.Link,poster:movie.poster_path,data:{lampa:true,movie:movie}},function(result){var attempts=0;
           function files(){if(own!==generation||Lampa.Activity.active()!==active)return;Lampa.Torserver.files(result.hash,function(d){if(d.file_stats){var list=videoFiles(d.file_stats,movie,target);list.forEach(function(f){consider(release,f);});next();}else if(++attempts<6&&Date.now()<deadline)setTimeout(files,750);else next();},next);}files();
         },next);
       }
       Lampa.Torserver.connected(next,function(){fallback();});
     },function(){fallback();});
   });return;
 };
 window.__homeTorrentRetry=function(result){var job=pendingRetry;if(!job||!result||result.attempt!==job.attempt||result.profile!==profile.id)return;if(!result.startupFailed){pendingRetry=null;return;}if(job.used||Date.now()-job.at>20000||!job.release){pendingRetry=null;Lampa.Noty.show('Не запустилось — ручной выбор');return manual(job.object);}job.used=true;job.at=Date.now();Lampa.Noty.show('Пробую другую раздачу…');pendingAuto={path:job.file.path,movie:job.movie};Lampa.Torrent.start(job.release,job.movie);};
 Lampa.Listener.follow('torrent_file',function(e){if(e.type==='render'&&pendingAuto&&e.element.path===pendingAuto.path){pendingAuto=null;autoPlaybackPending=true;setTimeout(function(){e.item.trigger('hover:enter');},0);}if(e.type==='list_close')pendingAuto=null;});
 Lampa.Listener.follow('full',function(e){if(e.type!=='complite')return;
   var movie=e.data&&e.data.movie;if(movie&&movie.id)$.getJSON(owner.server_base+'/home/content-kind?media='+(movie.name?'tv':'movie')+'&id='+movie.id,function(d){if(d.kind)movie.home_kind=d.kind;});
   try{var render=e.object.activity.render();render.find('.view--torrent').off('hover:long.homeProfiles').on('hover:long.homeProfiles',function(){manualUntil=Date.now()+3000;$(this).trigger('hover:enter');return false;});}catch(x){}});
 // Holding the torrent entry in the source menu opens classic release selection, rather than changing priority.
 var show=Lampa.Select.show;Lampa.Select.show=function(p){if(cfg.torrent_auto&&p&&p.items){var long=p.onLong;p.onLong=function(it){if(it.btn&&$(it.btn).is('.view--torrent')){manualUntil=Date.now()+3000;return p.onSelect(it);}return long&&long.apply(this,arguments);};}return show.apply(this,arguments);};
}

})();
