using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Player
{
    /// <summary>
    /// Scene-authored positions where networked players appear.
    /// </summary>
    /// <remarks>
    /// Netcode instantiates <c>NetworkManager.PlayerPrefab</c> at the prefab's own transform, which is
    /// the origin. Any level whose floor is not at the origin therefore drops every player into empty
    /// space, so a spawned player has to be placed deliberately.
    ///
    /// Points are assigned by owner client id rather than first-come, so four players do not all
    /// arrive inside one another, and every client agrees on who went where without a round trip.
    /// </remarks>
    public class PlayerSpawnPoints : MonoBehaviour
    {
        /// <summary>The spawn points in the loaded scene, or <c>null</c> if none are present.</summary>
        /// <remarks>
        /// A static stands in for the DI container we are deferring — a network-spawned player has no
        /// other handle on a scene service. Replace with an injected reference once DI lands.
        /// </remarks>
        public static PlayerSpawnPoints Active { get; private set; }

        [Tooltip("Where players appear, one per connected client. Assign at least as many as MaxPlayers.")]
        [SerializeField] private List<Transform> m_spawnPoints = new();

        [Tooltip("Radius of the spawn gizmo drawn in the Scene view.")]
        [SerializeField] private float m_gizmoRadius = 0.4f;

        public int Count => m_spawnPoints?.Count ?? 0;

        private void Awake()
        {
            Active = this;
        }

        private void OnDestroy()
        {
            // Guard against clobbering a set from a scene that loaded before this one unloaded.
            if (ReferenceEquals(Active, this)) Active = null;
        }

        /// <summary>
        /// Resolves the spawn point for a given owner, wrapping when there are more players than points.
        /// </summary>
        /// <param name="ownerClientId">The owning client's id; decides which point is used.</param>
        /// <param name="position">Resolved world position.</param>
        /// <param name="rotation">Resolved world rotation.</param>
        /// <returns>False when no usable spawn point is configured.</returns>
        public bool TryGetSpawnPoint(ulong ownerClientId, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (m_spawnPoints == null || m_spawnPoints.Count == 0) return false;

            var index = (int)(ownerClientId % (ulong)m_spawnPoints.Count);
            var point = m_spawnPoints[index];

            // A list entry can be left empty in the inspector; fall back to any point that is not.
            if (point == null) point = FindFirstAssignedPoint();
            if (point == null) return false;

            position = point.position;
            rotation = point.rotation;
            return true;
        }

        private Transform FindFirstAssignedPoint()
        {
            foreach (var point in m_spawnPoints)
            {
                if (point != null) return point;
            }

            return null;
        }

        private void OnDrawGizmos()
        {
            if (m_spawnPoints == null) return;

            Gizmos.color = Color.cyan;

            foreach (var point in m_spawnPoints)
            {
                if (point == null) continue;

                Gizmos.DrawWireSphere(point.position, m_gizmoRadius);
                Gizmos.DrawRay(point.position, point.forward);
            }
        }
    }
}
