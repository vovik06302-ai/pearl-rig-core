import test from 'node:test';
import http from 'node:http';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, mkdir, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { normalizeEarnings, fetchEarnings } from '../scripts/pool-earnings.mjs';
import { validateProfile, authorizeRequest } from '../scripts/control-policy.mjs';
import { classifyMiner, parseMinerLog } from '../scripts/miner-status.mjs';
import { createPanelServer } from '../scripts/panel-server.mjs';
import { normalizeMarketRates } from '../scripts/market-rates.mjs';

const wallet = 'prl1p'+'q'.repeat(58);
const profile = [0,1].map(id => ({id,cclk:2400,mclk:810,pl:150,kernel:2,fan:id ? '*' : '65'}));
test('earnings keep confirmed, pending, paid and UTC rewards separate', () => {
  const now = Date.UTC(2026,9,26,17);
  const r = normalizeEarnings({total:0.4431750679674985,confirmed:0.2771399007510668,unconfirmed:0.16603516721643172,threshold:1}, {paid:0,reward:{week:0.2771399007510668}}, [{timestamp:Date.UTC(2026,9,26),reward:0.15545618394307423}], {results:[]}, now);
  assert.equal(r.paid,0);
  assert.equal(r.confirmed,0.2771399007510668);
  assert.equal(r.todayUtc,0.15545618394307423);
  assert.notEqual(r.balance,r.confirmed);
  assert.equal(normalizeEarnings(null,null,null,null,now).paid,null);
  assert.equal(normalizeEarnings(null,null,[],null,now).todayUtc,null);
});
test('pool outages never fabricate zero earnings', async () => {
  await assert.rejects(fetchEarnings(wallet, async () => {throw new Error('offline');}));
  const r = await fetchEarnings(wallet, async url => {
    if (url.includes('/balance/')) return {ok:true,json:async()=>({confirmed:0.2,unconfirmed:0.1,total:0.3})};
    throw new Error('offline');
  });
  assert.equal(r.partial,true);
  assert.equal(r.paid,null);
  assert.equal(r.confirmed,0.2);
});
test('official market rates combine the latest Kryptex PRL/USD point with CBR USD/RUB', () => {
  const rates = normalizeMarketRates([{ timestamp: 1700000000000, price: 1.5 }, { timestamp: 1700003600000, price: 1.6 }], '<ValCurs><Valute><CharCode>USD</CharCode><Value>90,00</Value></Valute></ValCurs>', 1700004000000);
  assert.equal(rates.prlUsd, 1.6);
  assert.equal(rates.usdRub, 90);
  assert.equal(rates.prlRub, 144);
});
test('profiles reject unsafe limits, shell fragments and wallet modifications', () => {
  assert.deepEqual(validateProfile(profile),profile);
  for (const changes of [{pl:285},{pl:150.2},{cclk:3000},{mclk:10251},{fan:'65; whoami'},{kernel:1}]) {
    assert.throws(()=>validateProfile([{...profile[0],...changes},profile[1]]));
  }
  assert.equal(authorizeRequest({method:'POST',headers:{host:'127.0.0.1:3000',origin:'https://evil.example','content-type':'application/json','x-pearl-token':'x'}},'x'),false);
  assert.equal(authorizeRequest({method:'POST',headers:{host:'127.0.0.1:3000',origin:'http://127.0.0.1:3000','content-type':'application/json','x-pearl-token':'x'}},'x'),true);
});
test('process, API, pool and both GPU rates are checked independently', () => {
  const processes=[{ProcessId:10,ParentProcessId:1},{ProcessId:11,ParentProcessId:10}];
  const api={last_update:1000,uptime_seconds:500,algos:[{name:'pearlhash',pool_status:'Connected',hashrates:[1e14,1e14],total_accepted_shares:7}]};
  assert.equal(classifyMiner(processes,api,1000000).state,'healthy');
  assert.equal(classifyMiner(processes,api,1000000).rootCount,1);
  assert.equal(classifyMiner([],api,1000000).state,'stopped');
  assert.equal(classifyMiner(processes,api,1021000).apiAvailable,false);
  api.algos[0].pool_status='Disconnected';
  assert.equal(classifyMiner(processes,api,1000000).poolConnected,false);
  api.algos[0].pool_status='Connected'; api.algos[0].hashrates[1]=0;
  assert.equal(classifyMiner(processes,api,1000000).hashing,false);
});
test('log panel shows current-session errors and escapes nothing into commands', () => {
  const r=parseMinerLog('2026-10-26 09:00:00 [Error] old\nMiner started at 2026-10-26 18:51:51\n2026-10-26 18:52:00 [Error] TCP connection timed out\n2026-10-26 18:53:00 [Info] GPU 0 Share accepted\n');
  assert.equal(r.errors.length,1);
  assert.ok(r.errors[0].text.includes('18:52:00'));
  assert.ok(!r.errors[0].text.includes('old'));
  assert.ok(r.tail.some(line=>line.includes('Share accepted')));
});
test('HTTP control requires token and origin, serializes jobs, persists only validated profiles', async t => {
  const prefix=path.join(os.tmpdir(),'pearl-panel-test-');
  const base=await mkdtemp(prefix);
  await mkdir(path.join(base,'dist'));
  await writeFile(path.join(base,'dist/index.html'),'<h1>Panel</h1>');
  await writeFile(path.join(base,'rig-local.json'),JSON.stringify({wallet,pool:'stratum+tcp://prl-ru.kryptex.network:7048',gpus:profile}));
  let release;
  let operations=0;
  const server=createPanelServer({base,loadEarnings:async()=>({fetchedAt:new Date().toISOString(),confirmed:0.2}),loadMinerStatus:async()=>({state:'healthy',sampledAt:new Date().toISOString()}),runControl:async args=>{
    operations++;
    await new Promise(resolve=>{release=resolve;});
    const request=JSON.parse(await readFile(args.requestPath,'utf8'));
    return {status:'success',message:'Applied',applied:true,gpus:request.gpus};
  }});
  try {
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', resolve);
    });
  } catch (error) {
    if (error?.code === 'EACCES') {
      t.skip('sandbox denies loopback sockets');
      await rm(base, {recursive:true, force:true});
      return;
    }
    throw error;
  }
  t.after(async()=>{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));if(path.resolve(base).startsWith(prefix)) await rm(base,{recursive:true,force:true});});
  const url=`http://127.0.0.1:${server.address().port}`;
  const headers={Host:'127.0.0.1:3000'};
  const request = (target, options={}) => new Promise((resolve,reject) => {
    const call=http.request(target,options,response=>{
      let text='';response.setEncoding('utf8');response.on('data',chunk=>{text+=chunk;});
      response.on('end',()=>resolve({status:response.statusCode,json:async()=>JSON.parse(text)}));
    });
    call.on('error',reject);call.end(options.body);
  });
  let session;
  try {
    session = await request(url+'/api/session',{headers}).then(r=>r.json());
  } catch (error) {
    if (error?.code === 'EACCES') {
      t.skip('sandbox denies loopback sockets');
      return;
    }
    throw error;
  }
  assert.equal(session.wallet,wallet);
  const submit=(body,extra={})=>request(url+'/api/control',{method:'POST',headers:{...headers,Origin:'http://127.0.0.1:3000','Content-Type':'application/json','X-Pearl-Token':session.token,...extra},body:JSON.stringify(body)});
  assert.equal((await submit({action:'apply',gpus:profile},{'X-Pearl-Token':'wrong'})).status,403);
  assert.equal((await submit({action:'apply',gpus:profile,wallet:'other'})).status,400);
  assert.equal((await submit({action:'apply',gpus:[{...profile[0],pl:285},profile[1]]})).status,400);
  const changed=[{...profile[0],pl:125},profile[1]];
  assert.equal((await submit({action:'apply',gpus:changed})).status,202);
  assert.equal((await submit({action:'stop'})).status,409);
  for(let i=0;i<100 && !release;i++) await new Promise(resolve=>setTimeout(resolve,10));
  release();
  let result;
  for(let i=0;i<100;i++) {result=await request(url+'/api/control',{headers}).then(r=>r.json()); if(!result.busy)break;await new Promise(resolve=>setTimeout(resolve,10));}
  assert.equal(operations,1);
  assert.equal(result.status,'success');
  assert.equal(JSON.parse(await readFile(path.join(base,'rig-local.json'),'utf8')).gpus[0].pl,125);
  assert.equal((await request(url+'/api/earnings',{headers}).then(r=>r.json())).confirmed,0.2);
});
