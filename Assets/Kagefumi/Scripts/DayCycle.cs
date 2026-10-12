using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kagefumi
{
    /// <summary>
    /// Drives the sun across the sky and the street lamps at night. The sun's path decides where
    /// every shadow points and how long it is, so the time of day IS the level design.
    /// </summary>
    public class DayCycle : MonoBehaviour
    {
        public enum Phase { Dawn, Day, Dusk, Night }

        public struct LightInfo
        {
            public bool directional;
            public Vector3 position;
            public Vector3 forward;
            public float range;
        }

        [Header("Lights")]
        public Light sun;
        public Light[] lamps;
        public Renderer[] lampBulbs;
        public Camera skyCamera;

        [Header("Timing (seconds)")]
        public float dawnDuration = 3f;
        public float dayDuration = 36f;
        public float duskDuration = 3f;
        public float nightDuration = 18f;
        public bool paused;
        [Tooltip("Multiplies the clock while not paused. 0 freezes the sun's own travel (e.g. while a player holds it).")]
        public float clockScale = 1f;

        [Header("Sun path")]
        public float minElevation = 15f;
        public float maxElevation = 64f;
        public float sunIntensity = 1.8f;
        [Tooltip("Extra brightness for a low sun, which hits the ground at a grazing angle.")]
        public float lowSunBoost = 1.9f;
        public Color sunLow = new Color(1f, 0.74f, 0.52f);
        public Color sunHigh = new Color(1f, 0.96f, 0.88f);

        [Header("Sun tilt (SUN GRAB)")]
        [Tooltip("World-space rotation applied to the sun's position on its path (identity = untouched).")]
        public Quaternion sunTilt = Quaternion.identity;
        [Tooltip("A tilted sun never sinks below this many degrees above the horizon.")]
        public float tiltedMinElevation = 5f;

        [Header("Lamps")]
        public float lampIntensity = 60f;
        public float lampGameplayRange = 15f;
        public Color bulbColor = new Color(1f, 0.78f, 0.45f);
        public float bulbEmission = 8f;

        [Header("Ambient & sky")]
        public Color ambientNoon = new Color(0.56f, 0.6f, 0.68f);
        public Color ambientLow = new Color(0.56f, 0.48f, 0.54f);
        public Color ambientNight = new Color(0.1f, 0.12f, 0.24f);
        public Color skyNoon = new Color(0.55f, 0.72f, 0.82f);
        public Color skyLow = new Color(0.95f, 0.55f, 0.42f);
        public Color skyNight = new Color(0.04f, 0.05f, 0.12f);

        [Tooltip("A light only creates stompable shadows while it is at least this bright (0..1).")]
        [Range(0f, 1f)] public float gameplayThreshold = 0.35f;

        public float CycleTime { get; private set; }
        public float CycleLength => dawnDuration + dayDuration + duskDuration + nightDuration;
        public Phase CurrentPhase { get; private set; }
        public float PhaseProgress { get; private set; }
        /// <summary>0 = sunrise, 1 = sunset.</summary>
        public float DayProgress { get; private set; }
        public List<LightInfo> ActiveLights { get; } = new List<LightInfo>();

        public string PhaseLabel
        {
            get
            {
                switch (CurrentPhase)
                {
                    case Phase.Dawn: return "DAWN";
                    case Phase.Dusk: return "DUSK";
                    case Phase.Night: return "NIGHT";
                    default: return DayProgress < 0.33f ? "MORNING" : DayProgress < 0.67f ? "NOON" : "AFTERNOON";
                }
            }
        }

        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        float[] flicker;
        int flickerLamp = -1;
        float flickerEnd, nextFlicker;
        MaterialPropertyBlock block;

        void Awake()
        {
            flicker = new float[lamps.Length];
            for (int i = 0; i < flicker.Length; i++) flicker[i] = 1f;
            block = new MaterialPropertyBlock();
            if (skyCamera == null) skyCamera = Camera.main;
            Apply();
        }

        void Update()
        {
            if (!paused) CycleTime = Mathf.Repeat(CycleTime + Time.deltaTime * clockScale, CycleLength);
            UpdateFlicker();
            Apply();
        }

        /// <summary>Jump to the first moment of full daylight: long morning shadows.</summary>
        public void ResetToMorning()
        {
            CycleTime = dawnDuration;
            clockScale = 1f;
            sunTilt = Quaternion.identity;
            flickerLamp = -1;
            for (int i = 0; i < flicker.Length; i++) flicker[i] = 1f;
            Apply();
        }

        void UpdateFlicker()
        {
            for (int i = 0; i < flicker.Length; i++) flicker[i] = 1f;
            if (CurrentPhase != Phase.Night)
            {
                flickerLamp = -1;
                return;
            }

            float now = Time.time;
            if (flickerLamp < 0 && now >= nextFlicker)
            {
                flickerLamp = Random.Range(0, lamps.Length);
                flickerEnd = now + Random.Range(0.9f, 1.8f);
            }
            if (flickerLamp >= 0)
            {
                if (now >= flickerEnd)
                {
                    flickerLamp = -1;
                    nextFlicker = now + Random.Range(2.5f, 5f);
                }
                else
                {
                    flicker[flickerLamp] = Mathf.PerlinNoise(now * 16f, flickerLamp * 3.1f) > 0.45f ? 1f : 0.04f;
                }
            }
        }

        void Apply()
        {
            float t = CycleTime;
            float sunFactor, lampFactor;
            if (t < dawnDuration)
            {
                CurrentPhase = Phase.Dawn;
                PhaseProgress = t / dawnDuration;
                DayProgress = 0f;
                sunFactor = PhaseProgress;
                lampFactor = 1f - PhaseProgress;
            }
            else if ((t -= dawnDuration) < dayDuration)
            {
                CurrentPhase = Phase.Day;
                PhaseProgress = t / dayDuration;
                DayProgress = PhaseProgress;
                sunFactor = 1f;
                lampFactor = 0f;
            }
            else if ((t -= dayDuration) < duskDuration)
            {
                CurrentPhase = Phase.Dusk;
                PhaseProgress = t / duskDuration;
                DayProgress = 1f;
                sunFactor = 1f - PhaseProgress;
                lampFactor = PhaseProgress;
            }
            else
            {
                t -= duskDuration;
                CurrentPhase = Phase.Night;
                PhaseProgress = t / nightDuration;
                DayProgress = 1f;
                sunFactor = 0f;
                lampFactor = 1f;
            }

            // Shadows sweep from west (morning) through north (noon) to east (evening).
            float height = Mathf.Sin(Mathf.PI * DayProgress);
            float elevation = Mathf.Lerp(minElevation, maxElevation, height);
            float azimuth = 270f + 180f * DayProgress;
            var sunRotation = Quaternion.Euler(elevation, azimuth, 0f);
            if (Quaternion.Angle(sunTilt, Quaternion.identity) > 0.01f)
            {
                // A player holds the sun: rotate the direction towards it, keep it above the horizon.
                var toSun = sunTilt * (sunRotation * Vector3.back);
                float minY = Mathf.Sin(tiltedMinElevation * Mathf.Deg2Rad);
                if (toSun.y < minY)
                {
                    var flat = new Vector3(toSun.x, 0f, toSun.z);
                    flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
                    toSun = flat * Mathf.Cos(tiltedMinElevation * Mathf.Deg2Rad) + Vector3.up * minY;
                }
                sunRotation = Quaternion.LookRotation(-toSun, Mathf.Abs(toSun.y) > 0.999f ? Vector3.forward : Vector3.up);
                float tiltedElevation = Mathf.Asin(Mathf.Clamp(toSun.y, -1f, 1f)) * Mathf.Rad2Deg;
                height = Mathf.InverseLerp(minElevation, maxElevation, tiltedElevation);
            }
            sun.transform.rotation = sunRotation;
            float sunK = Smooth(sunFactor);

            sun.color = Color.Lerp(sunLow, sunHigh, height);
            sun.intensity = sunIntensity * sunK * Mathf.Lerp(lowSunBoost, 1f, height);
            sun.enabled = sunK > 0.001f;

            for (int i = 0; i < lamps.Length; i++)
            {
                float k = Smooth(lampFactor) * flicker[i];
                lamps[i].intensity = lampIntensity * k;
                lamps[i].enabled = k > 0.001f;
                if (lampBulbs != null && i < lampBulbs.Length && lampBulbs[i] != null)
                {
                    lampBulbs[i].GetPropertyBlock(block);
                    block.SetColor(EmissionId, bulbColor * (0.05f + bulbEmission * k));
                    lampBulbs[i].SetPropertyBlock(block);
                }
            }

            var ambient = Color.Lerp(ambientNight, Color.Lerp(ambientLow, ambientNoon, height), sunK);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(ambient);
            RenderSettings.ambientProbe = sh;
            if (skyCamera != null) skyCamera.backgroundColor = Color.Lerp(skyNight, Color.Lerp(skyLow, skyNoon, height), sunK);

            ActiveLights.Clear();
            if (sunFactor >= gameplayThreshold)
                ActiveLights.Add(new LightInfo { directional = true, forward = sun.transform.forward });
            for (int i = 0; i < lamps.Length; i++)
            {
                if (lampFactor * flicker[i] < gameplayThreshold) continue;
                ActiveLights.Add(new LightInfo { position = lamps[i].transform.position, range = lampGameplayRange });
            }
        }

        static float Smooth(float x) => x * x * (3f - 2f * x);
    }
}
