using Joycon2;
using UnityEngine;

namespace Kagefumi
{
    /// <summary>
    /// SUN GRAB — the catch-up rule. After every <see cref="interval"/> seconds of play, the trailing player
    /// takes the sun for <see cref="duration"/> seconds, if they play on a Joy-Con 2 (it needs the gyro).
    /// Tilting the Joy-Con tilts the sun away from where the time of day put it: tilt right and the sun
    /// goes right, so shadows fall left. The sun's own travel stops meanwhile; afterwards it eases back.
    /// Only in daylight: at night the lamps cast the shadows.
    ///
    /// The tilt is measured from the Joy-Con's zero pose: set by Select, silently at every match start,
    /// and slowly adapted to the way the player usually holds it (see <see cref="Joycon2Orientation"/>).
    /// </summary>
    public class SunGrab : MonoBehaviour
    {
        [Tooltip("Seconds of play between grabs.")]
        public float interval = 15f;
        [Tooltip("Seconds the trailing player holds the sun.")]
        public float duration = 10f;
        [Tooltip("Sun degrees per degree of Joy-Con tilt.")]
        public float tiltGain = 1.5f;
        [Tooltip("Largest tilt of the sun from its place on the path, degrees.")]
        public float maxSunTilt = 80f;
        [Tooltip("Seconds for the sun to ease back to its path after a grab.")]
        public float releaseTime = 0.6f;

        static readonly Color Gold = new Color(1f, 0.84f, 0.4f);

        KagefumiGame game;
        DayCycle day;
        KagefumiHud hud;
        float cooldown, timeLeft;
        Joycon2Gamepad holderPad;

        /// <summary>Player index holding the sun, or -1.</summary>
        public int Holder { get; private set; } = -1;

        public void Init(KagefumiGame owner, DayCycle dayCycle, KagefumiHud headsUp)
        {
            game = owner;
            day = dayCycle;
            hud = headsUp;
            ResetForMatch();
        }

        /// <summary>New match: timers reset, sun back on its path, every Joy-Con's zero = how it is held now.</summary>
        public void ResetForMatch()
        {
            End(false);
            cooldown = interval;
            day.sunTilt = Quaternion.identity;
            for (int i = 0; i < 2; i++)
                (game.DeviceOf(i) as Joycon2Gamepad)?.Recenter();
        }

        void Update()
        {
            if (game == null) return;
            bool playing = game.CurrentState == KagefumiGame.State.Playing;

            if (Holder >= 0)
            {
                if (!playing || day.CurrentPhase != DayCycle.Phase.Day) End(true);
                else Steer();
                return;
            }

            // Back to the sun's own path (frame-rate independent ease).
            if (day.sunTilt != Quaternion.identity)
                day.sunTilt = Quaternion.Slerp(day.sunTilt, Quaternion.identity, 1f - Mathf.Exp(-Time.deltaTime * 3f / releaseTime));

            if (!playing) return;
            cooldown -= Time.deltaTime;
            if (cooldown > 0f || day.CurrentPhase != DayCycle.Phase.Day) return;

            int outsider = game.Outsider;
            if (outsider >= 0 && game.DeviceOf(outsider) is Joycon2Gamepad pad) Begin(outsider, pad);
        }

        void Begin(int player, Joycon2Gamepad pad)
        {
            Holder = player;
            holderPad = pad;
            timeLeft = duration;
            day.clockScale = 0f;
            // The zero must not follow the player's hand while they steer.
            pad.AutoCenter = false;

            var p = game.players[player];
            hud.Toast($"P{player + 1} GRABS THE SUN!  tilt the Joy-Con", p.color, 2.6f);
            game.Sfx.Go();
            game.Fx.Shockwave(p.transform.position, p.color, 5f);
        }

        void Steer()
        {
            if (holderPad == null || !holderPad.added)
            {
                End(true);
                return;
            }

            // Where the button face points relative to its zero (sideways frame) → plaza, relative to the camera:
            // right → screen right, rail edge → away from the camera.
            var face = holderPad.attitude.ReadValue() * Vector3.up;
            var right = Vector3.right;
            if (game.gameCamera != null)
            {
                var r = Vector3.ProjectOnPlane(game.gameCamera.transform.right, Vector3.up);
                if (r.sqrMagnitude > 1e-6f) right = r.normalized;
            }
            var away = Vector3.Cross(right, Vector3.up);
            var faceWorld = right * face.x + Vector3.up * face.y + away * face.z;

            // Tilt the sun the way the Joy-Con is tilted, amplified.
            var tilt = Quaternion.FromToRotation(Vector3.up, faceWorld);
            tilt.ToAngleAxis(out float angle, out var axis);
            if (angle > 180f) angle -= 360f;
            day.sunTilt = Quaternion.AngleAxis(Mathf.Clamp(angle * tiltGain, -maxSunTilt, maxSunTilt), axis);

            timeLeft -= Time.deltaTime;
            hud.SetSunHolder($"P{Holder + 1} HOLDS THE SUN  {Mathf.CeilToInt(timeLeft)}", game.players[Holder].color);
            if (timeLeft <= 0f) End(true);
        }

        void End(bool announce)
        {
            if (Holder < 0) return;
            Holder = -1;
            if (holderPad != null) holderPad.AutoCenter = true;
            holderPad = null;
            cooldown = interval;
            day.clockScale = 1f;
            hud.SetSunHolder(null, Color.clear);
            if (announce) hud.Toast("THE SUN SLIPS AWAY", Gold, 1.6f);
        }
    }
}
