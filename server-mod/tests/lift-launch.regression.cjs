const fs=require('fs'),path=require('path'),vm=require('vm'),test=require('node:test'),assert=require('node:assert/strict');
const dir=path.join(__dirname,'../HomeMedia');
function fixture(){
 const owner={token:'fixture',profile:{id:'u_default',is_admin:true,is_child:false}},activity={},requests=[],dialogs=[],launched=[],timers=[],fail=[];
 const window={HomeSrc:{},__home_lift_enabled:true,appready:true,HomeUser:{owner},AndroidJS:{liftVersion:()=>1},location:{origin:'http://fixture'}};
 const Lampa={Activity:{active:()=>activity},Noty:{show:s=>fail.push(s)},Listener:{follow(){}},Controller:{enabled:()=>({name:'full_start'}),toggle(){}},Select:{show:d=>dialogs.push(d)},Android:{openPlayer:(url,data)=>launched.push({url,data})}};
 Lampa.Player={play:d=>Lampa.Android.openPlayer(d.url,d)};
 window.Lampa=Lampa;
 function $(){return {}};$.extend=Object.assign;$.ajax=r=>{r.abort=()=>{};requests.push(r);return r};
 const ctx={window,Lampa,HomeSrc:window.HomeSrc,HomeUser:window.HomeUser,AndroidJS:window.AndroidJS,location:window.location,$,BASE:'http://fixture',setInterval:f=>timers.push(f),clearInterval(){},setTimeout(){},Date,localStorage:{getItem:()=>null},registerHashes(){},getMaxH:()=>0,getAfr:()=>true};
 vm.createContext(ctx);
 vm.runInContext(fs.readFileSync(path.join(dir,'lift-flow.js'),'utf8'),ctx);ctx.LiftFlow=window.LiftFlow;
 vm.runInContext(fs.readFileSync(path.join(dir,'lift.js'),'utf8'),ctx);ctx.HomeLift=window.HomeLift;
 vm.runInContext(fs.readFileSync(path.join(dir,'LiftBeforeNative.js'),'utf8'),ctx);
 window.__home_prepare_progress=(d,ok)=>{d.timeline.time=345;d.timeline.duration=1000;ok()};
 let tv=fs.readFileSync(path.join(dir,'tv.js'),'utf8'),a=tv.indexOf('  (function playerExtras() {'),b=tv.indexOf('\n  })();',a);
 vm.runInContext(tv.slice(a,b)+'\n  })();',ctx);timers.forEach(f=>f());
 function reply(path,data){const r=requests.find(x=>!x.replied&&x.url.endsWith(path));assert.ok(r,'request '+path);r.replied=true;r.success(data);return r}
 const descriptor=url=>({source:'lift',codec:'vp9',protocol:'dash',profile_id:'u_default',identity:'movie-42',card:'42_movie',hash:'111',item:99,edition:'e1',binding_revision:'r1',video:url,voices:[{name:'Voice',representation_id:'0'}],selected_voice:{name:'Voice',representation_id:'0'},representations:[{id:'4',width:1920,height:1080,codecs:'vp09.00.40.08'}]});
 function start(){window.HomeLift.open({id:42,title:'Fixture'},'movie',{voice_name:'Voice'},()=>{},s=>fail.push(s));reply('/home/lift/item?media=movie&tmdb=42',{items:[{key:'movie:42:0:0'}]});reply('/home/lift/options',descriptor());reply('/home/lift/resolve',descriptor('https://media.example/old.mpd'));}
 return {window,requests,dialogs,launched,fail,reply,descriptor,start};
}
test('Lift waits for position decision, then passes refreshed URI to native bridge',()=>{
 const f=fixture();f.start();assert.equal(f.launched.length,0);f.reply('/home/lift/position-context',{compatible:true});assert.equal(f.dialogs.length,1);
 f.dialogs[0].onSelect({decision:'resume'});f.reply('/home/lift/resolve',f.descriptor('https://media.example/fresh.mpd'));
 assert.equal(f.launched.length,1);assert.equal(f.launched[0].url,'https://media.example/fresh.mpd');assert.equal(f.launched[0].data.playlist[0].url,f.launched[0].url);assert.equal(f.launched[0].data.timeline.time,345);
 assert.equal(JSON.parse(f.launched[0].data.tl_meta)[0].r.voice_name,'Voice');
});
test('Lift start from zero affects both packet and selected file',()=>{
 const f=fixture();f.start();f.reply('/home/lift/position-context',{compatible:false});f.dialogs[0].onSelect({decision:'start_zero'});f.reply('/home/lift/resolve',f.descriptor('https://media.example/fresh.mpd'));
 assert.equal(f.launched[0].data.timeline.time,0);assert.equal(f.launched[0].data.playlist[0].timeline.time,0);
});
test('profile switch during resume dialog prevents resolve and native launch',()=>{
 const f=fixture();f.start();f.reply('/home/lift/position-context',{compatible:false});const before=f.requests.length;f.window.HomeUser.owner={profile:{id:'other'},token:'other'};f.dialogs[0].onSelect({decision:'resume'});
 assert.equal(f.requests.length,before);assert.equal(f.launched.length,0);
});
test('cancelled Lift request cannot launch after reply arrives',()=>{
 const f=fixture();f.start();f.window.HomeLift.cancel();f.reply('/home/lift/position-context',{compatible:true});assert.equal(f.dialogs.length,0);assert.equal(f.launched.length,0);
});
test('administrator can search, confirm a binding and proceed to voice options',()=>{
 const f=fixture();f.window.HomeLift.open({id:42,title:'Fixture'},'movie');f.reply('/home/lift/item?media=movie&tmdb=42',{items:[]});
 f.reply('/home/lift/find',{items:[{id:99,name:'Fixture',year:2026}]});f.dialogs.shift().onSelect({candidate:{id:99}});
 f.reply('/home/lift/catalog',{items:[{title:'Fixture',season:0,episode:0}]});f.dialogs.shift().onSelect({season:0,episode:0});
 assert.equal(f.requests.filter(x=>x.url.endsWith('/bind')).length,0);f.dialogs.shift().onSelect({confirmed:true});
 const binding=JSON.parse(f.requests.find(x=>x.url.endsWith('/bind')).data);assert.equal(binding.tmdb,42);assert.equal(binding.item,99);
 f.reply('/home/lift/bind',{ok:true});assert.ok(f.requests.find(x=>x.url.endsWith('/options')));
});
test('series binding explicitly maps provider numbering to TMDB numbering',()=>{
 const f=fixture();f.window.HomeLift.open({id:42,name:'Series'},'tv');f.reply('/home/lift/item?media=tv&tmdb=42',{items:[]});
 f.reply('/home/lift/find',{items:[{id:99,name:'Series',year:2026}]});f.dialogs.shift().onSelect({candidate:{id:99}});
 f.reply('/home/lift/catalog',{items:[{title:'Provider special',season:0,episode:2}]});f.dialogs.shift().onSelect({season:0,episode:2});
 f.reply('/home/lift/episodes',{seasons:[{title:'Season 1',season:1,count:12}]});f.dialogs.shift().onSelect({season:1,count:12});f.dialogs.shift().onSelect({episode:5});f.dialogs.shift().onSelect({confirmed:true});
 const binding=JSON.parse(f.requests.find(x=>x.url.endsWith('/bind')).data);
 assert.equal(binding.season,1);assert.equal(binding.episode,5);assert.equal(binding.provider_season,0);assert.equal(binding.provider_episode,2);
});
