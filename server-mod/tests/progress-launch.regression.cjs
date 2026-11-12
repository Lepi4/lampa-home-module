const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict'),test=require('node:test');
const whole=fs.readFileSync(require('path').join(__dirname,'../HomeMedia/tv.js'),'utf8');
const begin=whole.indexOf('  // Read the selected episode immediately before');
const source=whole.slice(begin,whole.indexOf('  (function playerExtras()',begin));
function fixture(){
 let current={},pending=false,webPending=false,ok=0,fail=[],requests=[],timers=[];
 const user={owner:{server_base:'http://fixture',token:'fixture',profile:{id:'u_default'}}};
 const AndroidJS={tlPendingOwned(){return JSON.stringify({pending});}};
 const Lampa={Activity:{active:()=>current}};
 const window={HomeUser:user,AndroidJS,__home_tl_barrier:()=>({ready:!webPending})};
 const ctx={window,HomeUser:user,AndroidJS,Lampa,BASE:'http://fixture',$:{ajax:r=>requests.push(r)},setTimeout:f=>timers.push(f)};
 vm.createContext(ctx);vm.runInContext(source,ctx);
 const data=()=>({url:'http://fixture/stream',timeline:{hash:'111',time:0},tl_meta:JSON.stringify([{id:'tv-42-s1e1',h:'111',c:'42_tv'}])});
 return {ctx,window,user,requests,timers,data,run:d=>window.__home_prepare_progress(d,()=>ok++,e=>fail.push(e)),reply:r=>requests.at(-1).success({profile_id:'u_default',now:10000,row:{id:'tv-42-s1e1',t:123,d:1000,p:12.3,u:9000,...r}}),tick:()=>timers.shift()(),native:v=>pending=v,web:v=>webPending=v,navigate:()=>current={},get ok(){return ok},fail};
}
test('cold device receives server position before native launch',()=>{const x=fixture(),d=x.data();x.run(d);assert.equal(x.ok,0);x.reply();assert.equal(d.timeline.time,123);assert.equal(d.home_clock_ms,10000);assert.equal(x.ok,1)});
test('authoritative reset clears old local position',()=>{const x=fixture(),d=x.data();d.timeline.time=555;x.run(d);x.reply({t:0,p:0,op:'reset'});assert.equal(d.timeline.time,0)});
test('waits for browser pending before resolve',()=>{const x=fixture();x.web(true);x.run(x.data());assert.equal(x.requests.length,0);x.web(false);x.tick();assert.equal(x.requests.length,1)});
test('waits for native pending before resolve',()=>{const x=fixture();x.native(true);x.run(x.data());assert.equal(x.requests.length,0);x.native(false);x.tick();assert.equal(x.requests.length,1)});
test('profile switch cancels an in-flight reply',()=>{const x=fixture();x.run(x.data());x.user.owner.profile.id='child';x.reply();assert.equal(x.ok,0)});
test('navigation cancels an in-flight reply',()=>{const x=fixture();x.run(x.data());x.navigate();x.reply();assert.equal(x.ok,0)});
test('new live playback cancels an earlier VOD reply',()=>{const x=fixture();x.run(x.data());x.run({live:true});assert.equal(x.ok,1);x.reply();assert.equal(x.ok,1)});
test('mismatched episode is not used',()=>{const x=fixture();x.run(x.data());x.reply({id:'tv-42-s1e2'});assert.equal(x.ok,0);assert.equal(x.fail.length,1)});
test('missing canonical row does not erase a legacy local mark',()=>{const x=fixture(),d=x.data();d.timeline.time=55;x.run(d);x.requests[0].success({profile_id:'u_default',now:10000,row:null});assert.equal(d.timeline.time,55)});
test('selected second file retains its identity',()=>{const x=fixture(),d=x.data();d.timeline.hash='222';d.tl_meta=JSON.stringify([{id:'tv-42-s1e1',h:'111',c:'42_tv'},{id:'tv-42-s1e2',h:'222',c:'42_tv'}]);d.playlist=[{timeline:{hash:'111',time:3}},{timeline:{hash:'222',time:0}}];x.run(d);assert.equal(JSON.parse(x.requests[0].data).id,'tv-42-s1e2');x.reply({id:'tv-42-s1e2',t:234});assert.equal(d.playlist[0].timeline.time,3);assert.equal(d.playlist[1].timeline.time,234)});
test('waits for KinoPub fallback queue',()=>{const x=fixture();let pending=true,flush=0;x.window.__homeTlQ={pending:()=>pending,flush:()=>flush++};x.run(x.data());assert.equal(x.requests.length,0);assert.equal(flush,1);pending=false;x.tick();assert.equal(x.requests.length,1)});
