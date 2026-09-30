const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync(require('node:path').join(__dirname, '../AnimeScheduleSync/Configuration/account.js'), 'utf8');
function setup(age=0, idle=0, missingTimes=false) {
  let now=2_000_000;
  const key='AnimeSchedule:/:session';
  const data=new Map([[key,'session-token'],[key+':device','test-device']]);
  if(!missingTimes)data.set(key+':times',JSON.stringify({started:now-age,lastActivity:now-idle}));
  const elements=new Map(), events={}, windowEvents={}, calls=[];
  let timer;
  const element=id=>{if(!elements.has(id))elements.set(id,{value:'',textContent:'',hidden:false,addEventListener(){}});return elements.get(id)};
  const context={URL,Date:{now:()=>now},JSON,Number,encodeURIComponent,sessionStorage:{getItem:k=>data.get(k),setItem:(k,v)=>data.set(k,v),removeItem:k=>data.delete(k)},
    document:{currentScript:{src:'https://example.test/AnimeSchedule/account.js'},getElementById:element,addEventListener:(k,f)=>events[k]=f},
    window:{addEventListener:(k,f)=>windowEvents[k]=f},setInterval:f=>timer=f,
    fetch:(url,options)=>{calls.push({url:String(url),options});return Promise.resolve({status:200,ok:true,json:async()=>({users:[{name:'Bob',userId:'bob',mode:'None',status:'Sync disabled'}]})})}};
  vm.runInNewContext(source,context);
  return {data,key,calls,element,events,windowEvents,tick:n=>{now+=n;timer()},advance:n=>now+=n};
}
test('idle timeout clears tab credentials, hides settings and requests session revocation',()=>{
 const s=setup();s.tick(300000);assert.equal(s.data.has(s.key),false);assert.equal(s.element('account-panel').hidden,true);assert.equal(s.calls.length,1);assert.match(s.calls[0].url,/Sessions\/Logout$/);s.tick(1000);assert.equal(s.calls.length,1);
});
test('reload uses stored activity time instead of extending the session',()=>{const s=setup(299999,299999);s.tick(1);assert.equal(s.data.has(s.key),false)});
test('trusted interaction renews idle time but never the absolute limit',()=>{const s=setup(1700000,10000);s.events.pointerdown({isTrusted:true});s.tick(100000);assert.equal(s.data.has(s.key),false)});
test('untrusted events cannot keep a session alive',()=>{const s=setup(299000,299000);s.events.pointerdown({isTrusted:false});s.tick(1000);assert.equal(s.data.has(s.key),false)});
test('returning to a suspended tab expires before accepting activity',()=>{const s=setup();s.advance(300001);s.events.pointerdown({isTrusted:true});assert.equal(s.data.has(s.key),false)});
test('old sessions without timestamps require fresh sign-in',()=>{const s=setup(0,0,true);s.tick(0);assert.equal(s.data.has(s.key),false)});
test('active sessions remain signed in within both limits',()=>{const s=setup();s.advance(200000);s.events.keydown({isTrusted:true});s.tick(200000);assert.equal(s.data.has(s.key),true)});
