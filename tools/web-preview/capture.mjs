// Renders scenarios headlessly and saves PNG screenshots to generated/shots.
// Usage: node capture.mjs [scenario[:frame,frame…] …]
import { chromium } from 'playwright';
import { createServer } from 'http';
import { readFile, mkdir } from 'fs/promises';
import { extname, join, dirname } from 'path';
import { fileURLToPath } from 'url';

const root = dirname(fileURLToPath(import.meta.url));
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.woff2': 'font/woff2' };
const server = createServer(async (req, res) => {
  try {
    const path = join(root, decodeURIComponent(new URL(req.url, 'http://x').pathname));
    const body = await readFile(path);
    res.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' });
    res.end(body);
  } catch { res.writeHead(404); res.end(); }
});
await new Promise(r => server.listen(0, r));
const port = server.address().port;

const requests = process.argv.slice(2).length ? process.argv.slice(2) : ['hero'];
await mkdir(join(root, 'generated', 'shots'), { recursive: true });
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 2560, height: 1440 } });
page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') console.log('[page]', m.text().slice(0, 300)); });
page.on('pageerror', e => console.log('[pageerror]', e.message));
for (const request of requests) {
  const [scenario, list] = request.split(':');
  await page.goto(`http://localhost:${port}/index.html?scenario=${scenario}`);
  await page.waitForFunction(() => window.ready === true, null, { timeout: 120000 });
  const count = await page.evaluate(() => window.frameCount);
  const frames = list === 'all' ? [...Array(count).keys()] : (list ? list.split(',').map(Number) : [...Array(count).keys()].slice(0, 1));
  for (const f of frames) {
    await page.evaluate(i => window.renderFrame(i), f);
    const file = join(root, 'generated', 'shots', `${scenario}-${String(f).padStart(3, '0')}.png`);
    await page.locator('canvas').screenshot({ path: file });
    console.log(file);
  }
}
await browser.close();
server.close();
