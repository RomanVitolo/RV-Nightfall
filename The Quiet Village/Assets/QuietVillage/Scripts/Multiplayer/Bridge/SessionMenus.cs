using System.Collections.Generic;
using QuietVillage.Multiplayer.Flow;
using TMPro;
using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Adapts UHFPS's pause and death menus to a multiplayer session: no saving, loading or restarting, and
    /// quitting leaves the room.
    /// </summary>
    /// <remarks>
    /// Each of those buttons ends in a plain SceneManager load, which takes one player out of the session, or,
    /// pressed by the host, ends it for everyone without warning. Rooms lock when the game starts, so whoever
    /// leaves cannot come back. Save belongs to the host, who saves the whole room's game through World Sync, and
    /// is hidden for everyone else; Load is hidden because a save is resumed from the lobby, where the room that
    /// plays it is made; Restart because a level cannot restart under one player (<see cref="SessionDeathScreen"/>
    /// brings it back for the host, once everyone is dead). Quit and Main Menu become Leave Game (End Game on the
    /// host), and act only on a second press.
    ///
    /// Buttons are found by the UHFPS method their click calls, not by name or reference, so this needs no prefab
    /// wiring and survives the menus being rearranged. <see cref="GameManager.SessionExit"/> covers every other
    /// route to a scene change, such as a level exit in the world.
    ///
    /// Local player only: <see cref="HeroPlayerNetworkSetup"/> adds it to the owner's copy.
    /// </remarks>
    public class SessionMenus : MonoBehaviour, ISessionExit
    {
        private const float ConfirmSeconds = 4f;
        private const float BlockedHintSeconds = 3f;

        private const string BlockedHint = "Changing levels isn't available in multiplayer yet.";

        private const string SaveLabel = "Save Game";
        private const string SaveTooltip = "Saves the world and everyone's belongings, for the host to resume later.";
        private const string LeavingLabel = "Leaving...";

        private sealed class LeaveButton
        {
            public Button Button;
            public TMP_Text Label;
        }

        private readonly List<LeaveButton> m_leaveButtons = new();

        private GameManager m_gameManager;
        private bool m_isHost;
        private LeaveButton m_armed;
        private float m_armedUntil;
        private bool m_leaving;

        private string IdleLabel => m_isHost ? "End Game" : "Leave Game";
        private string ConfirmLabel => m_isHost ? "End for everyone?" : "Leave for good?";

        private string Tooltip => m_isHost
            ? "Ends the game and sends everyone back to the lobby."
            : "Returns you to the lobby. The game is locked, so you cannot rejoin.";

        /// <summary>Takes over the menus of the local player's HUD.</summary>
        /// <param name="gameManager">The local player's GameManager.</param>
        /// <param name="isHost">Whether leaving ends the game for everyone.</param>
        public void Bind(GameManager gameManager, bool isHost)
        {
            m_gameManager = gameManager;
            m_isHost = isHost;
            m_gameManager.SessionExit = this;

            // Runs during OnNetworkSpawn, before the HUD's Start, which is where the labels would be localized.
            foreach (var button in GetComponentsInChildren<Button>(true))
            {
                AdaptButton(button);
            }
        }

        private void OnDestroy()
        {
            if (m_gameManager != null && ReferenceEquals(m_gameManager.SessionExit, this))
                m_gameManager.SessionExit = null;
        }

        private void Update()
        {
            if (m_armed != null && Time.unscaledTime > m_armedUntil) Disarm();
        }

        public void LeaveGame()
        {
            if (m_leaving) return;

            m_leaving = true;
            m_armed = null;

            foreach (var leave in m_leaveButtons)
            {
                SetLabel(leave, LeavingLabel);
                leave.Button.interactable = false;
            }

            // Lives on the persistent multiplayer root, not the level or the player.
            var flow = FindAnyObjectByType<SessionFlow>();
            if (flow != null)
            {
                _ = flow.LeaveGameAsync();
                return;
            }

            Debug.LogError($"{nameof(SessionMenus)}: no {nameof(SessionFlow)} loaded, so there is no session to leave.", this);
        }

        /// <summary>Host only: saves the room's game through the level's World Sync, which gathers the players.</summary>
        private void HandleSaveClicked()
        {
            var world = FindAnyObjectByType<World.WorldSync>();
            if (world == null)
            {
                Debug.LogError($"{nameof(SessionMenus)}: no WorldSync in the level, so the game cannot be saved.", this);
                return;
            }

            world.SaveGame();
        }

        public void NotifySceneChangeBlocked()
        {
            if (m_gameManager != null) m_gameManager.ShowHintMessage(BlockedHint, BlockedHintSeconds);
        }

        private void AdaptButton(Button button)
        {
            var onClick = button.onClick;

            for (var i = 0; i < onClick.GetPersistentEventCount(); i++)
            {
                var target = onClick.GetPersistentTarget(i);
                var method = onClick.GetPersistentMethodName(i);

                // The host saves for the whole room; a client's copy of the button would save nothing of the world.
                if (Calls<SaveGameManager>(target, method, nameof(SaveGameManager.SaveGame)) && m_isHost)
                {
                    onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
                    TakeOverButton(button, SaveLabel, SaveTooltip, HandleSaveClicked);
                    return;
                }

                if (Calls<SaveGameManager>(target, method, nameof(SaveGameManager.SaveGame))
                    || Calls<SavesUILoader>(target, method, nameof(SavesUILoader.LoadSavedGames))
                    || Calls<GameManager>(target, method, nameof(GameManager.RestartGame)))
                {
                    // The vertical layout closes the gap.
                    button.gameObject.SetActive(false);
                    return;
                }

                if (Calls<GameManager>(target, method, nameof(GameManager.MainMenu)))
                {
                    onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
                    TakeOverLeaveButton(button);
                    return;
                }
            }
        }

        /// <summary>Whether a button's listener calls a UHFPS method, by the method and, when it has one, its target.</summary>
        /// <remarks>
        /// A missing target still counts: the HUD's move onto the player prefab emptied some targets and kept the method
        /// names (Tools > Quiet Village > Multiplayer > Repair Player UI Events puts them back), and a button that is
        /// adapted here must be recognised either way. The method names matched are UHFPS's own and unambiguous.
        /// </remarks>
        internal static bool Calls<T>(Object target, string method, string expected) =>
            (target is T || target == null) && method == expected;

        /// <summary>Gives a button a new label, tooltip and action, in place of the UHFPS one it called.</summary>
        private static void TakeOverButton(Button button, string label, string tooltip, UnityAction onClick)
        {
            // GLocText localizes the label once, in Start. Stopped before then, it leaves the label to us.
            foreach (var localized in button.GetComponentsInChildren<GLocText>(true))
            {
                localized.enabled = false;
            }

            var text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null) text.text = label;

            if (button.TryGetComponent(out MenuHoverTooltip hover)) hover.TooltipMessage = tooltip;

            button.onClick.AddListener(onClick);
        }

        private void TakeOverLeaveButton(Button button)
        {
            // GLocText localizes the label once, in Start. Stopped before then, it leaves the label to us.
            foreach (var localized in button.GetComponentsInChildren<GLocText>(true))
            {
                localized.enabled = false;
            }

            var tooltip = button.GetComponent<MenuHoverTooltip>();
            if (tooltip != null) tooltip.TooltipMessage = Tooltip;

            var leave = new LeaveButton { Button = button, Label = button.GetComponentInChildren<TMP_Text>(true) };
            m_leaveButtons.Add(leave);

            SetLabel(leave, IdleLabel);
            button.onClick.AddListener(() => HandleLeaveClicked(leave));
        }

        private void HandleLeaveClicked(LeaveButton leave)
        {
            if (m_leaving) return;

            // Leaving cannot be undone, so the first press only asks.
            if (m_armed != leave)
            {
                Disarm();
                m_armed = leave;
                m_armedUntil = Time.unscaledTime + ConfirmSeconds;
                SetLabel(leave, ConfirmLabel);
                return;
            }

            LeaveGame();
        }

        private void Disarm()
        {
            if (m_armed != null) SetLabel(m_armed, IdleLabel);
            m_armed = null;
        }

        private static void SetLabel(LeaveButton leave, string text)
        {
            if (leave.Label != null) leave.Label.text = text;
        }
    }
}
