const fs=require('node:fs'),vm=require('node:vm'),test=require('node:test'),assert=require('node:assert/strict');
const src=fs.readFileSync(__dirname+'/../../LAMPA/app/src/mc/assets/shell/shell.js','utf8');
function fixture(){
 const box={innerHTML:'',classList:{add(){},remove(){}}},cache={},timers=new Map();let serial=0;
 const c={state:{dev:{}},$:()=>box,document:{body:{classList:{toggle(){}}}},clearTimeout:id=>timers.delete(id),setTimeout:fn=>{timers.set(++serial,fn);return serial},load:()=>cache,scopedKey:x=>x,cget:(m,k)=>m[k],esc:s=>String(s).replace(/</g,'&lt;'),GENRES:{},COUNTRY:{}};
 vm.createContext(c);
 vm.runInContext(src.slice(src.indexOf('  function infoOn()'),src.indexOf('  function esc('))+'\n'+src.slice(src.indexOf('  function qualityKey('),src.indexOf('  function needKp(')),c);
 function tick(){const all=[...timers.values()];timers.clear();all.forEach(fn=>fn())}
 return {c,box,cache,tick};
}
test('right detail reads the same cached quality as its poster',()=>{const f=fixture();f.cache.m42='WEBDL 4K';f.c.showInfo({id:42,title:'Fixture'});f.tick();assert.match(f.box.innerHTML,/WEBDL 4K/)});
test('late quality result repaints the still focused card',()=>{const f=fixture();f.c.showInfo({id:42,title:'Fixture'});f.tick();assert.doesNotMatch(f.box.innerHTML,/FHD/);f.cache.m42='FHD';f.c.refreshInfoQuality(['m42']);f.tick();assert.match(f.box.innerHTML,/FHD/)});
test('late result cannot replace the new focused item',()=>{const f=fixture();f.c.showInfo({id:43,title:'New focus'});f.tick();f.cache.m42='FHD';f.c.refreshInfoQuality(['m42']);f.tick();assert.match(f.box.innerHTML,/New focus/);assert.doesNotMatch(f.box.innerHTML,/FHD/)});
test('movie and series with the same ID do not share quality',()=>{const f=fixture();f.cache.m42='HD';f.cache.t42='4K';f.c.showInfo({id:42,first_air_date:'2026-01-01',media_type:'tv',name:'Series'});f.tick();assert.match(f.box.innerHTML,/4K/);assert.doesNotMatch(f.box.innerHTML,/ · HD/)});
test('explicit quality wins over cache and hidden info stays hidden',()=>{const f=fixture();f.cache.m42='HD';f.c.showInfo({id:42,title:'Fixture',cp_quality:'FHD'});f.tick();assert.match(f.box.innerHTML,/FHD/);f.c.showInfo(null);f.cache.m42='4K';f.c.refreshInfoQuality(['m42']);f.tick();assert.doesNotMatch(f.box.innerHTML,/4K/)});
