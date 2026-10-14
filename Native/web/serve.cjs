// Local WebGL server. node Native/web/serve.cjs [port] [build-directory]
// Android over USB: adb reverse tcp:8765 tcp:8765, then Chrome -> http://localhost:8765
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const port = Number(process.argv[2] || 8765);
const root = path.resolve(process.argv[3] || path.join(__dirname, '../../Builds/WebGL'));
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid port');
const types = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.wasm': 'application/wasm',
  '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.data': 'application/octet-stream' };
http.createServer((request, response) => {
  let filename;
  try {
    filename = path.resolve(root, '.' + decodeURIComponent(new URL(request.url, 'http://localhost').pathname));
  } catch { response.writeHead(400); response.end(); return; }
  if (filename !== root && !filename.startsWith(root + path.sep)) { response.writeHead(403); response.end(); return; }
  if (filename === root) filename = path.join(root, 'index.html');
  fs.stat(filename, (error, stat) => {
    if (error || !stat.isFile()) { response.writeHead(404); response.end('Not found'); return; }
    response.writeHead(200, { 'Content-Type': types[path.extname(filename)] || 'application/octet-stream',
      'Content-Length': stat.size, 'Cache-Control': 'no-cache' });
    if (request.method === 'HEAD') { response.end(); return; }
    fs.createReadStream(filename).on('error', () => response.destroy()).pipe(response);
  });
}).listen(port, '127.0.0.1', () => console.log('KAGEFUMI: http://localhost:' + port));
