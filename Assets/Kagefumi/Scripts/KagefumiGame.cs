using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Utilities;

namespace Kagefumi
{
    /// <summary>
    /// KAGEFUMI — the shadow-stepping duel.
    /// Pairs devices to the two players, runs lobby → countdown → match → rematch, and scores stomps.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class KagefumiGame : MonoBehaviour
    {
        public enum State { Lobby, Countdown, Playing, Paused, GameOver }

        class Slot
        {
            public InputDevice device;
            public string scheme;
            public InputActionAsset actions;
            public InputAction move, stomp, crouch;
            public bool Joined => device != null;
        }

        [Header("Scene")]
        public ShadowPlayer[] players = new ShadowPlayer[2];
        public DayCycle dayCycle;
        public Camera gameCamera;
        public Vector3[] spawnPoints = { new Vector3(-7f, 0f, -0.5f), new Vector3(7f, 0f, -0.5f) };

        [Header("Assets")]
        public InputActionAsset controls;
        public Material fxMaterial;

        [Header("Rules")]
        public int winScore = 5;
        [Tooltip("Radius of the foot that has to touch the shadow on landing.")]
        public float stompRadius = 0.34f;

        public State CurrentState { get; private set; } = State.Lobby;
        public KagefumiSfx Sfx { get; private set; }
        public KagefumiFx Fx { get; private set; }

        static readonly string[] StompLines = { "STOMP!", "GOTCHA!", "SHADOW PINNED!", "KAGEFUMI!", "STEPPED ON!" };
        static readonly Color Gold = new Color(1f, 0.84f, 0.4f);

        readonly Slot[] slots = { new Slot(), new Slot() };
        readonly int[] scores = new int[2];
        readonly List<(InputDevice device, string scheme)> pendingJoins = new List<(InputDevice, string)>();
        readonly List<InputDevice> pendingLost = new List<InputDevice>();

        KagefumiHud hud;
        IDisposable anyButtonSubscription;
        Coroutine flow;
        State stateBeforePause;
        float baseTimeScale = 1f, shake;
        Vector3 cameraHome;
        bool rematchReady, noonAnnounced;
        DayCycle.Phase lastPhase;

        void OnEnable()
        {
            anyButtonSubscription = InputSystem.onAnyButtonPress.Call(OnAnyButtonPress);
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        void OnDisable()
        {
            anyButtonSubscription?.Dispose();
            anyButtonSubscription = null;
            InputSystem.onDeviceChange -= OnDeviceChange;
            for (int i = 0; i < slots.Length; i++) ReleaseSlot(i);
            Time.timeScale = 1f;
        }

        void Start()
        {
            Application.runInBackground = true;
            if (gameCamera == null) gameCamera = Camera.main;
            cameraHome = gameCamera.transform.position;

            Sfx = gameObject.AddComponent<KagefumiSfx>();
            Fx = gameObject.AddComponent<KagefumiFx>();
            Fx.Init(fxMaterial);
            hud = gameObject.AddComponent<KagefumiHud>();
            hud.Build(new[] { players[0].color, players[1].color }, winScore);

            for (int i = 0; i < players.Length; i++)
            {
                players[i].Init(this, fxMaterial);
                players[i].ResetTo(spawnPoints[i], SpawnFacing(i));
                players[i].gameObject.SetActive(false);
                hud.SetPlayer(i, false, null);
            }
            EnterLobby();
        }

        void Update()
        {
            ProcessDeviceQueues();
            Physics.SyncTransforms();
            UpdateLocks();
            UpdatePhase();

            if (CurrentState == State.GameOver && rematchReady)
            {
                foreach (var s in slots)
                {
                    if (s.Joined && s.stomp.WasPressedThisFrame())
                    {
                        StartMatch();
                        break;
                    }
                }
            }
            UpdateShake();
        }

        // ───────────────────────────── Devices ─────────────────────────────

        void OnAnyButtonPress(InputControl control)
        {
            // Called from inside the input update: only record the request, act on it in Update.
            var device = control.device;
            string scheme = null;
            if (device is Gamepad) scheme = "Gamepad";
            else if (device is Joystick) scheme = "Joystick";
            else if (device is Keyboard) scheme = KeyboardScheme(control as KeyControl);
            if (scheme != null) pendingJoins.Add((device, scheme));
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected) pendingLost.Add(device);
        }

        static string KeyboardScheme(KeyControl key)
        {
            if (key == null) return null;
            switch (key.keyCode)
            {
                case Key.W: case Key.A: case Key.S: case Key.D:
                case Key.Space: case Key.LeftShift: case Key.C:
                    return "KeyboardLeft";
                case Key.UpArrow: case Key.DownArrow: case Key.LeftArrow: case Key.RightArrow:
                case Key.Enter: case Key.NumpadEnter: case Key.Numpad0: case Key.Numpad1: case Key.RightShift:
                    return "KeyboardRight";
                default:
                    return null;
            }
        }

        void ProcessDeviceQueues()
        {
            foreach (var device in pendingLost)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i].device != device) continue;
                    ReleaseSlot(i);
                    hud.SetPlayer(i, false, null);
                    hud.Toast($"P{i + 1} CONTROLLER LOST", players[i].color, 2f);
                    if (CurrentState == State.Lobby) players[i].gameObject.SetActive(false);
                    else Pause();
                }
            }
            pendingLost.Clear();

            foreach (var (device, scheme) in pendingJoins)
            {
                if (!device.added || IsAssigned(device, scheme)) continue;
                int free = Array.FindIndex(slots, s => !s.Joined);
                if (free >= 0) Join(free, device, scheme);
            }
            pendingJoins.Clear();
        }

        bool IsAssigned(InputDevice device, string scheme)
        {
            // A keyboard can host two players (left and right halves); any other device hosts one.
            foreach (var s in slots)
                if (s.device == device && (s.scheme == scheme || !(device is Keyboard))) return true;
            return false;
        }

        void Join(int i, InputDevice device, string scheme)
        {
            var s = slots[i];
            s.device = device;
            s.scheme = scheme;
            s.actions = Instantiate(controls);
            s.actions.name = $"P{i + 1} Controls";
            s.actions.devices = new ReadOnlyArray<InputDevice>(new[] { device });
            s.actions.bindingMask = InputBinding.MaskByGroup(scheme);
            var map = s.actions.FindActionMap("Player", true);
            s.move = map.FindAction("Move", true);
            s.stomp = map.FindAction("Stomp", true);
            s.crouch = map.FindAction("Crouch", true);
            map.Enable();

            var p = players[i];
            p.BindInput(s.move, s.stomp, s.crouch);
            if (!p.gameObject.activeSelf)
            {
                p.gameObject.SetActive(true);
                p.ResetTo(spawnPoints[i], SpawnFacing(i));
                Fx.Shockwave(p.transform.position, p.color, 2.2f);
            }
            p.Controllable = CurrentState == State.Lobby || CurrentState == State.Playing || CurrentState == State.GameOver;

            hud.SetPlayer(i, true, DeviceLabel(device, scheme));
            hud.Toast($"P{i + 1} READY", p.color, 1.2f);
            Sfx.Join();

            bool both = slots[0].Joined && slots[1].Joined;
            if (both && CurrentState == State.Lobby) StartMatch();
            else if (both && CurrentState == State.Paused) Resume();
        }

        void ReleaseSlot(int i)
        {
            var s = slots[i];
            if (s.actions != null)
            {
                s.actions.Disable();
                Destroy(s.actions);
            }
            s.actions = null;
            s.device = null;
            s.scheme = null;
            s.move = s.stomp = s.crouch = null;
            if (players[i] != null) players[i].UnbindInput();
        }

        static string DeviceLabel(InputDevice device, string scheme)
        {
            if (scheme == "KeyboardLeft") return "Keyboard: WASD · Space · LShift";
            if (scheme == "KeyboardRight") return "Keyboard: Arrows · Enter · RShift";
            return string.IsNullOrEmpty(device.displayName) ? device.layout : device.displayName;
        }

        // ───────────────────────────── Flow ─────────────────────────────

        void EnterLobby()
        {
            CurrentState = State.Lobby;
            SetControllable(true);
            hud.ShowBig("KAGEFUMI", Color.white,
                "the shadow-stepping duel\n\nPress A on two gamepads to join\n(keyboard: SPACE for P1, ENTER for P2)");
        }

        void StartMatch()
        {
            if (flow != null) StopCoroutine(flow);
            flow = StartCoroutine(CountdownRoutine(true));
        }

        void Pause()
        {
            if (CurrentState == State.Paused) return;
            if (flow != null) StopCoroutine(flow);
            flow = null;
            stateBeforePause = CurrentState == State.Countdown ? State.Playing : CurrentState;
            CurrentState = State.Paused;
            Time.timeScale = 0f;
            SetControllable(false);
            hud.ShowBig("PAUSED", Color.white, "A controller was disconnected.\nPress any button on a controller to take its place.");
        }

        void Resume()
        {
            if (stateBeforePause == State.GameOver)
            {
                CurrentState = State.GameOver;
                baseTimeScale = 1f;
                Time.timeScale = 1f;
                SetControllable(true);
                rematchReady = true;
                hud.ShowBig("REMATCH?", Gold, "Press STOMP to play again");
                return;
            }
            flow = StartCoroutine(CountdownRoutine(false));
        }

        IEnumerator CountdownRoutine(bool newMatch)
        {
            CurrentState = State.Countdown;
            rematchReady = false;
            baseTimeScale = 1f;
            Time.timeScale = 1f;
            SetControllable(false);

            if (newMatch)
            {
                for (int i = 0; i < 2; i++)
                {
                    scores[i] = 0;
                    hud.SetScore(i, 0, false);
                    players[i].ResetTo(spawnPoints[i], SpawnFacing(i));
                }
                dayCycle.ResetToMorning();
                lastPhase = dayCycle.CurrentPhase;
                noonAnnounced = false;
            }

            dayCycle.paused = true;
            for (int n = 3; n > 0; n--)
            {
                hud.ShowBig(n.ToString(), Color.white, newMatch ? "Your body can't be hit. Your SHADOW can." : "Get ready");
                Sfx.Beep();
                yield return new WaitForSecondsRealtime(0.75f);
            }
            hud.ShowBig("STEP!", Gold, null);
            Sfx.Go();
            dayCycle.paused = false;
            CurrentState = State.Playing;
            SetControllable(true);

            yield return new WaitForSecondsRealtime(0.7f);
            if (CurrentState == State.Playing) hud.HideBig();
            flow = null;
        }

        IEnumerator GameOverRoutine(int winner)
        {
            CurrentState = State.GameOver;
            rematchReady = false;
            yield return new WaitForSecondsRealtime(0.12f); // let the hit-stop finish

            var c = players[winner].color;
            baseTimeScale = 0.3f;
            Time.timeScale = baseTimeScale;
            hud.ShowBig($"P{winner + 1} WINS", c, $"{scores[0]} : {scores[1]}");
            Sfx.Win();
            Fx.Shockwave(players[winner].transform.position, c, 6f);

            yield return new WaitForSecondsRealtime(1.6f);
            baseTimeScale = 1f;
            Time.timeScale = 1f;
            hud.ShowBig($"P{winner + 1} WINS", c, $"{scores[0]} : {scores[1]}\nPress STOMP to rematch");
            rematchReady = true;
            flow = null;
        }

        void SetControllable(bool on)
        {
            foreach (var p in players) p.Controllable = on;
        }

        static Vector3 SpawnFacing(int i) => i == 0 ? Vector3.right : Vector3.left;

        // ───────────────────────────── Stomps ─────────────────────────────

        bool StompsCount => CurrentState == State.Playing || CurrentState == State.Lobby || CurrentState == State.GameOver;

        /// <summary>Called by a player at the exact moment a stomp-leap touches the ground.</summary>
        public void HandleLanding(ShadowPlayer attacker, Vector3 point)
        {
            var victim = players[1 - attacker.index];
            Physics.SyncTransforms();
            if (StompsCount && victim.gameObject.activeInHierarchy &&
                ShadowJudge.DiscInShadow(point, stompRadius, victim, dayCycle))
            {
                StompHit(attacker, victim, point);
            }
            else
            {
                Sfx.Land();
                Fx.Dust(point);
            }
        }

        void StompHit(ShadowPlayer attacker, ShadowPlayer victim, Vector3 point)
        {
            Sfx.Tear();
            Fx.InkBurst(point, 56);
            Fx.InkBurst(victim.transform.position, 28); // the shadow rips away from the victim's feet too
            Fx.Shockwave(point, attacker.color, 3.2f);
            shake = Mathf.Max(shake, 0.6f);
            victim.Stomped(victim.transform.position - attacker.transform.position);
            StartCoroutine(HitStop(0.1f));

            if (CurrentState != State.Playing)
            {
                hud.Toast("STOMP!", attacker.color, 0.9f);
                return;
            }

            int score = ++scores[attacker.index];
            hud.SetScore(attacker.index, score, true);
            if (score >= winScore)
            {
                if (flow != null) StopCoroutine(flow);
                flow = StartCoroutine(GameOverRoutine(attacker.index));
            }
            else
            {
                var line = StompLines[UnityEngine.Random.Range(0, StompLines.Length)];
                hud.Toast($"P{attacker.index + 1}  {line}", attacker.color, 1.3f);
            }
        }

        IEnumerator HitStop(float duration)
        {
            Time.timeScale = 0.03f;
            yield return new WaitForSecondsRealtime(duration);
            if (CurrentState != State.Paused) Time.timeScale = baseTimeScale;
        }

        void UpdateLocks()
        {
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                var rival = players[1 - i];
                bool lockOn = StompsCount && p.gameObject.activeInHierarchy && rival.gameObject.activeInHierarchy &&
                              ShadowJudge.DiscInShadow(p.LandingPoint, stompRadius, rival, dayCycle);
                p.SetLock(lockOn);
            }
        }

        // ───────────────────────────── Presentation ─────────────────────────────

        void UpdatePhase()
        {
            hud.SetPhase(dayCycle.PhaseLabel, dayCycle.CycleTime / dayCycle.CycleLength, dayCycle.CurrentPhase);

            var phase = dayCycle.CurrentPhase;
            if (CurrentState != State.Playing)
            {
                lastPhase = phase;
                return;
            }
            if (phase != lastPhase)
            {
                lastPhase = phase;
                noonAnnounced = false;
                switch (phase)
                {
                    case DayCycle.Phase.Dusk: hud.Toast("DUSK — the lamps wake up", new Color(1f, 0.6f, 0.45f), 2.2f); break;
                    case DayCycle.Phase.Night: hud.Toast("NIGHT — every lamp gives you one more shadow", new Color(1f, 0.8f, 0.45f), 2.6f); break;
                    case DayCycle.Phase.Dawn: hud.Toast("DAWN — long shadows are back", new Color(1f, 0.7f, 0.5f), 2.2f); break;
                }
            }
            if (phase == DayCycle.Phase.Day && !noonAnnounced && dayCycle.DayProgress > 0.42f)
            {
                noonAnnounced = true;
                hud.Toast("NOON — shadows shrink. Get close!", Gold, 2.2f);
            }
        }

        void UpdateShake()
        {
            if (gameCamera == null) return;
            shake = Mathf.MoveTowards(shake, 0f, Time.unscaledDeltaTime * 2.5f);
            float s = shake * shake * 0.9f;
            float t = Time.unscaledTime * 40f;
            gameCamera.transform.position = cameraHome +
                new Vector3(Mathf.PerlinNoise(t, 0f) - 0.5f, Mathf.PerlinNoise(0f, t) - 0.5f, 0f) * s;
        }
    }
}
