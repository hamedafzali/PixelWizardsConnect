// T7 Playwright smoke test. Follows the pattern proven in
// spikes/webrtc-desktop/web-peer/drive.mjs: headless Chromium stands in for a human opening a
// browser tab. This one is simpler than the WebRTC spike -- no signaling files, no SDP -- it
// just needs a real WebSocketHostServer instance to point the browser at.
//
// What this does and doesn't cover: it proves the WS wire protocol (the 0x01/0x02 type-tag
// framing WebSocketHostServer.BroadcastFrameAsync writes) and the embedded viewer HTML's JS
// decode path survived the T7 move. It does not exercise real screen capture -- see
// tests/WebViewerSmoke/Host/Program.cs's own comment for why.
import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const hostProject = path.join(__dirname, 'Host', 'WebViewerSmokeHost.csproj');

function fail(msg) {
  console.error('[smoke] FAIL:', msg);
  process.exitCode = 1;
}

const host = spawn('dotnet', ['run', '--project', hostProject, '-c', 'Release'], {
  stdio: ['ignore', 'pipe', 'inherit'],
});

let port = null;
let sawSent = false;
const readyPromise = new Promise((resolve, reject) => {
  const timeout = setTimeout(() => reject(new Error('host did not print READY within 60s')), 60_000);
  host.stdout.on('data', (chunk) => {
    const text = chunk.toString();
    process.stdout.write('[host] ' + text);
    if (port === null) {
      const m = text.match(/READY (\d+)/);
      if (m) { port = parseInt(m[1], 10); clearTimeout(timeout); resolve(); }
    }
    if (text.includes('SENT frame')) sawSent = true;
  });
  host.on('exit', (code) => {
    if (port === null) { clearTimeout(timeout); reject(new Error(`host exited early with code ${code}`)); }
  });
});

try {
  await readyPromise;
  console.log('[smoke] host ready on port', port);

  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  page.on('console', (msg) => console.log('[browser]', msg.text()));

  await page.goto(`http://localhost:${port}/`);
  console.log('[smoke] page loaded, waiting for WebSocket connect + rendered frame…');

  await page.waitForFunction(
    () => document.getElementById('dot')?.classList.contains('connected'),
    { timeout: 20_000 },
  );
  console.log('[smoke] WebSocket connected');

  await page.waitForFunction(
    () => document.getElementById('screen')?.style.display === 'block',
    { timeout: 20_000 },
  );
  console.log('[smoke] PASS: canvas shows a rendered frame');

  await browser.close();

  // The canvas becoming visible already proves the frame was sent, received, and decoded --
  // this is just a secondary sanity check, not a pass/fail signal (stdout can arrive after
  // the page has already rendered).
  if (!sawSent) console.warn('[smoke] note: host stdout "SENT frame" line arrived after (or was missed before) the canvas render was observed');
} catch (err) {
  fail(err.message);
} finally {
  host.kill();
}
