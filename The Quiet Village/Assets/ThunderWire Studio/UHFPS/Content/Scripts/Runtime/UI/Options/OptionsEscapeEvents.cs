using UnityEngine;
using UnityEngine.Events;
using ThunderWire.Attributes;

namespace UHFPS.Runtime
{
    [InspectorHeader("Options Escape Events")]
    public class OptionsEscapeEvents : MonoBehaviour
    {
        public bool DiscardChanges;
        [Space]
        public UnityEvent EscapeEvents;

        private OptionsManager optionsManager;

        private void Awake()
        {
            // MULTIPLAYER PATCH: the options manager of the player this menu belongs to, found in its own hierarchy.
            // OptionsManager.Instance took the first in the scene, which on a client can be a teammate's copy, so
            // discarding changes reverted the wrong menu's options.
            optionsManager = GetComponentInParent<OptionsManager>(true);
            if (optionsManager == null) optionsManager = OptionsManager.Instance;
            GameManager.SubscribePauseEvent(esc =>
            {
                if (!esc) OnEscape();
            });
        }

        public void OnEscape()
        {
            EscapeEvents?.Invoke();
            if (DiscardChanges)
                optionsManager.DiscardChanges();
        }
    }
}