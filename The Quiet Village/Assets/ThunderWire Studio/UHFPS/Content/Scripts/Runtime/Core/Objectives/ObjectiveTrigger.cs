using Newtonsoft.Json.Linq;
using UnityEngine;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class ObjectiveTrigger : MonoBehaviour, IInteractStart, ISaveable
    {
        public enum TriggerType { Trigger, Interact, Event }
        public enum ObjectiveType { New, Complete, NewAndComplete }

        public TriggerType triggerType = TriggerType.Trigger;
        public ObjectiveType objectiveType = ObjectiveType.New;

        public ObjectiveSelect objectiveToAdd;
        public ObjectiveSelect objectiveToComplete;

        private bool isTriggered;

        // MULTIPLAYER PATCH: this client's objectives, not ObjectiveManager.Instance. That lookup takes the first manager
        // in the scene, which can be a teammate's switched-off copy, so an objective could go to nobody.
        private ObjectiveManager ObjectiveManager => LocalPlayerContext.ObjectiveManager;

        public void InteractStart()
        {
            if (triggerType != TriggerType.Interact || triggerType == TriggerType.Event || isTriggered)
                return;

            TriggerObjective();
            isTriggered = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (triggerType != TriggerType.Trigger || triggerType == TriggerType.Event || isTriggered)
                return;

            // MULTIPLAYER PATCH: objectives are per-player, so only this client's own body triggers one.
            if (other.CompareTag("Player") && LocalPlayerContext.IsLocalPlayer(other) && ObjectiveManager != null)
            {
                TriggerObjective();
                isTriggered = true;
            }
        }

        public void TriggerObjective()
        {
            if (ObjectiveManager == null) return;

            if (objectiveType == ObjectiveType.New)
            {
                ObjectiveManager.AddObjective(objectiveToAdd.ObjectiveKey, objectiveToAdd.SubObjectives);
            }
            else if (objectiveType == ObjectiveType.Complete)
            {
                ObjectiveManager.CompleteObjective(objectiveToComplete.ObjectiveKey, objectiveToComplete.SubObjectives);
            }
            else if(objectiveType == ObjectiveType.NewAndComplete)
            {
                ObjectiveManager.AddObjective(objectiveToAdd.ObjectiveKey, objectiveToAdd.SubObjectives);
                ObjectiveManager.CompleteObjective(objectiveToComplete.ObjectiveKey, objectiveToComplete.SubObjectives);
            }
        }

        public StorableCollection OnSave()
        {
            return new StorableCollection()
            {
                { nameof(isTriggered), isTriggered }
            };
        }

        public void OnLoad(JToken data)
        {
            isTriggered = (bool)data[nameof(isTriggered)];
        }
    }
}