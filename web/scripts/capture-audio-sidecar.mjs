// Only fabricated state and a measured, generated audio fixture. No production server.
import { chromium, expect } from '@playwright/test'
import { spawn } from 'node:child_process'
import { readFile } from 'node:fs/promises'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
const web=resolve(dirname(fileURLToPath(import.meta.url)),'..'),origin='http://127.0.0.1:4219'
const spectrum=await readFile(process.argv[2]);if(spectrum.length>8192)throw Error('Preview is too large')
const server=spawn(process.execPath,['node_modules/vite/bin/vite.js','--host','127.0.0.1','--port','4219','--strictPort'],{cwd:web,stdio:'pipe'})
const status={name:'Studio Linux',state:'Working',serverAddress:'https://optimisarr.example',scratchPath:'/work',storage:{kind:'Disk',freeBytes:400e9,totalBytes:1e12},concurrency:1,
 capabilities:{videoEncoders:['libx265'],audioEncoders:['aac','libopus','libmp3lame'],hardwareDecoders:[],vmaf:'Cpu'},recent:[],version:'0.2.17',brandStyle:'precession',update:null,
 pairing:{required:false,configuredServer:null,inProgress:false,problem:null},metrics:{cpuPercent:18,gpuPercent:null,gpuEngine:null,sampledAt:'2026-10-01T10:00:00Z'},
 jobs:[{jobId:42,title:'Audio Study · Generated fixture',kind:'Audio',encoder:'libopus',stage:'Encoding',encodedSeconds:31,sourceBytes:18e6,outputExtension:'opus',hardwareDecoder:null,previewRevision:1,hasArtwork:false,startedAt:'2026-10-01T10:00:00Z',sourceMedia:{videoCodec:null,width:null,height:null,durationSeconds:100,audioCodecs:'flac',pixelFormat:null}}]}
let browser
try{
 await new Promise((ready,reject)=>{let out='';const timer=setTimeout(()=>reject(Error('Owned server did not start')),15000);server.once('error',reject);server.once('exit',()=>reject(Error('Owned server exited')));server.stdout.on('data',data=>{out+=data;if(out.includes(origin)){clearTimeout(timer);ready()}})})
 browser=await chromium.launch();const page=await browser.newPage({viewport:{width:1440,height:1000},colorScheme:'dark'});const problems=[]
 page.on('pageerror',e=>problems.push(e.message))
 await page.route('**/*',route=>{const url=new URL(route.request().url());if(url.origin!==origin){problems.push('External request');return route.abort()}if(url.pathname==='/api/sidecar/status')return route.fulfill({json:status});if(url.pathname==='/api/sidecar/jobs/42/preview')return route.fulfill({contentType:'image/jpeg',body:spectrum});if(url.pathname.startsWith('/api/')){problems.push('Unexpected API');return route.abort()}return route.continue()})
 await page.goto(origin+'/sidecar.html');await expect(page.getByRole('img',{name:'Source audio spectrogram of Audio Study · Generated fixture'})).toBeVisible()
 for(const [name,width,height,light] of [['dark',1440,1000,false],['light',1440,1000,true],['phone',390,844,true]]){await page.setViewportSize({width,height});const toggle=page.getByRole('button',{name:light?'Use light theme':'Use dark theme'});if(await toggle.count())await toggle.click();if(!await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth))throw Error('Horizontal overflow');await page.screenshot({path:resolve(web,`../docs/images/optimisarr-sidecar-linux-audio-${name}.png`),fullPage:true})}
 if(problems.length)throw Error(problems.join('\n'))
}finally{await browser?.close();server.kill('SIGTERM')}
