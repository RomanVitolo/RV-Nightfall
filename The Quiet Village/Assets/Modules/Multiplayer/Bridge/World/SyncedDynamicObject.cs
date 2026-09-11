using Newtonsoft.Json.Linq;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Replicates a UHFPS door, drawer, lever or valve: its motion, its open or locked state, and its sounds.
    /// </summary>
    /// <remarks>
    /// All four DynamicObject types, in all three interaction modes, move exactly one transform
    /// (<c>DynamicObject.target</c>), so streaming that transform covers every combination with no per-type code.
    /// Followers pause the DynamicObject while the stream plays — its own update would otherwise keep writing
    /// its internal angle over the incoming one — and on the stream's end apply the author's state through
    /// <c>DynamicObject.OnLoad</c>, the same path a save game uses, which sets angles, open flags and lock
    /// status consistently so the next player to touch the door starts from the right place.
    ///
    /// The paused DynamicObject cannot play its own sounds, so the follower plays the one the author heard.
    /// A lock change without movement (unlocking with a key) is caught separately and sent as plain state.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DynamicObject))]
    public class SyncedDynamicObject : SyncedMotionEntity
    {
        [SerializeField] private DynamicObject m_dynamicObject;

        private bool m_suspended;
        private bool m_lastLocked;

        protected override Transform Moving => m_dynamicObject != null ? m_dynamicObject.target : null;

        protected override bool LocalSpace => true;

        private void Awake()
        {
            if (m_dynamicObject == null) m_dynamicObject = GetComponent<DynamicObject>();
        }

        protected override void OnBound()
        {
            base.OnBound();
            if (m_dynamicObject != null) m_lastLocked = m_dynamicObject.IsLocked;
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            PublishLockChange();
        }

        private void PublishLockChange()
        {
            if (m_dynamicObject == null || IsFollowing || !IsLive) return;

            var locked = m_dynamicObject.IsLocked;
            if (locked == m_lastLocked) return;

            m_lastLocked = locked;
            if (CanPublish) World.PublishState(this, CaptureState());
        }

        protected override string CaptureState() =>
            m_dynamicObject != null ? Serialize(m_dynamicObject.OnSave()) : null;

        protected override void SuspendDriver()
        {
            if (m_suspended || m_dynamicObject == null) return;

            m_dynamicObject.enabled = false;
            m_suspended = true;
        }

        protected override void ResumeDriver(string finalState)
        {
            ApplyState(finalState);

            if (!m_suspended || m_dynamicObject == null) return;

            m_dynamicObject.enabled = true;
            m_suspended = false;
        }

        internal override void ApplyRemoteState(string json)
        {
            // Mid-stream, the stream's end carries the authoritative state; applying this now would snap.
            if (IsFollowing) return;

            ApplyState(json);
        }

        private void ApplyState(string json)
        {
            var state = Parse(json);
            if (state == null || m_dynamicObject == null) return;

            m_dynamicObject.OnLoad(state);
            m_lastLocked = m_dynamicObject.IsLocked;
        }

        protected override void OnRemoteBegin(string state)
        {
            if (m_dynamicObject == null) return;

            var incoming = Parse(state);
            if (incoming == null) return;

            // A locked door being tried rattles in place; the rattle arrives as motion, the sound comes from here.
            if (incoming.Value<bool?>("isLocked") == true)
            {
                m_dynamicObject.PlaySound(DynamicSoundType.Locked);
                return;
            }

            var before = OpenFlag(Parse(CaptureState()));
            var after = OpenFlag(incoming);

            // Only a real open/close change makes a sound; a mouse drag that keeps the state does not.
            if (before.HasValue && after.HasValue && before.Value != after.Value)
                m_dynamicObject.PlaySound(after.Value ? DynamicSoundType.Open : DynamicSoundType.Close);
        }

        /// <summary>Each DynamicObject type saves its open state under its own name.</summary>
        private static bool? OpenFlag(JToken state)
        {
            if (state == null) return null;

            return state.Value<bool?>("isOpened") ?? state.Value<bool?>("isSwitched") ?? state.Value<bool?>("isRotated");
        }
    }
}
