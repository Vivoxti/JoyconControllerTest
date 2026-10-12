using System;
using UnityEngine;

namespace Kagefumi
{
    /// <summary>Every sound in the game is synthesized at startup — no audio assets needed.</summary>
    public class KagefumiSfx : MonoBehaviour
    {
        const int Rate = 44100;
        const float Tau = Mathf.PI * 2f;

        AudioSource source;
        AudioClip leap, land, tear, beep, go, join, win;
        readonly System.Random rng = new System.Random(1234);

        void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;

            float lp = 0f;
            leap = Build("leap", 0.18f, t =>
            {
                // Breathy whoosh: low-passed noise that opens up as it rises.
                float k = Mathf.Lerp(0.05f, 0.35f, t / 0.18f);
                lp += (Noise() - lp) * k;
                return lp * Mathf.Sin(Mathf.PI * t / 0.18f) * 0.55f;
            });

            float ph = 0f;
            land = Build("land", 0.22f, t =>
            {
                float f = 45f + 110f * Mathf.Exp(-t * 18f);
                ph += f / Rate;
                float thud = Mathf.Sin(ph * Tau) * Mathf.Exp(-t * 16f);
                float click = Noise() * Mathf.Exp(-t * 90f) * 0.3f;
                return (thud + click) * 0.8f;
            });

            float ph2 = 0f, lp2 = 0f;
            tear = Build("tear", 0.6f, t =>
            {
                // Paper rip + falling tone + low boom: the sound of a shadow being torn off.
                float f = 70f + 520f * Mathf.Exp(-t * 9f);
                ph2 += f / Rate;
                float tone = Mathf.Sin(ph2 * Tau) * Mathf.Exp(-t * 5f) * 0.45f;
                lp2 += (Noise() - lp2) * 0.3f;
                float crackle = (Noise() > 0.6f ? 1f : 0.35f);
                float rip = lp2 * crackle * Mathf.Exp(-t * 7f) * 1.1f;
                float boom = Mathf.Sin(Tau * 50f * t) * Mathf.Exp(-t * 9f) * 0.7f;
                return (tone + rip + boom) * 0.75f;
            });

            beep = Build("beep", 0.14f, t => Mathf.Sin(Tau * 660f * t) * Envelope(t, 0.14f) * 0.4f);
            go = Build("go", 0.4f, t => (Mathf.Sin(Tau * 880f * t) + 0.6f * Mathf.Sin(Tau * 1320f * t)) * Envelope(t, 0.4f) * 0.3f);
            join = Build("join", 0.22f, t => Mathf.Sin(Tau * (t < 0.09f ? 660f : 990f) * t) * Envelope(t, 0.22f) * 0.35f);

            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
            win = Build("win", 1.1f, t =>
            {
                int n = Mathf.Min(notes.Length - 1, (int)(t / 0.12f));
                float local = t - n * 0.12f;
                float len = n == notes.Length - 1 ? 0.75f : 0.12f;
                float tri = Mathf.PingPong(t * notes[n] * 2f, 1f) * 2f - 1f;
                return tri * Envelope(local, len) * 0.35f;
            });
        }

        public void Leap() => Play(leap, 0.7f);
        public void Land() => Play(land, 0.8f);
        public void Tear() => Play(tear, 1f);
        public void Beep() => Play(beep, 0.8f);
        public void Go() => Play(go, 0.9f);
        public void Join() => Play(join, 0.8f);
        public void Win() => Play(win, 0.9f);

        void Play(AudioClip clip, float volume)
        {
            if (clip != null) source.PlayOneShot(clip, volume);
        }

        float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

        static float Envelope(float t, float length)
        {
            if (t < 0f || t > length) return 0f;
            float attack = Mathf.Clamp01(t / 0.008f);
            float release = Mathf.Clamp01((length - t) / (length * 0.6f));
            return attack * release;
        }

        static AudioClip Build(string name, float seconds, Func<float, float> sample)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(sample(i / (float)Rate), -1f, 1f);
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
