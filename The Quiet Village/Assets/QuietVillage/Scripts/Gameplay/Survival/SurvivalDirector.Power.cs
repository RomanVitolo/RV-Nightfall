using System;
using System.Collections.Generic;
using QuietVillage.Multiplayer.Bridge;
using Newtonsoft.Json.Linq;
using UHFPS.Runtime;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The shelter's generator: fuel players find by day, and floodlights that keep creatures off the lit ground by night.
    /// </summary>
    /// <remarks>
    /// Authority: server, like the barricades. Players ask to refuel or to switch it; the host decides and everyone reads
    /// the result. A canister is spent the way scrap is: by its owner before asking, handed back if the host refuses.
    ///
    /// Fuel burns only while the generator runs at night. By day a running generator just shows its lights, so turning it
    /// on to check it works costs nothing; the choice that matters is when to spend the fuel once creatures are out.
    ///
    /// Replicated as one value that changes only when someone acts or the tank runs dry: whether it runs, the fuel left at
    /// that moment, and the server time of it. Fuel remaining is worked out from those, so nothing is sent while it burns.
    ///
    /// Not UHFPS's own PowerGenerator: that one runs its motor and drains its tank on every client separately, and its
    /// ever-changing save state is exactly what World Sync's churn guard stops syncing.
    /// </remarks>
    public partial class SurvivalDirector
    {
        /// <summary>The generator as the host last set it.</summary>
        private struct PowerState : INetworkSerializable, IEquatable<PowerState>
        {
            public bool Running;
            public float Fuel;
            public double Since;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Running);
                serializer.SerializeValue(ref Fuel);
                serializer.SerializeValue(ref Since);
            }

            public bool Equals(PowerState other) => Running == other.Running && Fuel.Equals(other.Fuel) && Since.Equals(other.Since);
        }

        [Header("Generator")]
        [Tooltip("Inventory item that fuels the generator. UHFPS's fuel canister.")]
        [SerializeField] private ItemGuid m_fuelItem;

        // Tuned, not playtested: a team that searches well finds 5–7 canisters, lighting about half a 240–300 s night.
        // At 60 s each, the tank covered whole nights, and floodlights keep creatures off every barricade.
        [Tooltip("Seconds of night light one canister adds when played outside a room. In a room, the room's settings decide.")]
        [SerializeField] private float m_secondsPerCanister = 40f;

        [Tooltip("Most fuel the tank holds, in seconds of light. Below a night's length, so someone has to refuel mid-night.")]
        [SerializeField] private float m_maxFuelSeconds = 120f;

        [Tooltip("Fuel in the tank when a new game starts, in seconds. None: light has to be earned by day.")]
        [SerializeField] private float m_startingFuelSeconds = 0f;

        private const string SavePowerRunning = "generatorRunning";
        private const string SavePowerFuel = "generatorFuel";

        private const string TankFullHint = "The generator's tank is full.";
        private const string NoFuelHint = "The generator has no fuel.";
        private const string LightsOutHint = "The floodlights have gone out.";

        private readonly NetworkVariable<PowerState> m_power =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly List<Floodlight> m_floodlights = new();
        private bool m_shownLit;

        /// <summary>Raised on every client when the generator starts, stops or is refuelled.</summary>
        public event Action PowerChanged;

        public string FuelItemGuid => m_fuelItem;

        public bool HasGenerator => m_floodlights.Count > 0;

        public bool GeneratorRunning => m_power.Value.Running;

        public float MaxFuelSeconds => m_maxFuelSeconds;

        /// <summary>Seconds of night light left, as this client estimates server time.</summary>
        public float FuelSeconds => IsSpawned ? FuelAt(m_power.Value, NetworkManager.ServerTime.Time) : 0f;

        /// <summary>True while the floodlights shine and creatures keep out of them.</summary>
        public bool FloodlightsOn => IsSpawned && m_power.Value.Running && FuelSeconds > 0f;

        /// <summary>Whether a creature at this position stands in light (floodlight or a flare), and so will not stay or attack there.</summary>
        public bool IsLit(Vector3 position)
        {
            // A flare burns whatever the generator is doing.
            if (FlareCovers(position, out _)) return true;

            if (m_phase.Value != SurvivalPhase.Night || !FloodlightsOn) return false;

            foreach (var light in m_floodlights)
            {
                if (light != null && light.Covers(position)) return true;
            }

            return false;
        }

        /// <summary>The nearest point outside the light from <paramref name="position"/>, for a creature to back off to.</summary>
        public Vector3 EdgeOfLight(Vector3 position)
        {
            if (FlareCovers(position, out var flare)) return OutsideFlare(flare, position);

            Floodlight nearest = null;
            var nearestDistance = float.MaxValue;
            foreach (var light in m_floodlights)
            {
                if (light == null || !light.Covers(position)) continue;

                var distance = Vector3.SqrMagnitude(light.Centre - position);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearest = light;
            }

            return nearest != null ? nearest.PointOutside(position) : position;
        }

        private float FuelAt(PowerState state, double now)
        {
            if (!state.Running || m_phase.Value != SurvivalPhase.Night) return state.Fuel;

            // Fuel only burns at night, and the state is re-stamped when night falls, so elapsed time is all night time.
            return Mathf.Max(0f, state.Fuel - (float)(now - state.Since));
        }

        private void CollectFloodlights()
        {
            m_floodlights.Clear();
            m_floodlights.AddRange(FindObjectsByType<Floodlight>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        }

        // ---- Server ----------------------------------------------------------------------------------

        private void RestorePower(JToken saved)
        {
            var fuel = (float?)saved?[SavePowerFuel] ?? m_startingFuelSeconds;
            var running = (bool?)saved?[SavePowerRunning] ?? false;

            SetPower(running, Mathf.Clamp(fuel, 0f, m_maxFuelSeconds));
        }

        private void SetPower(bool running, float fuel)
        {
            m_power.Value = new PowerState { Running = running, Fuel = fuel, Since = NetworkManager.ServerTime.Time };
        }

        /// <summary>Server, each frame: stops the generator when the tank runs dry.</summary>
        private void UpdatePower()
        {
            var state = m_power.Value;
            if (!state.Running || m_phase.Value != SurvivalPhase.Night) return;

            if (FuelAt(state, NetworkManager.ServerTime.Time) <= 0f)
            {
                SetPower(false, 0f);
                LightsOutRpc();
            }
        }

        /// <summary>Server: fuel burnt so far is booked when a phase begins, so burning always counts from night's start.</summary>
        private void StampPowerForPhase()
        {
            var state = m_power.Value;
            SetPower(state.Running, FuelAt(state, NetworkManager.ServerTime.Time));
        }

        /// <summary>Owner client: spends one canister and asks the host to pour it in.</summary>
        internal void RequestRefuel()
        {
            if (!IsSpawned) return;

            var inventory = LocalPlayerContext.Inventory;
            if (inventory == null || !inventory.ContainsItem(FuelItemGuid)) return;

            if (FuelSeconds >= m_maxFuelSeconds - 0.5f)
            {
                ShowHint(TankFullHint);
                return;
            }

            inventory.RemoveItem(FuelItemGuid, 1);
            RefuelRpc();
        }

        [Rpc(SendTo.Server)]
        private void RefuelRpc(RpcParams rpcParams = default)
        {
            var state = m_power.Value;
            var fuel = FuelAt(state, NetworkManager.ServerTime.Time);

            if (fuel >= m_maxFuelSeconds - 0.5f)
            {
                RefuelRefusedRpc(RpcTarget.Single(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp));
                return;
            }

            SetPower(state.Running, Mathf.Min(m_maxFuelSeconds, fuel + m_settings.FuelPerCanister));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void RefuelRefusedRpc(RpcParams rpcParams = default)
        {
            var inventory = LocalPlayerContext.Inventory;
            if (inventory != null) inventory.AddItem(FuelItemGuid, 1, null);

            ShowHint(TankFullHint);
        }

        /// <summary>Any client: asks the host to start or stop the generator.</summary>
        internal void RequestSwitchPower()
        {
            if (!IsSpawned) return;

            if (!m_power.Value.Running && FuelSeconds <= 0f)
            {
                ShowHint(NoFuelHint);
                return;
            }

            SwitchPowerRpc(!m_power.Value.Running);
        }

        [Rpc(SendTo.Server)]
        private void SwitchPowerRpc(bool running)
        {
            var state = m_power.Value;
            var fuel = FuelAt(state, NetworkManager.ServerTime.Time);
            if (running && fuel <= 0f) return;

            SetPower(running, fuel);
        }

        [Rpc(SendTo.Everyone)]
        private void LightsOutRpc() => ShowHint(LightsOutHint);

        // ---- Every client ----------------------------------------------------------------------------

        private void HandlePowerChanged(PowerState previous, PowerState current)
        {
            ShowPower();
            PowerChanged?.Invoke();
        }

        /// <summary>Lights the floodlights to match the generator. Run every frame on clients, so they go out as fuel runs dry.</summary>
        private void ShowPower()
        {
            // By day a running generator still lights the lamps, to show it works; only at night does that cost fuel.
            var lit = IsSpawned && m_power.Value.Running && (m_phase.Value != SurvivalPhase.Night || FuelSeconds > 0f);
            if (lit == m_shownLit) return;

            m_shownLit = lit;
            foreach (var light in m_floodlights)
            {
                if (light != null) light.SetLit(lit);
            }
        }
    }
}
