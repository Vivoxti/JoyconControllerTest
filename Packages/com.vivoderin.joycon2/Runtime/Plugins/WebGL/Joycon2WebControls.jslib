// Built-in connection panel (Joycon2WebControls.cs): C# supplies device filters and status; the protocol stays in C#.
// Optional: Joycon2WebBluetooth.jslib works without it (Joycon2Settings.WebConnectionPanel).
var Joycon2WebControls = {
  $Jc2UI: {
    root: null, actions: 0, dumpUrl: null, visibility: null, lastPickerMessage: '', lastError: '',
    boostLaunchingUntil: 0, boostHiddenAt: 0,
    feedback: function (message) { if (Jc2UI.message) Jc2UI.message.textContent = message; },
    supportError: function () {
      if (!window.isSecureContext) return 'Open the game over HTTPS. An HTTP address on your Wi-Fi network cannot access Bluetooth.';
      if (!navigator.bluetooth || !navigator.bluetooth.requestDevice) return Jc2.isIOS()
        ? 'Safari and other iOS browsers cannot connect Joy-Con 2. Tap Open in Bluefy (free Web Bluetooth browser from the App Store).'
        : 'This browser cannot connect Joy-Con 2. Open this page in Chrome.';
      var policy = document.permissionsPolicy || document.featurePolicy;
      if (policy && policy.allowsFeature && !policy.allowsFeature('bluetooth'))
        return 'Bluetooth access is blocked by the hosting page. Open the game directly in Chrome.';
      return '';
    },
    button: function (label, action) {
      var b = document.createElement('button');
      b.textContent = label;
      b.type = 'button';
      b.addEventListener('click', action);
      Jc2UI.buttons.appendChild(b);
      return b;
    },
    closeLinks: function () {
      for (var id in Jc2.devices) {
        var dev = Jc2.devices[id];
        if (!dev.live) continue;
        _jc2web_disconnect(Number(id));
        Jc2.push(Jc2.EV_DISCONNECTED, Number(id), 0, 0, Number(id), null);
      }
      Jc2UI.feedback('Game left the foreground. Hold SYNC and reconnect to continue.');
    },
  },

  // androidHelperPtr: file name of the Android rate helper APK next to index.html, '' = no Boost buttons.
  jc2web_ui_init: function (leftPtr, rightPtr, androidHelperPtr) {
    if (Jc2UI.root) return;
    var left = Jc2.toRequestOptions(UTF8ToString(leftPtr));
    var right = Jc2.toRequestOptions(UTF8ToString(rightPtr));
    var android = /Android/i.test((navigator.userAgentData && navigator.userAgentData.platform) || navigator.userAgent || '');
    var ios = Jc2.isIOS();
    var mobile = android || ios;
    var service = JSON.parse(UTF8ToString(leftPtr)).optionalServices[0];
    var helperFile = UTF8ToString(androidHelperPtr);
    var helperUrl = helperFile ? new URL(helperFile, document.baseURI).href : '';
    var root = document.createElement('section');
    root.id = Jc2.UI_ROOT_ID;
    root.setAttribute('aria-label', 'Joy-Con 2 connections');
    // Next to the canvas, so the panel stays visible when the template makes the canvas container fullscreen.
    var canvas = typeof Module !== 'undefined' && Module.canvas;
    ((canvas && canvas.parentElement) || document.getElementById('unity-container') || document.body).appendChild(root);
    Jc2UI.root = root;
    var style = document.createElement('style');
    style.textContent = '#joycon2-controls{position:fixed;z-index:10000;bottom:max(8px,env(safe-area-inset-bottom));left:max(8px,env(safe-area-inset-left));max-width:min(560px,calc(100vw - 16px));box-sizing:border-box;background:#16202eed;color:#eef2f6;padding:8px 10px;border-radius:10px;font:14px system-ui,sans-serif;text-align:left;max-height:48vh;overflow:auto}#joycon2-controls button{font:inherit;min-height:44px;padding:7px 12px;margin:3px;border:1px solid #789;border-radius:7px;background:#26384c;color:#fff;touch-action:manipulation;cursor:pointer}#joycon2-controls button:disabled{opacity:.45;cursor:default}#joycon2-controls p{margin:4px 0}#joycon2-controls pre{font:12px ui-monospace,monospace;white-space:pre-wrap;margin:6px 0}#joycon2-controls summary{padding:7px 0;cursor:pointer}';
    root.appendChild(style);
    Jc2UI.buttons = document.createElement('div');
    root.appendChild(Jc2UI.buttons);
    if (android && helperUrl) {
      Jc2UI.boost = Jc2UI.button('Boost', function () {
        if (Jc2.requesting || Jc2.armedGesture) return;
        // Chrome launches the installed helper or offers the APK on first use.
        // Android returns immediately after the service starts; keep links for this brief hop.
        Jc2UI.boostLaunchingUntil = Date.now() + 5000;
        Jc2UI.feedback('Starting boost… first use needs the helper app and Nearby devices permission.');
        var a = document.createElement('a');
        a.href = 'intent://start?service=' + encodeURIComponent(service) +
          '#Intent;scheme=joycon2boost;package=com.vivoderin.joycon2boost;S.browser_fallback_url=' + encodeURIComponent(helperUrl) + ';end';
        root.appendChild(a); a.click(); a.remove();
      });
    }
    if (ios && !navigator.bluetooth) {
      // bluefy://open?url=… is what the reference Joy-Con 2 demo uses; without Bluefy installed iOS shows an error.
      Jc2UI.button('Open in Bluefy', function () { location.href = 'bluefy://open?url=' + encodeURIComponent(location.href); });
      Jc2UI.button('Get Bluefy', function () { location.href = 'https://apps.apple.com/app/id1492822055'; });
    }
    var choose = function (options, tag) {
      if (!Jc2.running || Jc2.requesting || Jc2.armedGesture) return;
      Jc2UI.left.disabled = Jc2UI.right.disabled = true;
      Jc2UI.feedback(ios ? 'Choose the blinking Joy-Con. Allow Bluetooth access if asked.'
        : 'Choose the blinking Joy-Con. Allow Chrome access to nearby devices if asked.');
      // No frame, Promise, or C# callback before requestDevice: preserves the tap's activation.
      Jc2.request(options, tag);
    };
    Jc2UI.left = Jc2UI.button('Connect L', function () { choose(left, 1); });
    Jc2UI.right = Jc2UI.button('Connect R', function () { choose(right, 2); });
    Jc2UI.left.disabled = Jc2UI.right.disabled = true;
    Jc2UI.message = document.createElement('p');
    Jc2UI.message.setAttribute('role', 'status');
    root.appendChild(Jc2UI.message);
    Jc2UI.status = document.createElement('p');
    root.appendChild(Jc2UI.status);
    var details = document.createElement('details');
    var summary = document.createElement('summary');
    summary.textContent = 'Details / diagnostics';
    details.appendChild(summary);
    Jc2UI.details = document.createElement('pre');
    details.appendChild(Jc2UI.details);
    var diagnosticButtons = document.createElement('div');
    details.appendChild(diagnosticButtons);
    Jc2UI.buttons = diagnosticButtons;
    Jc2UI.record = Jc2UI.button('Record', function () { Jc2UI.actions |= 1; });
    Jc2UI.download = Jc2UI.button('Download recording', function () {
      if (!Jc2UI.dumpUrl) return;
      var a = document.createElement('a');
      a.href = Jc2UI.dumpUrl;
      a.download = Jc2UI.dumpName;
      root.appendChild(a);
      a.click();
      a.remove();
    });
    Jc2UI.download.disabled = true;
    if (android && helperUrl) Jc2UI.button('Install boost helper', function () {
      var a = document.createElement('a'); a.href = helperUrl; a.download = helperFile;
      root.appendChild(a); a.click(); a.remove();
    });
    root.appendChild(details);
    Jc2UI.feedback(Jc2UI.supportError() || (android && helperUrl ? 'Tap Boost for faster input, then SYNC + Connect L / R.' : 'Hold SYNC, tap Connect L / R, then press a Joy-Con button to join.'));
    Jc2UI.visibility = function () {
      // Chooser/permission UI may temporarily hide Chrome; let that request finish.
      if (document.hidden && !Jc2.requesting && !Jc2.armedGesture) {
        if (Date.now() < Jc2UI.boostLaunchingUntil) { Jc2UI.boostHiddenAt = Date.now(); return; }
        Jc2UI.closeLinks();
      } else if (!document.hidden && Jc2UI.boostHiddenAt) {
        if (Date.now() - Jc2UI.boostHiddenAt > 2000) Jc2UI.closeLinks();
        else Jc2UI.feedback('Back in the game. Check controller Hz in Details.');
        Jc2UI.boostHiddenAt = Jc2UI.boostLaunchingUntil = 0;
      }
    };
    // Phones only: a hidden mobile tab loses its links without telling. A desktop tab in the background keeps them.
    if (mobile) document.addEventListener('visibilitychange', Jc2UI.visibility);
  },

  jc2web_ui_actions: function () { var result = Jc2UI.actions; Jc2UI.actions = 0; return result; },
  jc2web_ui_update: function (left, right, statusPtr, detailsPtr, recording) {
    if (!Jc2UI.root) return;
    var error = Jc2UI.supportError();
    Jc2UI.left.disabled = !left || !!error || !Jc2.running || Jc2.requesting || !!Jc2.armedGesture;
    Jc2UI.right.disabled = !right || !!error || !Jc2.running || Jc2.requesting || !!Jc2.armedGesture;
    Jc2UI.status.textContent = UTF8ToString(statusPtr);
    Jc2UI.details.textContent = UTF8ToString(detailsPtr);
    Jc2UI.record.disabled = !Jc2.running;
    Jc2UI.record.textContent = recording ? 'Stop recording' : 'Record';
    if (Jc2UI.boost) Jc2UI.boost.disabled = !!error || Jc2.requesting || !!Jc2.armedGesture;
    if (Jc2.pickerMessage && Jc2.pickerMessage !== Jc2UI.lastPickerMessage) {
      Jc2UI.lastPickerMessage = Jc2.pickerMessage;
      Jc2UI.feedback(Jc2.pickerMessage);
    }
    if (Jc2.lastError && Jc2.lastError !== Jc2UI.lastError) {
      Jc2UI.lastError = Jc2.lastError;
      Jc2UI.feedback('Connection problem: ' + Jc2.lastError + '. Hold SYNC and reconnect.');
    }
    if (error) Jc2UI.feedback(error);
  },
  jc2web_ui_dump: function (filenamePtr, contentsPtr) {
    if (Jc2UI.dumpUrl) URL.revokeObjectURL(Jc2UI.dumpUrl);
    Jc2UI.dumpName = UTF8ToString(filenamePtr);
    Jc2UI.dumpUrl = URL.createObjectURL(new Blob([UTF8ToString(contentsPtr)], { type: 'text/tab-separated-values;charset=utf-8' }));
    if (!Jc2UI.root) {
      // No panel (Joycon2Settings.WebConnectionPanel off): download right away (F10 is the user gesture).
      var a = document.createElement('a');
      a.href = Jc2UI.dumpUrl; a.download = Jc2UI.dumpName; a.style.display = 'none';
      document.body.appendChild(a); a.click(); a.remove();
      return;
    }
    if (Jc2UI.download) Jc2UI.download.disabled = false;
    Jc2UI.feedback('Recording saved. Tap Download recording to save it on your device.');
  },
  jc2web_ui_shutdown: function () {
    if (Jc2UI.visibility) document.removeEventListener('visibilitychange', Jc2UI.visibility);
    if (Jc2UI.dumpUrl) URL.revokeObjectURL(Jc2UI.dumpUrl);
    if (Jc2UI.root) Jc2UI.root.remove();
    Jc2UI.root = Jc2UI.dumpUrl = Jc2UI.visibility = null;
    Jc2UI.actions = 0;
    Jc2UI.lastPickerMessage = '';
    Jc2UI.lastError = '';
    Jc2UI.boostLaunchingUntil = Jc2UI.boostHiddenAt = 0;
  },
};
autoAddDeps(Joycon2WebControls, '$Jc2UI');
autoAddDeps(Joycon2WebControls, '$Jc2');
Joycon2WebControls.jc2web_ui_init__deps = ['$Jc2UI', '$Jc2', 'jc2web_disconnect'];
mergeInto(LibraryManager.library, Joycon2WebControls);
