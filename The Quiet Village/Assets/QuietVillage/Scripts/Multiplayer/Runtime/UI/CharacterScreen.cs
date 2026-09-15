using System;
using System.Collections.Generic;
using QuietVillage.Multiplayer.Characters;
using QuietVillage.Multiplayer.Sessions;
using UnityEngine;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.UI
{
    /// <summary>
    /// The Character screen: pick a character (and so a role), a look, and a name, with the body turning beside it.
    /// </summary>
    /// <remarks>
    /// Edits a draft. Nothing reaches <see cref="CharacterSelections"/>, the room or player preferences until Done, so
    /// browsing the roster never flickers the player's role in everyone else's room list, and Cancel simply forgets.
    ///
    /// Owned by <see cref="LobbyScreen"/>, which decides when it may open; this only runs the panel. It keeps no
    /// session state of its own.
    /// </remarks>
    internal sealed class CharacterScreen
    {
        /// <summary>Someone else in the room, and the character they will play.</summary>
        public readonly struct Teammate
        {
            public readonly string DisplayName;
            public readonly string CharacterId;

            public Teammate(string displayName, string characterId)
            {
                DisplayName = displayName;
                CharacterId = characterId;
            }
        }

        private const float DragDegreesPerPixel = 0.45f;

        private readonly VisualElement m_dialog;
        private readonly ScrollView m_list;
        private readonly VisualElement m_preview;
        private readonly TextField m_nameField;
        private readonly Label m_role;
        private readonly Label m_title;
        private readonly Label m_description;
        private readonly VisualElement m_perks;
        private readonly VisualElement m_items;
        private readonly VisualElement m_variants;
        private readonly Label m_hint;
        private readonly Label m_saveNotice;
        private readonly Button m_cancel;
        private readonly Button m_confirm;
        private readonly Button m_random;

        private CharacterSelections m_selections;
        private CharacterPreviewStage m_stage;
        private Image m_previewImage;

        private readonly List<Teammate> m_teammates = new();

        // What the screen opened on, so it can mark the current character and tell whether anything changed.
        private CharacterChoice m_current;
        private string m_openedName;

        // True when a resumed save decides this player's character: the roster is shown but cannot be changed.
        private bool m_locked;

        private string m_draftId;
        private int m_draftVariant;
        private bool m_dragging;
        private float m_lastPointerX;

        /// <summary>Raised on Done with the chosen name; the choice itself is already applied.</summary>
        public event Action<string> Confirmed;

        public bool IsOpen => m_dialog != null && !m_dialog.ClassListContains("mp-hidden");

        /// <summary>False when the layout lacks the screen, e.g. an older LobbyScreen.uxml; the lobby then hides its buttons.</summary>
        public bool IsAvailable { get; }

        public CharacterScreen(VisualElement root)
        {
            m_dialog = root.Q("character-dialog");
            m_list = root.Q<ScrollView>("character-list");
            m_preview = root.Q("character-preview");
            m_nameField = root.Q<TextField>("character-player-name");
            m_role = root.Q<Label>("character-role");
            m_title = root.Q<Label>("character-title");
            m_description = root.Q<Label>("character-description");
            m_perks = root.Q("character-perks");
            m_items = root.Q("character-items");
            m_variants = root.Q("character-variants");
            m_hint = root.Q<Label>("character-lock-hint");
            m_saveNotice = root.Q<Label>("character-save-notice");

            m_cancel = root.Q<Button>("character-cancel");
            m_confirm = root.Q<Button>("character-confirm");

            IsAvailable = m_dialog != null && m_list != null && m_preview != null && m_nameField != null && m_role != null
                          && m_title != null && m_description != null && m_perks != null && m_items != null
                          && m_variants != null && m_cancel != null && m_confirm != null;

            if (!IsAvailable) return;

            m_nameField.maxLength = SessionService.MaxDisplayNameLength;
            m_nameField.RegisterValueChangedCallback(_ => RefreshConfirm());
            m_cancel.clicked += Close;
            m_confirm.clicked += Confirm;

            // Optional, so an older layout without it still opens.
            m_random = root.Q<Button>("character-random");
            if (m_random != null) m_random.clicked += PickRandom;

            m_dialog.RegisterCallback<NavigationMoveEvent>(HandleNavigationMove);
            m_dialog.RegisterCallback<NavigationCancelEvent>(HandleNavigationCancel);
            m_dialog.RegisterCallback<KeyDownEvent>(HandleKeyDown);
            m_dialog.RegisterCallback<NavigationSubmitEvent>(HandleNavigationSubmit, TrickleDown.TrickleDown);

            m_previewImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            m_previewImage.AddToClassList("mp-character-preview-image");
            m_preview.Insert(0, m_previewImage);

            m_preview.RegisterCallback<PointerDownEvent>(BeginDrag);
            m_preview.RegisterCallback<PointerMoveEvent>(Drag);
            m_preview.RegisterCallback<PointerUpEvent>(EndDrag);
            m_preview.RegisterCallback<PointerCaptureOutEvent>(_ => m_dragging = false);
        }

        public void Bind(CharacterSelections selections, CharacterPreviewStage stage)
        {
            m_selections = selections;
            m_stage = stage;

            if (m_previewImage != null) m_previewImage.image = m_stage != null ? m_stage.Texture : null;
        }

        /// <summary>Opens on the player's current choice.</summary>
        /// <param name="nameEditable">False in a room: the name went out with the join and would not reach anyone.</param>
        /// <param name="savedCharacter">
        /// The character a resumed save gives this player, which the host spawns them as whatever they pick; the screen
        /// then only shows it. <c>null</c> when their pick decides.
        /// </param>
        /// <param name="teammates">Everyone else in the room, so the roster can show who is already playing what.</param>
        public void Open(string playerName, bool nameEditable, CharacterChoice? savedCharacter = null,
            IReadOnlyList<Teammate> teammates = null)
        {
            if (!IsAvailable || m_selections == null || m_selections.Catalog == null) return;

            m_locked = savedCharacter.HasValue;
            m_current = m_selections.Catalog.Resolve(savedCharacter ?? m_selections.LocalChoice);
            m_draftId = m_current.CharacterId;
            m_draftVariant = m_current.Variant;

            m_openedName = playerName ?? string.Empty;
            m_nameField.value = m_openedName;
            m_nameField.SetEnabled(nameEditable);
            if (m_hint != null)
                m_hint.text = nameEditable ? string.Empty : "Your name is fixed while you are in a room.";

            if (m_saveNotice != null)
            {
                var role = m_selections.Catalog.RoleOf(m_current);
                m_saveNotice.text = m_locked
                    ? $"This room continues a saved game, so you play as the {role} you were when it was saved."
                    : string.Empty;
                m_saveNotice.EnableInClassList("mp-hidden", !m_locked);
            }

            m_confirm.EnableInClassList("mp-hidden", m_locked);
            m_cancel.text = m_locked ? "Back" : "Cancel";
            m_random?.EnableInClassList("mp-hidden", m_locked);

            m_teammates.Clear();
            if (teammates != null) m_teammates.AddRange(teammates);

            BuildList();
            ShowDraft();

            if (m_stage != null) m_stage.SetShowing(true);
            m_dialog.RemoveFromClassList("mp-hidden");

            // Focus the chosen card once the dialog has laid out, so arrows, Enter and Escape work without a click first.
            m_dialog.schedule.Execute(FocusChosenCard);
        }

        private void FocusChosenCard()
        {
            foreach (var card in m_list.Children())
            {
                if (card.userData is not CharacterCatalog.Character listed || listed.Id != m_draftId) continue;

                card.Focus();
                m_list.ScrollTo(card);
                return;
            }
        }

        /// <summary>Updates who is playing what, e.g. as players join, leave or change while the screen is open.</summary>
        public void SetTeammates(IReadOnlyList<Teammate> teammates)
        {
            if (!IsAvailable) return;

            m_teammates.Clear();
            if (teammates != null) m_teammates.AddRange(teammates);

            foreach (var card in m_list.Children()) RefreshCard(card);
        }

        public void Close()
        {
            if (!IsAvailable) return;

            m_dialog.AddToClassList("mp-hidden");
            m_dragging = false;
            if (m_stage != null) m_stage.SetShowing(false);
        }

        private void Confirm()
        {
            // A saved character is not the player's pick; recording it would overwrite the one they come back to.
            if (!m_locked) m_selections?.SetLocalChoice(new CharacterChoice(m_draftId, m_draftVariant));

            var name = m_nameField.value;
            Close();
            Confirmed?.Invoke(name);
        }

        // ---- Roster ----------------------------------------------------------------------------------

        private void BuildList()
        {
            m_list.Clear();

            foreach (var character in m_selections.Catalog.Characters)
            {
                if (m_selections.Catalog.Find(character?.Id) == null) continue;

                var card = new Button { userData = character };
                card.AddToClassList("mp-character-card");
                card.clicked += () => SelectCharacter(character);

                var role = new Label(character.RoleOrName);
                role.AddToClassList("mp-character-card-role");
                card.Add(role);

                var body = new Label(character.DisplayName);
                body.AddToClassList("mp-muted");
                card.Add(body);

                var perks = CharacterCatalog.Summarise(character.Perks);
                if (perks.Length > 0)
                {
                    var summary = new Label(perks);
                    summary.AddToClassList("mp-character-card-perks");
                    card.Add(summary);
                }

                var current = new Label("Current") { name = "current" };
                current.AddToClassList("mp-badge");
                current.AddToClassList("mp-badge--you");
                current.AddToClassList("mp-character-card-current");
                card.Add(current);

                var playedBy = new Label { name = "played-by" };
                playedBy.AddToClassList("mp-character-card-played-by");
                card.Add(playedBy);

                // Shown, so a player kept to their saved character still sees what the others are, but not choosable.
                card.SetEnabled(!m_locked || character.Id == m_current.CharacterId);

                RefreshCard(card);
                m_list.Add(card);
            }
        }

        /// <summary>Marks the player's current character and names the teammates playing this one.</summary>
        private void RefreshCard(VisualElement card)
        {
            if (card.userData is not CharacterCatalog.Character character) return;

            card.Q<Label>("current").EnableInClassList("mp-hidden", character.Id != m_current.CharacterId);

            var names = new List<string>();
            foreach (var teammate in m_teammates)
            {
                if (teammate.CharacterId == character.Id) names.Add(teammate.DisplayName);
            }

            var playedBy = card.Q<Label>("played-by");
            playedBy.text = names.Count > 0 ? $"Played by {string.Join(", ", names)}" : string.Empty;
            playedBy.EnableInClassList("mp-hidden", names.Count == 0);
        }

        private void SelectCharacter(CharacterCatalog.Character character)
        {
            if (m_locked || character == null || character.Id == m_draftId) return;

            m_draftId = character.Id;
            m_draftVariant = 0;
            ShowDraft();
        }

        private void SelectVariant(int variant)
        {
            if (m_locked || variant == m_draftVariant) return;

            m_draftVariant = variant;
            ShowDraft();
        }

        /// <summary>Moves the selection up or down the roster, keeping keyboard focus on the chosen card.</summary>
        private void StepCharacter(int step)
        {
            if (m_locked) return;

            var cards = new List<VisualElement>(m_list.Children());
            var index = cards.FindIndex(card => card.userData is CharacterCatalog.Character listed && listed.Id == m_draftId);
            if (cards.Count == 0 || index < 0) return;

            var next = cards[(index + step + cards.Count) % cards.Count];
            SelectCharacter(next.userData as CharacterCatalog.Character);

            next.Focus();
            m_list.ScrollTo(next);
        }

        /// <summary>Moves to the previous or next look that has a body, wrapping round.</summary>
        private void StepVariant(int step)
        {
            var variants = UsableVariants(m_selections.Catalog.Find(m_draftId));
            if (variants.Count < 2) return;

            var index = Mathf.Max(0, variants.IndexOf(m_draftVariant));
            SelectVariant(variants[(index + step + variants.Count) % variants.Count]);
        }

        /// <summary>Any character and look other than the one shown, for players who would rather not choose.</summary>
        private void PickRandom()
        {
            if (m_locked) return;

            var options = new List<CharacterChoice>();
            foreach (var character in m_selections.Catalog.Characters)
            {
                if (m_selections.Catalog.Find(character?.Id) == null) continue;

                foreach (var variant in UsableVariants(character))
                {
                    if (character.Id != m_draftId || variant != m_draftVariant) options.Add(new CharacterChoice(character.Id, variant));
                }
            }

            if (options.Count == 0) return;

            var picked = options[UnityEngine.Random.Range(0, options.Count)];
            m_draftId = picked.CharacterId;
            m_draftVariant = picked.Variant;
            ShowDraft();

            foreach (var card in m_list.Children())
            {
                if (card.userData is CharacterCatalog.Character listed && listed.Id == m_draftId) m_list.ScrollTo(card);
            }
        }

        /// <summary>Indices of a character's looks that have a body; the ones the Look buttons offer.</summary>
        private static List<int> UsableVariants(CharacterCatalog.Character character)
        {
            var usable = new List<int>();
            if (character?.Variants == null) return usable;

            for (var i = 0; i < character.Variants.Count; i++)
            {
                if (character.Variants[i] != null && character.Variants[i].Prefab != null) usable.Add(i);
            }

            return usable;
        }

        // ---- Keyboard and controller -------------------------------------------------------------------

        private void HandleNavigationMove(NavigationMoveEvent evt)
        {
            if (!IsOpen || IsInNameField(evt.target)) return;

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up: StepCharacter(-1); break;
                case NavigationMoveEvent.Direction.Down: StepCharacter(1); break;
                case NavigationMoveEvent.Direction.Left: StepVariant(-1); break;
                case NavigationMoveEvent.Direction.Right: StepVariant(1); break;
                default: return;
            }

            // Otherwise UI Toolkit also moves focus to whatever lies that way, away from the card just chosen.
            evt.StopPropagation();
            m_dialog.focusController?.IgnoreEvent(evt);
        }

        private void HandleNavigationCancel(NavigationCancelEvent evt)
        {
            if (!IsOpen) return;

            evt.StopPropagation();
            Close();
        }

        private void HandleKeyDown(KeyDownEvent evt)
        {
            if (!IsOpen || (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)) return;

            // A focused button such as Cancel or a look does what it says; Enter only means Done from a card or the name.
            if (evt.target is Button button && !button.ClassListContains("mp-character-card")) return;

            evt.StopPropagation();
            ConfirmFromKeys();
        }

        /// <summary>
        /// A controller's submit (or Enter) on a card means Done: moving already chose the card, so pressing on it again
        /// can only be meant as "this one".
        /// </summary>
        /// <remarks>
        /// Caught while trickling down, before the card sees it as a click. Other buttons, Cancel and the looks, still get
        /// theirs. A mouse click is a separate event and still only selects.
        /// </remarks>
        private void HandleNavigationSubmit(NavigationSubmitEvent evt)
        {
            if (!IsOpen || evt.target is not Button button || !button.ClassListContains("mp-character-card")) return;

            evt.StopPropagation();
            ConfirmFromKeys();
        }

        private void ConfirmFromKeys()
        {
            // Both Enter's key event and its submit event can arrive; the first one closes the screen.
            if (!IsOpen) return;

            if (m_locked) Close();
            else if (m_confirm.enabledSelf) Confirm();
        }

        private bool IsInNameField(IEventHandler target) =>
            target is VisualElement element && (element == m_nameField || m_nameField.Contains(element));

        private void ShowDraft()
        {
            var catalog = m_selections.Catalog;
            var resolved = catalog.Resolve(new CharacterChoice(m_draftId, m_draftVariant));
            m_draftId = resolved.CharacterId;
            m_draftVariant = resolved.Variant;

            var character = catalog.Find(m_draftId);

            foreach (var child in m_list.Children())
            {
                child.EnableInClassList("mp-character-card--selected",
                    child.userData is CharacterCatalog.Character listed && listed.Id == m_draftId);
            }

            m_role.text = character != null ? character.RoleOrName : "No characters set up";
            m_title.text = character != null ? character.DisplayName : string.Empty;
            m_description.text = character?.Description ?? string.Empty;

            var perkLines = character != null ? CharacterCatalog.Describe(character.Perks) : null;
            var ability = character != null ? CharacterCatalog.DescribeAbility(character.Ability) : string.Empty;
            if (perkLines != null && ability.Length > 0) perkLines.Insert(0, ability);
            FillLines(m_perks, perkLines, "No special perks.");
            FillItems(character);
            FillVariants(character);
            RefreshConfirm();

            if (m_stage != null) m_stage.Show(character, m_draftVariant, catalog.BodyAnimator);
        }

        /// <summary>Done only means something once the character, look or name differs from what the screen opened on.</summary>
        private void RefreshConfirm()
        {
            var changed = m_draftId != m_current.CharacterId || m_draftVariant != m_current.Variant
                          || m_nameField.value != m_openedName;
            m_confirm.SetEnabled(changed);
        }

        private static void FillLines(VisualElement container, System.Collections.Generic.List<string> lines, string empty)
        {
            container.Clear();

            if (lines == null || lines.Count == 0)
            {
                var none = new Label(empty);
                none.AddToClassList("mp-muted");
                container.Add(none);
                return;
            }

            foreach (var line in lines)
            {
                var label = new Label($"•  {line}");
                label.AddToClassList("mp-character-line");
                container.Add(label);
            }
        }

        private void FillItems(CharacterCatalog.Character character)
        {
            m_items.Clear();

            if (character?.StartingItems != null)
            {
                foreach (var item in character.StartingItems)
                {
                    if (item == null || string.IsNullOrEmpty(item.ItemGuid) || item.Quantity <= 0) continue;

                    var row = new VisualElement();
                    row.AddToClassList("mp-character-item");

                    // Every row keeps the icon's space, so names line up whether or not an item has one.
                    var icon = new Image { sprite = item.Icon, scaleMode = ScaleMode.ScaleToFit };
                    icon.AddToClassList("mp-character-item-icon");
                    row.Add(icon);

                    var label = new Label(item.Quantity > 1 ? $"{item.Title} ×{item.Quantity}" : item.Title);
                    label.AddToClassList("mp-character-line");
                    row.Add(label);

                    m_items.Add(row);
                }
            }

            if (m_items.childCount > 0) return;

            var none = new Label("Nothing extra.");
            none.AddToClassList("mp-muted");
            m_items.Add(none);
        }

        private void FillVariants(CharacterCatalog.Character character)
        {
            m_variants.Clear();
            if (character?.Variants == null) return;

            for (var i = 0; i < character.Variants.Count; i++)
            {
                var variant = character.Variants[i];
                if (variant == null || variant.Prefab == null) continue;

                var index = i;
                var button = new Button(() => SelectVariant(index));
                button.AddToClassList("mp-character-variant");

                // The body itself, drawn by the stage; without a stage the button is just its name.
                var thumbnail = m_stage != null ? m_stage.Thumbnail(character, i, m_selections.Catalog.BodyAnimator) : null;
                if (thumbnail != null)
                {
                    var picture = new Image { image = thumbnail, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                    picture.AddToClassList("mp-character-variant-picture");
                    button.Add(picture);
                }

                var name = new Label(string.IsNullOrWhiteSpace(variant.DisplayName) ? $"Look {i + 1}" : variant.DisplayName)
                {
                    pickingMode = PickingMode.Ignore
                };
                name.AddToClassList("mp-character-variant-name");
                button.Add(name);

                button.EnableInClassList("mp-character-variant--selected", i == m_draftVariant);
                button.SetEnabled(!m_locked || i == m_draftVariant);
                m_variants.Add(button);
            }
        }

        // ---- Turning the preview ---------------------------------------------------------------------

        private void BeginDrag(PointerDownEvent evt)
        {
            m_dragging = true;
            m_lastPointerX = evt.position.x;
            m_preview.CapturePointer(evt.pointerId);
        }

        private void Drag(PointerMoveEvent evt)
        {
            if (!m_dragging || m_stage == null) return;

            var delta = evt.position.x - m_lastPointerX;
            m_lastPointerX = evt.position.x;
            m_stage.Turn(-delta * DragDegreesPerPixel);
        }

        private void EndDrag(PointerUpEvent evt)
        {
            m_dragging = false;
            if (m_preview.HasPointerCapture(evt.pointerId)) m_preview.ReleasePointer(evt.pointerId);
        }
    }
}
