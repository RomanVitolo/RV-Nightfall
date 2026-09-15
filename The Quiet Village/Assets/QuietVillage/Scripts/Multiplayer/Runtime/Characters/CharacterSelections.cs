using System;
using System.Collections.Generic;
using QuietVillage.Multiplayer.Sessions;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>
    /// Holds this player's character choice, and on the host, everyone's: what each player spawns as.
    /// </summary>
    /// <remarks>
    /// Authority: each player chooses for themselves, and the host keeps the record it spawns from. A client sends its
    /// choice to the host when it connects to a room and again whenever it changes it, as a Netcode named message: the
    /// room is connected while players wait in the lobby, before any NetworkObject exists that could carry an RPC.
    ///
    /// The same choice also goes into the room's player properties (<see cref="SessionService.SetLocalCharacter"/>), so
    /// everyone waiting sees each other's roles. That copy is for display only. The host could read it, but it has no
    /// reliable way to tell which Netcode client a room player is, and the spawn has to be right.
    ///
    /// Lives on the persistent multiplayer root, beside the NetworkManager, so the record survives the scene change
    /// into the level where players are spawned.
    /// </remarks>
    public class CharacterSelections : MonoBehaviour
    {
        private const string PrefsKey = "Modules.Multiplayer.Character";
        private const string MessageName = "QuietVillage.CharacterChoice";

        [SerializeField] private CharacterCatalog m_catalog;
        [SerializeField] private NetworkManager m_networkManager;
        [SerializeField] private SessionService m_sessions;

        // Server: what each connected client last said it wants to be.
        private readonly Dictionary<ulong, CharacterChoice> m_choices = new();

        private CustomMessagingManager m_registeredMessaging;
        private CharacterChoice m_localChoice;

        /// <summary>Raised on this client when its own choice changes.</summary>
        public event Action LocalChoiceChanged;

        public CharacterCatalog Catalog => m_catalog;

        /// <summary>This player's current choice, always one that exists in the catalog.</summary>
        public CharacterChoice LocalChoice => m_localChoice;

        private void Awake()
        {
            if (m_networkManager == null) m_networkManager = GetComponent<NetworkManager>();
            if (m_sessions == null) m_sessions = GetComponent<SessionService>();

            if (m_catalog == null)
            {
                Debug.LogError($"{nameof(CharacterSelections)}: no {nameof(CharacterCatalog)} assigned, so everyone " +
                               "plays the player prefab's default body. Run Tools > Quiet Village > Characters > Set Up Characters.",
                    this);
            }

            m_localChoice = Resolve(CharacterChoice.Parse(PlayerPrefs.GetString(PrefsKey, string.Empty)));
            PublishToRoom();
        }

        private void OnEnable()
        {
            if (m_networkManager == null) return;

            m_networkManager.OnServerStarted += HandleServerStarted;
            m_networkManager.OnServerStopped += HandleServerStopped;
            m_networkManager.OnClientConnectedCallback += HandleClientConnected;
            m_networkManager.OnClientDisconnectCallback += HandleClientDisconnected;

            if (m_networkManager.IsServer) HandleServerStarted();
        }

        private void OnDisable()
        {
            if (m_networkManager != null)
            {
                m_networkManager.OnServerStarted -= HandleServerStarted;
                m_networkManager.OnServerStopped -= HandleServerStopped;
                m_networkManager.OnClientConnectedCallback -= HandleClientConnected;
                m_networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
            }

            UnregisterHandler();
        }

        /// <summary>Changes this player's character, remembers it on this machine, and tells the room.</summary>
        /// <remarks>
        /// Meant for the lobby. A change after the level has loaded reaches the host's record, but players already
        /// spawned keep the body they spawned with.
        /// </remarks>
        public void SetLocalChoice(CharacterChoice choice)
        {
            var resolved = Resolve(choice);
            if (resolved.Equals(m_localChoice)) return;

            m_localChoice = resolved;

            PlayerPrefs.SetString(PrefsKey, resolved.Encode());
            PlayerPrefs.Save();

            PublishToRoom();
            SendToHost();

            LocalChoiceChanged?.Invoke();
        }

        /// <summary>
        /// Server: the character to spawn a client as. A saved game's character first, then their choice, then the default.
        /// </summary>
        public CharacterChoice ChoiceFor(ulong clientId)
        {
            var saved = SavedCharacterSource.Active;
            if (saved != null && saved.TryGetSavedCharacter(clientId, out var savedChoice) && !savedChoice.IsEmpty)
                return Resolve(savedChoice);

            if (m_networkManager != null && clientId == m_networkManager.LocalClientId) return m_localChoice;

            return m_choices.TryGetValue(clientId, out var chosen) ? chosen : Resolve(CharacterChoice.None);
        }

        /// <summary>
        /// The character a room member plays, as the room knows it: their saved one when the room resumes a save, their own
        /// pick otherwise. Resolved against the catalog.
        /// </summary>
        /// <remarks>
        /// For display, in the lobby and the in-game room panel. Built from room properties, which every member can read;
        /// the host's spawn record (<see cref="ChoiceFor"/>) is keyed by Netcode client and cannot be matched to a member.
        /// </remarks>
        public CharacterChoice RoomChoiceOf(RoomMember member)
        {
            var saved = m_sessions != null ? CharacterChoice.Parse(m_sessions.SavedCharacterOf(member.Id)) : CharacterChoice.None;
            return Resolve(saved.IsEmpty ? CharacterChoice.Parse(member.Character) : saved);
        }

        /// <summary>The role a room member plays, e.g. "Medic"; empty without a catalog.</summary>
        public string RoomRoleOf(RoomMember member) => m_catalog != null ? m_catalog.RoleOf(RoomChoiceOf(member)) : string.Empty;

        private CharacterChoice Resolve(CharacterChoice choice) =>
            m_catalog != null ? m_catalog.Resolve(choice) : choice;

        private void PublishToRoom()
        {
            if (m_sessions != null) m_sessions.SetLocalCharacter(m_localChoice.Encode());
        }

        // ---- Host record -------------------------------------------------------------------------------

        private void HandleServerStarted()
        {
            m_choices.Clear();
            RegisterHandler();
        }

        private void HandleServerStopped(bool wasHost)
        {
            UnregisterHandler();
            m_choices.Clear();
        }

        private void RegisterHandler()
        {
            // The messaging manager is recreated each time Netcode starts, so register with whichever is current.
            UnregisterHandler();

            m_registeredMessaging = m_networkManager.CustomMessagingManager;
            m_registeredMessaging?.RegisterNamedMessageHandler(MessageName, ReceiveChoice);
        }

        private void UnregisterHandler()
        {
            if (m_registeredMessaging == null) return;

            m_registeredMessaging.UnregisterNamedMessageHandler(MessageName);
            m_registeredMessaging = null;
        }

        private void ReceiveChoice(ulong senderClientId, FastBufferReader payload)
        {
            if (m_networkManager == null || !m_networkManager.IsServer) return;

            try
            {
                payload.ReadValueSafe(out string encoded);

                // Resolved on arrival: the host spawns from this, and must never be handed a character it does not have.
                m_choices[senderClientId] = Resolve(CharacterChoice.Parse(encoded));
            }
            catch (OverflowException)
            {
                Debug.LogWarning($"{nameof(CharacterSelections)}: client {senderClientId} sent an unreadable character choice.", this);
            }
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            if (m_networkManager != null && m_networkManager.IsServer) m_choices.Remove(clientId);
        }

        // ---- Client side ------------------------------------------------------------------------------

        private void HandleClientConnected(ulong clientId)
        {
            // Raised on a client for its own connection; the host keeps its choice locally and sends nothing.
            if (m_networkManager == null || m_networkManager.IsServer || clientId != m_networkManager.LocalClientId) return;

            SendToHost();
        }

        private void SendToHost()
        {
            if (m_networkManager == null || !m_networkManager.IsConnectedClient || m_networkManager.IsServer) return;

            var messaging = m_networkManager.CustomMessagingManager;
            if (messaging == null) return;

            var encoded = m_localChoice.Encode();
            using var writer = new FastBufferWriter(FastBufferWriter.GetWriteSize(encoded), Allocator.Temp);
            writer.WriteValueSafe(encoded);
            messaging.SendNamedMessage(MessageName, NetworkManager.ServerClientId, writer);
        }

#if UNITY_EDITOR
        /// <summary>Clears the remembered choice, for testing the first-run default.</summary>
        [ContextMenu("Forget Local Choice")]
        private void ForgetLocalChoice()
        {
            PlayerPrefs.DeleteKey(PrefsKey);
            m_localChoice = Resolve(CharacterChoice.None);
            PublishToRoom();
            LocalChoiceChanged?.Invoke();
        }
#endif
    }
}
