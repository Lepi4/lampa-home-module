const fs=require('fs'),vm=require('vm'),assert=require('node:assert/strict'),test=require('node:test');
const source=fs.readFileSync(require('path').join(__dirname,'../HomeMedia/vk.js'),'utf8');
function fixture(){
 const requests=[],plays=[],messages=[];let active={};
 const HomeUser={owner:{token:'fixture',profile:{id:'adult-a',settings:{}}}};
 const Lampa={Listener:{follow(){}},Activity:{active:()=>active},Controller:{enabled:()=>({name:'content'}),toggle(){}},Utils:{hash:()=>111},Player:{play:d=>plays.push(d)},Noty:{show:m=>messages.push(m)}};
 const $={ajax:r=>{requests.push(r);return {abort(){}}}};
 const window={HomeUser,Lampa};const document={createElement:()=>({}),head:{appendChild(){}}};
 const ctx={window,HomeUser,Lampa,$,document,location:{origin:'http://fixture'},localStorage:{getItem:()=>null},setTimeout(){}};
 vm.createContext(ctx);vm.runInContext(source,ctx);
 function replyVideo(i,id){requests[i].success({id,video:'https://media.example/video.mp4',duration:1000});}
 return {requests,plays,HomeUser,play:window.__vk.play,replyVideo,leave(){active={};}};
}
test('newer B cancels the late result A',()=>{const x=fixture();x.play('A');x.play('B');x.replyVideo(1,'B');x.requests[2].success({position:50});x.replyVideo(0,'A');assert.equal(x.plays.length,1);assert.equal(x.plays[0].home_vk_id,'B')});
test('leaving activity must cancel the pending video',()=>{const x=fixture();x.play('A');x.leave();x.replyVideo(0,'A');assert.equal(x.requests.length,1);assert.equal(x.plays.length,0,'late callback still called Lampa.Player.play after navigation')});
test('changing owner must cancel the pending progress reply',()=>{const x=fixture();x.play('A');x.replyVideo(0,'A');x.HomeUser.owner={token:'new-fixture',profile:{id:'adult-b',settings:{}}};x.requests[1].success({position:50});assert.equal(x.plays.length,0,'late callback still called Lampa.Player.play under another owner')});
