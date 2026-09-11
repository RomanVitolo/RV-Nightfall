using System.Collections.Generic;
using Modules.Multiplayer.Scripts.Runtime.Flow;
using TMPro;
using UHFPS.Rendering;
using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Multiplayer death: permanent until the level restarts. The dead watch a living teammate, and once
    /// everyone is dead the host restarts the level for the whole room.
    /// </summary>
    /// <remarks>
    /// UHFPS's own death runs first and unchanged: the death screen, the blood, the eyes closing. After a short
    /// beat this takes that screen over. While a teammate lives, the backdrop clears, the buttons move out of
    /// the way and a camera follows the teammate over their shoulder. When nobody is left, the backdrop returns
    /// and the host gets Restart Level; everyone else waits for it.
    ///
    /// Who is alive comes from <see cref="PlayerHealthSync"/>, which every client replicates for every player,
    /// so each client works it out for itself; there is no extra network state.
    ///
    /// Local player only: <see cref="HeroPlayerNetworkSetup"/> adds it to the owner's copy, after
    /// <see cref="SessionMenus"/>.
    /// </remarks>
    public class SessionDeathScreen : MonoBehaviour
    {
        private const float DeathBeatSeconds = 3f;

        private const string RestartLabel = "Restart Level";
        private const string RestartingLabel = "Restarting...";
        private const string RestartTooltip = "Reloads the level for everyone.";
        private const string NextLabel = "Next Player";
        private const string NextTooltip = "Watch another teammate.";
        private const string EveryoneDeadTitle = "Everyone is dead";
        private const string WaitingForHostTitle = "Everyone is dead. Waiting for the host to restart.";

        private enum Phase { Alive, Dying, Spectating, EveryoneDead }

        private GameManager m_gameManager;
        private PlayerHealthSync m_health;
        private PlayerHealth m_playerHealth;
        private bool m_isHost;

        private Image m_backdrop;
        private Color m_backdropColor;
        private TMP_Text m_title;
        private RectTransform m_titleRect;
        private Vector2 m_titlePosition;
        private RectTransform m_buttons;
        private Vector2 m_buttonsAnchor;
        private Vector2 m_buttonsPivot;
        private Vector2 m_buttonsPosition;
        private Button m_restart;
        private TMP_Text m_restartText;
        private Button m_next;

        private Phase m_phase;
        private float m_diedAt;
        private SpectatorCamera m_camera;
        private PlayerHealthSync m_target;
        private bool m_restarting;

        /// <summary>Takes over the local player's death screen.</summary>
        /// <param name="gameManager">The local player's GameManager.</param>
        /// <param name="health">The local player's replicated health.</param>
        /// <param name="isHost">Whether this player can restart the level.</param>
        public void Bind(GameManager gameManager, PlayerHealthSync health, bool isHost)
        {
            m_gameManager = gameManager;
            m_health = health;
            m_playerHealth = GetComponent<PlayerHealth>();
            m_isHost = isHost;

            if (!FindDeathScreen())
            {
                Debug.LogError($"{nameof(SessionDeathScreen)}: the death screen does not have the expected Restart " +
                               "button, title and backdrop; dead players will not spectate.", this);
                enabled = false;
                return;
            }

            TakeOverButton(m_restart, RestartLabel, RestartTooltip, HandleRestartClicked);
            m_restartText = m_restart.GetComponentInChildren<TMP_Text>(true);

            // A copy of Restart, so it looks like the rest of the menu. Copied after the takeover, so the copy's
            // own click already calls nothing.
            m_next = Instantiate(m_restart, m_buttons);
            m_next.name = "NextPlayer";
            m_next.transform.SetSiblingIndex(m_restart.transform.GetSiblingIndex());
            TakeOverButton(m_next, NextLabel, NextTooltip, HandleNextClicked);

            m_restart.gameObject.SetActive(false);
            m_next.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            m_camera?.Destroy();
        }

        private void Update()
        {
            switch (m_phase)
            {
                case Phase.Alive:
                    if (m_health.IsDead)
                    {
                        // UHFPS shows its own death screen; let it play before taking over.
                        m_phase = Phase.Dying;
                        m_diedAt = Time.time;
                    }
                    break;

                case Phase.Dying:
                    if (Time.time - m_diedAt >= DeathBeatSeconds) Refresh();
                    break;

                default:
                    Refresh();
                    break;
            }
        }

        private void Refresh()
        {
            if (PlayerHealthSync.EveryoneDead)
            {
                if (m_phase != Phase.EveryoneDead) EnterEveryoneDead();
                return;
            }

            if (m_target == null || !m_target.IsSpawned || m_target.IsDead) Watch(NextLivingTeammate(m_target));

            if (m_phase != Phase.Spectating) EnterSpectating();

            // One teammate left leaves nobody to switch to.
            var canSwitch = CountLivingTeammates() > 1;
            if (m_next.gameObject.activeSelf != canSwitch) m_next.gameObject.SetActive(canSwitch);
        }

        private void EnterSpectating()
        {
            m_phase = Phase.Spectating;

            ClearDeathEffects();

            // The view is the point now: no backdrop, the title up top and the buttons down at the bottom.
            m_backdrop.color = Color.clear;
            m_titleRect.anchoredPosition = new Vector2(m_titlePosition.x, -40f);
            m_buttons.anchorMin = m_buttons.anchorMax = new Vector2(0.5f, 0f);
            m_buttons.pivot = new Vector2(0.5f, 0f);
            m_buttons.anchoredPosition = new Vector2(0f, 100f);

            m_restart.gameObject.SetActive(false);
        }

        private void EnterEveryoneDead()
        {
            m_phase = Phase.EveryoneDead;

            // Back to the death screen as UHFPS laid it out. The camera stays where it was, on the last to fall.
            m_backdrop.color = m_backdropColor;
            m_titleRect.anchoredPosition = m_titlePosition;
            m_buttons.anchorMin = m_buttons.anchorMax = m_buttonsAnchor;
            m_buttons.pivot = m_buttonsPivot;
            m_buttons.anchoredPosition = m_buttonsPosition;

            m_title.text = m_isHost ? EveryoneDeadTitle : WaitingForHostTitle;
            m_next.gameObject.SetActive(false);
            m_restart.gameObject.SetActive(m_isHost);
        }

        private void Watch(PlayerHealthSync teammate)
        {
            m_target = teammate;
            if (teammate == null) return;

            m_camera ??= new SpectatorCamera();

            var locomotion = teammate.GetComponent<PlayerLocomotionSync>();
            m_camera.Follow(locomotion != null ? locomotion.LookTransform : teammate.transform);

            var setup = teammate.GetComponent<HeroPlayerNetworkSetup>();
            var teammateName = setup != null ? setup.DisplayName : "a teammate";
            m_title.text = $"You died  ·  Watching {teammateName}";
        }

        /// <summary>The living teammate after <paramref name="current"/>, in a fixed order, wrapping round.</summary>
        private PlayerHealthSync NextLivingTeammate(PlayerHealthSync current)
        {
            var living = LivingTeammates();
            if (living.Count == 0) return null;

            var index = current != null ? living.IndexOf(current) : -1;
            return living[(index + 1) % living.Count];
        }

        private int CountLivingTeammates() => LivingTeammates().Count;

        private List<PlayerHealthSync> LivingTeammates()
        {
            var living = new List<PlayerHealthSync>();
            foreach (var player in PlayerHealthSync.Spawned)
            {
                if (player != null && player != m_health && !player.IsDead) living.Add(player);
            }

            // Spawn order differs between clients; client id does not.
            living.Sort((a, b) => a.OwnerClientId.CompareTo(b.OwnerClientId));
            return living;
        }

        /// <summary>Opens the eyes UHFPS closed and clears the blood, so the teammate can be seen.</summary>
        private void ClearDeathEffects()
        {
            // Its Update keeps closing the eyes and pushing the blood while the player is dead.
            if (m_playerHealth != null) m_playerHealth.enabled = false;

            var volume = m_gameManager.HealthPPVolume;
            if (volume == null) return;

            volume.weight = 0f;
            if (volume.profile.TryGet(out EyeBlink blink)) blink.Blink.value = 0f;
        }

        private void HandleNextClicked()
        {
            if (m_phase == Phase.Spectating) Watch(NextLivingTeammate(m_target));
        }

        private void HandleRestartClicked()
        {
            if (!m_isHost || m_restarting || m_phase != Phase.EveryoneDead) return;

            // Lives on the persistent multiplayer root.
            var flow = FindAnyObjectByType<SessionFlow>();
            if (flow == null || !flow.RestartLevel()) return;

            m_restarting = true;
            m_restart.interactable = false;
            if (m_restartText != null) m_restartText.text = RestartingLabel;
        }

        /// <summary>Finds the death screen's Restart button, and the backdrop and title around it.</summary>
        private bool FindDeathScreen()
        {
            var deadPanel = m_gameManager.DeadPanel;
            if (deadPanel == null) return false;

            foreach (var button in deadPanel.GetComponentsInChildren<Button>(true))
            {
                var onClick = button.onClick;
                for (var i = 0; i < onClick.GetPersistentEventCount(); i++)
                {
                    if (onClick.GetPersistentTarget(i) is GameManager
                        && onClick.GetPersistentMethodName(i) == nameof(GameManager.RestartGame))
                    {
                        m_restart = button;
                    }
                }
            }

            // Restart sits in the button column, which sits on the backdrop beside the title.
            m_buttons = m_restart != null ? m_restart.transform.parent as RectTransform : null;
            var panel = m_buttons != null ? m_buttons.parent : null;
            if (panel == null || !panel.TryGetComponent(out m_backdrop)) return false;

            foreach (Transform child in panel)
            {
                if (child.TryGetComponent(out m_title)) break;
            }

            if (m_title == null) return false;

            // The title was localized when the HUD started; from now on it says what this screen needs.
            foreach (var localized in m_title.GetComponents<GLocText>()) localized.enabled = false;

            m_backdropColor = m_backdrop.color;
            m_titleRect = m_title.rectTransform;
            m_titlePosition = m_titleRect.anchoredPosition;
            m_buttonsAnchor = m_buttons.anchorMin;
            m_buttonsPivot = m_buttons.pivot;
            m_buttonsPosition = m_buttons.anchoredPosition;
            return true;
        }

        private static void TakeOverButton(Button button, string label, string tooltip, UnityAction onClick)
        {
            for (var i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                button.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            }

            // GLocText localizes the label in Start, which a hidden button has not reached yet.
            foreach (var localized in button.GetComponentsInChildren<GLocText>(true)) localized.enabled = false;

            var text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null) text.text = label;

            if (button.TryGetComponent(out MenuHoverTooltip hover)) hover.TooltipMessage = tooltip;

            button.onClick.AddListener(onClick);
        }
    }
}
