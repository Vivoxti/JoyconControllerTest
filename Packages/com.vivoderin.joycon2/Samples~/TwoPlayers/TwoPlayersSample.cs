using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Joycon2.Samples
{
    /// <summary>
    /// Add to an empty GameObject in an empty scene and press Play. Hold SYNC on a Joy-Con 2 (on the web: then tap
    /// Connect L / R), press any of its buttons to join: the stick moves your cube, tilting the Joy-Con tilts it, South
    /// (the lowest face button held sideways) jumps, Select (Capture / Home) recenters the tilt. Any other gamepad joins too —
    /// a Joy-Con 2 is just a <see cref="Gamepad"/>, the tilt is the extra.
    /// </summary>
    public sealed class TwoPlayersSample : MonoBehaviour
    {
        [SerializeField] float speed = 4f;
        [SerializeField] Color[] colors = { new Color(0.95f, 0.35f, 0.25f), new Color(0.25f, 0.6f, 0.95f) };

        sealed class Player
        {
            public Gamepad Pad;
            public Transform Body;
            public float VerticalSpeed;
        }

        readonly List<Player> players = new List<Player>();
        string lastEvent = "";

        void OnEnable()
        {
            Joycon2Manager.ControllerConnected += OnConnected;
            Joycon2Manager.ControllerDisconnected += OnDisconnected;
        }

        void OnDisable()
        {
            Joycon2Manager.ControllerConnected -= OnConnected;
            Joycon2Manager.ControllerDisconnected -= OnDisconnected;
        }

        void Start()
        {
            if (Camera.main == null)
            {
                var cam = new GameObject("Camera", typeof(Camera)) { tag = "MainCamera" };
                cam.transform.SetPositionAndRotation(new Vector3(0, 7, -9), Quaternion.Euler(35, 0, 0));
            }
            if (FindFirstObjectByType<Light>() == null)
                new GameObject("Sun", typeof(Light)).transform.rotation = Quaternion.Euler(50, -30, 0);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(2, 1, 2);
        }

        void OnConnected(Joycon2Controller c) => lastEvent = $"{c.Name} connected ({c.ReportRate:0} Hz soon)";
        void OnDisconnected(Joycon2Controller c, string reason) => lastEvent = $"{c.Name} left: {reason}";

        void Update()
        {
            // Join: any button on a gamepad that has no cube yet.
            foreach (var pad in Gamepad.all)
                if (players.Count < colors.Length && players.TrueForAll(p => p.Pad != pad) && AnyButton(pad))
                    Join(pad);

            for (int i = players.Count - 1; i >= 0; i--)
            {
                var p = players[i];
                if (!p.Pad.added) // unplugged / Joy-Con turned off
                {
                    Destroy(p.Body.gameObject);
                    players.RemoveAt(i);
                    continue;
                }
                Vector2 move = p.Pad.leftStick.ReadValue();
                p.Body.position += new Vector3(move.x, 0, move.y) * (speed * Time.deltaTime);

                bool grounded = p.Body.position.y <= 0.5f;
                if (grounded && p.Pad.buttonSouth.wasPressedThisFrame) p.VerticalSpeed = 6f;
                p.VerticalSpeed -= 18f * Time.deltaTime;
                var pos = p.Body.position;
                pos.y = Mathf.Max(0.5f, pos.y + p.VerticalSpeed * Time.deltaTime);
                if (pos.y <= 0.5f) p.VerticalSpeed = 0f;
                p.Body.position = pos;

                // Joy-Con only: the pose since the last recenter.
                if (p.Pad is Joycon2Gamepad joycon)
                    p.Body.rotation = Quaternion.Slerp(p.Body.rotation, joycon.attitude.ReadValue(), 15f * Time.deltaTime);
            }
        }

        void Join(Gamepad pad)
        {
            int index = players.Count;
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
            body.name = $"P{index + 1} ({pad.displayName})";
            body.position = new Vector3(index == 0 ? -2 : 2, 0.5f, 0);
            body.GetComponent<Renderer>().material.color = colors[index];
            players.Add(new Player { Pad = pad, Body = body });
            Joycon2Manager.SetPlayerIndex(pad, index); // player LEDs; does nothing for other gamepads
        }

        static bool AnyButton(Gamepad pad)
        {
            foreach (var control in pad.allControls)
                if (control is UnityEngine.InputSystem.Controls.ButtonControl b && !b.synthetic && b.wasPressedThisFrame) return true;
            return false;
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, Screen.height - 110, 520, 100), GUI.skin.box);
            GUILayout.Label($"Joy-Con 2: {Joycon2Lifecycle.Status}");
            if (lastEvent.Length > 0) GUILayout.Label(lastEvent);
            // Web without the built-in panel: the chooser must open from a click. OnGUI runs in that click's frame.
            if (Joycon2Manager.ConnectsOnRequest && !Joycon2Manager.Settings.WebConnectionPanel &&
                GUILayout.Button("Connect a Joy-Con 2", GUILayout.Width(220)))
                Joycon2Manager.RequestController();
            GUILayout.EndArea();
        }
    }
}
