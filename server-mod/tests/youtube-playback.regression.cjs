const test=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),vm=require('vm'),path=require('path');
const src=fs.readFileSync(path.join(__dirname,'../HomeMedia/youtube.js'),'utf8');
const code=src.slice(src.indexOf('  var pendingPlay ='),src.indexOf('  function boundPack()'));
function fixture(){
 const calls=[],played=[],notices=[],timers=new Map(),intervals=new Map();let modal,seq=0;
 const c={window:{__mc_nav_id:1},document:{body:{contains:()=>true}},BASE:'http://local',ctrl:()=> 'content',maxH:()=>1080,$:x=>x,
 noty:x=>notices.push(x),api:(url,ok,fail)=>{const r={url,ok,fail,aborted:false};calls.push(r);return{abort(){r.aborted=true;}};},
 setTimeout:(f,ms)=>{timers.set(++seq,{f,ms});return seq;},clearTimeout:i=>timers.delete(i),setInterval:f=>{intervals.set(++seq,f);return seq;},clearInterval:i=>intervals.delete(i),
 Lampa:{Modal:{open:x=>modal=x,close(){}},Controller:{toggle(){},enabled:()=>({name:'modal'})},Select:{close(){}},Player:{play:d=>played.push(d)}}};
 vm.createContext(c);vm.runInContext(code,c);
 return {c,calls,played,notices,timers,intervals,back:()=>modal.onBack(),play:(id='abcdefghijk')=>c.play(id,'fixture',false),answer:(i=0)=>calls[i].ok({id:'abcdefghijk',video:'http://local/yt/p',title:'fixture'}),tick:ms=>{const [id,t]=[...timers].find(([,t])=>t.ms===ms);timers.delete(id);t.f();}};
}
test('Back aborts extraction and ignores a late response',()=>{const f=fixture();f.play();f.back();assert.ok(f.calls[0].aborted);f.answer();assert.equal(f.played.length,0);assert.equal(f.intervals.size,0);});
test('a new play cancels the previous extraction',()=>{const f=fixture();f.play();f.play();f.answer();assert.equal(f.played.length,0);assert.ok(f.calls[0].aborted);f.answer(1);assert.equal(f.played.length,1);});
test('navigation cancels preparation and prevents late player opening',()=>{const f=fixture();f.play();f.c.window.__mc_nav_id++;[...f.intervals.values()][0]();assert.ok(f.calls[0].aborted);f.answer();assert.equal(f.played.length,0);});
test('verification errors explain failure without automatic retry',()=>{const f=fixture();f.play();f.calls[0].fail({responseJSON:{error:'verification_required',message:'Проверка подключения'}});assert.equal(f.timers.size,0);assert.match(f.notices[0],/Проверка подключения/);});
test('temporary failure retries once and remains cancellable',()=>{const f=fixture();f.play();const e={responseJSON:{error:'network_error',message:'Нет связи'}};f.calls[0].fail(e);f.tick(15000);assert.equal(f.calls.length,2);f.calls[1].fail(e);assert.equal(f.timers.size,0);assert.equal(f.notices.length,1);});
test('Back during retry delay prevents another request',()=>{const f=fixture();f.play();f.calls[0].fail({responseJSON:{error:'busy'}});f.back();assert.equal(f.timers.size,0);assert.equal(f.calls.length,1);});
test('non-history playback still receives its server base for renewal',()=>{const f=fixture();f.play();f.answer();assert.equal(f.played[0].home_base,'http://local');assert.equal(f.played[0].home_youtube_id,'abcdefghijk');assert.equal(f.played[0].tl_meta,undefined);});
test('single progressive quality retains its height for renewal',()=>{const f=fixture();f.play();f.calls[0].ok({id:'abcdefghijk',video:'http://local/yt/p',height:360,vcodec:'progressive'});assert.equal(JSON.parse(f.played[0].yt_quals)[0].h,360);});
