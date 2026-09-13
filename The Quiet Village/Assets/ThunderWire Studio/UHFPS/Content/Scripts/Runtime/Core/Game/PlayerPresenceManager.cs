using System;
using System.Collections;
using UnityEngine;
using Unity.Cinemachine;
using UHFPS.Tools;
using ThunderWire.Attributes;

namespace UHFPS.Runtime
{
    [Docs("https://docs.twgamesdev.com/uhfps/getting-started")]
    public class PlayerPresenceManager : MonoBehaviour
    {
        public enum UnlockType
        {
            /// <summary>
            /// Player will be unlocked at the start or after the game state is loaded.
            /// </summary>
            Automatically,

            /// <summary>
            /// Player will be unlocked after calling the <b>UnlockPlayer()</b> function.
            /// </summary>
            Manually
        }

        public UnlockType PlayerUnlockType = UnlockType.Automatically;
        public GameObject Player;

        public float WaitFadeOutTime = 0.5f;
        public float FadeOutSpeed = 3f;

        private PlayerComponent[] playerComponents;
        private GameManager gameManager;
        private GameObject activeCamera;

        public bool PlayerIsUnlocked;
        public bool GameStateIsLoaded;
        public bool IsCameraSwitched;

        private bool isBackgroundFadedOut;

        private PlayerManager playerManager;

        public PlayerManager PlayerManager
        {
            get
            {
                // MULTIPLAYER PATCH: Player is null until the networked local player is bound.
                if (playerManager == null && Player != null)
                    playerManager = Player.GetComponent<PlayerManager>();

                return playerManager;
            }
        }

        public PlayerStateMachine StateMachine => PlayerManager.PlayerStateMachine;
        public LookController LookController => PlayerManager.LookController;

        /// <summary>
        /// Check if player is unlocked and the active camera is player camera.
        /// </summary>
        public bool IsUnlockedAndCamera => PlayerIsUnlocked && !IsCameraSwitched;

        public Camera PlayerCamera => PlayerManager.MainCamera;

        public CinemachineCamera PlayerVirtualCamera => PlayerManager.MainVirtualCamera;

        private void OnEnable()
        {
            // MULTIPLAYER PATCH: this player's own SaveGameManager, which moved onto the prefab beside this component.
            // SaveGameManager.Instance took the first in the scene, so every copy subscribed to one player's manager.
            var saveGameManager = GetComponent<SaveGameManager>();
            if (saveGameManager == null) saveGameManager = SaveGameManager.Instance;

            saveGameManager.OnGameLoaded += () =>
            {
                // Executed only when loading game state
                UnlockPlayer();
                GameStateIsLoaded = true;
            };
        }

        private void Awake()
        {
            gameManager = GetComponent<GameManager>();

            // MULTIPLAYER PATCH: this component now lives on the player prefab rather than in the
            // scene, so the player it manages is simply the object it is attached to.
            Player = gameObject;

            // Freezing and cursor handling deliberately do NOT happen here. Awake runs on every
            // spawned copy of every player, and a remote copy of somebody else's player must not
            // seize this client's cursor. BindPlayer does that work, and only the owner calls it.
            // See Assets/QuietVillage/Docs/UHFPS-PATCHES.md.
        }

        /// <summary>
        /// Rebinds this manager to a player that came into existence after scene load.
        /// </summary>
        /// <remarks>
        /// MULTIPLAYER PATCH. Netcode spawns the local player once a session connects, which is after
        /// Awake has run. This repeats the work Awake would have done for a scene-placed player, and
        /// drops the cached PlayerManager so the properties above resolve against the new instance.
        /// </remarks>
        /// <param name="player">The newly spawned local player. Ignored when null.</param>
        public void BindPlayer(GameObject player)
        {
            if (player == null) return;

            Player = player;
            playerManager = null;
            playerComponents = Player.GetComponentsInChildren<PlayerComponent>(true);

            // Match the original Awake contract: frozen first, then released by UnlockPlayer().
            FreezePlayer(true);

            // Same condition Start used to apply — a loaded save unlocks through OnGameLoaded instead.
            if (!SaveGameManager.GameActuallyLoad && PlayerUnlockType == UnlockType.Automatically)
                UnlockPlayer();
        }

        private void Start()
        {
            // MULTIPLAYER PATCH: unlocking moved to BindPlayer. Start runs on every spawned copy,
            // including other players' bodies, and only the owning client may unlock and take the
            // cursor. BindPlayer still honours PlayerUnlockType.
        }

        public T Component<T>()
        {
            return Player.GetComponentInChildren<T>(true);
        }

        public T[] Components<T>()
        {
            return Player.GetComponentsInChildren<T>(true);
        }

        public void FreezeMovement(bool freeze)
        {
            StateMachine.SetEnabled(!freeze);
        }

        public void FreezeLook(bool freeze, bool showCursor = false)
        {
            GameTools.ShowCursor(!showCursor, showCursor);
            LookController.SetEnabled(!freeze);
        }

        public void FreezePlayer(bool freeze, bool showCursor = false)
        {
            GameTools.ShowCursor(!showCursor, showCursor);

            // MULTIPLAYER PATCH: null before the networked player has been bound.
            if (playerComponents == null) return;

            foreach (var component in playerComponents)
            {
                component.SetEnabled(!freeze);
            }
        }

        public void FadeBackground(bool fadeOut, Action onBackgroundFade)
        {
            StartCoroutine(StartFadeBackground(fadeOut, onBackgroundFade));
        }

        IEnumerator StartFadeBackground(bool fadeOut, Action onBackgroundFade)
        {
            yield return gameManager.StartBackgroundFade(fadeOut, WaitFadeOutTime, FadeOutSpeed);
            isBackgroundFadedOut = fadeOut;
            onBackgroundFade?.Invoke();
        }

        public void UnlockPlayer()
        {
            StartCoroutine(DoUnlockPlayer());
        }

        private IEnumerator DoUnlockPlayer()
        {
            if (!isBackgroundFadedOut)
                yield return gameManager.StartBackgroundFade(true, WaitFadeOutTime, FadeOutSpeed);

            FreezePlayer(false);
            PlayerIsUnlocked = true;
        }

        public (Vector3 position, Vector2 rotation) GetPlayerTransform()
        {
            return (Player.transform.position, LookController.LookRotation);
        }

        public void SetPlayerTransform(Vector3 position, Vector2 rotation)
        {
            Player.transform.SetPositionAndRotation(position, Quaternion.identity);
            LookController.LookRotation = rotation;
            Physics.SyncTransforms(); // sync position to character controller
        }

        public void SetPlayerPositionAndLook(Vector3 position, Vector2 eulerLook)
        {
            Player.transform.SetPositionAndRotation(position, Quaternion.identity);
            LookController.ApplyEulerLook(eulerLook);
            Physics.SyncTransforms(); // sync position to character controller
        }

        public void Teleport(Vector3 position)
        {
            Player.transform.SetPositionAndRotation(position, Quaternion.identity);
            Physics.SyncTransforms();
        }

        public void Teleport(Vector3 position, bool colliderState = true)
        {
            Player.transform.position = position;
            StateMachine.PlayerCollider.enabled = colliderState;

            // sync position to character controller
            if (colliderState) Physics.SyncTransforms();
        }

        public void Teleport(Vector3 position, Vector2 eulerLook, bool colliderState = true)
        {
            Player.transform.SetPositionAndRotation(position, Quaternion.identity);
            StateMachine.PlayerCollider.enabled = colliderState;
            LookController.ApplyEulerLook(eulerLook);

            // sync position to character controller
            if (colliderState) Physics.SyncTransforms();
        }

        public void SwitchActiveCamera(GameObject virtualCameraObj, float fadeSpeed, Action onBackgroundFade)
        {
            StartCoroutine(SwitchCamera(virtualCameraObj, fadeSpeed, onBackgroundFade, null));
            IsCameraSwitched = true;
        }

        public void SwitchActiveCamera(GameObject virtualCameraObj, float fadeSpeed, Action onBackgroundFade,
            Action onFadeComplete)
        {
            StartCoroutine(SwitchCamera(virtualCameraObj, fadeSpeed, onBackgroundFade, onFadeComplete));
            IsCameraSwitched = true;
        }

        public void SwitchToPlayerCamera(float fadeSpeed, Action onBackgroundFade)
        {
            StartCoroutine(SwitchCamera(null, fadeSpeed, onBackgroundFade, null));
        }

        public void SwitchToPlayerCamera(float fadeSpeed, Action onBackgroundFade, Action onFadeComplete)
        {
            StartCoroutine(SwitchCamera(null, fadeSpeed, onBackgroundFade, onFadeComplete));
        }

        public IEnumerator SwitchCamera(GameObject cameraObj, float fadeSpeed)
        {
            yield return gameManager.StartBackgroundFade(false, fadeSpeed: fadeSpeed);
            playerManager.MainVirtualCamera.gameObject.SetActive(cameraObj == null);

            if (cameraObj != null) playerManager.PlayerItems.DeactivateCurrentItem();
            else playerManager.PlayerItems.ActivatePreviouslyDeactivatedItem();

            if (activeCamera != null) activeCamera.SetActive(false);
            if (cameraObj != null) cameraObj.SetActive(cameraObj != null);
            activeCamera = cameraObj;

            yield return new WaitForEndOfFrame();
            yield return gameManager.StartBackgroundFade(true, fadeSpeed: fadeSpeed);

            IsCameraSwitched = cameraObj != null; // check if camera switched to player camera
        }

        private IEnumerator SwitchCamera(GameObject cameraObj, float fadeSpeed, Action onBackgroundFade,
            Action onFadeComplete)
        {
            yield return gameManager.StartBackgroundFade(false, fadeSpeed: fadeSpeed);
            playerManager.MainVirtualCamera.gameObject.SetActive(cameraObj == null);

            if (cameraObj != null) playerManager.PlayerItems.DeactivateCurrentItem();
            else playerManager.PlayerItems.ActivatePreviouslyDeactivatedItem();

            if (activeCamera != null) activeCamera.SetActive(false);
            if (cameraObj != null) cameraObj.SetActive(cameraObj != null);
            activeCamera = cameraObj;

            onBackgroundFade?.Invoke();

            yield return new WaitForEndOfFrame();
            yield return gameManager.StartBackgroundFade(true, fadeSpeed: fadeSpeed);

            IsCameraSwitched = cameraObj != null; // check if camera switched to player camera

            yield return new WaitForSeconds(0.1f);
            onFadeComplete?.Invoke();
        }
    }
}