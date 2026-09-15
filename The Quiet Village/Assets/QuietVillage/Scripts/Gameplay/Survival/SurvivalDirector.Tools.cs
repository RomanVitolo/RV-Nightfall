using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using QuietVillage.Multiplayer.Bridge;
using UHFPS.Runtime;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// What players can use against the creatures besides light and barricades: flares, holy water, the chapel bell and
    /// scrap traps. None of them kills; each buys time.
    /// </summary>
    /// <remarks>
    /// Authority: server, as everywhere else in the director. Players spend their own items, then ask; the host decides
    /// what happens to the creatures, which only it simulates, and every client shows it.
    /// <list type="bullet">
    /// <item><b>Flare</b> (inventory item): lit where the player stands for <see cref="m_flareSeconds"/>. Creatures treat it
    /// like floodlight (<see cref="IsLit"/>). Replicated as a list of position and server end time.</item>
    /// <item><b>Holy Water</b> (inventory item): stuns and repels every creature within <see cref="m_holyWaterRadius"/>.</item>
    /// <item><b>Chapel bell</b> (<see cref="ChapelBell"/>): stuns every creature in earshot, then rests for
    /// <see cref="m_bellCooldown"/>. The rest is one replicated server time.</item>
    /// <item><b>Scrap traps</b> (<see cref="BarricadeTrap"/>): one per barricade, set for <see cref="m_trapCost"/> Scrap; the
    /// next creature at that barricade is stunned and slowed. Armed state is one replicated list, saved.</item>
    /// </list>
    /// The bell and trap spots are placed at runtime on every client from the level's barricades, so no level needs
    /// rebuilding for them. The two items are UHFPS inventory items with a custom use event, added to the database by
    /// <c>Tools > Quiet Village > Survival > Set Up Tool Items</c> and found here by title.
    /// </remarks>
    public partial class SurvivalDirector
    {
        public const string FlareTitle = "Flare";
        public const string HolyWaterTitle = "Holy Water";

        [Header("Flares")]
        [SerializeField] private float m_flareSeconds = 25f;

        [Tooltip("Radius (m) of ground a flare lights, which creatures keep out of.")]
        [SerializeField] private float m_flareRadius = 6f;

        [Header("Holy water")]
        [SerializeField] private float m_holyWaterRadius = 6f;
        [SerializeField] private float m_holyWaterStunSeconds = 2f;
        [SerializeField] private float m_holyWaterRepelSeconds = 3f;

        [Header("Chapel bell")]
        [SerializeField] private float m_bellRadius = 25f;
        [SerializeField] private float m_bellStunSeconds = 4f;
        [SerializeField] private float m_bellCooldown = 120f;

        [Header("Scrap traps")]
        [SerializeField] private int m_trapCost = 2;

        [Tooltip("How close (m) a creature must come to an armed trap to spring it.")]
        [SerializeField] private float m_trapReach = 1.3f;

        [SerializeField] private float m_trapStunSeconds = 2f;
        [SerializeField] private float m_trapSlowSeconds = 4f;
        [SerializeField] private float m_trapSlowFactor = 0.5f;

        private const string SaveTraps = "traps";

        private struct FlareState : INetworkSerializeByMemcpy, IEquatable<FlareState>
        {
            public Vector3 Position;
            public double EndsAt;

            public bool Equals(FlareState other) => Position == other.Position && EndsAt.Equals(other.EndsAt);
        }

        private readonly NetworkList<FlareState> m_flares =
            new(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkList<byte> m_traps =
            new(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<double> m_bellReadyAt =
            new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Transform m_toolsRoot;
        private ChapelBell m_bell;
        private readonly List<BarricadeTrap> m_trapSpots = new();
        private readonly List<GameObject> m_flareVisuals = new();

        public int TrapCost => m_trapCost;

        /// <summary>Seconds before the bell can ring again, as this client estimates server time.</summary>
        public float BellCooldownRemaining =>
            IsSpawned ? Mathf.Max(0f, (float)(m_bellReadyAt.Value - NetworkManager.ServerTime.Time)) : 0f;

        public bool IsTrapArmed(int index) => index >= 0 && index < m_traps.Count && m_traps[index] != 0;

        // ---- Setup (every client) --------------------------------------------------------------------

        private void OnToolsSpawn(JToken saved)
        {
            m_flares.OnListChanged += HandleFlaresChanged;
            m_traps.OnListChanged += HandleTrapsChanged;

            if (IsServer)
            {
                m_traps.Clear();
                var savedTraps = saved?[SaveTraps] as JArray;
                for (var i = 0; i < m_barricades.Count; i++)
                    m_traps.Add((byte)(savedTraps != null && i < savedTraps.Count && ((int?)savedTraps[i] ?? 0) != 0 ? 1 : 0));
            }

            PlaceTools();
            ShowTraps();
            ShowFlares();
        }

        private void OnToolsDespawn()
        {
            m_flares.OnListChanged -= HandleFlaresChanged;
            m_traps.OnListChanged -= HandleTrapsChanged;

            if (m_toolsRoot != null) Destroy(m_toolsRoot.gameObject);
            m_toolsRoot = null;
            m_trapSpots.Clear();
            m_flareVisuals.Clear();
            m_bell = null;
        }

        /// <summary>The bell and a trap spot per barricade, at the same place on every machine.</summary>
        private void PlaceTools()
        {
            if (m_barricades.Count == 0 || m_toolsRoot != null) return;

            // Not under the director: a scene object's scale and position would carry into everything placed here.
            m_toolsRoot = new GameObject("SurvivalTools").transform;

            var middle = Vector3.zero;
            for (var i = 0; i < m_barricades.Count; i++)
            {
                middle += m_barricades[i].transform.position;
                m_trapSpots.Add(BarricadeTrap.Create(this, i, SurvivalProps.Ground(m_barricades[i].AttackPoint), m_toolsRoot));
            }

            // The shelter's barricades ring its openings, so their middle is the middle of the shelter.
            middle /= m_barricades.Count;
            m_bell = ChapelBell.Create(this, SurvivalProps.Ground(middle), m_toolsRoot);
        }

        private void HandleTrapsChanged(NetworkListEvent<byte> change) => ShowTraps();

        private void ShowTraps()
        {
            for (var i = 0; i < m_trapSpots.Count; i++) m_trapSpots[i].Show(IsTrapArmed(i));
        }

        private JToken CaptureTraps()
        {
            var traps = new JArray();
            foreach (var armed in m_traps) traps.Add((int)armed);
            return traps;
        }

        // ---- Items (this client) ---------------------------------------------------------------------

        private Inventory m_toolsInventory;

        /// <summary>Hooks the flare and holy water into this client's inventory, again whenever its player is new.</summary>
        private void RegisterToolUses()
        {
            var inventory = LocalPlayerContext.Inventory;
            if (inventory == null || ReferenceEquals(inventory, m_toolsInventory) || inventory.items == null) return;

            m_toolsInventory = inventory;

            var flare = ItemGuidByTitle(inventory, FlareTitle);
            if (flare != null) inventory.RegisterUseEvent(flare, _ => UseFlare());

            var holyWater = ItemGuidByTitle(inventory, HolyWaterTitle);
            if (holyWater != null) inventory.RegisterUseEvent(holyWater, _ => UseHolyWater());
        }

        /// <summary>An item's GUID in this inventory's database by its title, or <c>null</c> when it is not set up.</summary>
        internal static string ItemGuidByTitle(Inventory inventory, string title)
        {
            if (inventory == null || inventory.items == null) return null;

            foreach (var pair in inventory.items)
            {
                if (pair.Value != null && pair.Value.Title == title) return pair.Key;
            }

            return null;
        }

        /// <summary>Where the local player stands, a little ahead, on the ground.</summary>
        private static Vector3 InFrontOfLocalPlayer(float distance)
        {
            var player = LocalPlayerContext.Player;
            if (player == null) return Vector3.zero;

            var forward = player.transform.forward;
            forward.y = 0f;
            return SurvivalProps.Ground(player.transform.position + forward.normalized * distance);
        }

        // Spent by UHFPS as it is used (the item removes itself), so these only ask.
        private void UseFlare()
        {
            if (IsSpawned && LocalPlayerContext.IsReady) DeployFlareRpc(InFrontOfLocalPlayer(0.8f));
        }

        private void UseHolyWater()
        {
            if (IsSpawned && LocalPlayerContext.IsReady) HolyWaterRpc(InFrontOfLocalPlayer(1f));
        }

        // ---- Flares ----------------------------------------------------------------------------------

        [Rpc(SendTo.Server)]
        private void DeployFlareRpc(Vector3 position)
        {
            m_flares.Add(new FlareState { Position = position, EndsAt = NetworkManager.ServerTime.Time + m_flareSeconds });
        }

        /// <summary>Server, each frame: burnt-out flares leave the list.</summary>
        private void PruneFlares()
        {
            var now = NetworkManager.ServerTime.Time;
            for (var i = m_flares.Count - 1; i >= 0; i--)
            {
                if (m_flares[i].EndsAt <= now) m_flares.RemoveAt(i);
            }
        }

        private bool FlareCovers(Vector3 position, out Vector3 centre)
        {
            centre = default;
            if (!IsSpawned) return false;

            var now = NetworkManager.ServerTime.Time;
            var radiusSquared = m_flareRadius * m_flareRadius;
            var best = float.MaxValue;
            var found = false;

            foreach (var flare in m_flares)
            {
                if (flare.EndsAt <= now) continue;

                var offset = position - flare.Position;
                offset.y = 0f;
                var distance = offset.sqrMagnitude;
                if (distance > radiusSquared || distance >= best) continue;

                best = distance;
                centre = flare.Position;
                found = true;
            }

            return found;
        }

        /// <summary>The point just outside a flare's light, straight out from its centre.</summary>
        private Vector3 OutsideFlare(Vector3 centre, Vector3 position)
        {
            var away = position - centre;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;

            return centre + away.normalized * (m_flareRadius + 0.75f);
        }

        private void HandleFlaresChanged(NetworkListEvent<FlareState> change) => ShowFlares();

        /// <summary>Every client: a burning flare for each one in the list. Rebuilt on change; flares are few and short.</summary>
        private void ShowFlares()
        {
            foreach (var visual in m_flareVisuals)
            {
                if (visual != null) Destroy(visual);
            }

            m_flareVisuals.Clear();
            if (!IsSpawned) return;

            var now = NetworkManager.ServerTime.Time;
            foreach (var flare in m_flares)
            {
                var remaining = (float)(flare.EndsAt - now);
                if (remaining > 0f) m_flareVisuals.Add(FlareVisual.Create(flare.Position, m_flareRadius, remaining));
            }
        }

        // ---- Holy water ------------------------------------------------------------------------------

        [Rpc(SendTo.Server)]
        private void HolyWaterRpc(Vector3 position)
        {
            foreach (var creature in CreaturesWithin(position, m_holyWaterRadius))
            {
                creature.Repel(position, m_holyWaterRepelSeconds + m_holyWaterStunSeconds);
                creature.Stun(m_holyWaterStunSeconds);
            }

            HolyWaterEffectRpc(position);
        }

        [Rpc(SendTo.Everyone)]
        private void HolyWaterEffectRpc(Vector3 position)
        {
            SynthSounds.PlayAt(position, SynthSounds.Hiss, 30f, 0.9f);
            PulseLight(position, new Color(0.55f, 0.75f, 1f), m_holyWaterRadius * 1.5f, 6f, 0.8f);
        }

        // ---- Chapel bell -----------------------------------------------------------------------------

        private const string BellRestingHint = "The bell is still ringing out. Let it rest.";

        internal void RequestRingBell()
        {
            if (IsSpawned) RingBellRpc();
        }

        [Rpc(SendTo.Server)]
        private void RingBellRpc(RpcParams rpcParams = default)
        {
            var now = NetworkManager.ServerTime.Time;
            if (m_bell == null || now < m_bellReadyAt.Value)
            {
                BellRestingRpc(RpcTarget.Single(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp));
                return;
            }

            m_bellReadyAt.Value = now + m_bellCooldown;

            foreach (var creature in CreaturesWithin(m_bell.transform.position, m_bellRadius)) creature.Stun(m_bellStunSeconds);

            BellRungRpc();
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void BellRestingRpc(RpcParams rpcParams = default) => ShowHint(BellRestingHint);

        [Rpc(SendTo.Everyone)]
        private void BellRungRpc()
        {
            if (m_bell == null) return;

            SynthSounds.PlayAt(m_bell.transform.position + Vector3.up * 1.8f, SynthSounds.Bell, 90f);
        }

        // ---- Scrap traps -----------------------------------------------------------------------------

        private const string TrapAlreadyArmedHint = "There is already a trap here.";

        /// <summary>Owner client: spends the scrap and asks the host to arm a barricade's trap.</summary>
        internal void RequestTrap(int index)
        {
            if (!IsSpawned || IsTrapArmed(index)) return;

            var inventory = LocalPlayerContext.Inventory;
            if (inventory == null || inventory.GetItemQuantity(BuildItemGuid) < m_trapCost) return;

            // As with building: spent before asking, handed back if refused.
            inventory.RemoveItem(BuildItemGuid, (ushort)m_trapCost);
            SetTrapRpc(index);
        }

        [Rpc(SendTo.Server)]
        private void SetTrapRpc(int index, RpcParams rpcParams = default)
        {
            if (index < 0 || index >= m_traps.Count || m_traps[index] != 0)
            {
                TrapRefusedRpc(RpcTarget.Single(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp));
                return;
            }

            m_traps[index] = 1;
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void TrapRefusedRpc(RpcParams rpcParams = default)
        {
            var inventory = LocalPlayerContext.Inventory;
            if (inventory != null) inventory.AddItem(BuildItemGuid, (ushort)m_trapCost, new ItemCustomData());

            ShowHint(TrapAlreadyArmedHint);
        }

        /// <summary>Server, each frame at night: springs any armed trap a creature has walked onto.</summary>
        private void CheckTraps()
        {
            if (m_phase.Value != SurvivalPhase.Night) return;

            for (var i = 0; i < m_traps.Count && i < m_barricades.Count; i++)
            {
                if (m_traps[i] == 0) continue;

                var point = m_barricades[i].AttackPoint;
                foreach (var creature in CreaturesWithin(point, m_trapReach))
                {
                    creature.Stun(m_trapStunSeconds);
                    creature.Slow(m_trapSlowFactor, m_trapSlowSeconds + m_trapStunSeconds);
                    m_traps[i] = 0;
                    TrapSprungRpc(i);
                    break;
                }
            }
        }

        [Rpc(SendTo.Everyone)]
        private void TrapSprungRpc(int index)
        {
            if (index < 0 || index >= m_barricades.Count) return;

            SynthSounds.PlayAt(m_barricades[index].AttackPoint, SynthSounds.Snap, 35f);
        }

        // ---- Creatures (server) ----------------------------------------------------------------------

        /// <summary>Server: the living creatures within a distance of a point, measured along the ground.</summary>
        private List<NightCreature> CreaturesWithin(Vector3 point, float radius)
        {
            m_nearbyCreatures.Clear();
            var radiusSquared = radius * radius;

            foreach (var creature in m_creatures)
            {
                if (creature == null || !creature.IsSpawned || !creature.TryGetComponent<NightCreature>(out var night)) continue;
                if (night.IsDying) continue;

                var offset = creature.transform.position - point;
                offset.y = 0f;
                if (offset.sqrMagnitude <= radiusSquared) m_nearbyCreatures.Add(night);
            }

            return m_nearbyCreatures;
        }

        private readonly List<NightCreature> m_nearbyCreatures = new();

        /// <summary>Server: a Screamer's call. The creatures near it go after the player it found.</summary>
        internal void CallCreatures(NightCreature caller, PlayerHealthSync target, float radius, float seconds)
        {
            if (!IsServer || caller == null || target == null) return;

            foreach (var creature in CreaturesWithin(caller.transform.position, radius))
            {
                if (creature != caller) creature.CallTo(target, seconds);
            }
        }

        // ---- Presentation ----------------------------------------------------------------------------

        /// <summary>A short burst of light that fades, for effects without a model of their own.</summary>
        private static void PulseLight(Vector3 position, Color color, float range, float intensity, float seconds)
        {
            var holder = new GameObject("EffectLight");
            holder.transform.position = position + Vector3.up * 1f;

            var light = holder.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;

            holder.AddComponent<FadeAndDestroy>().Begin(light, seconds);
        }
    }
}
