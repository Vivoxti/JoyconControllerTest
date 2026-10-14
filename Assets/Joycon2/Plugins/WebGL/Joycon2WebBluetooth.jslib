// Joy-Con 2 Web Bluetooth transport: a thin byte pipe, the protocol lives in C# (Joycon2.Runtime).
// Same model as Native/windows: every result is queued as an event, C# drains them with jc2web_poll_event
// once per frame, no callbacks cross into C#. Event layout matches Joycon2TransportEventType / NativeEvent.
var Joycon2WebBluetooth = {
  $Jc2: {
    EV_LOG: 1, EV_CONNECTED: 3, EV_CONNECT_FAILED: 4, EV_DISCONNECTED: 5, EV_SUBSCRIBED: 6,
    EV_NOTIFICATION: 7, EV_WRITE_FAILED: 8, EV_DEVICE_PICKED: 10,
    MAX_QUEUED_NOTIFICATIONS: 2048, // the page may stop rendering (hidden tab) while notifications keep coming

    events: [],
    notificationsQueued: 0,
    droppedNotifications: 0,
    devices: {},       // id -> state; the id doubles as address and connection handle
    nextId: 1,
    requesting: false,
    armedGesture: null,
    running: false,
    lifecycle: 0,
    pickerMessage: '',
    lastError: '',

    push: function (type, handle, tag, status, address, bytes) {
      if (type === Jc2.EV_NOTIFICATION) {
        if (Jc2.notificationsQueued >= Jc2.MAX_QUEUED_NOTIFICATIONS) {
          if (Jc2.droppedNotifications++ === 0) console.warn('[Joycon2] notification queue full, dropping (page not updating?)');
          return;
        }
        Jc2.notificationsQueued++;
      }
      Jc2.events.push({ type: type, handle: handle, tag: tag, status: status, address: address, bytes: bytes });
    },

    log: function (level, text) {
      if (level >= 1) Jc2.lastError = text;
      (level >= 2 ? console.error : level === 1 ? console.warn : console.log)('[Joycon2/web] ' + text);
      Jc2.push(Jc2.EV_LOG, 0, 0, level, 0, new TextEncoder().encode(text));
    },

    describe: function (err) {
      return err ? (err.name ? err.name + ': ' : '') + (err.message || String(err)) : 'unknown error';
    },

    // Web Bluetooth wants BufferSource values; JSON from C# has plain arrays.
    toRequestOptions: function (json) {
      var options = JSON.parse(json);
      (options.filters || []).forEach(function (filter) {
        (filter.manufacturerData || []).forEach(function (m) {
          if (m.dataPrefix) m.dataPrefix = new Uint8Array(m.dataPrefix);
          if (m.mask) m.mask = new Uint8Array(m.mask);
        });
      });
      return options;
    },

    idOf: function (device) {
      for (var id in Jc2.devices) if (Jc2.devices[id].device === device) return Number(id);
      var newId = Jc2.nextId++;
      Jc2.devices[newId] = { device: device, gen: 0, live: false, chain: Promise.resolve(), chars: {}, listeners: {} };
      device.addEventListener('gattserverdisconnected', function () {
        var dev = Jc2.devices[newId];
        if (!dev || !dev.live) return; // we disconnected it ourselves
        dev.live = false;
        dev.gen++;
        dev.chars = {};
        Jc2.push(Jc2.EV_DISCONNECTED, newId, 0, 0, newId, null);
      });
      return newId;
    },

    request: function (options, tag) {
      Jc2.requesting = true;
      Jc2.lastError = '';
      var lifecycle = Jc2.lifecycle;
      Jc2.pickerMessage = 'Choose a Joy-Con in the Bluetooth list.';
      var picked;
      try {
        picked = navigator.bluetooth.requestDevice(options);
      } catch (err) {
        picked = Promise.reject(err);
      }
      picked.then(function (device) {
        if (!Jc2.running || Jc2.lifecycle !== lifecycle) return;
        var id = Jc2.idOf(device);
        Jc2.pickerMessage = 'Joy-Con selected. Connecting…';
        Jc2.push(Jc2.EV_DEVICE_PICKED, 0, tag, 0, id, new TextEncoder().encode(device.name || ''));
      }, function (err) {
        if (!Jc2.running || Jc2.lifecycle !== lifecycle) return;
        if (err && err.name === 'SecurityError' &&
            (!navigator.userActivation || !navigator.userActivation.isActive) && Jc2.armGesture(options, tag)) return;
        // NotFoundError = the user closed the chooser (or Bluetooth is off / no adapter: see the message).
        var status = err && err.name === 'NotFoundError' ? 1 : 2;
        Jc2.pickerMessage = status === 1
          ? 'No device selected. Check Bluetooth, hold SYNC and try again.'
          : 'Bluetooth access failed. Check Chrome permissions for nearby devices. ' + Jc2.describe(err);
        Jc2.push(Jc2.EV_DEVICE_PICKED, 0, tag, status, 0, new TextEncoder().encode(Jc2.describe(err)));
      }).then(function () { if (Jc2.lifecycle === lifecycle) Jc2.requesting = false; });
    },

    // requestDevice needs transient user activation. C# normally calls us within a frame of the click/key,
    // which is well inside Chrome's window; if not, open the chooser straight from the next DOM gesture.
    armGesture: function (options, tag) {
      if (Jc2.armedGesture) return false;
      var fire = function () {
        Jc2.disarmGesture();
        Jc2.request(options, tag);
      };
      Jc2.armedGesture = fire;
      Jc2.pickerMessage = 'Tap the game again to open the Bluetooth list.';
      window.addEventListener('pointerup', fire, true);
      window.addEventListener('keyup', fire, true);
      Jc2.log(1, 'the browser wants a fresh click: click the game (or press a key) to open the Bluetooth chooser');
      return true;
    },

    disarmGesture: function () {
      if (!Jc2.armedGesture) return;
      window.removeEventListener('pointerup', Jc2.armedGesture, true);
      window.removeEventListener('keyup', Jc2.armedGesture, true);
      Jc2.armedGesture = null;
    },

    // GATT operations strictly one at a time per device: Chrome rejects overlapping ones
    // ("GATT operation already in progress"). Ops from an older connection are skipped.
    enqueue: function (dev, op) {
      var gen = dev.gen;
      dev.chain = dev.chain.then(function () {
        if (dev.gen === gen && dev.live) return op();
      }).catch(function () {});
    },

    characteristic: function (dev, service, uuid) {
      var key = service + '|' + uuid;
      if (!dev.chars[key]) {
        var attempt = function (left) {
          return dev.device.gatt.getPrimaryService(service)
            .then(function (s) { return s.getCharacteristic(uuid); })
            .catch(function (err) {
              // Right after connect Windows may still be discovering services.
              if (left <= 0 || !dev.live) throw err;
              return new Promise(function (r) { setTimeout(r, 300); }).then(function () { return attempt(left - 1); });
            });
        };
        dev.chars[key] = attempt(3).catch(function (err) {
          delete dev.chars[key];
          throw err;
        });
      }
      return dev.chars[key];
    },
  },

  jc2web_version: function () {
    return 1;
  },

  jc2web_init: function () {
    if (!window.isSecureContext) {
      console.warn('[Joycon2] Web Bluetooth needs HTTPS or localhost; Joy-Con 2 support is off.');
      return -2;
    }
    if (!navigator.bluetooth || !navigator.bluetooth.requestDevice) {
      console.warn('[Joycon2] this browser has no Web Bluetooth (use Chrome or Edge); Joy-Con 2 support is off.');
      return -1;
    }
    var policy = document.permissionsPolicy || document.featurePolicy;
    if (policy && policy.allowsFeature && !policy.allowsFeature('bluetooth')) {
      console.warn('[Joycon2] Bluetooth access is blocked by the hosting page Permissions Policy.');
      return -3;
    }
    Jc2.running = true;
    Jc2.lifecycle++;
    return 0;
  },

  jc2web_shutdown: function () {
    Jc2.running = false;
    Jc2.lifecycle++;
    Jc2.requesting = false;
    Jc2.disarmGesture();
    for (var id in Jc2.devices) {
      var dev = Jc2.devices[id];
      dev.live = false;
      dev.gen++;
      dev.chars = {};
      if (dev.device.gatt.connected) dev.device.gatt.disconnect();
    }
    Jc2.events = [];
    Jc2.notificationsQueued = 0;
  },

  jc2web_request_device: function (jsonPtr, tag) {
    var options;
    try {
      options = Jc2.toRequestOptions(UTF8ToString(jsonPtr));
    } catch (err) {
      Jc2.push(Jc2.EV_DEVICE_PICKED, 0, tag, 2, 0, new TextEncoder().encode('bad request: ' + Jc2.describe(err)));
      return;
    }
    if (Jc2.requesting || Jc2.armedGesture) {
      Jc2.push(Jc2.EV_DEVICE_PICKED, 0, tag, 2, 0, new TextEncoder().encode('the Bluetooth chooser is already open'));
      return;
    }
    if (navigator.userActivation && !navigator.userActivation.isActive && Jc2.armGesture(options, tag)) return;
    Jc2.request(options, tag);
  },

  jc2web_poll_event: function (evPtr, payloadPtr, capacity) {
    if (Jc2.events.length === 0) return 0;
    var e = Jc2.events.shift();
    if (e.type === Jc2.EV_NOTIFICATION) Jc2.notificationsQueued--;
    var length = e.bytes ? e.bytes.length : 0;
    var i = evPtr >> 2;
    HEAP32[i] = e.type;
    HEAP32[i + 1] = e.handle;
    HEAP32[i + 2] = e.tag;
    HEAP32[i + 3] = e.status;
    HEAP32[i + 4] = e.address >>> 0; // ulong address: ids are small, high word stays 0
    HEAP32[i + 5] = 0;
    HEAP32[i + 6] = 0;               // rssi: not exposed by requestDevice
    HEAP32[i + 7] = length;
    if (length > 0 && capacity > 0) HEAPU8.set(e.bytes.subarray(0, Math.min(length, capacity)), payloadPtr);
    return 1;
  },

  jc2web_connect: function (id) {
    var dev = Jc2.devices[id];
    if (!dev || !Jc2.running) return -1;
    dev.gen++;
    dev.chars = {};
    dev.listeners = {};
    dev.writeFailures = 0;
    dev.live = true;
    var gen = dev.gen;
    dev.device.gatt.connect().then(function () {
      if (dev.gen !== gen) {
        if (!dev.live && dev.device.gatt.connected) dev.device.gatt.disconnect();
        return;
      }
      // Chrome does not expose the ATT MTU; Windows negotiates one large enough for 63-byte reports.
      Jc2.push(Jc2.EV_CONNECTED, id, 0, 0, id, null);
    }, function (err) {
      if (dev.gen !== gen) return;
      dev.live = false;
      Jc2.log(1, 'connect failed: ' + Jc2.describe(err));
      Jc2.push(Jc2.EV_CONNECT_FAILED, id, 0, 1, id, null);
    });
    return id;
  },

  jc2web_disconnect: function (handle) {
    var dev = Jc2.devices[handle];
    if (!dev) return;
    dev.live = false;
    dev.gen++;
    dev.chars = {};
    dev.listeners = {};
    if (dev.device.gatt.connected) dev.device.gatt.disconnect();
  },

  jc2web_subscribe: function (handle, servicePtr, characteristicPtr, tag) {
    var dev = Jc2.devices[handle];
    if (!dev) return;
    var service = UTF8ToString(servicePtr), uuid = UTF8ToString(characteristicPtr);
    var gen = dev.gen;
    Jc2.enqueue(dev, function () {
      return Jc2.characteristic(dev, service, uuid).then(function (c) {
        var key = service + '|' + uuid;
        if (!dev.listeners[key]) {
          var listener = function (ev) {
            if (dev.gen !== gen) return;
            var v = ev.target.value;
            Jc2.push(Jc2.EV_NOTIFICATION, handle, tag, 0, handle, new Uint8Array(v.buffer, v.byteOffset, v.byteLength).slice());
          };
          dev.listeners[key] = listener;
          c.addEventListener('characteristicvaluechanged', listener);
        }
        return c.startNotifications();
      }).then(function () {
        if (dev.gen === gen) Jc2.push(Jc2.EV_SUBSCRIBED, handle, tag, 0, handle, null);
      }, function (err) {
        if (dev.gen !== gen) return;
        Jc2.log(1, 'subscribe ' + uuid + ' failed: ' + Jc2.describe(err));
        Jc2.push(Jc2.EV_SUBSCRIBED, handle, tag, 1, handle, null);
      });
    });
  },

  // Not transport: helpers for the rate booster (Joycon2RateBooster.cs). 1 = Windows, 2 = macOS, 0 = other.
  jc2web_os: function () {
    var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '';
    if (/^win/i.test(platform) || /Windows NT/.test(navigator.userAgent)) return 1;
    if (/^mac/i.test(platform) || /Mac OS X/.test(navigator.userAgent)) return 2;
    return 0;
  },

  // Follows a link without leaving the page: a custom protocol (the OS starts its app; Chrome asks first) or a
  // download. Browsers allow both only right after a click or key press, so call it from that frame.
  jc2web_open_link: function (urlPtr, download) {
    var a = document.createElement('a');
    a.href = UTF8ToString(urlPtr);
    if (download) a.download = '';
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    a.remove();
  },

  jc2web_write: function (handle, servicePtr, characteristicPtr, dataPtr, length) {
    var dev = Jc2.devices[handle];
    if (!dev) return;
    var service = UTF8ToString(servicePtr), uuid = UTF8ToString(characteristicPtr);
    var data = HEAPU8.slice(dataPtr, dataPtr + length); // copy now: the C# buffer may be reused
    var gen = dev.gen;
    Jc2.enqueue(dev, function () {
      return Jc2.characteristic(dev, service, uuid).then(function (c) {
        return c.writeValueWithoutResponse ? c.writeValueWithoutResponse(data) : c.writeValue(data);
      }).catch(function (err) {
        if (dev.gen !== gen) return;
        if (dev.writeFailures++ === 0) Jc2.log(1, 'write ' + uuid + ' failed: ' + Jc2.describe(err));
        Jc2.push(Jc2.EV_WRITE_FAILED, handle, 0, 1, handle, null);
      });
    });
  },
};

autoAddDeps(Joycon2WebBluetooth, '$Jc2');
mergeInto(LibraryManager.library, Joycon2WebBluetooth);
