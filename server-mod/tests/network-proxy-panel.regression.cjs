const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../HomeMedia/panel.html'),'utf8');
const code=html.slice(html.indexOf('  function renderProxy('),html.indexOf('  // ------------------------------------------------------------------ YouTube:'));
class Element{
 constructor(tag,a={}){this.tag=tag;this.attrs=a;this.children=[];this.value=a.value||'';this.text=a.text||'';this.listeners={};this.checked=false;}
 appendChild(n){this.children.push(n);n.parent=this;return n;}
 set textContent(s){this.text=s;this.children=[];}get textContent(){return this.text;}
 addEventListener(e,f){this.listeners[e]=f;}
 remove(){this.parent.children=this.parent.children.filter(n=>n!==this);}
}
function fixture(){
 const requests=[],notices=[];let answer={ok:true,json:{message:'reachable'}};
 const el=(tag,a,...children)=>{const e=new Element(tag,a);children.filter(x=>x!=null).forEach(x=>e.appendChild(typeof x==='string'?new Element('#text',{text:x}):x));return e;};
 const ctx={el,toast:x=>notices.push(x),confirm:()=>true,api:(url,opts)=>{requests.push({url,body:JSON.parse(opts.body)});return Promise.resolve(answer);}};
 vm.createContext(ctx);vm.runInContext(code,ctx);
 const box=el('section');const state={revision:'rev1',can_rollback:true,connections:[{id:'socks',name:'Mihomo SOCKS5',url:'socks5://192.168.1.61:7893',has_auth:true},{id:'http',name:'Mihomo HTTP',url:'http://192.168.1.61:7890',has_auth:false}],routes:{tmdb_api:'socks',tmdb_images:'socks',kinopub:'socks',youtube:'http',jacred:'direct'}};
 ctx.renderProxy(box,state);
 function all(n=box){return[n,...n.children.flatMap(x=>all(x))];}
 return{box,state,requests,notices,all,find:(tag,text)=>all().find(e=>e.tag===tag&&e.text===text),route:label=>all().find(e=>e.tag==='select'&&e.attrs['aria-label']===label),answer:x=>answer=x};
}
const settle=()=>new Promise(r=>setImmediate(r));
test('all five services offer direct and both imported Mihomo connections',()=>{const f=fixture();const selects=f.all().filter(e=>e.tag==='select');assert.equal(selects.length,5);for(const s of selects)assert.deepEqual(s.children.map(x=>x.value),['direct','socks','http']);assert.equal(f.route('JacRed — поиск раздач').value,'direct');assert.equal(f.route('YouTube — поиск и видео').value,'http');});
test('saved authentication is never prefilled and empty fields preserve it on save',async()=>{const f=fixture();assert.equal(f.all().filter(e=>e.tag==='input'&&e.attrs.type==='password').every(e=>e.value===''),true);f.find('button','Сохранить маршруты').attrs.onclick();await settle();const b=f.requests[0].body;assert.equal(b.connections[0].username,'');assert.equal(b.connections[0].password,'');assert.equal(b.connections[0].clear_auth,false);assert.equal(b.revision,'rev1');});
test('JacRed proxy choice does not change other service routes',async()=>{const f=fixture();f.route('JacRed — поиск раздач').value='socks';f.find('button','Сохранить маршруты').attrs.onclick();await settle();assert.equal(f.requests[0].body.routes.jacred,'socks');assert.equal(f.requests[0].body.routes.youtube,'http');assert.equal(f.requests[0].body.routes.kinopub,'socks');});
test('connection probe uses unsaved selection and never calls save',async()=>{const f=fixture();f.route('YouTube — поиск и видео').value='direct';const button=f.all().find(e=>e.tag==='select'&&e.attrs['aria-label']==='YouTube — поиск и видео').parent.children.find(e=>e.tag==='button');button.attrs.onclick();await settle();assert.equal(f.requests.length,1);assert.equal(f.requests[0].url,'/home/admin/proxy/test');assert.equal(f.requests[0].body.routes.youtube,'direct');assert.equal(f.requests[0].body.service,'youtube');});
test('connection in use cannot be removed',()=>{const f=fixture();f.find('button','Убрать подключение').attrs.onclick();assert.equal(f.all().filter(e=>e.tag==='input'&&e.attrs['aria-label']==='Название прокси').length,2);assert.equal(f.notices.length,1);});
test('rollback submits the revision only, with no stale secrets',async()=>{const f=fixture();f.find('button','Вернуть предыдущие настройки').attrs.onclick();await settle();assert.equal(f.requests[0].url,'/home/admin/proxy/rollback');assert.deepEqual(f.requests[0].body,{revision:'rev1'});});
test('service administration links stay on the current origin',()=>{assert.match(html,/\['Channel Manager','\/home\/services\/channel-manager\/'\]/);assert.match(html,/\['TorrServer','\/home\/services\/torrserver\/'\]/);assert.match(html,/\['Tvheadend','\/home\/services\/tvheadend\/gui\/'\]/);const block=html.slice(html.indexOf('  function renderServices()'),html.indexOf('  function load()'));assert.doesNotMatch(block,/192\.168\.|http:\/\//);});
