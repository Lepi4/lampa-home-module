const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('node:assert/strict'),test=require('node:test');
const root=path.join(__dirname,'../..'),media=path.join(root,'server-mod/HomeMedia');
function fixture(bootstrap){
 const requests=[],errors=[],activity={},owner={token:'fixture-token',server_base:'http://fixture',profile:{id:'a',is_admin:true,is_child:false}};let launched=0;
 // XMLHttpRequest combines repeated header values, as required by the XHR standard.
 class XHR {open(method,url){this.method=method;this.url=url;this.headers={}}setRequestHeader(k,v){k=k.toLowerCase();this.headers[k]=this.headers[k]?this.headers[k]+', '+v:String(v)}send(body){this.body=body}abort(){}}
 function $(){};$.extend=Object.assign;$.ajax=o=>{const x=new XHR();x.open(o.type||'GET',o.url);Object.entries(o.headers||{}).forEach(([k,v])=>x.setRequestHeader(k,v));x.send(o.data);requests.push({x,o});return x};
 const Lampa={Activity:{active:()=>activity},Listener:{follow(){}},Controller:{enabled:()=>({name:'full_start'})},Noty:{show:s=>errors.push(s)},Select:{show(){throw Error('unexpected dialog')}}};
 const c={XMLHttpRequest:XHR,URL,owner,profile:owner.profile,origin:'http://fixture',location:{href:'http://fixture/lampa',origin:'http://fixture'},HomeUser:{owner},HomeSrc:{},__home_lift_enabled:true,appready:true,AndroidJS:{liftVersion:()=>1},Lampa,$};c.window=c;
 vm.createContext(c);
 const injection=fs.readFileSync(bootstrap,'utf8'),begin=injection.indexOf('var open=XMLHttpRequest.prototype.open'),end=injection.indexOf('if(window.fetch)',begin);assert.ok(begin>=0&&end>begin);
 vm.runInContext(injection.slice(begin,end),c);
 vm.runInContext(fs.readFileSync(path.join(media,'lift-flow.js'),'utf8'),c);
 vm.runInContext(fs.readFileSync(path.join(media,'lift.js'),'utf8'),c);
 vm.runInContext(fs.readFileSync(path.join(media,'LiftBeforeNative.js'),'utf8'),c);
 c.__home_prepare_progress=(data,ok)=>ok();
 Lampa.Player={play:data=>c.__home_lift_before_native(data,()=>launched++,s=>errors.push(s))};
 const descriptor={source:'lift',codec:'vp9',protocol:'dash',profile_id:'a',identity:'movie-42',card:'42_movie',hash:'111',item:99,edition:'e1',binding_revision:'r1',video:'https://media.example/video.mpd',voices:[{name:'Voice',representation_id:'0'}],selected_voice:{name:'Voice',representation_id:'0'},representations:[]};
 function reply(suffix,result){const req=requests.find(r=>!r.done&&r.x.url.endsWith(suffix));assert.ok(req,suffix);req.done=true;req.o.success(result)}
 function start(){c.HomeLift.open({id:42,title:'Fixture'},'movie',{voice_name:'Voice'});}
 return {requests,owner,start,reply,descriptor,errors,get launched(){return launched}};
}
for(const [name,file] of [['APK','LAMPA/app/src/main/assets/profile-start.js'],['web','server-mod/HomeMedia/profiles-web.js']]){
 test(name+': first Lift request has exactly one owner session header',()=>{
  const f=fixture(path.join(root,file));f.start();assert.equal(f.requests[0].x.headers['x-home-session'],f.owner.token);
 });
 test(name+': every step through fresh resolve uses one session header',()=>{
  const f=fixture(path.join(root,file));f.start();f.reply('/home/lift/item?media=movie&tmdb=42',{items:[{key:'movie:42:0:0'}]});f.reply('/home/lift/options',f.descriptor);f.reply('/home/lift/resolve',f.descriptor);f.reply('/home/lift/position-context',{compatible:true});f.reply('/home/lift/resolve',f.descriptor);
  assert.equal(f.requests.length,5);f.requests.forEach(r=>assert.equal(r.x.headers['x-home-session'],f.owner.token));assert.equal(f.launched,1);assert.equal(f.errors.length,0);
 });
}
