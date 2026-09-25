// Fabricated documentation fixtures. No production endpoints, credentials, media or artwork.
export const library = {
  id: 1, name: 'Documentary films', path: '/data/films', mediaType: 'Film', ruleProfile: 'ConservativeHevc',
  enabled: true, priority: 0, minFileSizeBytes: null, maxHeight: null,
  reencodeSameCodecAboveBytes: null, skipEfficientSources: true, targetVideoCodec: null,
  targetContainer: null, hdrHandling: null, optimiseDolbyVision: false, excludePaths: null,
  qualityCrf: null, encoderPreset: null, audioTargetCodec: null, audioBitrateKbps: null,
  videoAudioCodec: null, videoAudioBitrateKbps: null, downmixToStereo: false,
  keepAudioLanguages: null, keepSubtitleLanguages: null, reencodeLossyAudio: false,
  targetImageFormat: null, imageQuality: null, reencodeLossyImages: false,
  imageDownscaleMode: 'None', imageDownscaleValue: 0, moveOnComplete: false,
  targetFolder: null, moveOverwrite: false, minVmafHarmonicMean: null, minVmafMin: null,
  vmafQualityGateEnabled: false, minVmafCatastrophicMin: null, clipVmafEnabled: null,
  vmafFrameSubsample: null, durationTolerancePercent: 1, requireAudioRetained: true,
  requireSubtitlesRetained: false, requireSizeReduction: true,
  audioLoudnessGateEnabled: false, maxLoudnessDriftLufs: 1,
  audioClippingGateEnabled: false, maxTruePeakDbtp: 0,
  imageQualityGateEnabled: true, minimumImageSsim: 0.95, imageMetadataGateEnabled: true,
  autoEnqueueEnabled: false, autoEnqueueWindowStart: '00:00',
  autoEnqueueWindowEnd: '00:00', autoReplace: false, videoQualityStrategy: 'Fixed',
  lastAutoEnqueueAt: null, fileCount: 48,
}

export const settings = {
  maxConcurrentJobs: 1,
  minFreeDiskBytes: 10_737_418_240,
  cpuThreadLimit: 0,
  libraryScanIntervalHours: 1,
  encoderMode: 'Auto',
  hardwareDecode: true,
  hdrToneMapMode: 'Software',
  replacementAllowCrossFilesystem: false,
  dryRunMode: true,
  replacementQuarantineRetentionDays: 14,
  remoteWorkersEnabled: true,
  remoteWorkersAvailable: true,
  workerVerificationRequired: true,
  workloadConcurrencyMode: 'Automatic',
  nonVideoSlots: 0,
  evidenceValidationSlots: 1,
  automaticNonVideoSlots: 1,
  automaticEvidenceValidationSlots: 2,
}

export const tools = [
  {
    name: 'FFmpeg',
    command: '/usr/lib/jellyfin-ffmpeg/ffmpeg',
    available: true,
    required: true,
    version: 'ffmpeg version 7.1.4-Jellyfin Copyright (c) 2000-2026 the FFmpeg developers',
    error: null,
  },
  {
    name: 'FFmpeg (VMAF)',
    command: '/usr/local/lib/optimisarr/ffmpeg-vmaf',
    available: true,
    required: false,
    version: 'libvmaf filter available',
    error: null,
  },
  {
    name: 'ffprobe',
    command: '/usr/lib/jellyfin-ffmpeg/ffprobe',
    available: true,
    required: true,
    version: 'ffprobe version 7.1.4-Jellyfin Copyright (c) 2007-2026 the FFmpeg developers',
    error: null,
  },
]

export const hardware = {
  hardwareAccelerators: ['cuda', 'vaapi', 'qsv', 'drm', 'opencl', 'vulkan'],
  encoders: [
    { name: 'libx264', codec: 'h264', mode: 'CPU', available: true },
    { name: 'libx265', codec: 'hevc', mode: 'CPU', available: true },
    { name: 'libsvtav1', codec: 'av1', mode: 'CPU', available: true },
    { name: 'h264_qsv', codec: 'h264', mode: 'Intel QSV', available: true },
    { name: 'hevc_qsv', codec: 'hevc', mode: 'Intel QSV', available: true },
    { name: 'av1_qsv', codec: 'av1', mode: 'Intel QSV', available: false },
  ],
  nvidiaRuntimeAvailable: false,
  driDeviceAvailable: true,
  error: null,
}

export const when = '2026-09-17T12:00:00Z'
export const titles = ['Lumen Coast', 'The Glass Observatory', 'Amber Transit', 'Signal Garden', 'The Quiet Meridian', 'Paper Satellites', 'Silver Canopy', 'Tidal Atlas']
export const libraries = [library,
  { ...library, id: 2, name: 'Nature series', path: '/data/series', mediaType: 'Tv', fileCount: 126, autoEnqueueEnabled: true, autoEnqueueWindowStart: '01:00', autoEnqueueWindowEnd: '06:00' },
  { ...library, id: 3, name: 'Field recordings', path: '/data/audio', mediaType: 'Music', fileCount: 64, audioTargetCodec: 'opus', audioBitrateKbps: 128 },
  { ...library, id: 4, name: 'Landscape studies', path: '/data/photos', mediaType: 'Photo', fileCount: 240, targetImageFormat: 'webp', imageQuality: 82 },
]
export const options = {
  mediaTypes: ['Film', 'TV', 'Music', 'Photo', 'Other'],
  ruleProfiles: ['CompatibilityH264', 'ConservativeHevc', 'ExperimentalAv1', 'ScottsSettings', 'RemuxCleanup', 'TrackCleanup'],
  ruleProfileSpecs: [
    { profile: 'CompatibilityH264', codec: 'h264', container: 'mp4', crf: 20, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
    { profile: 'ConservativeHevc', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'Exclude', videoAudioCodec: 'aac', videoAudioBitrateKbps: 160, downmixToStereo: false },
    { profile: 'ExperimentalAv1', codec: 'av1', container: 'mkv', crf: 30, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
    { profile: 'ScottsSettings', codec: 'hevc', container: 'mp4', crf: 24, hdrHandling: 'TonemapToSdr', videoAudioCodec: 'aac', videoAudioBitrateKbps: 96, downmixToStereo: true },
    { profile: 'RemuxCleanup', codec: null, container: 'mkv', crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
    { profile: 'TrackCleanup', codec: null, container: null, crf: null, hdrHandling: 'Preserve', videoAudioCodec: null, videoAudioBitrateKbps: 160, downmixToStereo: false },
  ],
  hdrHandlings: ['Exclude', 'Preserve', 'TonemapToSdr'], videoCodecs: ['h264', 'hevc', 'av1'], containers: ['mp4', 'mkv'],
  encoderPresets: ['quick', 'balanced', 'efficient'], legacyEncoderPresets: [], imageFormats: ['webp', 'avif', 'jpeg'],
}
export const files = titles.map((title, i) => ({ id: i + 1, libraryId: 1, relativePath: `${title} (2026)/${title}.mkv`, sizeBytes: (8 + i * 2.1) * 1e9, status: 'Probed', mediaKind: 'Video', container: 'matroska', videoCodec: i === 2 ? 'hevc' : 'h264', width: 1920, height: 1080, durationSeconds: 4200 + i * 160, audioCodecs: 'aac', audioLanguages: 'eng', audioTrackCount: 1, subtitleTrackCount: 2, probedAt: when, probeError: null, optimisedMarker: null }))
export const checks = [
  { name: 'Decode health', outcome: 'Passed', detail: 'Complete decode finished without errors.' },
  { name: 'Duration', outcome: 'Passed', detail: '4,200 seconds retained; difference below 1%.' },
  { name: 'Stream policy', outcome: 'Passed', detail: 'Required audio and subtitle tracks retained.' },
  { name: 'VMAF quality', outcome: 'Passed', detail: 'Harmonic mean 96.2 · fifth percentile 94.1 · minimum 88.4.' },
  { name: 'Size saving', outcome: 'Passed', detail: 'Output is 62% smaller than the original.' },
]
export const jobs = files.slice(0, 6).map((file, i) => ({ id: file.id, mediaFileId: file.id, libraryId: 1, relativePath: file.relativePath,
  status: ['Transcoding', 'ReadyToReplace', 'Queued', 'Queued', 'Completed', 'Failed'][i], priority: 0, progress: i === 0 ? .68 : [1,4].includes(i) ? 1 : 0,
  errorMessage: i === 5 ? 'The output did not meet the configured quality target. Original retained.' : null,
  enqueueReason: null, failureCategory: i === 5 ? 'Verification' : null,
  ffmpegArguments: '-i /data/films/Lumen Coast.mkv -c:v hevc_qsv -global_quality 24 /work/job-1/output.mp4',
  videoEncoder: 'hevc_qsv', requestedVideoQuality: 24, effectiveVideoQuality: 24, videoQualityMode: 'icq', qualityRetryCount: 0,
  outputSizeBytes: [1,4].includes(i) ? 3_040_000_000 : null, verificationPassed: [1,4].includes(i) ? true : i === 5 ? false : null,
  verificationReportJson: [1,4].includes(i) ? JSON.stringify({checks}) : null, verifiedAt: [1,4].includes(i) ? when : null,
  enqueuedAt: '2026-09-17T11:00:00Z', startedAt: i === 0 ? '2026-09-17T11:50:00Z' : null, finishedAt: [1,4].includes(i) ? when : null,
  clearable: i === 4 || i === 5, workerName: null, remoteStage: null, waitingForWorker: false,
}))
export const queue = { ...settings, canStart: true, blockedReason: null, manuallyPaused: false, manualPauseMode: 'inactive', runningEncodesSuspended: false, suspendedEncodeCount: 0, pauseFailedEncodeCount: 0, runningJobs: 1, hardwareAccelerated: true, freeDiskBytes: 680e9, workRoot: '/work', waitingReason: null,
  workloadLanes: [
    { lane: 'Video', active: 1, capacity: 1, waiting: 2, reason: 'All video slots are busy.' },
    { lane: 'NonVideo', active: 0, capacity: 1, waiting: 0, reason: null },
    { lane: 'Evidence', active: 0, capacity: 2, waiting: 0, reason: null },
    { lane: 'Workers', active: 0, capacity: 2, waiting: 0, reason: null },
  ] }
export const stats = { bytesSaved: 184e9, originalBytes: 320e9, optimisedBytes: 136e9, filesOptimised: 84, averageSavingPercent: 57.5, inQuarantine: 3, quarantineReclaimableBytes: 28e9, queued: 2, running: 1, readyToReplace: 1, failed: 1, libraries: 4, enabledLibraries: 4, discoveredFiles: 478 }
export const replacements = files.slice(0,3).map((f,i) => ({id:f.id,jobId:f.id,mediaFileId:f.id, originalPath:'/data/films/'+f.relativePath, finalPath:'/data/films/'+f.relativePath.replace('.mkv','.mp4'), quarantinePath:`/trash/job-${f.id}/${titles[i]}.mkv`, originalSizeBytes:f.sizeBytes,newSizeBytes:Math.round(f.sizeBytes*.38),crossFilesystem:false,status:'Replaced',replacedAt:when,rolledBackAt:null,purgedAt:null,mediaKind:'Video',verificationPassed:true,verificationReportJson:JSON.stringify({checks})}))
export const candidates = files.map(f => ({ mediaFileId:f.id, libraryId:1, relativePath:f.relativePath,sizeBytes:f.sizeBytes,videoCodec:f.videoCodec,ruleProfile:'ConservativeHevc',eligible:f.id!==3,reason:f.id===3?'Already uses the target codec.':'Video qualifies for the library’s HEVC target.',mediaKind:'Video' }))
export const workers = ['Studio Mac', 'Studio PC'].map((name,i)=>({id:i+1,name,operatingSystem:i?'Windows':'macOS',architecture:i?'x64':'arm64',protocolVersion:2,sidecarVersion:'0.2.13',cpuBusyFraction:.12,gpuBusyFraction:.08,loadReportedAt:when,videoEncoders:[i?'hevc_nvenc':'hevc_videotoolbox'],audioEncoders:['aac'],hardwareDecoders:[i?'cuda':'videotoolbox'],vmaf:'Cpu',freeScratchBytes:400e9,maxConcurrency:1,pairedAt:when,lastSeenAt:when,revokedAt:null,online:true,drainRequestedAt:null,heldLeases:0,activeJobs:[],lastProblem:null,lastProblemAt:null}))
// Original vector illustrations created for documentation, not posters from a media provider.
export function artwork(id,wide=false) {
 const i=(Number(id)-1)%titles.length, color=['#387c81','#8b6075','#bc9250','#46735a'][i%4]
 if(wide) return `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="675"><defs><linearGradient id="sky" x2="0" y2="1"><stop stop-color="#2b6583"/><stop offset="1" stop-color="#c2b890"/></linearGradient></defs><rect width="1200" height="675" fill="url(#sky)"/><circle cx="875" cy="188" r="81" fill="#e6daba"/><path d="M0 400L150 300l200 130 200-160 200 115 250-85v375H0" fill="#426779"/><path d="M0 510L180 375l260 155 220-145 230 150 310-100v240H0" fill="#284d61"/><path d="M0 540l200-75 250 160 220-70 240 65 290-100v155H0" fill="#163345"/><text x="38" y="637" font-family="sans-serif" font-size="21" fill="#dce8df">LUMEN LANDSCAPE · FABRICATED DOCUMENTATION MEDIA</text></svg>`
 return `<svg xmlns="http://www.w3.org/2000/svg" width="${wide?1200:400}" height="${wide?675:600}" viewBox="0 0 400 600"><defs><linearGradient id="s" x2="1" y2="1"><stop stop-color="${color}"/><stop offset="1" stop-color="#0b1323"/></linearGradient></defs><rect width="400" height="600" fill="url(#s)"/><circle cx="260" cy="190" r="115" fill="#e3d9b7" opacity=".75"/><path d="M0 380L125 270l100 95 100-85 75 95v225H0" fill="#193947"/><path d="M0 440l130-90 130 80 140-60v230H0" fill="#0d2434"/><path d="M0 470q100-35 210 0t190 0M0 495q100-35 210 0t190 0" fill="none" stroke="#bde2d2" opacity=".3"/><text x="28" y="535" font-family="sans-serif" font-size="22" fill="#f2eee2">${titles[i]}</text><text x="28" y="567" font-family="sans-serif" font-size="10" letter-spacing="3" fill="#b9d0d8">DOCUMENTATION FICTION</text></svg>`
}
