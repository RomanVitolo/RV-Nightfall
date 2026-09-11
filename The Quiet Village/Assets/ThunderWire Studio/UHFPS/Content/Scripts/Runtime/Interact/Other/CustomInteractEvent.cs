using UnityEngine.Events;
using UnityEngine;
using UHFPS.Tools;
using Newtonsoft.Json.Linq;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class CustomInteractEvent : MonoBehaviour, IInteractStart, IInteractHold, IInteractStop, ISaveable
    {
        public bool UseOnStartEvent;
        public bool UseOnHoldEvent;
        public bool UseOnStopEvent;

        public bool FreezePlayer;
        public bool InteractOnce;
        public bool UseInteractSound;
        public SoundClip InteractSound;

        public UnityEvent OnStart;
        public UnityEvent<Vector3> OnHold;
        public UnityEvent OnStop;

        // MULTIPLAYER PATCH: resolved on use instead of cached in Awake. World objects Awake at scene load,
        // before Netcode spawns the local player, so the cached reference stayed null for the whole session.
        private PlayerPresenceManager playerPresence => LocalPlayerContext.Presence;
        private bool isInteracted;


        public void InteractStart()
        {
            if (isInteracted) 
                return;

            if (UseOnStartEvent) OnStart?.Invoke();
            if (FreezePlayer) playerPresence.FreezePlayer(true);
            if (UseInteractSound) GameTools.PlayOneShot2D(transform.position, InteractSound, "InteractSound");
        }

        public void InteractHold(Vector3 point)
        {
            if (isInteracted) 
                return;

            if (UseOnHoldEvent) OnHold?.Invoke(point);
        }

        public void InteractStop()
        {
            if (isInteracted) 
                return;

            if (UseOnStopEvent) OnStop?.Invoke();
            if (FreezePlayer) playerPresence.FreezePlayer(false);
            if (InteractOnce) isInteracted = true;
        }

        public StorableCollection OnSave()
        {
            return new StorableCollection()
            {
                { "interactOnce", isInteracted }
            };
        }

        public void OnLoad(JToken data)
        {
            isInteracted = (bool)data["interactOnce"];
        }
    }
}