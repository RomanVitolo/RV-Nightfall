using System;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Lets only one player examine an object at a time: first come, first served, decided by the host.
    /// </summary>
    /// <remarks>
    /// ExamineController asks this gate before lifting the object. If another player holds it, the request is
    /// refused on the spot with a hint — every client knows who holds what, because the host broadcasts each
    /// change — so the common case costs no round trip. Otherwise the gate asks the host and the examine starts
    /// when the grant comes back; two players pressing examine in the same instant are settled there, and the
    /// loser is told the object is in use.
    ///
    /// Waiting for the grant, rather than starting at once and backing out, costs one round trip before the
    /// object lifts, but never shows a player an examine that is then yanked away.
    ///
    /// The lock is released when the examine ends, when the item is taken into the inventory mid-examine
    /// (which ends no examine — UHFPS just clears it), and by the host if the examiner disconnects.
    /// </remarks>
    [DisallowMultipleComponent]
    public class SyncedExamineLock : WorldSyncEntity, IExamineGate
    {
        [SerializeField] private InteractableItem m_item;

        [Tooltip("Seconds a grant may go unused before it is handed back, e.g. if the player looked away meanwhile.")]
        [SerializeField] private float m_unusedGrantTimeout = 1f;

        private const string InUseHint = "Someone else is examining this.";

        private SyncedMotionEntity m_motion;
        private ulong m_owner = WorldSync.NoOwner;
        private bool m_pending;
        private Action m_onGranted;
        private bool m_examining;
        private float m_grantedAt;

        private bool IsHeldByMe => IsLive && m_owner == World.LocalClientId;

        public bool IsHeldByOther => IsLive && m_owner != WorldSync.NoOwner && m_owner != World.LocalClientId;

        private void Awake()
        {
            if (m_item == null) m_item = GetComponent<InteractableItem>();

            // The object's own motion entity, if any, streams it while it is in someone's hands.
            m_motion = GetComponent<SyncedMotionEntity>();
        }

        private void OnEnable()
        {
            if (m_item == null) return;

            m_item.OnExamineStartEvent?.AddListener(HandleExamineStarted);
            m_item.OnExamineEndEvent?.AddListener(HandleExamineEnded);
        }

        private void OnDisable()
        {
            if (m_item != null)
            {
                m_item.OnExamineStartEvent?.RemoveListener(HandleExamineStarted);
                m_item.OnExamineEndEvent?.RemoveListener(HandleExamineEnded);
            }

            // Disabled in someone's hands means taken into their inventory: the object is gone, so is the lock.
            StopExamining();
        }

        private void Update()
        {
            // Granted, but the examine never started — hand it back rather than hold it forever.
            if (IsHeldByMe && !m_examining && Time.time - m_grantedAt > m_unusedGrantTimeout) Release();
        }

        public bool TryBeginExamine(Action onGranted)
        {
            // Not yet synchronised (the level is still loading): behave as single player.
            if (!IsLive) return true;

            if (IsHeldByMe) return true;

            if (IsHeldByOther)
            {
                NotifyBlocked();
                return false;
            }

            if (m_pending) return false;

            m_pending = true;
            m_onGranted = onGranted;
            World.RequestLock(this);
            return false;
        }

        public void NotifyBlocked()
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(InUseHint, 2f);
        }

        internal override void ApplyLockOwner(ulong owner)
        {
            m_owner = owner;
            if (!IsLive || owner != World.LocalClientId || !m_pending) return;

            m_pending = false;
            m_grantedAt = Time.time;

            var onGranted = m_onGranted;
            m_onGranted = null;
            onGranted?.Invoke();
        }

        internal override void OnLockDenied()
        {
            m_pending = false;
            m_onGranted = null;
            NotifyBlocked();
        }

        private void HandleExamineStarted()
        {
            m_examining = true;
            if (m_motion != null) m_motion.HoldOpen = true;
        }

        private void HandleExamineEnded() => StopExamining();

        private void StopExamining()
        {
            m_examining = false;
            if (m_motion != null) m_motion.HoldOpen = false;
            Release();
        }

        private void Release()
        {
            if (!IsHeldByMe) return;

            // Cleared here as well as by the host's broadcast, so the release is not re-sent every frame meanwhile.
            m_owner = WorldSync.NoOwner;
            World.ReleaseLock(this);
        }
    }
}
