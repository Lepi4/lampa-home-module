// AUTH-02: the browser session endpoint must be reachable before middleware auth
// and must still enforce Guard/admin/PIN inside the controller.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const read = (name) => fs.readFileSync(path.join(__dirname, '..', 'HomeMedia', name), 'utf8');
const modInit = read('ModInit.cs');
const panel = read('Panel.cs');
const profiles = read('Profiles.cs');
const controller = read('ProfilesController.cs');

assert.ok(modInit.includes('requestedPath == "/home/admin/session"'), 'session route is not public');
assert.ok(!modInit.includes('requestedPath.StartsWith("/home/admin/") && !publicProfileRoute'), 'broad admin route leaked');
assert.ok(panel.includes('Profiles.BrowserDevice(HttpContext)'), 'browser session does not use the stable browser device');
assert.ok(panel.includes('var current = Profiles.FromRequest(HttpContext);'), 'browser session does not validate the current session');
assert.ok(panel.includes('current != null) return current.Admin'), 'non-admin sessions are not rejected');
assert.ok(panel.includes('Guard.Ok(Request)'), 'browser session lost the LAN guard');
assert.ok(profiles.includes('public static string BrowserDevice(HttpContext ctx)'), 'BrowserDevice helper is missing');
assert.ok(profiles.includes('public static void SetSessionCookie(HttpContext ctx, string token)'), 'SetSessionCookie helper is missing');
assert.ok(controller.includes('Profiles.GenericBrowserDevice(requested)'), 'login still accepts shared generic device ids');
assert.ok(modInit.includes('requestedPath == "/home/session/entry"'), 'entry route is not public');
assert.ok(modInit.includes('htmlNavigation') && modInit.includes('text/html'), 'HTML service navigation is not redirected');
assert.ok(modInit.includes('/home/session/entry?next='), 'service redirect does not point at the entry endpoint');
assert.ok(controller.includes('Route("home/session/entry")'), 'entry endpoint is missing');
assert.ok(controller.includes('static bool SafeNext(string value)'), 'next validation helper is missing');
assert.ok(controller.includes('Uri.EscapeDataString(target)'), 'entry redirect does not encode next');
assert.ok(controller.includes("value.Contains('\\\\')") || controller.includes('value.Contains(\'\\\\\')'), 'next validation does not reject backslashes');
assert.ok(controller.includes('value.Contains("..")'), 'next validation does not reject parent traversal');
const profilesHtml = read('profiles.html');
assert.ok(profilesHtml.includes('function safeNext()'), 'profiles page does not validate next');
assert.ok(profilesHtml.includes('location.replace(n)'), 'profiles page does not return to the service after login');
assert.ok(fs.readFileSync(path.join(__dirname, '..', 'HomeMedia', 'panel.html'), 'utf8').includes('/profiles?next=/home'), 'panel login link does not return to the panel');
console.log('PASS AUTH-02 route/helper contract is present and narrow');
console.log('PASS AUTH-03 HTML entry, next validation and service redirect are present');
