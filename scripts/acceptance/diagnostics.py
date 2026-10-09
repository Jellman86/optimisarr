"""Exercise opted-in, correlated diagnostics during an owned real-media acceptance run."""
import json
import time

from .core import require, save


def start_capture(api):
    require(api.request('/api/diagnostics/capture') is None, 'Diagnostic acceptance requires a fresh isolated server')
    return api.post('/api/diagnostics/capture', {
        'durationHours': 1, 'includePaths': False, 'persistAcrossRestart': True,
        'retentionDays': 1, 'failureRetentionDays': 2, 'maximumBytes': 16 * 1024 * 1024})['id']


def validate_bundle(bundle, *, require_sidecar):
    manifest, events = bundle['manifest'], bundle['events']
    require(manifest['schemaVersion'] == 4 and not manifest['pathsIncluded'], 'Expected redacted schema-4 diagnostics')
    require(len(json.dumps(bundle).encode()) <= 8 * 1024 * 1024, 'Diagnostic bundle exceeded its limit')
    require(any(e['source'] == 'Server' for e in events), 'No server transitions captured')
    require(any(e['reasonCode'].startswith('Replacement.') for e in events), 'No replacement/rollback evidence captured')
    require(any((e.get('details') or {}).get('report') for e in events), 'No frozen verification report captured')
    sidecar = [e for e in events if e['source'] == 'Sidecar']
    if require_sidecar:
        require(sidecar, 'No authenticated sidecar records mirrored')
        require(all(e['leaseId'] and e['workerId'] and e['attempt'] > 0 for e in sidecar), 'Sidecar correlation is incomplete')
        keys = [(e['workerId'], e['instanceId'], e['sourceSequence']) for e in sidecar]
        require(len(keys) == len(set(keys)), 'Replay duplicated a local record')
        require(any(e['reasonCode'] == 'Worker.ToolsIdentified' and (e.get('details') or {}).get('ffmpegSha256') for e in sidecar), 'Missing tool identity')
        require(any('Transfer' in e['reasonCode'] for e in sidecar), 'No local transfer records captured')
        require(all(p['state'] == 'Collected' for p in manifest['participants'] if p['operatingSystem'] in ('linux', 'windows', 'macos')), 'Some available participants did not acknowledge final collection')
    require(all(job['job']['path'] is None for job in bundle['jobs']), 'Default bundle leaked a media path')
    return {'serverEvents': len(events) - len(sidecar), 'sidecarEvents': len(sidecar),
            'jobs': len(bundle['jobs']), 'participants': manifest['participants'], 'manifestId': manifest['manifestId']}


def collect_capture(api, session_id, root, *, require_sidecar, restart):
    api.post(f'/api/diagnostics/capture/{session_id}/stop')
    deadline = time.monotonic() + 45
    while True:
        participants = api.request(f'/api/diagnostics/capture/{session_id}/participants')
        if all(p['state'] == 'Collected' for p in participants if p['operatingSystem'] in ('linux', 'windows', 'macos')) or time.monotonic() >= deadline:
            break
        time.sleep(.5)
    bundle = api.request(f'/api/diagnostics/capture/{session_id}/bundle')
    save(root / 'diagnostic-bundle.json', bundle)
    evidence = validate_bundle(bundle, require_sidecar=require_sidecar)
    require(api.token not in json.dumps(bundle), 'Diagnostic export leaked the admin credential')
    api.request(f'/api/diagnostics/capture/{session_id}/pin', 'PUT', {'pinned': True})
    session = api.post('/api/diagnostics/capture', {'durationHours': 1, 'includePaths': False})
    restart()
    stopped = api.request('/api/diagnostics/capture')
    require(stopped['id'] == session['id'] and stopped['status'] != 'Recording', 'Capture resumed across restart without consent')
    recovered = api.request(f'/api/diagnostics/capture/{session_id}/bundle')
    require(recovered['events'] == bundle['events'], 'Captured history changed after restart')
    require(next(s for s in api.request('/api/diagnostics/captures') if s['id'] == session_id)['pinned'], 'Pin was not durable')
    return {**evidence, 'restartStopsByDefault': True, 'historySurvivesRestart': True}
