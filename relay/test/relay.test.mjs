import { test } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import WebSocket from 'ws';
import { createRelay } from '../src/server.mjs';
import { validSnapshot } from '../src/protocol.mjs';

const room={ name:'test',hostKey:'h'.repeat(32),viewerKey:'v'.repeat(32) };
function snapshot(seq=1) {
  return {type:'snapshot',v:3,seq,runId:'run1',attemptId:'attempt1',game:'Test',category:'Coop',attempts:1,
    phase:'Running',index:1,timingMethod:'RealTime',comparison:'Personal Best',offsetTicks:0,
    realTicks:150000000,gameTicks:null,gamePaused:false,
    segments:[0,1,2].map(i=>({name:`CP${i}`,splitRT:i===0?100000000:null,splitGT:null,pbRT:(i+1)*120000000,pbGT:null,bestRT:100000000,bestGT:null,comparisons:{}}))};
}
async function setup(t,options={}) {
  const relay=createRelay({rooms:[room],...options});relay.server.listen(0,'127.0.0.1');await once(relay.server,'listening');
  t.after(()=>relay.close());
  return `ws://127.0.0.1:${relay.server.address().port}/coop`;
}
async function client(url,role='viewer',key=role==='host'?room.hostKey:room.viewerKey,name=room.name) {
  const ws=new WebSocket(`${url}/${name}`);const queue=[],waiters=[];
  ws.on('message',data=>{const m=JSON.parse(data);const i=waiters.findIndex(w=>w.type===m.type);if(i<0)queue.push(m);else waiters.splice(i,1)[0].resolve(m);});
  ws.next=type=>new Promise((resolve,reject)=>{
    const index=queue.findIndex(m=>m.type===type);if(index>=0){resolve(queue.splice(index,1)[0]);return;}
    const timer=setTimeout(()=>reject(new Error('Timeout waiting for '+type)),2000);
    waiters.push({type,resolve:m=>{clearTimeout(timer);resolve(m);}});
  });
  await once(ws,'open');ws.send(JSON.stringify({type:'hello',v:3,role,key}));return ws;
}
test('host sends exact RTA and nullable GT to two viewers; late join receives all splits',async t=>{
  const url=await setup(t);const host=await client(url,'host');await host.next('ready');
  const a=await client(url),b=await client(url);await Promise.all([a.next('ready'),b.next('ready')]);
  const state=snapshot();host.send(JSON.stringify(state));
  for(const viewer of [a,b]) assert.deepEqual((await viewer.next('state')).snapshot,state);
  const late=await client(url);assert.deepEqual((await late.next('state')).snapshot,state);
});
test('viewer publish is rejected; wrong key is rejected; outdated clients are told to update',async t=>{
  const url=await setup(t);
  const wrong=await client(url,'viewer','x'.repeat(32));assert.equal((await once(wrong,'close'))[0],1008);
  const viewer=await client(url);await viewer.next('ready');viewer.send(JSON.stringify(snapshot()));assert.equal((await once(viewer,'close'))[0],1008);
  const old=new WebSocket(url);await once(old,'open');old.send(JSON.stringify({type:'hello',v:1,room:room.name,role:'viewer',key:room.viewerKey}));
  const [code,reason]=await once(old,'close');assert.equal(code,1008);assert.match(reason.toString(),/protocol version/);
});
test('a newer host connection replaces a stale one; the replaced host is told why',async t=>{
  const url=await setup(t);const first=await client(url,'host');await first.next('ready');
  const viewer=await client(url);await viewer.next('ready');await viewer.next('host');
  first.send(JSON.stringify(snapshot(5)));await viewer.next('state');
  const closed=once(first,'close');const second=await client(url,'host');await second.next('ready');
  const [code,reason]=await closed;assert.equal(code,4001);assert.match(reason.toString(),/Replaced/);
  second.send(JSON.stringify(snapshot(1)));assert.equal((await viewer.next('state')).snapshot.seq,1,'new host starts its own sequence');
  assert.equal(viewer.readyState,WebSocket.OPEN);
});
test('viewers cannot occupy the host slot',async t=>{
  const url=await setup(t,{maxRoomConnections:3});
  const a=await client(url),b=await client(url);await Promise.all([a.next('ready'),b.next('ready')]);
  const c=await client(url);assert.equal((await once(c,'close'))[0],1008);
  const host=await client(url,'host');assert.equal((await host.next('ready')).role,'host');
});
test('silent unauthenticated sockets are evicted instead of blocking real clients',async t=>{
  const url=await setup(t,{maxPending:2});
  const idle=[];for(let i=0;i<3;i++){const ws=new WebSocket(url);await once(ws,'open');idle.push(ws);}
  await once(idle[0],'close');assert.equal(idle[1].readyState,WebSocket.OPEN);
  for(let i=0;i<4;i++){const ws=new WebSocket(url);await once(ws,'open');idle.push(ws);}
  const viewer=await client(url);assert.equal((await viewer.next('ready')).role,'viewer');
  for(const ws of idle) ws.terminate();
});
test('ticks carry only the clock; late joiners get the merged complete snapshot',async t=>{
  const url=await setup(t);const host=await client(url,'host');await host.next('ready');
  const viewer=await client(url);await viewer.next('ready');await viewer.next('host');
  host.send(JSON.stringify(snapshot(1)));await viewer.next('state');
  host.send(JSON.stringify({type:'tick',v:3,seq:2,realTicks:160000000,gameTicks:null,gamePaused:false}));
  const tick=await viewer.next('tick');assert.deepEqual(tick,{type:'tick',seq:2,realTicks:160000000,gameTicks:null,gamePaused:false});
  const late=await client(url);const merged=(await late.next('state')).snapshot;
  assert.deepEqual(merged,{...snapshot(2),realTicks:160000000});
});
test('ticks need a snapshot from the same connection and cannot break finish invariants',async t=>{
  const url=await setup(t);const early=await client(url,'host');await early.next('ready');
  early.send(JSON.stringify({type:'tick',v:3,seq:1,realTicks:1,gameTicks:null,gamePaused:false}));
  assert.equal((await once(early,'close'))[0],1008);
  const host=await client(url,'host');await host.next('ready');
  const final=snapshot(1);final.phase='Ended';final.index=3;final.segments.forEach((s,i)=>{s.splitRT=(i+1)*100000000;});final.realTicks=300000000;
  host.send(JSON.stringify(final));
  host.send(JSON.stringify({type:'tick',v:3,seq:2,realTicks:300000001,gameTicks:null,gamePaused:false}));
  assert.equal((await once(host,'close'))[0],1008);
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

test('host takeover succeeds at the global connection limit without granting extra viewer slots',async t=>{
  const url=await setup(t,{maxConnections:2});
  const first=await client(url,'host');await first.next('ready');
  const viewer=await client(url);await viewer.next('ready');
  const closed=once(first,'close');
  const second=await client(url,'host');await second.next('ready');
  assert.equal((await closed)[0],4001);
  second.send(JSON.stringify(snapshot()));await viewer.next('state');
  const extra=await client(url);assert.equal((await once(extra,'close'))[0],1008);
});
test('rooms are addressed by path; unknown rooms close with 4004 and a missing path is refused',async t=>{
  const url=await setup(t);
  const unknown=await client(url,'viewer',room.viewerKey,'nosuchroom');const [code,reason]=await once(unknown,'close');
  assert.equal(code,4004);assert.match(reason.toString(),/expired or unknown/);
  const bare=new WebSocket(url);await once(bare,'open');bare.send(JSON.stringify({type:'hello',v:3,role:'viewer',key:room.viewerKey}));
  assert.equal((await once(bare,'close'))[0],1008);
  for(const path of ['/coop/','/coop/a/b','/coop/bad%20id']) { const ws=new WebSocket(url.replace('/coop',path));const [error]=await once(ws,'error');assert.match(error.message,/403/); }
});
test('room creation is off by default; created rooms work, are rate limited and expire when idle',async t=>{
  const plain=await setup(t);
  assert.equal((await fetch(plain.replace('ws:','http:').replace('/coop','/rooms'),{method:'POST'})).status,404);
  const url=await setup(t,{roomCreation:true,createPerHour:2,roomIdleMs:100});const rooms=url.replace('ws:','http:').replace('/coop','/rooms');
  assert.equal((await fetch(rooms,{method:'POST',headers:{Origin:'https://example.com'}})).status,404,'browsers cannot create rooms');
  const response=await fetch(rooms,{method:'POST'});assert.equal(response.status,201);
  const made=await response.json();assert.match(made.roomId,/^[A-Za-z0-9_-]{22}$/);assert.notEqual(made.hostKey,made.viewerKey);
  const host=await client(url,'host',made.hostKey,made.roomId);await host.next('ready');
  const viewer=await client(url,'viewer',made.viewerKey,made.roomId);await viewer.next('ready');
  host.send(JSON.stringify(snapshot()));await viewer.next('state');
  const cross=await client(url,'viewer',room.viewerKey,made.roomId);assert.equal((await once(cross,'close'))[0],1008,'keys are per room');
  host.close();viewer.close();await Promise.all([once(host,'close'),once(viewer,'close')]);
  await new Promise(r=>setTimeout(r,150));
  assert.equal((await fetch(rooms,{method:'POST'})).status,201,'creating sweeps idle rooms');
  const limited=await fetch(rooms,{method:'POST'});assert.equal(limited.status,429);assert.ok(Number(limited.headers.get('retry-after'))>0);
  const expired=await client(url,'viewer',made.viewerKey,made.roomId);assert.equal((await once(expired,'close'))[0],4004);
  const configured=await client(url);assert.equal((await configured.next('ready')).role,'viewer','configured rooms never expire');
});
