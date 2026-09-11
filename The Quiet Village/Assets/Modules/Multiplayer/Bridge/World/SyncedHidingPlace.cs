using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Gives a hiding place an identity every client shares, so a player can report which one they are in.
    /// </summary>
    /// <remarks>
    /// Carries no traffic of its own. The host's AI hunts a hiding player by walking to their hiding place and,
    /// eventually, pulling them out; for a remote player the host only learns which place that is through this
    /// id, which <see cref="PlayerAiTarget"/> replicates.
    /// </remarks>
    [DisallowMultipleComponent]
    public class SyncedHidingPlace : WorldSyncEntity
    {
        [SerializeField] private HideInteract m_hideInteract;

        public HideInteract HideInteract => m_hideInteract;

        private void Awake()
        {
            if (m_hideInteract == null) m_hideInteract = GetComponent<HideInteract>();
        }
    }
}
