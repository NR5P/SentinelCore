// Render intro.html frame by frame through headless Brave over CDP.
// usage: node capture.mjs <html> <outDir> <frames> [onlyFrame]
import { spawn } from 'node:child_process';
import { writeFileSync, mkdirSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { tmpdir } from 'node:os';

const [html, outDir, frameCount, only] = process.argv.slice(2);
const FPS = 30000 / 1001, PORT = 9337;
mkdirSync(outDir, { recursive: true });
const brave = spawn('/Applications/Brave Browser.app/Contents/MacOS/Brave Browser', [
  '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${tmpdir()}/sentinelcore-intro-brave`,
  '--window-size=1280,720', '--hide-scrollbars', '--force-device-scale-factor=1',
  '--allow-file-access-from-files', 'about:blank'], { stdio: 'ignore' });

const sleep = ms => new Promise(r => setTimeout(r, ms));
let targets;
for (let i = 0; i < 50; i++) {
  try { targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json(); if (targets.some(t => t.type === 'page')) break; } catch {}
  await sleep(200);
}
const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener('open', r));
let id = 0; const pending = new Map();
ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails)); return r.result?.result?.value; };

await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 720, deviceScaleFactor: 1, mobile: false });
await send('Page.enable');
await send('Page.navigate', { url: pathToFileURL(html).href });
for (let i = 0; i < 50; i++) { if (await evalJs('typeof window.ready !== "undefined"').catch(() => false)) break; await sleep(100); }
await evalJs('window.ready');

const list = only !== undefined ? only.split(',').map(Number) : [...Array(Number(frameCount)).keys()];
for (const f of list) {
  await evalJs(`render(${f / FPS})`);
  const shot = await send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width: 1280, height: 720, scale: 1 } });
  writeFileSync(`${outDir}/f${String(f).padStart(4, '0')}.png`, Buffer.from(shot.result.data, 'base64'));
}
ws.close(); brave.kill();
console.log(`rendered ${list.length} frames`);
