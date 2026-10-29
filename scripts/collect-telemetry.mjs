import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { parseMinerTelemetry } from './telemetry-core.mjs';
const executeFile=promisify(execFile);
const reading=value=>/^\d+(?:\.\d+)?$/.test(value.trim()) ? Number(value.trim()) : null;

export function mergeDriverTelemetry(api, text, now=Date.now()) {
  const snapshot=parseMinerTelemetry(api,now);
  const driver=[];
  for(const line of text.split(/\r?\n/)) {
    const columns=line.split(',').map(s=>s.trim());
    if(columns.length!==8)continue;
    const [index,bdf,temp,power,fan,core,memory,memTemp]=columns;
    const id=reading(index);
    if(![0,1].includes(id))continue;
    const match=bdf.match(/^[0-9a-f]+:([0-9a-f]+):[0-9a-f]+\.[0-9]+$/i);
    const bus=match ? parseInt(match[1],16) : null;
    const gpu=bus===null ? snapshot.gpus.find(g=>g.id===id) : snapshot.gpus.find(g=>g.busId===bus);
    driver.push({ ...gpu, id, name:gpu?.name ?? 'RTX 4070 Ti', hashrateTHs:gpu?.hashrateTHs ?? null, acceptedShares:gpu?.acceptedShares ?? null, rejectedShares:gpu?.rejectedShares ?? null, tempC:reading(temp),powerWatts:reading(power),fanPercent:reading(fan),cclkMHz:reading(core),mclkMHz:reading(memory),vramTempC:reading(memTemp),sensorSource:'nvidia-smi' });
  }
  return { ...snapshot, ok:true, sampledAt:new Date(now).toISOString(), nvmlAvailable:driver.length>0, gpus:[0,1].map(id=>driver.find(g=>g.id===id) ?? snapshot.gpus[id]) };
}

export async function collectTelemetry() {
  const [api,driver]=await Promise.allSettled([
    fetch('http://127.0.0.1:3001/',{signal:AbortSignal.timeout(2500)}).then(r=>r.json()),
    executeFile('nvidia-smi',['--query-gpu=index,pci.bus_id,temperature.gpu,power.draw,fan.speed,clocks.current.graphics,clocks.current.memory,temperature.memory','--format=csv,noheader,nounits'],{windowsHide:true,timeout:5000}),
  ]);
  return mergeDriverTelemetry(api.status==='fulfilled' ? api.value : {},driver.status==='fulfilled' ? driver.value.stdout : '');
}
