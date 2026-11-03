const test = require('node:test'), assert = require('node:assert/strict');
const fs = require('node:fs'), vm = require('node:vm'), path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../LAMPA/app/src/mc/assets/shell/shell.js'), 'utf8');
function fixture(bridge = true) {
  const saved = { passthrough: false, drc: 2 }, rows = [];
  const context = {
    group() {},
    MC: bridge ? {
      audioPassthrough: () => saved.passthrough,
      setAudioPassthrough: value => { saved.passthrough = value; },
      drcMode: () => saved.drc,
      setDrcMode: value => { saved.drc = value; }
    } : { drcMode: () => saved.drc },
    row(name, hint, value, change) {
      const r = { name, hint, value, change, _repaint() { this.painted = value(); } };
      r._repaint(); rows.push(r); return r;
    }
  };
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf("    group('Звук — все источники');"), source.indexOf("    group('Экран — все источники');")), context);
  return { saved, rows, toggle: () => rows[0].change(1, () => rows[0]._repaint()) };
}
test('launcher setting starts disabled without altering DRC', () => {
  const f = fixture(); assert.equal(f.rows[0].painted, 'Выкл'); assert.equal(f.rows[1].painted, 'Ночная');
});
test('on and off immediately repaint DRC and preserve its saved value', () => {
  const f = fixture(); f.toggle(); assert.equal(f.rows[0].painted, 'Вкл');
  assert.match(f.rows[1].painted, /Ночная.*не применяется/); assert.equal(f.saved.drc, 2);
  f.toggle(); assert.equal(f.rows[0].painted, 'Выкл'); assert.equal(f.rows[1].painted, 'Ночная'); assert.equal(f.saved.drc, 2);
});
test('changing DRC while bypassed keeps the new choice for later', () => {
  const f = fixture(); f.toggle(); f.rows[1].change(1, () => f.rows[1]._repaint());
  assert.equal(f.saved.drc, 0); assert.match(f.rows[1].painted, /Выключена.*не применяется/);
  f.toggle(); assert.equal(f.rows[1].painted, 'Выключена');
});
test('an older native bridge can still render audio settings', () => {
  const f = fixture(false); assert.equal(f.rows[0].painted, 'Выкл'); assert.equal(f.rows[1].painted, 'Ночная');
});

function afrFixture(native = false) {
  const requests = [], choices = []; let row;
  const context = {
    OPT: { afr: { t: 'AFR', o: [['1', 'включена'], ['0', 'выключена']] } },
    MC: { afrMode: () => native, chooseAfrMode: value => { choices.push(value); native = value; } },
    optIndex: () => 0, // A stale server cache still says enabled.
    row(title, hint, value, change) { row = { value, change }; },
    devSet(key, value, done) { requests.push({ key, value, native }); done(); }
  };
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf('    function opt(key, extra)'), source.indexOf('    function ytLabel()')) + "opt('afr');", context);
  return { row, requests, choices };
}
test('launcher AFR displays a newer native-player choice over stale server data', () => {
  const f = afrFixture(false); assert.equal(f.row.value(), 'выключена');
});
test('changing AFR in the launcher updates the same native setting before syncing', () => {
  const f = afrFixture(false); f.row.change(1, () => {});
  assert.deepEqual(f.choices, [true]); assert.deepEqual(f.requests, [{ key: 'afr', value: '1', native: true }]);
  assert.equal(f.row.value(), 'включена');
});
