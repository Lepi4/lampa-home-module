const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=process.env.HOME_MEDIA_SOURCE||path.join(__dirname,'../HomeMedia');
function fixture(active,data,apKey){
  let timers=[];const window={appready:false,__apKey:apKey,__apKeyAt:Date.now()};
  const Lampa={Listener:{follow(){}},Activity:{active:()=>active},Timeline:{view:h=>({hash:h})},Android:{openPlayer:(u,d)=>{d.card=d.card||d.movie||active.card||active.movie;return d;}}};window.Lampa=Lampa;
  const $=()=>({});$.extend=Object.assign;
  const c={window,Lampa,$,BASE:'http://fixture',location:{origin:'http://fixture'},Date,document:{},localStorage:{getItem:()=>null,setItem(){}},setInterval:f=>{timers.push(f);return 1;},clearInterval(){},setTimeout:()=>1,clearTimeout(){},getMaxH:()=>0,getAfr:()=>false,registerHashes(){}};
  vm.createContext(c);
  let cont=fs.readFileSync(path.join(root,'continue.js'),'utf8'),end=cont.lastIndexOf('})();');
  vm.runInContext(cont.slice(0,end)+'window.register=register;'+cont.slice(end),c);
  const tv=fs.readFileSync(path.join(root,'tv.js'),'utf8'),begin=tv.indexOf('  (function playerExtras() {'),finish=tv.indexOf('\n  })();',begin);
  vm.runInContext(tv.slice(begin,finish)+'\n  })();',c);timers.forEach(f=>f());window.register(data);
  return Lampa.Android.openPlayer(data.url,data);
}
const video=()=>({url:'http://fixture/stream',title:'Fixture',timeline:{hash:'12345'}});
test('global AFR disabled reaches movies and live streams explicitly',()=>{for(const live of [false,true])assert.equal(fixture({card:{id:42,title:'Fixture'}},{...video(),live}).tv_afr,0)});
test('TorrServer movie context creates native metadata and resume',()=>{
  const r=fixture({component:'torrserver',movie:{id:42,title:'Fixture'}},video());const m=JSON.parse(r.tl_meta)[0];
  assert.equal(m.c,'42_movie');assert.equal(m.id,'movie-42');assert.equal(m.r.src,'url');assert.equal(m.r.url,r.url);
});
test('TorrServer Continue passes native timeline metadata and bounded resume playlist',()=>{
  const text=fs.readFileSync(path.join(root,'continue.js'),'utf8'),begin=text.indexOf('  function reach('),end=text.indexOf('  window.HomeSrc.local',begin);
  let data;
  const ctx={window:{HomeSrc:{}},BASE:'http://fixture',$:{extend:Object.assign,ajax(o){o.success&&o.success({});}},Lampa:{Timeline:{view:h=>({hash:h})},Player:{play:d=>data=d,playlist(){}}},fetch:()=>({then(fn){fn({ok:true});return {then(){}};}}),setTimeout(){return 1},clearTimeout(){}};
  vm.createContext(ctx);vm.runInContext(text.slice(begin,end),ctx);
  ctx.window.HomeSrc.url.resume({src:'url',url:'http://fixture/stream',hash:'h1',season:1,ep:2,pl:[{u:'http://fixture/e1',t:'S1 E1',h:'h0'},{u:'http://fixture/e2',t:'S1 E2',h:'h1'}],idx:1}, {id:42,name:'Series'}, 'tv', ()=>{}, ()=>{});
  const meta=JSON.parse(data.tl_meta),list=JSON.parse(data.resume_playlist);
  assert.equal(meta[1].id,'tv-42-s1e2');assert.equal(meta[1].c,'42_tv');assert.equal(meta[1].r.pi,1);assert.equal(meta[1].r.season,1);assert.equal(meta[1].r.ep,2);
  assert.deepEqual(list,[{u:'http://fixture/e1',t:'S1 E1',h:'h0'},{u:'http://fixture/e2',t:'S1 E2',h:'h1',s:1,e:2}]);
});

function resumeFixture(rec){
  const text=fs.readFileSync(path.join(root,'continue.js'),'utf8'),begin=text.indexOf('  function reach('),end=text.indexOf('  window.HomeSrc.local',begin);let data;
  const ctx={window:{HomeSrc:{}},BASE:'http://fixture',URL,location:{href:'http://fixture/'},$:{extend:Object.assign},Lampa:{Timeline:{view:h=>({hash:h})},Player:{play:d=>data=d,playlist(){}}},fetch:()=>Promise.resolve({ok:true}),setTimeout(){return 1},clearTimeout(){}};
  vm.createContext(ctx);vm.runInContext(text.slice(begin,end),ctx);
  return new Promise((resolve,reject)=>ctx.window.HomeSrc.url.resume(rec,{id:42,name:'Series'},'tv',()=>resolve(data),reject));
}
test('Continue preserves every file episode and native playlist index, including a season boundary',async()=>{
  const data=await resumeFixture({src:'url',hash:'h1',season:1,ep:12,pl:[{u:'http://fixture/e12',t:'Episode 12',h:'h1',s:1,e:12},{u:'http://fixture/e21',t:'Episode 1',h:'h2',s:2,e:1}],idx:0});
  const meta=JSON.parse(data.tl_meta);
  assert.deepEqual(meta.map(m=>m.id),['tv-42-s1e12','tv-42-s2e1']);
  assert.deepEqual(meta.map(m=>m.r.pi),[0,1]);
  assert.deepEqual(JSON.parse(data.resume_playlist).map(p=>[p.s,p.e]),[[1,12],[2,1]]);
});
test('Continue after completed file uses next file identity instead of the completed episode',async()=>{
  const data=await resumeFixture({src:'url',done:true,hash:'h1',season:1,ep:12,pl:[{u:'http://fixture/e12',t:'Episode 12',h:'h1',s:1,e:12},{u:'http://fixture/e21',t:'Episode 1',h:'h2',s:2,e:1}],idx:0});
  assert.equal(data.url,'http://fixture/e21');const next=JSON.parse(data.tl_meta)[1];
  assert.equal(next.id,'tv-42-s2e1');assert.equal(next.r.season,2);assert.equal(next.r.ep,1);
});
test('initial torrent launch retains per-file episode metadata in native resume window',()=>{
  const items=[{...video(),season:1,episode:12},{...video(),url:'http://fixture/next',timeline:{hash:'67890'},season:2,episode:1}];
  const r=fixture({movie:{id:42,name:'Series'}},{...items[0],playlist:items});
  assert.deepEqual(JSON.parse(r.resume_playlist).map(p=>[p.s,p.e]),[[1,12],[2,1]]);
});

test('native completion UI echo cannot overwrite durable position with zero duration',()=>{
  const text=fs.readFileSync(path.join(root,'continue.js'),'utf8'),start=text.indexOf('  function note(tl)'),end=text.indexOf('  function hookRecorder()',start),posts=[];
  const ctx={window:{},MAP:{'123':{card:'42_tv',title:'Episode',hs:{src:'url',url:'http://fixture/e1'}}},SENT:{},Date,isDone:tl=>tl.percent>=90,post:(card,rec)=>posts.push(rec)};
  vm.createContext(ctx);vm.runInContext(text.slice(start,end),ctx);
  ctx.note({hash:'123',home_native:true,received:true,updated:Date.now(),time:0,duration:0,percent:100});
  assert.equal(posts.length,0,'The native outbox already committed the real position and duration');
  ctx.note({hash:'123',time:60,duration:90,percent:66});
  assert.equal(posts.length,1);assert.equal(posts[0].pos,60);assert.equal(posts[0].dur,90);
});
test('explicit playback card wins over old activity and temporary key',()=>{
  const r=fixture({card:{id:11,title:'Previous'}},{...video(),card:{id:42,title:'Current'}},'99_tv');
  assert.equal(JSON.parse(r.tl_meta)[0].c,'42_movie');
});
test('data.movie can supply a card before the core resolves it',()=>{
  const r=fixture({component:'torrserver'},{...video(),movie:{id:42,title:'Fixture'}});
  assert.equal(JSON.parse(r.tl_meta)[0].id,'movie-42');
});
test('episode identity survives movie-context resolution',()=>{
  const r=fixture({movie:{id:42,name:'Series'}},{...video(),season:2,episode:3});
  assert.equal(JSON.parse(r.tl_meta)[0].id,'tv-42-s2e3');
});
test('unknown context keeps file progress without guessing a movie',()=>{
  const r=fixture({component:'torrserver'},video());const m=JSON.parse(r.tl_meta)[0];assert.equal(m.c,'');assert.equal(m.id,'tl-12345');
});
test('wrestling progress cannot inherit an unrelated open movie',()=>{
  const r=fixture({card:{id:42,title:'Previous'}},{...video(),home_timeline_only:true},'99_tv');const m=JSON.parse(r.tl_meta)[0];
  assert.equal(m.c,'');assert.equal(m.id,'tl-12345');assert.equal(m.r,undefined);
});

test('anime opened above an unrelated card retains its own episode identity',()=>{
  const text=fs.readFileSync(path.join(root,'anime.js'),'utf8'),begin=text.indexOf('  function play(d, s, idx)'),end=text.indexOf('  // ---------- меню:',begin);let data;
  const active={card:{id:11,title:'Unrelated'}};
  const ctx={window:{},BASE:'http://fixture',$:{extend:Object.assign},localStorage:{getItem:()=>null},api(){},epLabel:()=> 'Episode',Lampa:{Activity:{active:()=>active},Timeline:{view:h=>({hash:h})},Player:{play:d=>data=d,playlist(){}}}};
  vm.createContext(ctx);vm.runInContext(text.slice(begin,end),ctx);
  ctx.play({tmdb:42,media:'tv',card:'42_tv',orig:'Anime title'},{season:2,source:'AL',release:7,voice:'Voice',episodes:[{hash:'9876',url:'/anime/play?ep=1',n:3}]},0);
  const r=fixture(active,data);const meta=JSON.parse(r.tl_meta)[0];assert.equal(meta.id,'tv-42-s2e3');assert.equal(meta.c,'42_tv');assert.equal(r.audio_key,'42_tv');assert.equal(r.card.id,42);
});
