const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict'),test=require('node:test');const c={};vm.createContext(c);vm.runInContext(fs.readFileSync(require('path').join(__dirname,'../HomeMedia/lift-flow.js'),'utf8'),c);const F=c.LiftFlow;
const ctx={profileId:'a',identity:'tv-42-s1e2',cardKey:'42_tv',card:{id:42},base:'http://fixture',season:1,episode:2};
function resolved(){return {source:'lift',codec:'vp9',protocol:'dash',profile_id:'a',identity:ctx.identity,card:ctx.cardKey,hash:'111',item:99,edition:'e1',binding_revision:'r1',video:'https://media.example/a.mpd',voices:[{name:'Voice',representation_id:'3'}],representations:[]};}
test('packet has canonical identity and stable hs',()=>{const r=resolved(),p=F.packet(ctx,r,r.voices[0]);assert.equal(p.hs.src,'lift');assert.equal(JSON.parse(p.tl_meta)[0].id,ctx.identity);assert.doesNotThrow(()=>JSON.stringify(p))});
test('persistent resume does not contain signed stream URL',()=>{const r=resolved(),p=F.packet(ctx,r,r.voices[0]);const a=F.persistentResume(p.hs,'Title');assert.equal(a.voice_name,'Voice');assert.equal(a.binding_revision,'r1');assert.equal(a.url,undefined)});
test('zero is applied to packet and selected playlist item after gate',()=>{const r=resolved(),p=F.packet(ctx,r,r.voices[0]);p.timeline.time=345;p.playlist[0].timeline={hash:'111',time:345,percent:30};F.position(p,'start_zero');assert.equal(p.timeline.time,0);assert.equal(p.playlist[0].timeline.time,0)});
test('unknown decision refuses launch',()=>{assert.throws(()=>F.position({},'unknown'))});
test('wrong profile descriptor refused',()=>{const r=resolved();r.profile_id='b';assert.throws(()=>F.packet(ctx,r,r.voices[0]))});
test('unknown voice refused',()=>{assert.throws(()=>F.packet(ctx,resolved(),{name:'Voice',representation_id:'4'}))});
test('navigation invalidates attempt',()=>{let a={};const m={},own=()=>({id:'a'}),current=()=>a;const x=F.attempt(m,own,current);a={};assert.equal(x.valid(),false)});
test('profile switch invalidates attempt',()=>{let owner={id:'a'};const act={};const x=F.attempt({},()=>owner,()=>act);owner={id:'b'};assert.equal(x.valid(),false)});
test('new attempt invalidates old result',()=>{const m={},a={};const x=F.attempt(m,()=>({id:'a'}),()=>a);F.attempt(m,()=>({id:'a'}),()=>a);assert.equal(x.valid(),false)});
test('old cancel does not abort a newer attempt',()=>{const m={},a={};const x=F.attempt(m,()=>({id:'a'}),()=>a);const y=F.attempt(m,()=>({id:'a'}),()=>a);let aborted=0;m.requests=[{abort(){aborted++}}];x.cancel();assert.equal(y.valid(),true);assert.equal(aborted,0)});
