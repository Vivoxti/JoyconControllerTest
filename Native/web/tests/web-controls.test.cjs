// Run with node --test Native/web/tests/web-controls.test.cjs; no Bluetooth hardware required.
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const flush = () => new Promise(resolve => setImmediate(resolve));

class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.listeners = {}; this.disabled = false; }
  appendChild(child) { child.parent = this; this.children.push(child); return child; }
  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }
  removeEventListener(type, fn) { this.listeners[type] = (this.listeners[type] || []).filter(f => f !== fn); }
  setAttribute() {}
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(c => c !== this); }
  click() { if (!this.disabled) this.dispatch('click'); }
  dispatch(type) { for (const fn of this.listeners[type] || []) fn({ target: this }); }
}
function setup({ secure = true, bluetooth = true, policy = true, android = false, userAgent } = {}) {
  const document = new Element('document');
  document.body = new Element('body');
  document.hidden = false;
  document.baseURI = 'https://test.example/game/';
  document.getElementById = () => document.body;
  document.createElement = tag => new Element(tag);
  document.permissionsPolicy = { allowsFeature: () => policy };
  const window = new Element('window');
  window.isSecureContext = secure;
  let resolvePicker, rejectPicker, inGesture = false;
  const calls = [], revoked = [], clickedLinks = [];
  const create = document.createElement;
  document.createElement = tag => {
    const element = create(tag);
    if (tag === 'a') element.addEventListener('click', () => clickedLinks.push(element.href));
    return element;
  };
  const navigator = { userActivation: { isActive: true }, platform: '', maxTouchPoints: 0,
    userAgent: userAgent || (android ? 'Chrome Android' : 'Chrome Windows') };
  if (bluetooth) navigator.bluetooth = {
    requestDevice(options) {
      calls.push({ options, inGesture });
      return new Promise((resolve, reject) => { resolvePicker = resolve; rejectPicker = reject; });
    }
  };
  const location = { href: 'https://test.example/game/index.html' };
  const context = vm.createContext({ document, window, navigator, location, TextEncoder, Uint8Array, DataView, ArrayBuffer,
    console: { log() {}, warn() {}, error() {} }, setTimeout, Blob,
    URL: class extends URL { static createObjectURL() { return 'blob:test-' + Math.random(); } static revokeObjectURL(url) { revoked.push(url); } },
    UTF8ToString: value => value, LibraryManager: { library: {} }, autoAddDeps() {},
    mergeInto: (library, definitions) => Object.assign(library, definitions) });
  for (const file of ['Joycon2WebBluetooth.jslib', 'Joycon2WebControls.jslib']) {
    vm.runInContext(fs.readFileSync(path.resolve(__dirname, '../../../Assets/Joycon2/Plugins/WebGL', file), 'utf8'), context);
  }
  const lib = context.LibraryManager.library;
  context.Jc2 = lib.$Jc2;
  context.Jc2UI = lib.$Jc2UI;
  context._jc2web_disconnect = lib.jc2web_disconnect;
  lib.jc2web_init();
  const options = side => JSON.stringify({ filters: [{ manufacturerData: [{ companyIdentifier: 1363,
    dataPrefix: [0, 0, 0, 0, 0, side === 'L' ? 103 : 102, 32], mask: [0, 0, 0, 0, 0, 255, 255] }] },
    { services: ['test-service'], name: 'Joy-Con 2 (' + side + ')' }], optionalServices: ['test-service'] });
  lib.jc2web_ui_init(options('L'), options('R'));
  const update = () => lib.jc2web_ui_update(1, 1, 'running', 'details', 0);
  update();
  const tap = button => { inGesture = true; try { button.click(); } finally { inGesture = false; } };
  const device = name => {
    const d = new Element('device'); d.name = name;
    d.gatt = { connected: false, connect: async () => { d.gatt.connected = true; return d.gatt; },
      disconnect: () => { d.gatt.connected = false; d.dispatch('gattserverdisconnected'); } };
    return d;
  };
  return { context, location, lib, ui: context.Jc2UI, pipe: context.Jc2, document, calls, revoked, clickedLinks, tap, update, device,
    pick: d => resolvePicker(d), cancel: error => rejectPicker(error) };
}

test('L/R choices run synchronously inside separate gestures and preserve device identity', async () => {
  const s = setup();
  s.tap(s.ui.left);
  assert.equal(s.calls[0].inGesture, true);
  assert.equal(s.lib.jc2web_is_picking(), 1);
  s.tap(s.ui.right);
  assert.equal(s.calls.length, 1, 'only one chooser at a time');
  const l = s.device('Joy-Con 2 (L)');
  s.pick(l); await flush(); s.update();
  s.tap(s.ui.right);
  assert.equal(s.calls[1].inGesture, true);
  assert.equal(s.calls[1].options.filters[0].manufacturerData[0].dataPrefix[5], 102);
  const r = s.device('Joy-Con 2 (R)');
  s.pick(r); await flush();
  const picked = s.pipe.events.filter(e => e.type === 10);
  assert.deepEqual(Array.from(picked, e => e.tag), [1, 2]);
  assert.notEqual(picked[0].address, picked[1].address);
  assert.equal(s.pipe.idOf(l), picked[0].address);
});

test('cancel and permission denial display feedback and allow another tap', async () => {
  const s = setup();
  for (const [name, status] of [['NotFoundError', 1], ['NotAllowedError', 2], ['SecurityError', 2]]) {
    s.tap(s.ui.left);
    s.cancel({ name, message: 'test error' }); await flush(); s.update();
    assert.equal(s.pipe.events.at(-1).status, status);
    assert.equal(s.ui.left.disabled, false);
    assert.match(s.ui.message.textContent, status === 1 ? /try again/ : /permissions/);
  }
});

test('missing activation arms one fresh gesture and shutdown removes the gesture listeners', () => {
  const s = setup();
  s.context.navigator.userActivation.isActive = false;
  s.lib.jc2web_request_device(JSON.stringify({ filters: [{ name: 'L' }] }), 1);
  assert.equal(s.calls.length, 0);
  assert.equal(s.lib.jc2web_is_picking(), 1);
  assert.equal(s.context.window.listeners.pointerup.length, 1);
  s.lib.jc2web_shutdown();
  assert.equal(s.context.window.listeners.pointerup.length, 0);
  assert.equal(s.lib.jc2web_is_picking(), 0);
});

test('HTTPS, unsupported browser and iframe policy failures are visible with disabled controls', () => {
  for (const [options, message] of [[{ secure: false }, /HTTPS/], [{ bluetooth: false }, /Chrome/], [{ policy: false }, /hosting page/]]) {
    const s = setup(options);
    assert.match(s.ui.message.textContent, message);
    s.tap(s.ui.left);
    assert.equal(s.calls.length, 0);
  }
});

test('shutdown ignores a pending chooser result from the old lifecycle', async () => {
  const s = setup();
  s.tap(s.ui.left);
  s.lib.jc2web_shutdown();
  s.lib.jc2web_init();
  s.pick(s.device('old')); await flush();
  assert.equal(s.pipe.events.length, 0);
  assert.equal(Object.keys(s.pipe.devices).length, 0);
  assert.equal(s.lib.jc2web_is_picking(), 0);
});

test('background closes live links and queues removal; chooser visibility does not interrupt selection', async () => {
  const s = setup();
  const id = s.pipe.idOf(s.device('L'));
  s.lib.jc2web_connect(id); await flush();
  s.document.hidden = true; s.document.dispatch('visibilitychange');
  assert.equal(s.pipe.devices[id].live, false);
  assert.equal(s.pipe.devices[id].device.gatt.connected, false);
  assert.equal(s.pipe.events.at(-1).type, 5);
  assert.match(s.ui.message.textContent, /reconnect/);
  s.document.hidden = false; s.update(); s.tap(s.ui.left);
  s.document.hidden = true; s.document.dispatch('visibilitychange');
  assert.equal(s.lib.jc2web_is_picking(), 1);
  s.pick(s.device('R')); await flush();
  assert.equal(s.pipe.events.at(-1).type, 10);
});

test('late GATT connect after a background disconnect is closed without Connected event', async () => {
  const s = setup();
  const d = s.device('L'); let finish;
  d.gatt.connect = () => new Promise(resolve => { finish = () => { d.gatt.connected = true; resolve(d.gatt); }; });
  const id = s.pipe.idOf(d);
  s.lib.jc2web_connect(id);
  s.lib.jc2web_disconnect(id);
  finish(); await flush();
  assert.equal(d.gatt.connected, false);
  assert.equal(s.pipe.events.some(e => e.type === 3), false);
});

test('record actions are drained once and downloads revoke old blobs on replacement and teardown', () => {
  const s = setup();
  s.tap(s.ui.record);
  assert.equal(s.lib.jc2web_ui_actions(), 1);
  assert.equal(s.lib.jc2web_ui_actions(), 0);
  s.lib.jc2web_ui_dump('first.tsv', '# data');
  assert.equal(s.ui.download.disabled, false);
  const first = s.ui.dumpUrl;
  s.lib.jc2web_ui_dump('second.tsv', '# next');
  assert.equal(s.revoked[0], first);
  s.lib.jc2web_ui_shutdown();
  assert.equal(s.revoked.length, 2);
  assert.equal(s.document.listeners.visibilitychange.length, 0);
});

test('Android Boost targets the helper with an APK fallback and keeps a brief app hop connected', async () => {
  const s = setup({ android: true });
  const id = s.pipe.idOf(s.device('L')); s.lib.jc2web_connect(id); await flush();
  s.tap(s.ui.boost);
  assert.match(s.clickedLinks[0], /^intent:\/\/start\?service=test-service#Intent;/);
  assert.ok(s.clickedLinks[0].includes(encodeURIComponent('https://test.example/game/Joycon2Boost-test.apk')));
  s.document.hidden = true; s.document.dispatch('visibilitychange');
  assert.equal(s.pipe.devices[id].live, true);
  s.document.hidden = false; s.document.dispatch('visibilitychange');
  assert.equal(s.pipe.devices[id].live, true);
  assert.equal(s.ui.boostLaunchingUntil, 0);
});

test('a long boost permission/install hop closes links on return; desktop does not show Android Boost', async () => {
  assert.equal(setup().ui.boost, undefined);
  const s = setup({ android: true });
  const id = s.pipe.idOf(s.device('L')); s.lib.jc2web_connect(id); await flush();
  s.tap(s.ui.boost);
  s.document.hidden = true; s.document.dispatch('visibilitychange');
  s.ui.boostHiddenAt -= 3000;
  s.document.hidden = false; s.document.dispatch('visibilitychange');
  assert.equal(s.pipe.devices[id].live, false);
  assert.equal(s.pipe.events.at(-1).type, 5);
});

const iPhoneSafari = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.7 Mobile/15E148 Safari/604.1';
const iPhoneBluefy = iPhoneSafari + ' Bluefy/3.9.3';

test('Bluefy: chooser filters by exact name only, keeps optionalServices; a numeric cancel counts as cancel', async () => {
  const s = setup({ userAgent: iPhoneBluefy });
  s.tap(s.ui.left);
  assert.deepEqual(JSON.parse(JSON.stringify(s.calls[0].options)),
    { filters: [{ name: 'Joy-Con 2 (L)' }], optionalServices: ['test-service'] });
  s.cancel(2); await flush(); s.update();
  assert.equal(s.pipe.events.at(-1).status, 1);
  assert.match(s.ui.message.textContent, /all Bluetooth devices/);
  s.tap(s.ui.right);
  assert.deepEqual(JSON.parse(JSON.stringify(s.calls[1].options)), { acceptAllDevices: true, optionalServices: ['test-service'] });
  s.cancel(2); await flush(); s.update();
  assert.match(s.ui.message.textContent, /try again/);
  assert.equal(setup().pipe.toRequestOptions(JSON.stringify({ filters: [{ manufacturerData: [] }, { name: 'x' }] })).filters.length, 2);
});

test('iOS without Web Bluetooth offers Bluefy; the iPhone UA is not macOS for the rate helper', () => {
  const s = setup({ userAgent: iPhoneSafari, bluetooth: false });
  assert.match(s.ui.message.textContent, /Bluefy/);
  const open = s.ui.root.children[1].children.find(b => b.textContent === 'Open in Bluefy');
  open.click();
  assert.equal(s.location.href, 'bluefy://open?url=' + encodeURIComponent('https://test.example/game/index.html'));
  assert.equal(setup({ userAgent: iPhoneBluefy }).lib.jc2web_os(), 0);
  const iPad = setup({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Bluefy/3.9.3' });
  iPad.context.navigator.platform = 'MacIntel'; iPad.context.navigator.maxTouchPoints = 5;
  assert.equal(iPad.lib.jc2web_os(), 0);
  assert.equal(setup({ userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/141' }).lib.jc2web_os(), 2);
  assert.equal(setup().ui.root.children[1].children.some(b => b.textContent === 'Open in Bluefy'), false);
});
