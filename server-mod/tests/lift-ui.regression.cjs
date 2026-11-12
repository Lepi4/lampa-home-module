const fs=require('fs'),path=require('path'),vm=require('vm'),assert=require('node:assert/strict'),test=require('node:test');
const source=fs.readFileSync(path.join(__dirname,'../HomeMedia/lift.js'),'utf8');
function fixture({legacy=true,late=false,enabled=true,card={id:42,title:'Fixture'}}={}){
 const header={classes:['full-start__buttons'],children:[],visible:true},sources={classes:['buttons--container'],children:[],visible:false};
 const torrent={classes:['view--torrent'],title:'Торренты',parent:sources},kp={classes:['view--kinopub'],title:'KinoPub',parent:sources};sources.children.push(torrent,kp);
 const roots=legacy?[header,sources]:[sources],events={},requests=[],notices=[];let focusAppend=0;
 function all(){return roots.flatMap(x=>[x,...x.children])}
 function Q(nodes){return {length:nodes.length,first:()=>Q(nodes.slice(0,1)),after(b){const anchor=nodes[0];assert.ok(anchor);const n=b.nodes[0];n.parent=anchor.parent;anchor.parent.children.splice(anchor.parent.children.indexOf(anchor)+1,0,n);return this},append(b){const p=nodes[0];assert.ok(p);b.nodes.forEach(n=>{n.parent=p;p.children.push(n)});return this},is(s){return s===':visible'&&!!nodes[0]?.parent?.visible},on(name,fn){nodes.forEach(n=>{n[name]=fn});return this},nodes};}
 const render={find(selector){const classes=selector.split(',').map(x=>x.trim().slice(1));return Q(all().filter(n=>classes.some(c=>n.classes.includes(c))))}};
 const activity={component:'full',card,method:'movie',activity:{render:()=>render}};
 function $(html){return Q([{classes:['full-start__button','selector','view--lift'],title:'Lift',hasIcon:/<svg\b[^>]*>[\s\S]*<\/svg>/.test(html)}])};$.ajax=r=>{requests.push(r);return {abort(){}}};
 const Lampa={Listener:{follow:(name,fn)=>events[name]=fn},Activity:{active:()=>late?activity:{}},Controller:{enabled:()=>({name:'full_start'}),collectionAppend(){focusAppend++}},Noty:{show:s=>notices.push(s)}};
 const owner={token:'fixture',profile:{id:'u_default',is_admin:true,is_child:false}};
 const ctx={window:null,Lampa,$,HomeUser:{owner},HomeSrc:{},AndroidJS:{liftVersion:()=>1},__home_lift_enabled:enabled,appready:true,location:{origin:'http://fixture'},LiftFlow:{attempt(){return {valid:()=>true,cancel(){}}}}};ctx.window=ctx;
 vm.createContext(ctx);vm.runInContext(source,ctx);
 return {sources,header,requests,notices,render,complete(){events.full({type:'complite',data:{movie:card},object:activity})},lift(){return sources.children.find(x=>x.title==='Lift')},menu:()=>sources.children.map(x=>x.title)};
}
test('Lift is in the source menu even when a visible legacy bar precedes it',()=>{const f=fixture();f.complete();assert.ok(f.menu().includes('Lift'));assert.equal(f.header.children.length,0)});
test('Lift is in the source menu of the new card layout',()=>{const f=fixture({legacy:false});f.complete();assert.ok(f.menu().includes('Lift'))});
test('late plugin load adds Lift to an already open card',()=>{const f=fixture({late:true});assert.ok(f.menu().includes('Lift'))});
test('repeated card completion does not duplicate Lift',()=>{const f=fixture();f.complete();f.complete();assert.equal(f.menu().filter(x=>x==='Lift').length,1)});
test('disabled profile does not get a Lift menu item',()=>{const f=fixture({enabled:false});f.complete();assert.equal(f.lift(),undefined)});
test('standalone non-TMDB card does not get a Lift source',()=>{const f=fixture({card:{id:'wr-42',source:'wrestling'}});f.complete();assert.equal(f.lift(),undefined)});
test('clicking Lift in source menu starts the card request',()=>{const f=fixture();f.complete();f.lift()['hover:enter']();assert.ok(f.requests[0].url.endsWith('/home/lift/item?media=movie&tmdb=42'))});
test('Lift source button supplies an SVG icon to the source menu',()=>{const f=fixture();f.complete();assert.equal(f.lift().hasIcon,true)});
