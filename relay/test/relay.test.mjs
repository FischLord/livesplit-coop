import { test } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import WebSocket from 'ws';
import { createRelay } from '../src/server.mjs';
import { validSnapshot } from '../src/protocol.mjs';

const room={ name:'test',hostKey:'h'.repeat(32),viewerKey:'v'.repeat(32) };
function snapshot(seq=1) {
  return {type:'snapshot',v:1,seq,runId:'run1',attemptId:'attempt1',game:'Test',category:'Coop',attempts:1,
    phase:'Running',index:1,timingMethod:'RealTime',comparison:'Personal Best',offsetTicks:0,
    realTicks:150000000,gameTicks:null,gamePaused:false,
    segments:[0,1,2].map(i=>({name:`CP${i}`,splitRT:i===0?100000000:null,splitGT:null,pbRT:(i+1)*120000000,pbGT:null,bestRT:100000000,bestGT:null,comparisons:{}}))};
}
async function setup(t) {
  const relay=createRelay({rooms:[room]});relay.server.listen(0,'127.0.0.1');await once(relay.server,'listening');
  t.after(()=>relay.close());
  return `ws://127.0.0.1:${relay.server.address().port}/coop`;
}
async function client(url,role='viewer',key=role==='host'?room.hostKey:room.viewerKey) {
  const ws=new WebSocket(url);const queue=[],waiters=[];
  ws.on('message',data=>{const m=JSON.parse(data);const i=waiters.findIndex(w=>w.type===m.type);if(i<0)queue.push(m);else waiters.splice(i,1)[0].resolve(m);});
  ws.next=type=>new Promise((resolve,reject)=>{
    const index=queue.findIndex(m=>m.type===type);if(index>=0){resolve(queue.splice(index,1)[0]);return;}
    const timer=setTimeout(()=>reject(new Error('Timeout waiting for '+type)),2000);
    waiters.push({type,resolve:m=>{clearTimeout(timer);resolve(m);}});
  });
  await once(ws,'open');ws.send(JSON.stringify({type:'hello',v:1,room:room.name,role,key}));return ws;
}
test('host sends exact RTA and nullable GT to two viewers; late join receives all splits',async t=>{
  const url=await setup(t);const host=await client(url,'host');await host.next('ready');
  const a=await client(url),b=await client(url);await Promise.all([a.next('ready'),b.next('ready')]);
  const state=snapshot();host.send(JSON.stringify(state));
  for(const viewer of [a,b]) assert.deepEqual((await viewer.next('state')).snapshot,state);
  const late=await client(url);assert.deepEqual((await late.next('state')).snapshot,state);
});
test('viewer publish is rejected; wrong key is rejected; second host cannot take over',async t=>{
  const url=await setup(t);const host=await client(url,'host');await host.next('ready');
  const second=await client(url,'host');assert.equal((await once(second,'close'))[0],1008);
  const wrong=await client(url,'viewer','x'.repeat(32));assert.equal((await once(wrong,'close'))[0],1008);
  const viewer=await client(url);await viewer.next('ready');viewer.send(JSON.stringify(snapshot()));assert.equal((await once(viewer,'close'))[0],1008);
});
test('host disconnect is announced; cached final result stays available offline',async t=>{
  const url=await setup(t);const host=await client(url,'host');await host.next('ready');
  const final=snapshot();final.phase='Ended';final.index=3;final.realTicks=300000001;
  final.segments.forEach((s,i)=>{s.splitRT=(i+1)*100000000;});final.segments[2].splitRT=300000001;
  const viewer=await client(url);await viewer.next('ready');await viewer.next('host');
  host.send(JSON.stringify(final));assert.equal((await viewer.next('state')).snapshot.realTicks,300000001);
  host.close();assert.equal((await viewer.next('host')).online,false);
  const late=await client(url);const cached=await late.next('state');assert.equal(cached.live,false);assert.deepEqual(cached.snapshot,final);
});
test('reconnect accepts new publisher sequence and stale sequence cannot overwrite latest',async t=>{
  const url=await setup(t);const host=await client(url,'host');await host.next('ready');
  const viewer=await client(url);await viewer.next('ready');await viewer.next('host');
  host.send(JSON.stringify(snapshot(20)));await viewer.next('state');
  host.send(JSON.stringify(snapshot(19)));host.send(JSON.stringify(snapshot(21)));assert.equal((await viewer.next('state')).snapshot.seq,21);
  host.close();await viewer.next('host');const next=await client(url,'host');await next.next('ready');
  const joining=await client(url);assert.equal((await joining.next('state')).live,false,'old host snapshot must stay stale before replacement publishes');
  next.send(JSON.stringify(snapshot(1)));assert.equal((await viewer.next('state')).snapshot.seq,1);
});
test('malformed snapshots and inconsistent finish values are rejected',()=>{
  assert.ok(validSnapshot(snapshot()));
  for(const change of [s=>s.index=256,s=>s.realTicks=Number.MAX_SAFE_INTEGER+1,s=>s.segments=[],s=>s.phase='Unknown',s=>s.segments[0].name='bad\nname',s=>{s.phase='Ended';s.index=3;}]) {
    const s=snapshot();change(s);assert.equal(validSnapshot(s),false);
  }
});
test('health endpoint exposes no room data and URL credentials are rejected',async t=>{
  const url=await setup(t);const response=await fetch(url.replace('ws:','http:').replace('/coop','/healthz'));
  assert.deepEqual(await response.json(),{ok:true});
  const ws=new WebSocket(url+'?key=secret');const [error]=await once(ws,'error');assert.match(error.message,/403/);
});
