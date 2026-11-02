const test=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),vm=require('vm');
const root=path.resolve(__dirname,'../..'),tv=fs.readFileSync(path.join(root,'server-mod/HomeMedia/tv.js'),'utf8');
const start=tv.indexOf('(function devsync()'),end=tv.indexOf('})();',start)+5;
function fixture(legacy=false){
 class Storage{constructor(){this.data={}}get length(){return Object.keys(this.data).length}key(i){return Object.keys(this.data)[i]??null}getItem(k){return this.data[k]??null}setItem(k,v){this.data[k]=String(v)}removeItem(k){delete this.data[k]}}
 const store=new Storage(),timers=new Map(),requests=[];let serial=0;
 const c={Storage,URL,XMLHttpRequest:function(){},setTimeout:(fn,ms)=>{timers.set(++serial,{fn,ms});return serial},clearTimeout:i=>timers.delete(i),setInterval:(fn,ms)=>{timers.set(++serial,{fn,ms});return serial},navigator:{userAgent:'fixture',platform:'fixture'},document:{},devId:()=> 'fixture',applyLook(){},api:(url,ok,fail)=>requests.push({url,ok,fail})};
 c.XMLHttpRequest.prototype={open(){},send(){}};c.window=c;c.localStorage=store;c.__home_scope={profiles_enabled:true,ready:true,server_id:'fixture',server_base:'http://fixture',token:'fixture',profile:{id:'child',is_child:false,policy_rev:1}};
 for(const k of ['MAXH_KEY','SAVER_KEY','WALL_KEY','HIDEL_KEY','AUTO_KEY','ENG_KEY','BUF_KEY','FONT_KEY','ALPHA_KEY','AFR_KEY'])c[k]=k;
 vm.createContext(c);let p=fs.readFileSync(path.join(root,'LAMPA/app/src/main/assets/profile-start.js'),'utf8');p=p.slice(0,p.indexOf('// Defer core hooks'))+'})();';vm.runInContext(p,c);if(legacy)vm.runInContext("window.HomeStorage=undefined;localStorage.setItem(MAXH_KEY,'1080')",c);vm.runInContext(tv.slice(start,end),c);
 function tick(ms){const e=[...timers.entries()].find(([,t])=>t.ms===ms);assert.ok(e,'timer '+ms);timers.delete(e[0]);e[1].fn()}
 return {c,requests,timers,tick,run:s=>vm.runInContext(s,c),get:k=>c.localStorage.getItem(k)};
}
test('a profile facade write sends the changed device setting',()=>{const f=fixture();f.run("localStorage.setItem(MAXH_KEY,'720')");f.tick(600);assert.match(f.requests[0].url,/k=maxh&v=720/)});
test('network failure retains the outbox and retries',()=>{const f=fixture();f.run("localStorage.setItem(MAXH_KEY,'720')");f.tick(600);f.requests[0].fail();assert.ok(f.get('home_dev_outbox_v2').includes('720'));f.tick(5000);assert.equal(f.requests[1].url,f.requests[0].url)});
test('an old ACK cannot delete a newer setting',()=>{const f=fixture();f.run("localStorage.setItem(MAXH_KEY,'720')");f.tick(600);f.run("localStorage.setItem(MAXH_KEY,'1080')");f.requests[0].ok({ok:true});assert.ok(f.get('home_dev_outbox_v2').includes('1080'));f.tick(5000);assert.match(f.requests[1].url,/v=1080/)});
test('server settings apply through the facade without an echo write',()=>{const f=fixture();f.tick(2500);f.requests[0].ok({cfg:{maxh:'1080'}});assert.equal(f.get('MAXH_KEY'),'1080');assert.equal([...f.timers.values()].filter(t=>t.ms===600).length,0)});
test('a pending local setting wins over a late hello snapshot',()=>{const f=fixture();f.tick(2500);f.run("localStorage.setItem(MAXH_KEY,'720')");f.requests[0].ok({cfg:{maxh:'1080'}});assert.equal(f.get('MAXH_KEY'),'720')});
test('older APK facade writes are detected by the fallback observer',()=>{const f=fixture(true);f.run("localStorage.setItem(MAXH_KEY,'720')");f.tick(1000);f.tick(600);assert.match(f.requests[0].url,/k=maxh&v=720/)});
test('older APK facade accepts server settings without an echo',()=>{const f=fixture(true);f.tick(2500);f.requests[0].ok({cfg:{maxh:'720'}});assert.equal(f.get('MAXH_KEY'),'720');f.tick(1000);assert.equal([...f.timers.values()].filter(t=>t.ms===600).length,0)});
