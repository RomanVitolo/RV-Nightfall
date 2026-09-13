using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Dresses a night creature in its catalog body and animates it from how it moves and what the host says it does.
    /// </summary>
    /// <remarks>
    /// Runs on every client, including the host, and replicates nothing itself. Movement is read from the transform, which
    /// the agent drives on the host and NetworkTransform delivers everywhere else, so walking and running cost no extra
    /// traffic and cannot disagree with where the creature actually is. Strikes, the entrance and death arrive from
    /// <see cref="NightCreature"/>.
    ///
    /// Parameter names are the contract with <c>CreatureBase.controller</c>, which <c>CreatureSetup</c> builds from these
    /// same constants; a mismatch fails silently and the body stands frozen.
    /// </remarks>
    public class CreatureBody : MonoBehaviour
    {
        public const string MoveBlendParameter = "MoveBlend";
        public const string MoveTimeScaleParameter = "MoveTimeScale";
        public const string AttackParameter = "Attack";
        public const string AttackVariantParameter = "AttackVariant";
        public const string EntranceParameter = "Entrance";
        public const string DeadParameter = "Dead";

        /// <summary>Attack clips behind the attack blend tree; the setup tool repeats clips to fill it.</summary>
        public const int AttackVariants = 3;

        /// <summary>MoveBlend values of the locomotion tree's points.</summary>
        public const float IdleBlend = 0f, WalkBlend = 1f, RunBlend = 2f;

        private static readonly int MoveBlendHash = Animator.StringToHash(MoveBlendParameter);
        private static readonly int MoveTimeScaleHash = Animator.StringToHash(MoveTimeScaleParameter);
        private static readonly int AttackHash = Animator.StringToHash(AttackParameter);
        private static readonly int AttackVariantHash = Animator.StringToHash(AttackVariantParameter);
        private static readonly int EntranceHash = Animator.StringToHash(EntranceParameter);
        private static readonly int DeadHash = Animator.StringToHash(DeadParameter);

        // Below this the creature is standing still; interpolation jitter must not shuffle its feet.
        private const float StillSpeed = 0.15f;
        private const float SpeedSmoothing = 8f;

        private CreatureCatalog.Body m_body;
        private Animator m_animator;
        private GameObject m_model;
        private Vector3 m_lastPosition;
        private float m_speed;

        public bool HasBody => m_animator != null;

        /// <summary>Builds the body and hides the placeholder. Call once, as the creature spawns.</summary>
        /// <returns><c>false</c> if the body could not be built; the placeholder then stays.</returns>
        public bool Build(CreatureCatalog.Body body)
        {
            if (body == null || body.Prefab == null || m_model != null) return false;

            // Built under an inactive holder so nothing on the vendor prefab wakes up before it is stripped and configured.
            var holder = new GameObject("CreatureBodyHolder");
            holder.SetActive(false);

            var model = Instantiate(body.Prefab, holder.transform, false);
            model.name = $"Body ({body.Prefab.name})";
            StripBehaviour(model);

            var animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null) animator = model.AddComponent<Animator>();

            animator.runtimeAnimatorController = body.Controller;
            if (body.Avatar != null) animator.avatar = body.Avatar;

            // The agent moves the creature and NetworkTransform replicates it; root motion would walk the model off both.
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            ApplyMaterialSwaps(model, body);

            model.transform.SetParent(transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0f, body.YawOffset, 0f);
            model.transform.localScale = Vector3.one * body.Scale;
            SetLayer(model, gameObject.layer);
            Destroy(holder);

            HidePlaceholder(model);

            m_body = body;
            m_model = model;
            m_animator = animator;
            m_lastPosition = transform.position;
            return true;
        }

        public void PlayAttack(int variant)
        {
            if (m_animator == null) return;

            m_animator.SetFloat(AttackVariantHash, Mathf.Clamp(variant, 0, AttackVariants - 1));
            m_animator.SetTrigger(AttackHash);
        }

        public void PlayEntrance()
        {
            if (m_animator != null && m_body != null && m_body.HasEntrance) m_animator.SetTrigger(EntranceHash);
        }

        public void SetDead(bool dead)
        {
            if (m_animator != null) m_animator.SetBool(DeadHash, dead);
        }

        private void Update()
        {
            if (m_animator == null || m_body == null) return;

            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            var position = transform.position;
            var planar = position - m_lastPosition;
            planar.y = 0f;
            m_lastPosition = position;

            // Smoothed, because NetworkTransform delivers positions in steps and raw per-frame speed flickers between them.
            m_speed = Mathf.Lerp(m_speed, planar.magnitude / deltaTime, 1f - Mathf.Exp(-SpeedSmoothing * deltaTime));
            var speed = m_speed < StillSpeed ? 0f : m_speed;

            m_animator.SetFloat(MoveBlendHash, BlendFor(speed));
            m_animator.SetFloat(MoveTimeScaleHash, TimeScaleFor(speed));
        }

        /// <summary>Idle at 0, walk at 1 (its clip speed), run at 2 (its clip speed), linear between.</summary>
        private float BlendFor(float speed)
        {
            var walk = m_body.WalkClipSpeed;
            var run = Mathf.Max(walk + 0.01f, m_body.RunClipSpeed);

            if (speed <= walk) return Mathf.Lerp(IdleBlend, WalkBlend, speed / walk);
            return Mathf.Lerp(WalkBlend, RunBlend, (speed - walk) / (run - walk));
        }

        /// <summary>
        /// Playback rate that keeps the feet from sliding: the speed actually moved over the speed the blended clips show.
        /// </summary>
        /// <remarks>
        /// Up to run speed the blend already shows the right speed, since its points sit at the clips' own speeds. Only a
        /// creature faster than its run clip needs the clip sped up. Clamped, since a large correction reads as
        /// fast-forward, which looks worse than a little sliding.
        /// </remarks>
        private float TimeScaleFor(float speed)
        {
            if (speed <= m_body.RunClipSpeed) return 1f;

            return Mathf.Clamp(speed / m_body.RunClipSpeed, 1f, 1.8f);
        }

        /// <summary>
        /// Removes what a pack's demo prefab brings besides looks: its physics, scripts and navigation.
        /// </summary>
        /// <remarks>
        /// The creature root already has the collider the level expects and the only agent that may move it. A vendor
        /// collider would block its own wall checks, and a demo script might play its own animations over these.
        /// </remarks>
        private static void StripBehaviour(GameObject model)
        {
            foreach (var behaviour in model.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(behaviour);
            foreach (var agent in model.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) DestroyImmediate(agent);
            foreach (var joint in model.GetComponentsInChildren<Joint>(true)) DestroyImmediate(joint);
            foreach (var rigidbody in model.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(rigidbody);
            foreach (var collider in model.GetComponentsInChildren<Collider>(true)) DestroyImmediate(collider);
        }

        private static void ApplyMaterialSwaps(GameObject model, CreatureCatalog.Body body)
        {
            if (body.MaterialSwaps == null || body.MaterialSwaps.Count == 0) return;

            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;

                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;

                    foreach (var swap in body.MaterialSwaps)
                    {
                        if (swap == null || swap.Replacement == null || string.IsNullOrEmpty(swap.NameContains)) continue;
                        if (materials[i].name.IndexOf(swap.NameContains, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                        materials[i] = swap.Replacement;
                        changed = true;
                        break;
                    }
                }

                if (changed) renderer.sharedMaterials = materials;
            }
        }

        /// <summary>Turns off the capsule body's renderers, leaving its collider for the level to hit.</summary>
        private void HidePlaceholder(GameObject model)
        {
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.transform.IsChildOf(model.transform)) renderer.enabled = false;
            }
        }

        private static void SetLayer(GameObject root, int layer)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
        }
    }
}
