using UnityEngine;
using UnityEngine.Events;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class DialogueBinder : MonoBehaviour
    {
        public UnityEvent<AudioSource, string> OnDialogueStart;
        public UnityEvent<AudioClip, string> OnSubtitle;
        public UnityEvent OnSubtitleFinish;
        public UnityEvent OnDialogueEnd;

        // MULTIPLAYER PATCH: resolved on use instead of DialogueSystem.Instance in Awake, which threw at level load
        // because the dialogue system lives on the player prefab. Calls before the player spawns do nothing.
        private DialogueSystem dialogueSystem => LocalPlayerContext.DialogueSystem;

        public void NextDialogue()
        {
            dialogueSystem?.NextDialogue();
        }

        public void StopDialogue()
        {
            dialogueSystem?.StopDialogue();
        }
    }
}
