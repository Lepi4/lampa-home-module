const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const src=fs.readFileSync(__dirname+'/../HomeMedia/kinopub.js','utf8');
const code=src.slice(src.indexOf('    var mcV = 0;'),src.indexOf('  // остаток подписки Кинопаба'));
// The extracted section starts inside a try block and ends after its catch.
// Wrap it exactly as production does, then expose __homeBack normally.
const adjusted=code.replace(/  \} catch \(e0\) \{\}/,'');
function run(originIsOldCard=false){let active={component:originIsOldCard?'full':'main'},stack=[{component:'main'},active],start,exits=0,backs=0;const c={Date,window:null,Lampa:{Activity:{active:()=>active,all:()=>stack,back(){backs++;stack.pop();active=stack.at(-1)}},Listener:{follow(n,f){start=f}},Select:{opened:()=>false},Controller:{back(){}}},AndroidJS:{mcDeviceId:()=> 'fixture',exit(){exits++}},setInterval(){},$:s=>({hasClass:()=>false,length:s==='.modal'?0:1}),history:{back(){backs++}}};c.window=c;vm.createContext(c);vm.runInContext(adjusted,c);c.__mc_shell_origin=active;c.__mc_lastT=Date.now();c.__mc_lastLen=stack.length;c.__mc_ret=1;assert.equal(c.__mc_ret,3);active={component:'full'};stack.push(active);start({type:'start'});assert.equal(c.__mc_ret,2);assert.equal(c.__mc_base,stack.length);c.__homeBack();assert.equal(exits,1);assert.equal(backs,1)}
run();run(true);console.log('PASS shell return ignores a recent main or previous card when setting the new boundary');
