using System;
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

        private CharacterSelections m_selections;
        private CharacterPreviewStage m_stage;
        private Image m_previewImage;

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

            var cancel = root.Q<Button>("character-cancel");
            var confirm = root.Q<Button>("character-confirm");

            IsAvailable = m_dialog != null && m_list != null && m_preview != null && m_nameField != null && m_role != null
                          && m_title != null && m_description != null && m_perks != null && m_items != null
                          && m_variants != null && cancel != null && confirm != null;

            if (!IsAvailable) return;

            m_nameField.maxLength = SessionService.MaxDisplayNameLength;
            cancel.clicked += Close;
            confirm.clicked += Confirm;

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
        public void Open(string playerName, bool nameEditable)
        {
            if (!IsAvailable || m_selections == null || m_selections.Catalog == null) return;

            var current = m_selections.LocalChoice;
            m_draftId = current.CharacterId;
            m_draftVariant = current.Variant;

            m_nameField.value = playerName ?? string.Empty;
            m_nameField.SetEnabled(nameEditable);
            if (m_hint != null)
                m_hint.text = nameEditable ? string.Empty : "Your name is fixed while you are in a room.";

            BuildList();
            ShowDraft();

            if (m_stage != null) m_stage.SetShowing(true);
            m_dialog.RemoveFromClassList("mp-hidden");
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
            m_selections?.SetLocalChoice(new CharacterChoice(m_draftId, m_draftVariant));

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

                m_list.Add(card);
            }
        }

        private void SelectCharacter(CharacterCatalog.Character character)
        {
            if (character == null || character.Id == m_draftId) return;

            m_draftId = character.Id;
            m_draftVariant = 0;
            ShowDraft();
        }

        private void SelectVariant(int variant)
        {
            if (variant == m_draftVariant) return;

            m_draftVariant = variant;
            ShowDraft();
        }

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

            FillLines(m_perks, character != null ? CharacterCatalog.Describe(character.Perks) : null, "No special perks.");
            FillItems(character);
            FillVariants(character);

            if (m_stage != null) m_stage.Show(character, m_draftVariant, catalog.BodyAnimator);
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
            var lines = new System.Collections.Generic.List<string>();

            if (character?.StartingItems != null)
            {
                foreach (var item in character.StartingItems)
                {
                    if (item == null || string.IsNullOrEmpty(item.ItemGuid) || item.Quantity <= 0) continue;
                    lines.Add(item.Quantity > 1 ? $"{item.Title} ×{item.Quantity}" : item.Title);
                }
            }

            FillLines(m_items, lines, "Nothing extra.");
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
                var button = new Button(() => SelectVariant(index))
                {
                    text = string.IsNullOrWhiteSpace(variant.DisplayName) ? $"Look {i + 1}" : variant.DisplayName
                };
                button.AddToClassList("mp-button");
                button.AddToClassList("mp-button--small");
                button.AddToClassList("mp-character-variant");
                button.EnableInClassList("mp-character-variant--selected", i == m_draftVariant);
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
