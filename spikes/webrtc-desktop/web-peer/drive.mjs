// Throwaway spike driver. Headless Chromium stands in for "a plain browser"
// since there is no human here to open a tab and paste SDP by hand.
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const sigDir = process.argv[2] || '/tmp/webrtc-spike-sig';
const offerPath = path.join(sigDir, 'offer.json');
const answerPath = path.join(sigDir, 'answer.json');
const runSeconds = parseInt(process.argv[3] || '600', 10);

console.log('[web-driver] waiting for offer at', offerPath);
while (!fs.existsSync(offerPath)) await new Promise(r => setTimeout(r, 200));
await new Promise(r => setTimeout(r, 300));
const offerSdp = fs.readFileSync(offerPath, 'utf8');

// Bundled open-source Chromium has no H.264 support at all (proprietary
// codec, stripped from the OSS build) -- RTCRtpSender.getCapabilities
// returns zero H264 entries and negotiation falls back to VP8/VP9, which
// FFmpegVideoEncoder here is not offering. Real Google Chrome ships H.264.
// This gap is itself a spike finding, not just a test-harness workaround.
const browser = await chromium.launch({ headless: true, channel: 'chrome' });
const page = await browser.newPage();
page.on('console', msg => console.log('[web]', msg.text()));

await page.goto('file://' + path.join(__dirname, 'index.html'));
const answerSdp = await page.evaluate((sdp) => window.startSpike(sdp), offerSdp);
fs.writeFileSync(answerPath, answerSdp);
console.log('[web-driver] wrote answer to', answerPath);

const start = Date.now();
let lastCpu = await page.evaluate(() => performance.now());
const samples = [];
while ((Date.now() - start) / 1000 < runSeconds) {
  await new Promise(r => setTimeout(r, 5000));
  const state = await page.evaluate(() => ({
    ice: window.spikePc ? window.spikePc.iceConnectionState : 'no-pc',
    conn: window.spikePc ? window.spikePc.connectionState : 'no-pc',
  }));
  console.log('[web-driver][sample]', 't=' + Math.round((Date.now() - start) / 1000) + 's', JSON.stringify(state));
}

const latencySummary = await page.evaluate(() => window.getLatencySummary());
console.log('[web-driver] latency summary:', JSON.stringify(latencySummary));
fs.writeFileSync(path.join(sigDir, 'latency-summary.json'), JSON.stringify(latencySummary, null, 2));

console.log('[web-driver] run complete');
await browser.close();
