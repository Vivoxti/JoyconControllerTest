using System.Collections;
using UnityEngine;

namespace Kagefumi
{
    /// <summary>Hit and landing effects, all generated at runtime from one unlit material.</summary>
    public class KagefumiFx : MonoBehaviour
    {
        Material material;
        ParticleSystem ink, dust;

        public void Init(Material unlit)
        {
            material = unlit;
            // Torn shadow scraps: dark paper-like squares that burst out of the stomp.
            ink = CreateSystem("Ink Burst", new Vector2(0.18f, 0.46f), new Vector2(4f, 10f), new Vector2(0.6f, 1.2f), 2.6f);
            dust = CreateSystem("Landing Dust", new Vector2(0.18f, 0.42f), new Vector2(0.8f, 2.4f), new Vector2(0.3f, 0.55f), 0.2f);
        }

        public void InkBurst(Vector3 point, int count)
        {
            Emit(ink, point, count, new Color(0.06f, 0.05f, 0.1f, 1f));
        }

        public void Dust(Vector3 point)
        {
            Emit(dust, point, 10, new Color(0.95f, 0.9f, 0.8f, 0.7f));
        }

        public void Shockwave(Vector3 point, Color color, float maxRadius)
        {
            StartCoroutine(ShockwaveRoutine(point, color, maxRadius));
        }

        IEnumerator ShockwaveRoutine(Vector3 point, Color color, float maxRadius)
        {
            var ring = new RingLine("Shockwave", transform, material, 56);
            var center = new Vector3(point.x, 0.05f, point.z);
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.5f)
            {
                float e = 1f - Mathf.Pow(1f - t, 3f);
                ring.Set(center, Mathf.Lerp(0.3f, maxRadius, e), Mathf.Lerp(0.28f, 0.02f, t),
                         new Color(color.r, color.g, color.b, 1f - t));
                yield return null;
            }
            ring.Destroy();
        }

        static void Emit(ParticleSystem ps, Vector3 point, int count, Color color)
        {
            ps.transform.position = new Vector3(point.x, 0.15f, point.z);
            var emit = new ParticleSystem.EmitParams { startColor = color };
            ps.Emit(emit, count);
        }

        ParticleSystem CreateSystem(string name, Vector2 size, Vector2 speed, Vector2 life, float gravity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.25f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var spin = ps.rotationOverLifetime;
            spin.enabled = true;
            spin.z = new ParticleSystem.MinMaxCurve(-8f, 8f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ps.Play();
            return ps;
        }
    }
}
