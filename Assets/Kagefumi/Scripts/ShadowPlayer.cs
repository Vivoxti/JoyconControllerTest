using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Kagefumi
{
    /// <summary>
    /// One of the two duellists. The body can't be hurt; the shadow it casts can.
    /// Move with the stick, stomp-leap to land on the rival's shadow, crouch to shrink your own.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class ShadowPlayer : MonoBehaviour
    {
        [Header("Identity")]
        public int index;
        public Color color = Color.red;

        [Header("Rig")]
        [Tooltip("Visual root: moved up while leaping and squashed while crouching. Its colliders define the shadow.")]
        public Transform body;
        [Tooltip("Trigger colliders that match the visible silhouette (what actually casts the shadow).")]
        public Collider[] shadowColliders;

        [Header("Movement")]
        public float moveSpeed = 6.4f;
        public float acceleration = 50f;
        public float crouchSpeed = 2.3f;

        [Header("Stomp-leap")]
        public float leapDistance = 3f;
        public float leapTime = 0.3f;
        public float leapHeight = 1f;
        public float leapCooldown = 0.35f;

        [Header("Crouch")]
        [Range(0.2f, 1f)] public float crouchHeight = 0.4f;
        public float crouchStamina = 2.2f;
        public float crouchRegen = 0.9f;

        [Header("When stomped")]
        public float shadowlessTime = 1.8f;
        public float knockbackSpeed = 11f;

        public bool Controllable { get; set; }
        public bool IsLeaping { get; private set; }
        public bool IsShadowless => shadowlessTimer > 0f;
        /// <summary>Where a stomp pressed right now would land.</summary>
        public Vector3 LandingPoint { get; private set; }

        KagefumiGame game;
        CharacterController controller;
        InputAction moveAction, stompAction, crouchAction;
        readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        readonly List<MeshRenderer> casters = new List<MeshRenderer>();

        Vector3 velocity, knockback, facing = Vector3.forward;
        Vector3 leapStart, leapDir;
        float leapDist, leapT, cooldown;
        float crouch, stamina, regenDelay, squash, shadowlessTimer, lockPulse, inputGrace;
        bool exhausted, locked;

        RingLine footRing, staminaRing, reticle;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            stamina = crouchStamina;
            body.GetComponentsInChildren(true, renderers);
            foreach (var r in renderers)
                if (r.shadowCastingMode != ShadowCastingMode.Off) casters.Add(r);
        }

        public void Init(KagefumiGame owner, Material lineMaterial)
        {
            game = owner;
            footRing = new RingLine("Foot Ring", transform, lineMaterial);
            staminaRing = new RingLine("Crouch Stamina", transform, lineMaterial);
            reticle = new RingLine($"P{index + 1} Stomp Reticle", owner.transform, lineMaterial, 28);
            reticle.Visible = isActiveAndEnabled;
        }

        void OnEnable()
        {
            if (reticle != null) reticle.Visible = true;
        }

        void OnDisable()
        {
            if (reticle != null) reticle.Visible = false;
        }

        public void BindInput(InputAction move, InputAction stomp, InputAction crouchButton)
        {
            moveAction = move;
            stompAction = stomp;
            crouchAction = crouchButton;
            inputGrace = 0.25f; // the button that joined the game must not also trigger a leap
        }

        public void UnbindInput()
        {
            moveAction = stompAction = crouchAction = null;
        }

        public bool IsShadowCollider(Collider c) => System.Array.IndexOf(shadowColliders, c) >= 0;

        public void SetLock(bool on) => locked = on;

        public void ResetTo(Vector3 position, Vector3 face)
        {
            controller.enabled = false;
            transform.position = position;
            controller.enabled = true;
            velocity = knockback = Vector3.zero;
            facing = face;
            IsLeaping = false;
            leapT = cooldown = crouch = squash = shadowlessTimer = 0f;
            stamina = crouchStamina;
            exhausted = false;
            SetShadowCasting(true);
            foreach (var r in renderers) r.enabled = true;
            UpdateBody(0f);
            LandingPoint = position;
        }

        /// <summary>Your shadow was stepped on: it tears away for a moment and you get shoved.</summary>
        public void Stomped(Vector3 away)
        {
            away.y = 0f;
            knockback = (away.sqrMagnitude > 0.001f ? away.normalized : -facing) * knockbackSpeed;
            shadowlessTimer = shadowlessTime;
            SetShadowCasting(false);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector2 stick = Vector2.zero;
            bool stompPressed = false, crouchHeld = false;
            if (Controllable && moveAction != null)
            {
                stick = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
                stompPressed = inputGrace <= 0f && stompAction.WasPressedThisFrame();
                crouchHeld = crouchAction.IsPressed();
            }
            inputGrace -= dt;
            var wish = new Vector3(stick.x, 0f, stick.y);

            UpdateCrouch(crouchHeld && !IsLeaping, dt);

            cooldown -= dt;
            if (stompPressed && !IsLeaping && cooldown <= 0f) BeginLeap(wish);

            Vector3 motion;
            if (IsLeaping)
            {
                leapT += dt / leapTime;
                motion = leapDir * (leapDist / leapTime);
            }
            else
            {
                float speed = Mathf.Lerp(moveSpeed, crouchSpeed, crouch);
                velocity = Vector3.MoveTowards(velocity, wish * speed, acceleration * dt);
                motion = velocity;
            }

            knockback *= Mathf.Exp(-6f * dt);
            if (controller.enabled) controller.Move((motion + knockback) * dt);
            SnapToGround();

            var planar = IsLeaping ? leapDir : velocity;
            if (planar.sqrMagnitude > 0.04f)
                facing = Vector3.Slerp(facing, planar.normalized, 1f - Mathf.Exp(-16f * dt));

            if (IsLeaping && leapT >= 1f) Land();

            UpdateBody(dt);
            LandingPoint = IsLeaping ? leapStart + leapDir * leapDist : transform.position + wish * leapDistance;
            UpdateShadowless(dt);
            UpdateIndicators(dt);
        }

        void UpdateCrouch(bool want, float dt)
        {
            want &= !exhausted;
            if (want)
            {
                stamina -= dt;
                regenDelay = 0.4f;
                if (stamina <= 0f)
                {
                    stamina = 0f;
                    exhausted = true;
                    want = false;
                }
            }
            else
            {
                regenDelay -= dt;
                if (regenDelay <= 0f) stamina = Mathf.Min(crouchStamina, stamina + crouchRegen * dt);
                if (exhausted && stamina >= crouchStamina * 0.4f) exhausted = false;
            }
            crouch = Mathf.MoveTowards(crouch, want ? 1f : 0f, dt * 10f);
        }

        void BeginLeap(Vector3 wish)
        {
            float mag = Mathf.Clamp01(wish.magnitude);
            IsLeaping = true;
            leapT = 0f;
            leapDir = mag > 0.15f ? wish.normalized : facing;
            leapDist = leapDistance * mag; // neutral stick = stomp in place
            leapStart = transform.position;
            crouch = 0f; // spring out of a crouch
            game.Sfx.Leap();
        }

        void Land()
        {
            IsLeaping = false;
            leapT = 0f;
            cooldown = leapCooldown;
            squash = 1f;
            velocity = leapDir * moveSpeed * 0.35f * (leapDist / leapDistance);
            UpdateBody(0f); // put the body back on the ground before the shadow is judged
            game.HandleLanding(this, transform.position);
        }

        void SnapToGround()
        {
            var p = transform.position;
            if (Mathf.Abs(p.y) < 0.0005f) return;
            p.y = 0f;
            controller.enabled = false;
            transform.position = p;
            controller.enabled = true;
        }

        void UpdateBody(float dt)
        {
            float jump = IsLeaping ? 4f * leapHeight * leapT * (1f - leapT) : 0f;
            squash = Mathf.MoveTowards(squash, 0f, dt * 5f);
            float h = Mathf.Lerp(1f, crouchHeight, crouch) * (1f - 0.3f * squash) * (IsLeaping ? 1.1f : 1f);
            float w = (1f + 0.2f * crouch) * (1f + 0.25f * squash) * (IsLeaping ? 0.94f : 1f);
            body.localPosition = new Vector3(0f, jump, 0f);
            body.localScale = new Vector3(w, h, w);
            body.rotation = Quaternion.LookRotation(facing, Vector3.up);
        }

        void UpdateShadowless(float dt)
        {
            if (shadowlessTimer <= 0f) return;
            shadowlessTimer -= dt;
            bool done = shadowlessTimer <= 0f;
            bool visible = done || Mathf.Repeat(shadowlessTimer * 12f, 1f) > 0.3f;
            foreach (var r in renderers) r.enabled = visible;
            if (done) SetShadowCasting(true);
        }

        void SetShadowCasting(bool on)
        {
            foreach (var r in casters) r.shadowCastingMode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        void UpdateIndicators(float dt)
        {
            if (footRing == null) return;
            lockPulse = locked ? lockPulse + dt * 14f : 0f;
            float pulse = locked ? 0.5f + 0.5f * Mathf.Sin(lockPulse) : 0f;
            var bright = Color.Lerp(Color.white, color, 0.15f);

            // A locked stomp makes your own ring flash too, so you notice it while watching your character.
            var feet = transform.position + Vector3.up * 0.03f;
            if (locked) footRing.Set(feet, 0.6f + 0.12f * pulse, 0.1f, bright);
            else footRing.Set(feet, 0.58f, 0.06f, new Color(color.r, color.g, color.b, IsShadowless ? 0.3f : 0.9f));

            bool showStamina = stamina < crouchStamina - 0.01f || crouch > 0.01f;
            staminaRing.Visible = showStamina;
            if (showStamina)
            {
                var c = exhausted ? new Color(1f, 0.35f, 0.3f, 0.85f) : new Color(1f, 1f, 1f, 0.8f);
                staminaRing.Set(feet, 0.76f, 0.05f, c, stamina / crouchStamina);
            }

            if (locked) reticle.Set(LandingPoint + Vector3.up * 0.04f, 0.38f + 0.12f * pulse, 0.14f, bright);
            else reticle.Set(LandingPoint + Vector3.up * 0.04f, 0.3f, 0.045f, new Color(color.r, color.g, color.b, 0.5f));
        }
    }
}
