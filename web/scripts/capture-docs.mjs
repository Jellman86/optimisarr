#!/usr/bin/env node
// Run from web: node scripts/capture-docs.mjs. Starts an isolated Vite server; every API response
// and media image is fabricated locally. External requests and unexpected API paths are rejected.
import { chromium, expect } from '@playwright/test'
import { spawn } from 'node:child_process'
import { mkdir, writeFile, copyFile } from 'node:fs/promises'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import * as f from './docs-fixtures.mjs'
const web = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const output = resolve(web, '../docs/images')
const port = Number(process.env.DOCS_PORT ?? 4197)
const origin = `http://127.0.0.1:${port}`
const server = spawn(process.execPath, ['node_modules/vite/bin/vite.js','--host','127.0.0.1','--port',String(port),'--strictPort'], {cwd:web,stdio:'pipe'})
let serverError='';server.stderr.on('data',d=>serverError+=d)
// HTTP alone cannot identify our build: an unrelated process may already own the port.
// Require this child to announce its own ready URL before accepting an HTTP response.
const serverReady = new Promise((ready, reject) => {
 let stdout = ''
 const timeout = setTimeout(() => reject(Error('Vite did not become ready within 15 seconds.')), 15_000)
 server.once('error', error => { clearTimeout(timeout); reject(error) })
 server.once('exit', code => { clearTimeout(timeout); reject(Error(`Vite exited before capture (${code}): ${serverError}`)) })
 server.stdout.on('data', data => {
  stdout += String(data).replace(/\x1b\[[0-9;]*m/g, '')
  if (stdout.includes(`${origin}/`)) { clearTimeout(timeout); ready() }
 })
})
const json=(route,body)=>route.fulfill({contentType:'application/json',body:JSON.stringify(body)})
const unexpected=new Set(), captured=[]
let browser
try {
 await serverReady
 if(server.exitCode!==null) throw Error(serverError)
 if(!(await fetch(origin,{signal:AbortSignal.timeout(5000)})).ok) throw Error('The owned Vite server did not answer successfully.')
 if(process.argv.includes('--check-server')) console.log('Owned Vite server is ready; startup check passed.')
 else {
 await mkdir(output,{recursive:true})
 browser=await chromium.launch()
 // Generate a playable, entirely synthetic landscape clip in Chromium. No FFmpeg or external
 // media download is needed, so the same capture command works on every Playwright host.
 const mediaPage=await browser.newPage()
 const clip=Buffer.from(await mediaPage.evaluate(async()=>{
  const canvas=document.createElement('canvas');canvas.width=960;canvas.height=540
  const ctx=canvas.getContext('2d'),chunks=[]
  const recorder=new MediaRecorder(canvas.captureStream(24),{mimeType:'video/webm;codecs=vp8',videoBitsPerSecond:2_000_000})
  const result=new Promise(resolve=>{recorder.ondataavailable=e=>chunks.push(e.data);recorder.onstop=async()=>{const bytes=new Uint8Array(await new Blob(chunks).arrayBuffer());let binary='';for(const byte of bytes)binary+=String.fromCharCode(byte);resolve(btoa(binary))}})
  function draw(){const sky=ctx.createLinearGradient(0,0,0,540);sky.addColorStop(0,'#2b6583');sky.addColorStop(1,'#c2b890');ctx.fillStyle=sky;ctx.fillRect(0,0,960,540);ctx.fillStyle='#e6daba';ctx.beginPath();ctx.arc(700,150,65,0,Math.PI*2);ctx.fill();for(let layer=0;layer<3;layer++){ctx.fillStyle=['#426779','#284d61','#163345'][layer];ctx.beginPath();ctx.moveTo(0,290+layer*70);for(let x=0;x<=960;x+=80)ctx.lineTo(x,270+layer*70+Math.sin(x/110+layer)*65);ctx.lineTo(960,540);ctx.lineTo(0,540);ctx.fill()}ctx.font='18px sans-serif';ctx.fillStyle='#dce8df';ctx.fillText('LUMEN COAST · FABRICATED DOCUMENTATION MEDIA',30,508)}
  draw();recorder.start();const timer=setInterval(draw,40);await new Promise(r=>setTimeout(r,12000));clearInterval(timer);recorder.stop();return await result
 }),'base64');await mediaPage.close()
 let calibrationKind='Video',calibrationLibrary=1
 const sessionId='11111111-1111-1111-1111-111111111111'
 function comparison(){return {id:sessionId,libraryId:calibrationLibrary,mediaFileId:1,source:calibrationKind==='Image'?'Lumen landscape.png':f.files[0].relativePath,mediaKind:calibrationKind,status:'Comparing',preparationProgress:1,preparationState:'Working',error:null,result:null,variants:['ORIGINAL','A','B','C','D',...(calibrationKind==='Image'?['E']:[])].map((name,index)=>({name,isOriginal:index===0,diagnostics:null,samples:Array.from({length:calibrationKind==='Image'?1:3},(_,scene)=>({sampleNumber:scene+1,sampleCount:calibrationKind==='Image'?1:3,durationSeconds:calibrationKind==='Image'?0:12,url:`/api/calibration/${sessionId}/variants/${name}/samples/${scene}/content`,startSeconds:0,gainDb:0}))}))}}
 const context=await browser.newContext({viewport:{width:1440,height:1000},deviceScaleFactor:1,colorScheme:'dark',reducedMotion:'reduce',locale:'en-GB',timezoneId:'UTC'})
 await context.addInitScript(()=>{localStorage.setItem('optimisarr-theme','dark');localStorage.setItem('optimisarr-locale','en')})
 await context.route('**/*',async route=> {
  const url=new URL(route.request().url()),path=url.pathname
  if(url.origin!==origin) throw Error(`External request blocked: ${url.origin}`)
  if(path==='/hubs/jobs/negotiate') return json(route,{negotiateVersion:1,connectionId:'documentation',connectionToken:'documentation',availableTransports:[{transport:'WebSockets',transferFormats:['Text']}]})
  if(!path.startsWith('/api/'))return route.continue()
  if(path.endsWith('/thumbnail'))return route.fulfill({contentType:'image/svg+xml',body:f.artwork(path.split('/')[3])})
  if(path==='/api/auth/status')return json(route,{required:false})
  if(path==='/api/setup')return json(route,{version:1,completedStep:5,currentStep:5,stepCount:5,completed:true})
  if(path==='/api/health')return json(route,{status:'healthy',service:'optimisarr',version:'0.2.13'})
  if(path==='/api/settings')return json(route,f.settings)
  if(path==='/api/diagnostics/capture')return json(route,null)
  if(path==='/api/settings/cleanup')return json(route,{retentionDays:14,dryRunMode:true,failedOutputCount:1,failedOutputBytes:2e9,quarantinedOriginalCount:0,quarantinedOriginalBytes:0,planToken:'documentation',totalCount:1,totalBytes:2e9})
  if(path==='/api/system/tools')return json(route,{tools:f.tools})
  if(path==='/api/system/hardware')return json(route,{hardware:f.hardware})
  if(path==='/api/stats')return json(route,f.stats)
  if(path==='/api/jobs')return json(route,f.jobs)
  if(path==='/api/jobs/failures')return json(route,[{category:'Verification',description:'The output did not meet a required verification gate.',count:1,samples:[{jobId:6,mediaFileId:6,relativePath:f.files[5].relativePath,jobType:'Normal',errorMessage:f.jobs[5].errorMessage,verificationChecks:[]}]}])
  if(path==='/api/queue/status')return json(route,f.queue)
  if(path==='/api/libraries')return json(route,f.libraries)
  if(path==='/api/library-options')return json(route,f.options)
  if(/^\/api\/libraries\/\d+\/access$/.test(path)) {const library=f.libraries[Number(path.split('/')[3])-1];return json(route,{path:library.path,exists:true,readable:true,writable:true,ok:true,message:'Ready',issue:'none',fileSystemId:'documentation',mountId:'1',mountPoint:'/data',fileSystemType:'ext4',availableBytes:680e9,totalBytes:2e12,atomicWithWork:true,atomicWithQuarantine:true})}
  if(path==='/api/candidates/summary')return json(route,f.libraries.map(l=>({libraryId:l.id,eligible:l.id===1?7:12,skipped:l.fileCount-(l.id===1?7:12)})))
  if(path==='/api/candidates')return json(route,f.candidates)
  if(path==='/api/exclusions')return json(route,[{id:1,mediaFileId:6,libraryId:1,path:'/data/films/'+f.files[5].relativePath,relativePath:f.files[5].relativePath,source:'RepeatedFailures',reason:'Quality target not met after the recovery retry. Original retained.',createdAt:f.when}])
  if(path==='/api/inventory')return json(route,{items:f.files.map(file=>({file,eligible:file.id!==3,reason:file.id===3?'Already uses the target codec.':'Video qualifies for the library’s HEVC target.'})),total:f.files.length,counts:{all:f.files.length,eligible:7,skipped:1,unprobed:0}})
  if(path==='/api/replacements')return json(route,f.replacements)
  if(/^\/api\/replacements\/\d+$/.test(path))return json(route,f.replacements[Number(path.split('/').pop())-1])
  if(path.endsWith('/content')||path.includes('/stream'))return calibrationKind==='Image'&&path.includes('/calibration/')?route.fulfill({contentType:'image/svg+xml',body:f.artwork(1,true)}):route.fulfill({status:200,contentType:'video/webm',body:clip})
  if(path==='/api/workers')return json(route,f.workers)
  if(path==='/api/workers/pairing-code')return route.fulfill({status:204})
  if(path==='/api/activity-watchers')return json(route,[{id:1,name:'Living room media',type:'Jellyfin',baseUrl:'https://media.example.com',hasToken:true,enabled:true,refreshOnReplace:true,createdAt:f.when,updatedAt:f.when}])
  if(path==='/api/arr-connections')return json(route,[{id:1,name:'Film imports',type:'Radarr',baseUrl:'https://films.example.com',hasApiKey:true,enabled:true,createdAt:f.when,updatedAt:f.when}])
  if(path==='/api/notification-targets')return json(route,[{id:1,name:'Media updates',type:'Webhook',url:'https://notifications.example.com/optimisarr',hasToken:false,enabled:true,notifyOnReplacement:true,notifyOnFailure:true,createdAt:f.when,updatedAt:f.when}])
  if(path.endsWith('/calibration/sources')) {calibrationLibrary=Number(path.split('/')[3]);calibrationKind=calibrationLibrary===4?'Image':'Video';return json(route,[{mediaFileId:1,relativePath:calibrationKind==='Image'?'Lumen landscape.png':f.files[0].relativePath,durationSeconds:calibrationKind==='Image'?0:4200,width:1920,height:1080,mediaKind:calibrationKind,isHdr:false}])}
  if(path.endsWith('/calibration')&&route.request().method()==='POST')return json(route,comparison())
  if(path===`/api/calibration/${sessionId}`)return route.request().method()==='DELETE'?route.fulfill({status:204}):json(route,comparison())
  unexpected.add(path);return route.fulfill({status:404,contentType:'application/json',body:'{}'})
 })
 await context.routeWebSocket('**/hubs/jobs?*',socket=>{socket.onMessage(message=>{if(String(message).includes('"protocol"')){socket.send('{}\u001e');for(let i=0;i<60;i++)socket.send(JSON.stringify({type:1,target:'systemMetrics',arguments:[{cpuPercent:34+Math.sin(i/6)*8,gpuSupported:true,gpuPercent:68+Math.sin(i/8)*11,gpuEngine:'Intel video engine'}]})+'\u001e');socket.send(JSON.stringify({type:1,target:'jobProgress',arguments:[{jobId:1,progress:.68,fps:124,speed:4.2,etaSeconds:320}]})+'\u001e')}})})
 const page=await context.newPage(),errors=[]
 await page.clock.setFixedTime(new Date('2026-09-17T12:00:30Z'))
 page.on('pageerror',error=>errors.push(error.message))
 async function go(route) {await page.goto(`${origin}/#${route}`);await page.locator('main').waitFor();await page.waitForTimeout(500);await page.evaluate(()=>document.fonts.ready)}
 async function shot(name,selector='main') {
  await page.mouse.move(0,0);await page.waitForTimeout(100)
  const target=selector?page.locator(selector):page
  await target.screenshot({path:resolve(output,`optimisarr-${name}-dark.png`),animations:'disabled'})
  captured.push(name);console.log(`Captured ${name}`)
 }
 async function aliases(source,names){for(const name of names){await copyFile(resolve(output,`optimisarr-${source}-dark.png`),resolve(output,`optimisarr-${name}-dark.png`));captured.push(name)}}
 await go('/');await expect(page.getByRole('heading',{name:'Dashboard',exact:true})).toBeVisible();await shot('dashboard',null);await shot('dashboard-main');await aliases('dashboard-main',['dashboard-savings'])
 await go('/libraries');await expect(page.locator('[data-library-card="1"]')).toBeVisible();await shot('libraries',null);await shot('libraries-main')
 await go('/libraries/1/configure');await expect(page.getByRole('button',{name:/Choose files/}).first()).toBeVisible();await shot('library-configure')
 for(const [route,name]of[['source','library-choose-files'],['source/advanced','library-advanced-eligibility'],['encode','library-encode'],['encode/video/advanced','library-advanced-encoding'],['verify/advanced','library-advanced-verification'],['automate','library-automation']]){await go('/libraries/1/configure/'+route);await shot(name)}
 await go('/libraries/1/configure');await page.getByRole('button',{name:/^Candidates/}).click();await shot('library-candidates');await page.getByRole('button',{name:/^Excluded/}).click();await shot('library-excluded')
 await go('/inventory');await expect(page.getByRole('button',{name:/Lumen Coast.mkv/})).toBeVisible();await shot('inventory',null);await shot('inventory-main');await page.getByRole('button',{name:/Lumen Coast.mkv/}).click();await expect(page.getByRole('dialog')).toBeVisible();await shot('inventory-detail','dialog')
 await go('/queue');await expect(page.getByRole('region',{name:'Working now'})).toBeVisible();await shot('queue',null);await shot('queue-main');await shot('queue-working-job','[aria-label="Working now"]');await page.getByRole('region',{name:'Working now'}).getByRole('button',{name:'View job',exact:true}).click();await expect(page.getByRole('dialog')).toBeVisible();await shot('queue-detail','dialog');await page.keyboard.press('Escape')
 await page.setViewportSize({width:390,height:1000});await shot('queue-mobile',null);await page.setViewportSize({width:1440,height:1000})
 await page.route('**/api/libraries',route=>json(route,f.libraries.map(library=>library.id===1?{...library,autoEnqueueEnabled:true,autoReplace:true}:library)))
 await go('/schedule');await expect(page.getByRole('region',{name:'Auto-optimise windows'})).toBeVisible();await shot('schedule');await page.unroute('**/api/libraries')
 await go('/quarantine');await shot('quarantine',null);await shot('quarantine-main');await page.setViewportSize({width:1440,height:1500});await go('/quarantine/1');await expect(page.locator('[data-quarantine-review]')).toBeVisible();await expect.poll(()=>page.locator('video').evaluateAll(videos=>videos.every(v=>v.readyState>=2))).toBe(true);await page.getByRole('button',{name:'Play both',exact:true}).click();await page.waitForTimeout(250);await page.getByRole('button',{name:'Pause both',exact:true}).click();await expect.poll(()=>page.locator('video').evaluateAll(videos=>videos.every(v=>!v.seeking))).toBe(true);await shot('quarantine-review');await page.getByRole('heading',{name:'Verification',exact:true}).scrollIntoViewIfNeeded();await shot('quarantine-verification','section:has(> div > #quarantine-verification)');await page.setViewportSize({width:1440,height:1000})
 await go('/settings');await shot('settings')
 for(const [room,name]of[['encoding','settings-general'],['files','settings-files'],['media-servers','settings-connections'],['download-managers','settings-downloads'],['notifications','settings-notifications'],['workers','settings-workers'],['system','settings-system']]){await go('/settings/'+room);await expect(page.getByRole('button',{name:'All settings',exact:true})).toBeVisible();if(room==='encoding')await page.locator('.workload-details summary').click();await shot(name)}
 await go('/settings/system');await page.getByRole('heading',{name:'Tools',exact:true}).scrollIntoViewIfNeeded();await shot('settings-tools','[data-config-section]:has(h2:text-is("Tools"))');await aliases('settings-tools',['tools']);await page.getByRole('heading',{name:'Hardware acceleration',exact:true}).scrollIntoViewIfNeeded();await page.locator('#global-hardware').evaluate(el=>el.scrollIntoView({block:'start'}));await page.waitForTimeout(100);const hardwareBounds=await page.locator('#global-hardware').boundingBox(),encoderBounds=await page.locator('#global-encoders').boundingBox();await page.screenshot({path:resolve(output,'optimisarr-settings-hardware-dark.png'),clip:{x:hardwareBounds.x,y:hardwareBounds.y,width:hardwareBounds.width,height:encoderBounds.y+encoderBounds.height-hardwareBounds.y},animations:'disabled'});captured.push('settings-hardware')
 await page.getByRole('heading',{name:'Backup & restore',exact:true}).scrollIntoViewIfNeeded();await shot('settings-backup','[data-config-section]:has(h2:text-is("Backup & restore"))')
 await page.setViewportSize({width:1440,height:1250});await go('/libraries/1/quality-check');await expect(page.getByRole('button',{name:'Prepare blind samples'})).toBeVisible();await shot('personal-quality-check');await page.getByRole('button',{name:'Prepare blind samples'}).click();await expect(page.locator('video')).toBeVisible();await expect.poll(()=>page.locator('video').evaluate(v=>v.readyState>=2)).toBe(true);await shot('personal-quality-video');await go('/libraries/4/quality-check');await page.getByRole('button',{name:'Prepare blind samples'}).click();await expect(page.locator('main img').first()).toBeVisible();await shot('personal-quality-image')
 if(errors.length)throw Error('UI errors: '+errors.join('\n'))
 if(unexpected.size)throw Error('Unmocked requests: '+[...unexpected].join(', '))
 await writeFile(resolve(output,'web-screenshot-manifest.json'),JSON.stringify({description:'Captured from the current local UI using fabricated API responses and original vector artwork. No production data or third-party media.',command:'cd web && node scripts/capture-docs.mjs',viewport:{width:1440,height:1000},mobileViewport:{width:390,height:1000},reviewViewport:{width:1440,height:1500},qualityViewport:{width:1440,height:1250},images:captured.map(name=>`optimisarr-${name}-dark.png`)},null,2)+'\n')
 }
}finally{await browser?.close();server.kill('SIGTERM')}
