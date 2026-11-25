import { test } from 'node:test'
import assert from 'node:assert/strict'
import { en } from './i18n/en.ts'
import { APP_TITLE, pageTitle, settingsRoomFromPath, showsActiveWork } from './route-meta.ts'

test('every top-level page names itself before the application', () => {
  assert.equal(pageTitle('/', en), 'Dashboard · Optimisarr')
  assert.equal(pageTitle('/libraries', en), 'Libraries · Optimisarr')
  assert.equal(pageTitle('/inventory', en), 'Inventory · Optimisarr')
  assert.equal(pageTitle('/queue', en), 'Queue · Optimisarr')
  assert.equal(pageTitle('/quarantine', en), 'Quarantine · Optimisarr')
  assert.equal(pageTitle('/schedule', en), 'Schedule · Optimisarr')
  assert.equal(pageTitle('/settings', en), 'Settings · Optimisarr')
})

test('a page inside a section names itself, then the section it belongs to', () => {
  assert.equal(pageTitle('/libraries/new', en), 'Add library · Libraries · Optimisarr')
  assert.equal(pageTitle('/libraries/new/files', en), 'Add library · Libraries · Optimisarr')
  assert.equal(pageTitle('/libraries/7/configure', en), 'Configure · Libraries · Optimisarr')
  assert.equal(pageTitle('/libraries/7/configure/video', en), 'Configure · Libraries · Optimisarr')
  assert.equal(pageTitle('/libraries/7/quality-check', en), 'Personal quality check · Libraries · Optimisarr')
  assert.equal(pageTitle('/libraries/7/duplicates', en), 'Find duplicates · Libraries · Optimisarr')
  assert.equal(pageTitle('/inventory/optimise/42', en), 'Optimise this file · Inventory · Optimisarr')
  assert.equal(pageTitle('/inventory/duplicates', en), 'Find duplicates · Inventory · Optimisarr')
  assert.equal(pageTitle('/quarantine/12', en), 'Replacement review · Quarantine · Optimisarr')
})

test('a settings room is titled by the room, inside Settings', () => {
  assert.equal(pageTitle('/settings/encoding', en), 'Encoding · Settings · Optimisarr')
  assert.equal(pageTitle('/settings/media-servers', en), 'Media servers · Settings · Optimisarr')
  assert.equal(pageTitle('/settings/system#diagnostic-capture', en), 'System · Settings · Optimisarr')
  // An unknown room shows the Settings overview, so it is titled as the overview.
  assert.equal(pageTitle('/settings/nowhere', en), 'Settings · Optimisarr')
})

test('former routes are titled by the page they land on', () => {
  assert.equal(pageTitle('/candidates', en), 'Inventory · Optimisarr')
  assert.equal(pageTitle('/tools', en), 'System · Settings · Optimisarr')
})

test('an unknown route is titled as the dashboard it falls back to', () => {
  assert.equal(pageTitle('/nowhere', en), 'Dashboard · Optimisarr')
  assert.equal(pageTitle('', en), 'Dashboard · Optimisarr')
})

test('the application title stands alone when no page is shown', () => {
  assert.equal(APP_TITLE, 'Optimisarr')
})

test('settings rooms are read from their URL slug', () => {
  assert.equal(settingsRoomFromPath('/settings'), null)
  assert.equal(settingsRoomFromPath('/settings/'), null)
  assert.equal(settingsRoomFromPath('/settings/super-resolution'), 'superResolution')
  assert.equal(settingsRoomFromPath('/settings/super-resolution/'), 'superResolution')
  assert.equal(settingsRoomFromPath('/settings/download-managers?tab=1'), 'downloads')
  assert.equal(settingsRoomFromPath('/settings/system#diagnostic-capture'), 'system')
  assert.equal(settingsRoomFromPath('/settings/unknown'), null)
  assert.equal(settingsRoomFromPath('/settingsencoding'), null)
  assert.equal(settingsRoomFromPath('encoding'), null)
  assert.equal(settingsRoomFromPath('/tools'), 'system')
  assert.equal(settingsRoomFromPath('/tools?from=old'), 'system')
  assert.equal(settingsRoomFromPath('/toolshed'), null)
})

test('trailing slashes, queries and fragments keep the route title intact', () => {
  assert.equal(pageTitle('/quarantine/12/', en), 'Replacement review · Quarantine · Optimisarr')
  assert.equal(pageTitle('/quarantine/12?from=dashboard', en), 'Replacement review · Quarantine · Optimisarr')
  assert.equal(pageTitle('/inventory/optimise/42?ref=list', en), 'Optimise this file · Inventory · Optimisarr')
  assert.equal(pageTitle('/settings/encoding/', en), 'Encoding · Settings · Optimisarr')
  assert.equal(pageTitle('/settings/encoding?focus=1', en), 'Encoding · Settings · Optimisarr')
})

test('active work is already shown on Dashboard, Queue and fallback routes', () => {
  assert.equal(showsActiveWork('/'), true)
  assert.equal(showsActiveWork('/?ref=email'), true)
  assert.equal(showsActiveWork('/nowhere'), true)
  assert.equal(showsActiveWork(''), true)
  assert.equal(showsActiveWork('/queue'), true)
  assert.equal(showsActiveWork('/queue/12'), true)
  assert.equal(showsActiveWork('/queue?filter=active'), true)
  assert.equal(showsActiveWork('/queue#list'), true)
  assert.equal(showsActiveWork('/libraries'), false)
  assert.equal(showsActiveWork('/inventory'), false)
  assert.equal(showsActiveWork('/candidates'), false)
  assert.equal(showsActiveWork('/quarantine'), false)
  assert.equal(showsActiveWork('/schedule'), false)
  assert.equal(showsActiveWork('/settings'), false)
  assert.equal(showsActiveWork('/settings/encoding'), false)
  assert.equal(showsActiveWork('/tools'), false)
})
