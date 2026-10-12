using UnityEngine;
using UnityEngine.Rendering;

namespace Kagefumi
{
    /// <summary>A flat circle (or arc) drawn on the ground with a LineRenderer.</summary>
    public class RingLine
    {
        readonly LineRenderer line;
        readonly int segments;
        float builtRadius = -1f, builtArc = -1f;

        public RingLine(string name, Transform parent, Material material, int segments = 40)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false;
            line.alignment = LineAlignment.TransformZ;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.numCapVertices = 2;
            this.segments = segments;
        }

        public bool Visible
        {
            set => line.enabled = value;
        }

        /// <param name="arc">Fraction of the circle to draw, clockwise from 12 o'clock.</param>
        public void Set(Vector3 worldCenter, float radius, float width, Color color, float arc = 1f)
        {
            var tr = line.transform;
            tr.SetPositionAndRotation(worldCenter, Quaternion.Euler(90f, 0f, 0f));
            line.widthMultiplier = width;
            line.startColor = color;
            line.endColor = color;
            arc = Mathf.Clamp01(arc);
            if (!Mathf.Approximately(radius, builtRadius) || Mathf.Abs(arc - builtArc) > 0.005f) Rebuild(radius, arc);
        }

        public void Destroy() => Object.Destroy(line.gameObject);

        void Rebuild(float radius, float arc)
        {
            builtRadius = radius;
            builtArc = arc;
            bool full = arc >= 0.999f;
            int count = full ? segments : Mathf.Max(2, Mathf.CeilToInt(segments * arc) + 1);
            line.loop = full;
            line.positionCount = count;
            float span = Mathf.PI * 2f * (full ? 1f : arc);
            for (int i = 0; i < count; i++)
            {
                float a = Mathf.PI * 0.5f - span * i / (full ? count : count - 1);
                line.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
            }
        }
    }
}
