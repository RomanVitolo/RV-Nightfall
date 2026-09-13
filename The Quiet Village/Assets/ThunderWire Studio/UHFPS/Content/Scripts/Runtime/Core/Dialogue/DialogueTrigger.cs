using UnityEngine;
using UHFPS.Scriptable;
using Newtonsoft.Json.Linq;
using static UHFPS.Scriptable.DialogueAsset;
// MULTIPLAYER PATCH: outside the editor block; Update uses LocalPlayerContext at runtime, so player builds failed to compile.
using QuietVillage.Multiplayer.Bridge;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UHFPS.Runtime
{
    public class DialogueTrigger : MonoBehaviour, IInteractStart, ISaveable
    {
        public enum TriggerTypeEnum { Trigger, Interact, Event }
        public enum DialogueTypeEnum { Local, Global }
        public enum DialogueContinueEnum { Sequence, Event }

        public DialogueSystem.DialogueData DialogueData { get; private set; }
        public bool IsCompleted { get; set; }

        public TriggerTypeEnum TriggerType;
        public DialogueTypeEnum DialogueType;
        public DialogueContinueEnum DialogueContinue;
        
        public DialogueAsset Dialogue;
        public AudioSource DialogueAudio;
        public string BinderName;

        public bool RangedDialogue;
        public bool ResetDialogueWhenOut;
        public float LocalDialogueRange;

        // MULTIPLAYER PATCH: resolved on use instead of DialogueSystem.Instance in Awake. The dialogue system lives on the
        // player prefab; at level load the singleton lookup threw, and later it could return a teammate's disabled copy.
        private DialogueSystem dialogueSystem => LocalPlayerContext.DialogueSystem;
        private Transform playerTransform;
        private bool isTriggered;

        private void Start()
        {
            DialogueData = new();
            foreach (var dialogue in Dialogue.Dialogues)
            {
                var copy = dialogue.Copy();
                if(copy.SubtitleType == DialogueAsset.SubtitleTypeEnum.Single)
                {
                    copy.SingleSubtitle.Text.SubscribeGloc();
                }
                else
                {
                    foreach (var subtitle in copy.Subtitles)
                    {
                        if (subtitle is DialogueSubtitle sub)
                        {
                            sub.Text.SubscribeGloc();
                        }
                    }
                }

                DialogueData.Add(copy);
            }
        }

        private void Update()
        {
            if (DialogueType == DialogueTypeEnum.Global || !RangedDialogue || !isTriggered || IsCompleted)
                return;

            // MULTIPLAYER PATCH: resolved on first use, since Netcode spawns the player after Awake.
            if (playerTransform == null)
            {
                var localPlayer = LocalPlayerContext.Player;
                if (localPlayer == null) return;

                playerTransform = localPlayer.transform;
            }

            Vector3 playerPos = playerTransform.position;
            Vector3 targetPos = transform.position;
            float distance = Vector3.Distance(playerPos, targetPos);

            if(distance > LocalDialogueRange)
            {
                dialogueSystem?.StopDialogue();
                isTriggered = !ResetDialogueWhenOut;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (TriggerType != TriggerTypeEnum.Trigger)
                return;

            // MULTIPLAYER PATCH: dialogue is per-player, so only this client's own body starts it here.
            if (other.CompareTag("Player") && LocalPlayerContext.IsLocalPlayer(other))
                TriggerDialogue();
        }

        public void InteractStart()
        {
            if (TriggerType != TriggerTypeEnum.Interact)
                return;

            TriggerDialogue();
        }

        public void TriggerDialogue()
        {
            // MULTIPLAYER PATCH: no player yet means nothing to play it to; left untriggered so it can play later.
            if (Dialogue == null || isTriggered || IsCompleted || dialogueSystem == null)
                return;

            isTriggered = dialogueSystem.PlayDialogue(this);
        }

        public StorableCollection OnSave()
        {
            return new StorableCollection()
            {
                { nameof(IsCompleted), IsCompleted }
            };
        }

        public void OnLoad(JToken data)
        {
            IsCompleted = (bool)data[nameof(IsCompleted)];
        }

        private void OnDrawGizmosSelected()
        {
            if (DialogueType != DialogueTypeEnum.Local || !RangedDialogue)
                return;

#if UNITY_EDITOR
            Handles.color = Color.cyan;
            Handles.DrawWireDisc(transform.position, Vector3.up, LocalDialogueRange);
#endif
        }
    }
}