using UnityEngine;

namespace Kagefumi
{
    /// <summary>
    /// Answers one question: "is this patch of ground darkened by that player's body?"
    /// It traces from the ground towards every light the DayCycle reports as active, which is
    /// exactly the test the renderer's shadow maps perform — so what you see is what scores.
    /// </summary>
    public static class ShadowJudge
    {
        static readonly RaycastHit[] Hits = new RaycastHit[32];

        static readonly Vector2[] DiscSamples =
        {
            Vector2.zero,
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.7f, 0.7f), new Vector2(-0.7f, 0.7f), new Vector2(0.7f, -0.7f), new Vector2(-0.7f, -0.7f),
        };

        /// <summary>True when any point of a foot-sized disc lands on <paramref name="owner"/>'s shadow.</summary>
        public static bool DiscInShadow(Vector3 center, float radius, ShadowPlayer owner, DayCycle lights)
        {
            if (owner == null || lights == null || owner.IsShadowless || !owner.isActiveAndEnabled) return false;

            foreach (var s in DiscSamples)
            {
                var p = new Vector3(center.x + s.x * radius, 0f, center.z + s.y * radius);
                if (PointInShadow(p, owner, lights)) return true;
            }
            return false;
        }

        /// <summary>
        /// A ground point belongs to a player's shadow when the ray towards a light hits that player's
        /// body and nothing else solid: shadows that merge with scenery shade don't count.
        /// Other players never block: the attacker standing on the point must not shade it.
        /// </summary>
        public static bool PointInShadow(Vector3 groundPoint, ShadowPlayer owner, DayCycle lights)
        {
            var origin = new Vector3(groundPoint.x, 0.03f, groundPoint.z);
            var active = lights.ActiveLights;

            for (int l = 0; l < active.Count; l++)
            {
                var light = active[l];
                Vector3 dir;
                float dist;
                if (light.directional)
                {
                    dir = -light.forward;
                    dist = 80f;
                }
                else
                {
                    var to = light.position - origin;
                    dist = to.magnitude;
                    if (dist > light.range || dist < 0.01f) continue;
                    dir = to / dist;
                }

                int n = Physics.RaycastNonAlloc(origin, dir, Hits, dist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
                bool hitOwner = false, blocked = false;
                for (int i = 0; i < n && !blocked; i++)
                {
                    var col = Hits[i].collider;
                    var player = col.GetComponentInParent<ShadowPlayer>();
                    if (player != null)
                    {
                        if (player == owner && owner.IsShadowCollider(col)) hitOwner = true;
                        continue;
                    }
                    if (!col.isTrigger) blocked = true;
                }
                if (hitOwner && !blocked) return true;
            }
            return false;
        }
    }
}
