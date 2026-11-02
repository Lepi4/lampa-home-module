const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../HomeMedia/ServiceUi.cs'),'utf8');
const start=source.indexOf('string script = @"')+'string script = @"'.length;
const end=source.indexOf('".Replace("__PREFIX__"',start);
const prefix='/home/services/channel-manager/';
const html=source.slice(start,end).replace(/""/g,'"').replaceAll('__PREFIX__',prefix).replaceAll('__BASE__',JSON.stringify(prefix));
const code=html.slice(html.indexOf('<script>')+8,html.lastIndexOf('</script>'));
function fixture(){
 const seen=[];function Xhr(){};Xhr.prototype.open=function(...args){seen.push(args);};
 function ES(url,opts){this.url=url;this.options=opts;}function WS(url,protocols){this.url=url;this.protocols=protocols;}
 const c={URL,Request,XMLHttpRequest:Xhr,EventSource:ES,WebSocket:WS,location:{origin:'http://lampac.lan:9118',host:'lampac.lan:9118',href:'http://lampac.lan:9118'+prefix},document:{baseURI:'http://lampac.lan:9118'+prefix,documentElement:{},addEventListener(){}},MutationObserver:class{observe(){}},fetch:(input,opts)=>{seen.push({input,opts});return Promise.resolve();}};c.window=c;
 vm.createContext(c);vm.runInContext(code,c);return{c,seen};
}
test('root fetch and XHR requests use the relative service mount',async()=>{const f=fixture();await f.c.fetch('/api/config');assert.equal(f.seen[0].input,prefix+'api/config');new f.c.XMLHttpRequest().open('POST','/api/channels?x=1');assert.equal(f.seen[1][1],prefix+'api/channels?x=1');});
test('already mounted URLs and external sources are preserved',async()=>{const f=fixture();await f.c.fetch(prefix+'api/config');await f.c.fetch('https://external.invalid/logo');assert.equal(f.seen[0].input,prefix+'api/config');assert.equal(f.seen[1].input,'https://external.invalid/logo');});
test('Request objects retain the method, headers and body when scoped',async()=>{const f=fixture();await f.c.fetch(new Request('http://lampac.lan:9118/api/config',{method:'POST',headers:{'Content-Type':'application/json'},body:'{"x":1}'}));const request=f.seen[0].input;assert.equal(request.url,'http://lampac.lan:9118'+prefix+'api/config');assert.equal(request.method,'POST');assert.equal(request.headers.get('Content-Type'),'application/json');assert.equal(await request.text(),'{"x":1}');});
test('event streams and WebSockets use the same mounted service',()=>{const f=fixture();assert.equal(new f.c.EventSource('/tsb/events').url,prefix+'tsb/events');assert.equal(new f.c.WebSocket('ws://lampac.lan:9118/events').url,'ws://lampac.lan:9118'+prefix+'events');});
