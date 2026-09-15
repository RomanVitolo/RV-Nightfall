using System;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Lets only one player use a close-up puzzle at a time, decided by the host. The puzzle counterpart of
    /// <see cref="SyncedExamineLock"/>.
    /// </summary>
    /// <remarks>
    /// The puzzle asks before switching to its camera. If another player is known to be using it, the request is refused
    /// on the spot with a hint; otherwise the host is asked and the puzzle opens when the grant arrives, so nobody is shown
    /// a close-up that is then taken away.
    ///
    /// Held from entering the puzzle until leaving it. Released when the puzzle reports the player has left, when the
    /// puzzle's object is switched off (a solved padlock hides itself), and by the host if the player disconnects.
    /// Only use is exclusive: the puzzle's state still replicates through its own saveable entity.
    /// </remarks>
    [DisallowMultipleComponent]
    public class SyncedPuzzleLock : WorldSyncEntity, IPuzzleGate
    {
        [Tooltip("The UHFPS puzzle this lock guards.")]
        [SerializeField] private MonoBehaviour m_puzzle;

        [Tooltip("Seconds a grant may go unused before it is handed back, e.g. if the player walked off meanwhile.")]
        [SerializeField] private float m_unusedGrantTimeout = 1.5f;

        private const string InUseHint = "Someone else is using this.";

        private ulong m_owner = WorldSync.NoOwner;
        private bool m_pending;
        private Action m_onGranted;
        private bool m_inUse;
        private float m_grantedAt;

        /// <summary>The puzzle component, for the setup tool's ids.</summary>
        public MonoBehaviour Puzzle => m_puzzle;

        private bool IsHeldByMe => IsLive && m_owner == World.LocalClientId;

        private bool IsHeldByOther => IsLive && m_owner != WorldSync.NoOwner && m_owner != World.LocalClientId;

        private void Update()
        {
            // Granted, but the puzzle never opened: hand it back rather than hold it forever.
            if (IsHeldByMe && !m_inUse && Time.time - m_grantedAt > m_unusedGrantTimeout) Release();
        }

        private void OnDisable()
        {
            m_inUse = false;
            Release();
        }

        public bool TryBeginPuzzle(Action onGranted)
        {
            // Not yet synchronised (the level is still loading): behave as single player.
            if (!IsLive) return true;

            if (IsHeldByMe)
            {
                m_inUse = true;
                return true;
            }

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

        public void EndPuzzle()
        {
            m_inUse = false;
            Release();
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

        private static void NotifyBlocked()
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(InUseHint, 2f);
        }

        private void Release()
        {
            if (!IsHeldByMe) return;

            // Cleared here as well as by the host's broadcast, so the release is not re-sent meanwhile.
            m_owner = WorldSync.NoOwner;
            World.ReleaseLock(this);
        }
    }
}
