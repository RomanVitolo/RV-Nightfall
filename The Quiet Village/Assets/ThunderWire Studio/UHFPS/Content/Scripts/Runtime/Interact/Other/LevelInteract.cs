using UnityEngine;
using UHFPS.Tools;
using ThunderWire.Attributes;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [Docs("https://docs.twgamesdev.com/uhfps/guides/save-load-manager/previous-scene-persistency")]
    public class LevelInteract : MonoBehaviour, IInteractStart
    {
        public enum TriggerTypeEnum { Trigger, Interact, Event }
        public enum LevelTypeEnum { NextLevel, WorldState, PlayerData }

        public TriggerTypeEnum TriggerType = TriggerTypeEnum.Interact;
        public LevelTypeEnum LevelType = LevelTypeEnum.NextLevel;
        public string NextLevelName;

        public bool CustomTransform;
        public Transform TargetTransform;
        public float LookUpDown;

        private void OnTriggerEnter(Collider other)
        {
            if (TriggerType != TriggerTypeEnum.Trigger)
                return;

            if (other.CompareTag("Player"))
                SwitchLevel();
        }

        public void InteractStart()
        {
            if (TriggerType != TriggerTypeEnum.Interact)
                return;

            SwitchLevel();
        }

        public void SwitchLevel()
        {
            // MULTIPLAYER PATCH: refused before saving, not only at the load. In a session the save would be this
            // player's alone, and the load is refused anyway; see GameManager.SessionExit.
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null && gameManager.BlockSceneChange())
                return;

            if (LevelType == LevelTypeEnum.PlayerData)
            {
                SaveGameManager.SavePlayer();
                LocalPlayerContext.GameManager.LoadNextLevel(NextLevelName);
            }
            else if (CustomTransform)
            {
                SaveGameManager.SaveGame(TargetTransform.position, new Vector2(TargetTransform.eulerAngles.y, LookUpDown), () =>
                {
                    if (LevelType == LevelTypeEnum.NextLevel)
                        LocalPlayerContext.GameManager.LoadNextLevel(NextLevelName);
                    else
                        LocalPlayerContext.GameManager.LoadNextWorld(NextLevelName);
                });
            }
            else
            {
                SaveGameManager.SaveGame(() =>
                {
                    if (LevelType == LevelTypeEnum.NextLevel)
                        LocalPlayerContext.GameManager.LoadNextLevel(NextLevelName);
                    else
                        LocalPlayerContext.GameManager.LoadNextWorld(NextLevelName);
                });
            }
        }

        private void OnDrawGizmos()
        {
            if(CustomTransform && TargetTransform != null)
            {
#if UNITY_EDITOR
                UnityEditor.Handles.color = Color.green.Alpha(0.01f);
                UnityEditor.Handles.DrawSolidDisc(TargetTransform.position, Vector3.up, 1f);
                UnityEditor.Handles.color = Color.green;
                UnityEditor.Handles.DrawWireDisc(TargetTransform.position, Vector3.up, 1f);
#endif
                Gizmos.DrawSphere(TargetTransform.position, 0.05f);
                GizmosE.DrawGizmosArrow(TargetTransform.position, TargetTransform.forward);
            }
        }
    }
}