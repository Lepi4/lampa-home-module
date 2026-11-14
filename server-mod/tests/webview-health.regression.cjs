const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const root = path.resolve(__dirname, '../..');
const read = name => fs.readFileSync(path.join(root, 'LAMPA/app/src', name), 'utf8');
const state = read('main/java/top/rootu/lampa/McDocumentState.kt');
const probe = state.match(/const val HEALTH_PROBE = "([^"]+)"/)[1];

test('working Lampa accepts commands', () => {
    const Lampa = { Activity: {}, Controller: {} };
    assert.equal(vm.runInNewContext(probe, { window: { appready: true, Lampa }, Lampa }), true);
});
for (const [name, window] of [
    ['certificate error page', {}],
    ['blank page with a stale ready flag', { appready: true }],
    ['partially loaded app', { appready: true, Lampa: {} }],
    ['startup before appready', { Lampa: { Activity: {}, Controller: {} } }]
]) test(name + ' cannot report healthy', () => {
    assert.equal(vm.runInNewContext(probe, { window, Lampa: window.Lampa }), false);
});

test('both startup and continuous probes use application health', () => {
    const main = read('main/java/top/rootu/lampa/MainActivity.kt');
    assert.equal((main.match(/evaluateJavascript\(McDocumentState.HEALTH_PROBE\)/g) || []).length, 2);
    assert.ok(!main.includes('evaluateJavascript("1")'));
});

test('SSL rejection preserves the current document and does not bypass TLS', () => {
    const sys = read('main/java/top/rootu/lampa/browser/SysView.kt');
    const callback = sys.slice(sys.indexOf('override fun onReceivedSslError'), sys.indexOf('if (BuildConfig.DEBUG)', sys.indexOf('override fun onReceivedSslError')));
    assert.ok(callback.includes('handler?.cancel()'));
    assert.ok(!/loadData|loadUrl|\.proceed\(/.test(callback));
    assert.ok(callback.includes('Log.w("HomeWeb"'));
    assert.ok(!callback.includes('$error"')); // Signed URL/certificate data must not enter release logs.
});

test('wake reporting freezes and attaches the attempt owner', () => {
    const wake = read('main/java/top/rootu/lampa/WakeTrace.kt');
    assert.ok(wake.includes('ProfileRuntime.requestHeaders(ProfileRuntime.ownerFor(root)).toMap()'));
    assert.ok(wake.includes('context.headers.forEach'));
    assert.ok(wake.includes('.put("attempt", context.id)'));
    assert.ok(wake.includes('.put("dev", context.dev)'));
});
