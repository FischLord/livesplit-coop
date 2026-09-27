import { createRelay } from '../src/server.mjs';
const relay=createRelay({rooms:[{name:'test',hostKey:'h'.repeat(32),viewerKey:'v'.repeat(32)}]});
relay.server.listen(0,'127.0.0.1',()=>console.log(`ws://127.0.0.1:${relay.server.address().port}/coop`));
setTimeout(()=>relay.close().then(()=>process.exit()),120000).unref();
