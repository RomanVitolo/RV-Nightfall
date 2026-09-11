using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Replicates a physics prop — carried, dragged, thrown or knocked over — from the client whose physics moved it.
    /// </summary>
    /// <remarks>
    /// Physics runs independently on every client, so two copies of a thrown box would land in different places.
    /// The author's simulation is the one that counts: followers make their copy kinematic while the stream
    /// plays, so their own physics cannot pull it elsewhere, then restore it at the author's resting pose.
    ///
    /// A prop only ends its stream once it is still and its rigidbody has come to rest, so a box sliding across
    /// the floor after being let go keeps streaming until it actually stops.
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class SyncedRigidbody : SyncedMotionEntity
    {
        [SerializeField] private Rigidbody m_body;

        [Tooltip("Speed (m/s) below which a still body counts as at rest.")]
        [SerializeField] private float m_restSpeed = 0.05f;

        private bool m_suspended;
        private bool m_wasKinematic;

        protected override Transform Moving => m_body != null ? m_body.transform : null;

        protected override bool LocalSpace => false;

        protected override bool CanRest =>
            m_body == null || m_body.isKinematic || m_body.IsSleeping()
            || (m_body.linearVelocity.sqrMagnitude < m_restSpeed * m_restSpeed
                && m_body.angularVelocity.sqrMagnitude < m_restSpeed * m_restSpeed);

        private void Awake()
        {
            if (m_body == null) m_body = GetComponent<Rigidbody>();
        }

        protected override string CaptureState() => m_body != null ? PoseToJson(m_body.transform) : null;

        protected override void SuspendDriver()
        {
            if (m_suspended || m_body == null) return;

            m_wasKinematic = m_body.isKinematic;
            m_body.isKinematic = true;
            m_suspended = true;
        }

        protected override void ResumeDriver(string finalState)
        {
            if (m_body == null) return;

            ApplyPoseJson(m_body.transform, finalState);

            if (!m_suspended) return;

            m_body.isKinematic = m_wasKinematic;
            if (!m_body.isKinematic)
            {
                // At rest where the author's copy came to rest; leftover velocity would carry it off again.
                m_body.linearVelocity = Vector3.zero;
                m_body.angularVelocity = Vector3.zero;
            }

            m_suspended = false;
        }
    }
}
