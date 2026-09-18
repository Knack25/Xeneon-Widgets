import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../dist');
const portIndex=process.argv.indexOf('--port');
const port=portIndex<0?8788:Number(process.argv[portIndex+1]);
const mime={'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.json':'application/json','.txt':'text/plain'};
export const server=http.createServer(async(req,res)=>{
  const url=new URL(req.url,'http://localhost');
  if(url.pathname.startsWith('/api/')){res.writeHead(404);res.end('Fixture preview has no helper API.');return;}
  const relative=decodeURIComponent(url.pathname).replace(/^\/outlook\/?/,'').replace(/^\//,'')||'index.html';
  const file=path.resolve(root,relative);
  if(!file.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
  try{const data=await readFile(file);res.writeHead(200,{'Content-Type':mime[path.extname(file)]||'application/octet-stream','Cache-Control':'no-store'});res.end(data);}catch{res.writeHead(404);res.end('Not found');}
});
server.listen(port,'127.0.0.1',()=>console.log(`Fixture preview: http://127.0.0.1:${server.address().port}/outlook/?demo=1`));
server.on('error',error=>{console.error(error.message);process.exitCode=1;});
