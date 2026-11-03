const test=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),vm=require('vm');
const src=fs.readFileSync(path.join(__dirname,'../../LAMPA/app/src/mc/assets/shell/shell.js'),'utf8');
function fixture(){
 const requests=[],saved=[],badges=[],timers=[],refreshed=[];
 function Xhr(){this.headers={};requests.push(this)}Xhr.prototype={open(){},setRequestHeader(k,v){this.headers[k]=v},send(){}};
 const c={state:{server:'http://fixture',profile:'main'},userGeneration:1,userContext:{token:'owner'},XMLHttpRequest:Xhr,qT:null,qQ:{m42:{item:{id:42,title:'fixture'},cards:[{querySelector:()=>null}]}},
 load:()=>({}),save:(key,data)=>saved.push({key,data}),setTimeout:(fn,ms)=>timers.push({fn,ms}),refreshInfoQuality:keys=>refreshed.push(keys),picOf:()=>({appendChild:x=>badges.push(x)}),el:(tag,cls,text)=>({text}),cput:(m,k,v)=>m[k]=v,ctrim:m=>m};
 vm.createContext(c);
 const header=src.split('\n').find(l=>l.includes('function ownerHeader('));
 vm.runInContext(header+'\n'+src.slice(src.indexOf('  function ctxOf()'),src.indexOf('  function qcache('))+'\n'+src.slice(src.indexOf('  function flushQuality()'),src.indexOf('  // ключ карточки')),c);
 c.flushQuality();return {c,requests,saved,badges,timers,refreshed};
}
test('quality request carries its profile owner',()=>{const f=fixture();assert.equal(f.requests[0].headers['X-Home-Session'],'owner')});
test('an HTTP error cannot poison the quality cache and keeps its batch',()=>{const f=fixture(),x=f.requests[0];x.status=401;x.onload();assert.equal(f.saved.length,0);assert.ok(f.c.qQ.m42);assert.equal(f.timers[0].ms,8000)});
test('a response after a viewer switch cannot paint or write into the new cache',()=>{const f=fixture(),x=f.requests[0];f.c.userGeneration++;x.status=200;x.responseText='{"m42":"FHD"}';x.onload();assert.equal(f.saved.length,0);assert.equal(f.badges.length,0)});
test('successful quality is painted, cached and refreshes the focused detail',()=>{const f=fixture(),x=f.requests[0];x.status=200;x.responseText='{"m42":"FHD"}';x.onload();assert.equal(f.badges[0].text,'FHD');assert.match(f.saved[0].key,/mc_q_v2/);assert.deepEqual(Array.from(f.refreshed[0]),['m42']);assert.equal(f.timers.length,0)});
test('episode/date/duration changes invalidate row rendering',()=>{
 const c={};vm.createContext(c);vm.runInContext(src.slice(src.indexOf('  function strHash('),src.indexOf('  // Последняя плитка')),c);
 const base={id:42,media_type:'tv',title:'fixture'};
 for(const key of ['history_episode','published','duration'])assert.notEqual(c.sig([base]),c.sig([{...base,[key]:'new'}]));
});
function command(){
 let result;const c={el:()=>({appendChild(){}}),lampaGo:s=>result=JSON.parse(s)};vm.createContext(c);
 vm.runInContext(src.slice(src.indexOf('  function allEl('),src.indexOf('  function fillRow('))+';allEl({id:"new_movie",title:"Fixture",style:"poster"})._act();',c);return result;
}
test('an already loaded section opens without a polling delay',()=>{
 const cmd=command();let called=0,timers=0;const c={window:{__mc_nav_id:1,__kp_section:()=>called++},setTimeout:()=>timers++,clearInterval(){}};
 vm.createContext(c);vm.runInContext(cmd.js+'('+cmd.arg+');',c);assert.equal(called,1);assert.equal(timers,0);
});
test('a cancelled deferred command cannot open a late plugin',()=>{
 const cmd=command();let timer,called=0;const c={window:{__mc_nav_id:1},setTimeout:fn=>timer=fn,clearInterval(){}};
 vm.createContext(c);vm.runInContext(cmd.js+'('+cmd.arg+');',c);c.window.__mc_nav_id++;c.window.__kp_section=()=>called++;timer();assert.equal(called,0);
});
