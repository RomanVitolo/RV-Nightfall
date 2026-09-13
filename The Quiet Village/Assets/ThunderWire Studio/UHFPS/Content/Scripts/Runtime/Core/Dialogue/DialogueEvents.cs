using System;
using System.Reactive.Disposables;
using UnityEngine;
using UnityEngine.Events;
using ThunderWire.Attributes;
using UHFPS.Tools;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Dialogue Events")]
    public class DialogueEvents : MonoBehaviour
    {
        public UnityEvent OnDialogueStart;
        public UnityEvent OnDialogueEnd;

        private readonly CompositeDisposable disposables = new();

        // MULTIPLAYER PATCH: subscribes to this client's dialogue system once its player exists, instead of reading
        // DialogueSystem.Instance in Awake. That lookup threw at level load, since the dialogue system lives on the
        // player prefab, and OnEnable then dereferenced the null.
        private bool subscribed;

        private void OnEnable()
        {
            if (!TrySubscribe()) LocalPlayerContext.Ready += HandlePlayerReady;
        }

        private void OnDisable()
        {
            LocalPlayerContext.Ready -= HandlePlayerReady;

            // Cleared rather than disposed: a disposed CompositeDisposable disposes whatever is added to it later, so
            // re-enabling this component silently subscribed nothing.
            disposables.Clear();
            subscribed = false;
        }

        private void HandlePlayerReady()
        {
            if (TrySubscribe()) LocalPlayerContext.Ready -= HandlePlayerReady;
        }

        private bool TrySubscribe()
        {
            if (subscribed) return true;

            var dialogueSystem = LocalPlayerContext.DialogueSystem;
            if (dialogueSystem == null) return false;

            dialogueSystem.OnDialogueStart.Subscribe(_ => OnDialogueStart?.Invoke()).AddTo(disposables);
            dialogueSystem.OnDialogueEnd.Subscribe(_ => OnDialogueEnd?.Invoke()).AddTo(disposables);
            subscribed = true;
            return true;
        }
    }
}
