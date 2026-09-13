using System.Collections;
using QuietVillage.Multiplayer.Characters;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Applies the parts of a role that live on its owner's machine: how fast they run, how much they can carry, and
    /// what they start with.
    /// </summary>
    /// <remarks>
    /// Authority: owner. Movement and the inventory are owner-run in this conversion, so these perks are applied where
    /// those systems run. The perks decided on the host, barricade repair and healing, are read there from
    /// <see cref="PlayerCharacter"/> instead.
    ///
    /// Inventory perks only apply on a fresh start. A resumed player's inventory comes back from the save with its
    /// unlocked slots and items already in it; granting them again would hand out another starter kit on every resume.
    ///
    /// Owner copy only: <see cref="HeroPlayerNetworkSetup"/> adds it to the player this client controls.
    /// </remarks>
    public class LocalRolePerks : MonoBehaviour
    {
        /// <param name="freshStart">True unless this player is being restored from a save.</param>
        public void Bind(PlayerCharacter character, bool freshStart)
        {
            if (character == null || character.Character == null) return;

            ApplyRunSpeed(character.Perks);

            if (freshStart) StartCoroutine(GiveStartingKit(character.Character));
        }

        private void ApplyRunSpeed(CharacterCatalog.Perks perks)
        {
            var stateMachine = GetComponent<PlayerStateMachine>();
            if (stateMachine == null || stateMachine.PlayerBasicSettings == null) return;

            // Per instance: BasicSettings is a serialized class, so each spawned player has its own copy to scale.
            stateMachine.PlayerBasicSettings.RunSpeed *= perks.RunSpeed;
        }

        private IEnumerator GiveStartingKit(CharacterCatalog.Character character)
        {
            // After every Start: the inventory adds UHFPS's own starting items there, and its slots exist from then on.
            yield return null;

            var inventory = GetComponentInChildren<Inventory>(true);
            if (inventory == null) yield break;

            // Slots first, so the kit has room.
            if (character.Perks.ExtraInventorySlots > 0) inventory.ExpandInventory(character.Perks.ExtraInventorySlots, false);

            if (character.StartingItems == null) yield break;

            foreach (var item in character.StartingItems)
            {
                if (item == null || string.IsNullOrEmpty(item.ItemGuid) || item.Quantity <= 0) continue;

                var quantity = (ushort)Mathf.Clamp(item.Quantity, 1, ushort.MaxValue);
                if (!inventory.AddItem(item.ItemGuid, quantity, new ItemCustomData()))
                {
                    Debug.LogWarning($"{nameof(LocalRolePerks)}: no room for the {character.RoleOrName}'s " +
                                     $"'{item.Title}', so they start without it.", this);
                }
            }
        }
    }
}
