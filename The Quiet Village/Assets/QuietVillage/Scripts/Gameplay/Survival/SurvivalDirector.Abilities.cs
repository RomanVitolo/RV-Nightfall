using QuietVillage.Multiplayer.Bridge;
using QuietVillage.Multiplayer.Characters;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The host's half of the role abilities: the two that change shared state, a Builder's reinforcement and a Medic's
    /// healing. The rest, and every cooldown, live with the player (<see cref="RoleAbilities"/>).
    /// </summary>
    /// <remarks>
    /// Authority: server. The host checks that the asker's character really has the ability, then applies it. Cooldowns
    /// are timed by the asker: it is a co-op game, and a second timer on the host would only disagree with the HUD.
    /// </remarks>
    public partial class SurvivalDirector
    {
        [Header("Abilities")]
        [Tooltip("Share of full health a Builder's reinforcement raises a barricade to.")]
        [SerializeField] private float m_reinforcedShare = 1.5f;

        [Tooltip("Health a Medic's healing area gives each teammate in it, before the Medic's healing perk.")]
        [SerializeField] private int m_healingAreaAmount = 20;

        [Tooltip("Radius (m) of a Medic's healing area.")]
        [SerializeField] private float m_healingAreaRadius = 5f;

        /// <summary>Most health a barricade can hold, reinforced.</summary>
        private int ReinforcedHealth => Mathf.RoundToInt(m_barricadeMaxHealth * Mathf.Max(1f, m_reinforcedShare));

        /// <summary>Server: the ability of a client's character, or None.</summary>
        private RoleAbility AbilityOf(ulong clientId)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null)
                return RoleAbility.None;

            var character = client.PlayerObject.GetComponent<PlayerCharacter>();
            return character != null && character.Character != null ? character.Character.Ability : RoleAbility.None;
        }

        private static PlayerHealthSync StandingPlayerOf(NetworkManager networkManager, ulong clientId)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null) return null;

            var health = client.PlayerObject.GetComponent<PlayerHealthSync>();
            return health != null && health.IsStanding ? health : null;
        }

        // ---- Reinforce -------------------------------------------------------------------------------

        internal void RequestReinforce(int index)
        {
            if (IsSpawned) ReinforceRpc(index);
        }

        [Rpc(SendTo.Server)]
        private void ReinforceRpc(int index, RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            var ok = AbilityOf(sender) == RoleAbility.Reinforce
                     && StandingPlayerOf(NetworkManager, sender) != null
                     && index >= 0 && index < m_barricadeHealth.Count
                     && m_barricadeHealth[index] < ReinforcedHealth;

            // Not while something stands in the opening: raising it from nothing would shut them inside, as building would.
            if (ok && m_barricadeHealth[index] == 0 && IsOpeningOccupied(m_barricades[index])) ok = false;

            if (ok) m_barricadeHealth[index] = ReinforcedHealth;

            ReinforceResultRpc(ok, RpcTarget.Single(sender, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void ReinforceResultRpc(bool ok, RpcParams rpcParams = default)
        {
            if (TryGetComponent<RoleAbilities>(out var abilities)) abilities.HandleReinforceResult(ok);
        }

        // ---- Healing area ----------------------------------------------------------------------------

        internal void RequestHealingArea()
        {
            if (IsSpawned) HealingAreaRpc();
        }

        [Rpc(SendTo.Server)]
        private void HealingAreaRpc(RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (AbilityOf(sender) != RoleAbility.HealingArea) return;

            var healer = StandingPlayerOf(NetworkManager, sender);
            if (healer == null) return;

            var character = healer.GetComponent<PlayerCharacter>();
            var amount = Mathf.RoundToInt(m_healingAreaAmount * (character != null ? character.Perks.Healing : 1f));
            var radiusSquared = m_healingAreaRadius * m_healingAreaRadius;

            foreach (var player in PlayerHealthSync.Spawned)
            {
                if (player == null || !player.IsStanding) continue;
                if (Vector3.SqrMagnitude(player.transform.position - healer.transform.position) > radiusSquared) continue;

                player.ServerHeal(amount);
            }

            HealingAreaEffectRpc(healer.transform.position);
        }

        [Rpc(SendTo.Everyone)]
        private void HealingAreaEffectRpc(Vector3 position)
        {
            SynthSounds.PlayAt(position, SynthSounds.Chime, 25f, 0.8f);
            PulseLight(position, new Color(0.45f, 1f, 0.55f), m_healingAreaRadius * 1.6f, 4f, 1.2f);
        }
    }
}
