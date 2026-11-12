const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict'),test=require('node:test');
const root=require('path').join(__dirname,'../HomeMedia');
function fixture(files){
 const text=fs.readFileSync(root+'/continue.js','utf8'),begin=text.indexOf('  function reach('),end=text.indexOf('  window.HomeSrc.local',begin);
 let played,failed,requests=[];const hash='a'.repeat(40);
 const ctx={window:{HomeSrc:{}},BASE:'http://fixture',URL,location:{href:'http://fixture/'},$:{extend:Object.assign,ajax(o){requests.push(JSON.parse(o.data));o.success&&o.success({file_stats:files});return {abort(){}};}},Lampa:{Timeline:{view:h=>({hash:h,time:0})},Torserver:{url:()=> 'http://new-torrserver:8090'},Player:{play:d=>played=d,playlist(){}}},setTimeout:()=>1,clearTimeout(){}};
 vm.createContext(ctx);vm.runInContext(text.slice(begin,end),ctx);
 return {run(){ctx.window.HomeSrc.url.resume({src:'url',url:'http://old-torrserver:8090/stream?link='+hash+'&index=7',hash:'111',infohash:hash,pos:123,season:1,ep:1},{id:42,name:'Series'},'tv',()=>{},e=>failed=e);},get played(){return played},get failed(){return failed},requests};
}
test('restored URL uses current TorrServer and original file ID',()=>{const x=fixture([{id:7,path:'Episode1.mkv'}]);x.run();assert.match(x.played.url,/^http:\/\/new-torrserver:8090\/stream\?/);assert.equal(new URL(x.played.url).searchParams.get('index'),'7');assert.equal(JSON.parse(x.played.tl_meta)[0].r.url,x.played.url)});
test('missing file cannot silently switch to index zero',()=>{const x=fixture([{id:0,path:'Other.mkv'}]);x.run();assert.equal(x.played,undefined);assert.ok(x.failed)});
