using System.Collections.Generic;
using System.Linq;
using ThunderWire.Attributes;
using UnityEngine;

namespace UHFPS.Runtime
{
    [InspectorHeader("Barricade Handler")]
    public class BarricadeHandler : MonoBehaviour, IDynamicBarricade
    {
        public List<BarricadeObject> Barricades = new();

        /// <summary>
        /// Returns true if all barricades are still barricaded.
        /// </summary>
        public bool CheckBarricaded()
        {
            // Check if all barricades are unblocked
            return !Barricades.All(b => b.IsUnblocked);
        }

        [ContextMenu("Get Barricades")]
        public void GetBarricades()
        {
            Barricades = GetComponentsInChildren<BarricadeObject>().ToList();
        }

        [ContextMenu("Transfer Settings From First")]
        public void TransferSettingsFromFirst()
        {
            if (Barricades.Count == 0 || Barricades.Count < 2) 
                return;

            BarricadeObject first = Barricades[0];
            for (int i = 1; i < Barricades.Count; i++)
            {
                BarricadeObject current = Barricades[i];

                current.BreakStyle = first.BreakStyle;
                current.UnblockedLayer = first.UnblockedLayer;
                current.PullStrength = first.PullStrength;
                current.InteractTime = first.InteractTime;

                current.RequiredPullWork = first.RequiredPullWork;
                current.PullEffort = first.PullEffort;
                current.DragPullStrength = first.DragPullStrength;
                current.PullResistance = first.PullResistance;
                current.ReturnSmoothTime = first.ReturnSmoothTime;
                current.ResistanceMultiplier = first.ResistanceMultiplier;
                current.ReturnThreshold = first.ReturnThreshold;
                current.UseDragPointForForce = first.UseDragPointForForce;

                current.UseCustomInteractIcon = first.UseCustomInteractIcon;
                current.DisableReticleWhileHolding = first.DisableReticleWhileHolding;
                current.HoldIcon = first.HoldIcon;
                current.HoldSize = first.HoldSize;

                current.BreakSounds = first.BreakSounds;
                current.BreakSoundVolume = first.BreakSoundVolume;

                current.CrackingAudioSource = current.GetComponent<AudioSource>();
                current.CrackingSounds = first.CrackingSounds;
                current.MinCrackingTime = first.MinCrackingTime;
                current.CrackingMaxVolume = first.CrackingMaxVolume;

                // Copying UnityEvents not supported
            }

            Debug.Log("Transferred Barricade Settings from First Barricade Object to the Rest.");
        }
    }
}