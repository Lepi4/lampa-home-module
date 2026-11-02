const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const source=fs.readFileSync(path.join(process.env.HOME_MEDIA_SOURCE||path.join(__dirname,'../HomeMedia'),'youtube.js'),'utf8');
const start=source.indexOf('  function hookTrailer()'),end=source.indexOf('\n  // ',start),hook=source.slice(start,end);
function fixture(controller='full_start',activeId=42){
  let timer,callback,inserted=false,registered=0;
  const node={},btn={0:node,on(){return this;}},cont={length:1,first(){return this;},append(){inserted=true;}};
  const render={0:{},find:s=>s==='.full-start-new__buttons'?cont:s==='.view--yttrailer'?{length:inserted?1:0}:{remove(){}}};
  const c={ICON:'<svg/>',$:()=>btn,document:{documentElement:{contains:()=>true}},setInterval:f=>timer=f,clearInterval(){},noty(){},api(){},play(){},
    Lampa:{Listener:{follow:(name,cb)=>callback=cb},Activity:{active:()=>({id:activeId})},Controller:{enabled:()=>({name:controller}),collectionAppend:b=>{assert.equal(b,btn);registered++;}}}};
  vm.createContext(c);vm.runInContext(hook+'\nhookTrailer();',c);
  callback({type:'complite',data:{movie:{id:42,title:'Fixture'}},object:{activity:{render:()=>render}}});timer();
  return {inserted,registered};
}
test('late trailer becomes reachable in the active card navigation',()=>assert.deepEqual(fixture(),{inserted:true,registered:1}));
test('late trailer does not steal navigation from an open popup',()=>assert.deepEqual(fixture('select'),{inserted:true,registered:0}));
test('late trailer for a preceding card cannot enter current navigation',()=>assert.deepEqual(fixture('full_start',99),{inserted:true,registered:0}));

test('YouTube playback keeps its media tracks without inheriting the movie card',()=>{
  const a=source.indexOf('  var pendingPlay ='),b=source.indexOf('\n  // ',a);
  assert.ok(a>=0&&b>a,'YouTube playback function must be present');
  let playback;
  const c={window:{},document:{body:{contains:()=>true}},BASE:'http://fixture',noty(){},maxH:()=>1080,ctrl:()=> 'full_start',setTimeout(){},clearTimeout(){},setInterval(){},clearInterval(){},$:x=>x,
    api:(url,ok)=>ok({video:'http://fixture/video',audio:'http://fixture/audio',quals:[{h:720},{h:1080}],sponsor:[{start:1,end:2}]}),
    Lampa:{Modal:{open(){},close(){}},Select:{close(){}},Controller:{toggle(){},enabled:()=>({name:'modal'})},Player:{play:d=>playback=d}}};
  vm.createContext(c);vm.runInContext(source.slice(a,b)+'\nplay("fixture","Trailer");',c);
  assert.equal(playback.home_timeline_only,true);assert.equal(playback.home_player,true);
  assert.equal(playback.yt_audio,'http://fixture/audio');assert.equal(JSON.parse(playback.yt_quals).length,2);
  assert.equal(playback.card,undefined);assert.equal(playback.timeline,undefined);
});
