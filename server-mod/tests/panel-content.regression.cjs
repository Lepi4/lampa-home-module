// RELEASE-01: the panel must stay valid UTF-8 with all service links.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const file = path.join(__dirname, '..', 'HomeMedia', 'panel.html');
const raw = fs.readFileSync(file);
const text = raw.toString('utf8');
assert.equal(Buffer.compare(raw, Buffer.from(text, 'utf8')), 0, 'panel.html is not valid UTF-8');
assert.ok(!text.includes('\uFFFD'), 'panel.html contains replacement characters');
assert.ok(text.includes('Lampa Home · сервер'), 'panel title is corrupted');
assert.ok(text.includes('Профили и вход'), 'Russian header text is corrupted');
assert.ok(text.includes('/home/services/channel-manager/'), 'Channel Manager link is missing');
assert.ok(text.includes('/home/services/torrserver/'), 'TorrServer link is missing');
assert.ok(text.includes('/home/services/tvheadend/gui/'), 'Tvheadend link is missing');
const serviceLinks = new Set(
  (text.match(/\/home\/services\/(channel-manager|torrserver|tvheadend)\//g) || [])
);
assert.equal(serviceLinks.size, 3, 'panel must expose all three service links');

const profilesFile = path.join(__dirname, '..', 'HomeMedia', 'profiles.html');
const profiles = fs.readFileSync(profilesFile, 'utf8');
for (const [name, body] of [['panel.html', text], ['profiles.html', profiles]]) {
  assert.ok(!body.includes('web_panel'), `${name} still uses the shared web_panel device`);
  assert.ok(!body.includes('web_profiles'), `${name} still uses the shared web_profiles device`);
  assert.ok(!body.includes('adminToken'), `${name} still keeps a JS admin token`);
  assert.ok(!body.includes('X-Home-Session'), `${name} still sends a JS session header`);
}
assert.ok(text.includes('/home/admin/session'), 'panel must check the browser session endpoint');
console.log('PASS panel.html is valid UTF-8 and exposes Channel Manager, TorrServer and Tvheadend');
console.log('PASS panel/profiles use cookie sessions without shared device ids or JS tokens');
