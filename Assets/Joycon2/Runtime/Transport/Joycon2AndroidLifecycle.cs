#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;

namespace Joycon2
{
    /// <summary>Permission UI and foreground lifetime. Independent of the manager, which can fail to start.</summary>
    public sealed class Joycon2AndroidLifecycle : MonoBehaviour
    {
        static Joycon2AndroidLifecycle instance;
        static readonly string[] Nearby = { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" };
        static readonly string[] Legacy = { Permission.FineLocation };
        string[] permissions;
        PermissionCallbacks callbacks;
        bool requestPending, paused, startupAttempted, quitting;
        float nextCheck;
        public static string Status { get; private set; } = "Bluetooth permission needed";

        public static void EnsureStarted()
        {
            if (instance != null) return;
            var go = new GameObject("Joycon2 Android permissions");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Joycon2AndroidLifecycle>();
            var controls = go.AddComponent<Joycon2MobileControls>();
            controls.Status = () => Status;
            controls.Retry = ConnectOrRetry;
            controls.SettingsButtons = new (string, Action)[]
            {
                ("Bluetooth settings", () => OpenSettings(true)),
                ("App settings", () => OpenSettings(false)),
            };
        }

        void Awake()
        {
            Application.targetFrameRate = 60;
            using var version = new AndroidJavaClass("android.os.Build$VERSION");
            permissions = version.GetStatic<int>("SDK_INT") >= 31 ? Nearby : Legacy;
            RequestPermissions();
        }

        bool Granted()
        {
            foreach (string permission in permissions)
                if (!Permission.HasUserAuthorizedPermission(permission)) return false;
            return true;
        }

        public static void ConnectOrRetry()
        {
            if (instance == null) return;
            Joycon2Manager.Stop();
            instance.startupAttempted = false;
            instance.nextCheck = Time.unscaledTime + 0.1f;
            instance.RequestPermissions();
        }

        void RequestPermissions()
        {
            if (requestPending || quitting) return;
            var missing = new List<string>();
            foreach (string permission in permissions)
                if (!Permission.HasUserAuthorizedPermission(permission)) missing.Add(permission);
            if (missing.Count == 0) return;
            requestPending = true;
            Status = "Allow Nearby devices to connect Joy-Con 2";
            callbacks = new PermissionCallbacks();
            // Callbacks may be per permission. Do not start until every required permission is present.
            callbacks.PermissionGranted += OnPermissionResult;
            callbacks.PermissionDenied += OnPermissionResult;
#pragma warning disable CS0618 // Older Android reports permanent denial through this separate callback.
            callbacks.PermissionDeniedAndDontAskAgain += OnPermissionResult;
#pragma warning restore CS0618
            callbacks.PermissionRequestDismissed += OnPermissionResult;
            Permission.RequestUserPermissions(missing.ToArray(), callbacks);
        }

        void OnPermissionResult(string permission)
        {
            requestPending = false;
            startupAttempted = false;
            Status = Granted() ? "Hold SYNC on each Joy-Con 2" : "Bluetooth access denied; allow it in App settings";
        }

        void Update()
        {
            if (paused || requestPending || quitting || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            if (!Granted())
            {
                if (Joycon2Manager.IsRunning) Joycon2Manager.Stop();
                startupAttempted = false;
                Status = "Bluetooth permission needed — tap Connect or App settings";
                return;
            }
            if (Joycon2Manager.IsRunning) { Status = Joycon2Manager.Status; return; }
            if (startupAttempted) return;
            startupAttempted = true;
            Joycon2Manager.Start();
            Status = Joycon2Manager.IsRunning ? "Hold SYNC on each Joy-Con 2" : "BLE could not start — tap Connect to retry";
        }

        void OnApplicationPause(bool value)
        {
            paused = value;
            if (value) Joycon2Manager.Stop();
            else { startupAttempted = false; nextCheck = Time.unscaledTime + 0.3f; }
        }

        void OnApplicationQuit() { quitting = true; Joycon2Manager.Stop(); }
        void OnDestroy() { if (instance == this) instance = null; }

        public static void OpenSettings(bool bluetooth)
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = new AndroidJavaObject("android.content.Intent",
                bluetooth ? "android.settings.BLUETOOTH_SETTINGS" : "android.settings.APPLICATION_DETAILS_SETTINGS");
            if (!bluetooth)
            {
                using var uri = new AndroidJavaClass("android.net.Uri");
                using var package = uri.CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier);
                using var result = intent.Call<AndroidJavaObject>("setData", package);
            }
            activity.Call("startActivity", intent);
        }
    }
}
#endif
