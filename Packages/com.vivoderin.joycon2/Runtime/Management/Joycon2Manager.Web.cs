using System.Text;
using UnityEngine;

namespace Joycon2
{
    // Web Bluetooth: no background scan — the player picks each Joy-Con in the browser's chooser — and, on macOS with the
    // joycon2boost helper, rebuilding links that stayed slow.
    public static partial class Joycon2Manager
    {
        /// <summary>
        /// The transport cannot scan in the background (WebGL): each controller is added by <see cref="RequestController"/>,
        /// which has to be called right after a click or key press.
        /// </summary>
        public static bool ConnectsOnRequest => Transport != null && Transport.RequiresUserGesture;

        /// <summary>A <see cref="RequestController"/> call would open the chooser now.</summary>
        public static bool CanRequestController => IsRunning && ConnectsOnRequest && !pickPending && !WebPickerBusy &&
                                                   (controllers.Count < Settings.MaxControllers || AnyRefreshing);

        static bool pickPending;

        const int PickAny = 0, PickLeft = 1, PickRight = 2;

        // macOS fixes a link's interval when the link is created, and makes it fast (15 ms) only if another app already
        // holds a low-latency link then (joycon2boost). With the helper running, the first Joy-Con stays at ~33 Hz while
        // the second gets ~66: reconnect a link much slower than another one. The gamepad and its player stay; only the
        // link is rebuilt, and the Joy-Con accepts it only in SYNC — show Joycon2Controller.Refreshing as "press SYNC".
        const float SlowLinkRate = 45f;
        const float FastLinkRate = 55f;
        const double SlowLinkSeconds = 4.0;
        const int MaxLinkRefreshes = 3;
        /// <summary>Pause between closing and reopening: Chrome would reattach to the old link while it is still up.</summary>
        const double RefreshDelay = 1.5;
        const double RefreshAttemptTimeout = 8.0;
        const double RefreshTimeout = 120.0;

        static bool WebPickerBusy
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return Joycon2WebBluetoothTransport.IsPicking;
#else
                return false;
#endif
            }
        }

        static bool AnyRefreshing
        {
            get
            {
                foreach (var c in controllers)
                    if (c.Refreshing) return true;
                return false;
            }
        }

        /// <summary>
        /// Opens the browser's Bluetooth chooser listing Joy-Con 2 (only <paramref name="side"/>, if given). Only when
        /// <see cref="ConnectsOnRequest"/>; call it from the frame of a click or key press (gamepad buttons do not count
        /// as a user gesture in browsers). The picked controller then connects as usual. The built-in web panel's
        /// Connect L / R buttons do the same straight from the DOM tap.
        /// </summary>
        public static bool RequestController(Joycon2Side? side = null)
        {
            if (!CanRequestController) return false;
            pickPending = true;
            Status = "Bluetooth chooser open";
            int tag = side == null ? PickAny : side == Joycon2Side.Left ? PickLeft : PickRight;
            Transport.RequestDevice(BuildDeviceRequest(side), tag);
            return true;
        }

        /// <summary>
        /// Web Bluetooth requestDevice options: manufacturer data with the PID (as the reference web demo), or the service
        /// and the exact name. Chrome on macOS also lists devices another app already connected (joycon2boost): it finds
        /// them by the services in the filters, and they have no advertisement data to match.
        /// </summary>
        internal static string BuildDeviceRequest(Joycon2Side? side)
        {
            var filters = new StringBuilder();
            void Filter(ushort pid, string name)
            {
                if (filters.Length > 0) filters.Append(',');
                // Manufacturer data after the company id: [3..4] VID, [5..6] PID — match the PID only.
                filters.Append("{\"manufacturerData\":[{\"companyIdentifier\":").Append(Joycon2Protocol.CompanyId)
                       .Append(",\"dataPrefix\":[0,0,0,0,0,").Append(pid & 0xFF).Append(',').Append(pid >> 8)
                       .Append("],\"mask\":[0,0,0,0,0,255,255]}]},{\"services\":[\"").Append(Joycon2Protocol.ServiceUuid)
                       .Append("\"],\"name\":\"").Append(name).Append("\"}");
            }
            if (side != Joycon2Side.Right) Filter(Joycon2Protocol.ProductIdLeft, "Joy-Con 2 (L)");
            if (side != Joycon2Side.Left) Filter(Joycon2Protocol.ProductIdRight, "Joy-Con 2 (R)");
            return $"{{\"filters\":[{filters}],\"optionalServices\":[\"{Joycon2Protocol.ServiceUuid}\"]}}";
        }

        static void OnDevicePicked(in Joycon2TransportEvent e, int length, double now)
        {
            pickPending = false;
            string text = Encoding.UTF8.GetString(payload, 0, length);
            if (e.Status != 0)
            {
                if (e.Status == 1) Debug.Log($"[Joycon2] no Joy-Con picked ({text})");
                else Debug.LogWarning($"[Joycon2] Bluetooth chooser failed: {text}");
                return;
            }

            // The chooser lists L and R together and Chrome may not know the name ("Unknown or unsupported
            // device"); then the session reads L/R from the controller itself.
            Joycon2Side? side = null;
            if (e.Tag == PickLeft || text.EndsWith("(L)")) side = Joycon2Side.Left;
            else if (e.Tag == PickRight || text.EndsWith("(R)")) side = Joycon2Side.Right;

            foreach (var existing in controllers)
                if (existing.Address == e.Address)
                {
                    if (existing.Refreshing && existing.Session.State != Joycon2Session.SessionState.Ready)
                    {
                        Debug.Log($"[Joycon2] picked {existing} again, reconnecting it now");
                        Transport.Disconnect(existing.Handle);
                        existing.refreshConnectAt = now;
                    }
                    else Debug.Log($"[Joycon2] {existing} is already connected");
                    return;
                }
            if (controllers.Count >= Settings.MaxControllers) return;

            var c = BeginConnect(e.Address, side, now);
            if (c != null) Debug.Log($"[Joycon2] picked {(side == null ? "a Joy-Con 2 (side from the controller)" : c.Name)} " +
                                     $"{c.AddressText} (name \"{text}\"), connecting…");
        }

        static void CheckSlowLinks(double now)
        {
            bool anyFast = false;
            foreach (var c in controllers)
                if (!c.Refreshing && c.Session.State == Joycon2Session.SessionState.Ready && c.ReportRate >= FastLinkRate)
                    anyFast = true;

            foreach (var c in controllers)
            {
                bool slow = anyFast && !c.Refreshing && c.Session.State == Joycon2Session.SessionState.Ready &&
                            c.ReportRate > 0 && c.ReportRate < SlowLinkRate;
                if (!slow)
                {
                    c.slowSince = -1;
                    continue;
                }
                if (c.slowSince < 0) c.slowSince = now;
                else if (now - c.slowSince >= SlowLinkSeconds && c.refreshCount < MaxLinkRefreshes && Settings.RefreshSlowLinks)
                    StartRefresh(c, now);
            }
        }

        static void StartRefresh(Joycon2Controller c, double now)
        {
            c.refreshCount++;
            Debug.Log($"[Joycon2] {c}: {c.ReportRate:0} Hz while another Joy-Con is fast — " +
                      $"reconnecting it for a faster link ({c.refreshCount}/{MaxLinkRefreshes})");
            Transport.Disconnect(c.Handle);
            c.refreshUntil = now + RefreshTimeout;
            c.refreshConnectAt = now + RefreshDelay;
            c.slowSince = -1;
            c.ResetRate(now);
        }

        static void ReconnectWhenDue(Joycon2Controller c, double now)
        {
            if (now > c.refreshUntil)
            {
                c.refreshUntil = 0;
                Drop(c, "not back for a faster link — hold SYNC and pick it again", Settings.RetryCooldown);
                return;
            }
            if (now < c.refreshConnectAt) return;

            int handle = Transport.Connect(c.Address);
            if (handle <= 0)
            {
                c.refreshConnectAt = now + 1;
                return;
            }
            c.refreshConnectAt = 0;
            c.Handle = handle;
            c.ConnectStartTime = now;
            c.ResetRate(now);
            c.Session = NewSession(c, c.Side);
            c.Session.PlayerLed = Joycon2Protocol.PlayerLedPattern(c.PlayerIndex);
        }

        static void RetryRefresh(Joycon2Controller c, double now, string reason)
        {
            Debug.Log($"[Joycon2] {c} reconnect: {reason}, retrying (waiting for SYNC on it)");
            Transport.Disconnect(c.Handle);
            c.refreshConnectAt = now + 1;
        }
    }
}
