// Throwaway spike code. Not real signaling -- a dumb HTTP shim in front of
// T8's exact file-drop mechanism (offer.json / answer.json), because the
// phone can't read this Mac's filesystem directly the way T8's two
// same-machine processes could. GET /offer returns the offer once the .NET
// peer has written it; POST /answer writes the phone's answer back to disk
// for the .NET peer to pick up. No candidate-level messages, no protocol --
// same two SDP blobs T8 exchanged, just carried over a socket instead of a
// shared directory.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const sigDir = process.argv[2] || '/tmp/webrtc-spike-mobile-sig';
const port = parseInt(process.argv[3] || '8787', 10);
fs.mkdirSync(sigDir, { recursive: true });
const offerPath = path.join(sigDir, 'offer.json');
const answerPath = path.join(sigDir, 'answer.json');

const server = http.createServer((req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  if (req.method === 'GET' && req.url === '/offer') {
    if (fs.existsSync(offerPath)) {
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end(fs.readFileSync(offerPath, 'utf8'));
    } else {
      res.writeHead(404);
      res.end();
    }
    return;
  }
  if (req.method === 'POST' && req.url === '/answer') {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      fs.writeFileSync(answerPath, body);
      console.log('[signaling-server] wrote answer, ' + body.length + ' bytes');
      res.writeHead(200);
      res.end('ok');
    });
    return;
  }
  res.writeHead(404);
  res.end();
});

server.listen(port, '0.0.0.0', () => {
  console.log(`[signaling-server] listening on 0.0.0.0:${port}, sigDir=${sigDir}`);
});
