using UnityEngine;
using UnityEngine.Rendering;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Scene objects the player's <c>GameManager</c> needs but cannot hold a reference to.
    /// </summary>
    /// <remarks>
    /// When the HUD moved onto the player prefab, the post-processing Volumes stayed behind — they are
    /// scene lighting, not player UI, and four stacked copies of a global grade would be wrong. A
    /// prefab cannot reference scene objects, so those two references broke on the move and are
    /// restored here at spawn, the same way <c>PlayerManager.MainCamera</c> is.
    /// </remarks>
    public class SceneGameReferences : MonoBehaviour
    {
        [Tooltip("Scene-wide post-processing volume (GameManager.GlobalPPVolume).")]
        [SerializeField] private Volume m_globalPostProcessing;

        [Tooltip("Health/damage post-processing volume (GameManager.HealthPPVolume).")]
        [SerializeField] private Volume m_healthPostProcessing;

        /// <summary>The holder in the loaded scene, or <c>null</c> if none is present.</summary>
        /// <remarks>
        /// A static stands in for the DI container we are deferring — a network-spawned player has no
        /// other handle on a scene service. Replace with an injected reference once DI lands.
        /// </remarks>
        public static SceneGameReferences Active { get; private set; }

        public Volume GlobalPostProcessing => m_globalPostProcessing;

        public Volume HealthPostProcessing => m_healthPostProcessing;

        private void Awake()
        {
            Active = this;
        }

        private void OnDestroy()
        {
            // Guard against clobbering a holder from a scene that loaded before this one unloaded.
            if (ReferenceEquals(Active, this)) Active = null;
        }
    }
}
