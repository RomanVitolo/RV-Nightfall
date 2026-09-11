# UHFPS source patches for multiplayer

UHFPS is third-party code. A package update **will overwrite** these edits. **64 of its .cs files** are
now modified. Find what survived an update with:

```bash
grep -rl "MULTIPLAYER PATCH\|LocalPlayerContext" "Assets/ThunderWire Studio" --include=*.cs | wc -l
```

That must report **64**. A lower number means an update reverted some of them.

## Why these exist

UHFPS assumes one scene-placed player that exists before `Awake`. Netcode spawns the local player after
scene load, and there are up to four of them. Two consequences:

1. Anything resolving the player during startup had to become lazy.
2. `GameManager`, `Inventory` and `PlayerPresenceManager` could no longer be scene singletons.

## 1. De-singletoning (52 files)

`GameManager`, `Inventory` and `PlayerPresenceManager` lost their `Singleton<T>` base and now live on
the **player prefab**, alongside the HUD. Five more managers moved with them for reference reasons only
(see below) but kept their `Singleton<T>` base. Only `InputManager` and `GameLocalization` remain
scene-global and fully untouched.

| Old | New |
|---|---|
| `GameManager.Instance` | `LocalPlayerContext.GameManager` |
| `Inventory.Instance` | `LocalPlayerContext.Inventory` |
| `PlayerPresenceManager.Instance` | `LocalPlayerContext.Presence` |
| `PlayerManager.Instance` | `LocalPlayerContext.PlayerManager` |
| `X.HasReference` | `LocalPlayerContext.X != null` |

`LocalPlayerContext` (`Assets/Modules/Multiplayer/Bridge/`) is the **one** remaining global. It is
written only by the owning client, in `HeroPlayerNetworkSetup.OnNetworkSpawn`, and cleared on despawn.
Every accessor returns null rather than throwing, because the window between scene load and the local
player spawning is real. This global cannot be removed: ~42 of the calling files are world-side (doors,
triggers, puzzles) with no hierarchy path to a player.

### Eight managers moved onto the player prefab

`GameManager`, `Inventory` and `PlayerPresenceManager` are per-player by nature, and are coupled to each
other by `GetComponent` so they cannot be split up. `DialogueSystem`, `ObjectiveManager`,
`JumpscareManager`, `SaveGameManager` and `OptionsManager` moved **only because they hold serialized
references into the game UI**, which now lives under the player prefab. Leaving them behind stranded 34
references — `OptionsManager` alone lost all 28 of its section/option links, stored in a nested list
that cannot be rebound by name (several `SectionParent` targets are all called `Content`).

`InputManager` and `GameLocalization` reference no UI and stay scene-global.

`HeroPlayerNetworkSetup.DisablePlayerManagers` switches all eight off on non-owned copies.

### Awake ordering

Scripts on the player run `Awake` during `Instantiate`, **before** `OnNetworkSpawn` registers the local
player — so anything caching from `LocalPlayerContext` in `Awake` got null. The 11 player-resident
files under `Controllers/` therefore use `LocalPlayerContext.ResolveGameManager(this)` (and the
`ResolveInventory`/`ResolvePresence` equivalents), which walk their own hierarchy first and fall back to
the registry. That is also the correct answer on a remote copy, where the registry would return *this*
client's player rather than the one they belong to. `FSMPlayerState` is not a `Component` and resolves
through the `PlayerStateMachine` it drives.

`PlayerManager.MainCamera` became a lazily-resolving property for the same reason: it is a scene
reference a prefab cannot hold, and `CutsceneModule.OnAwake` and `ReticleController.Awake` dereference
it during `Instantiate`. It resolves through the scene's `CinemachineBrain` — not `Camera.main`, which
is null here because the brain camera is untagged.

### Structural changes inside the three managers

- **`GameManager`** — `GraphicReferences` moved from a field initializer into the constructor (a field
  initializer cannot close over instance state, which it now must). `SubscribePauseEvent` and
  `SubscribeInventoryEvent` **queue** subscriptions made before the player exists and flush them on
  `LocalPlayerContext.Ready`; world objects subscribe from `Awake`, long before spawn. `Module<T>()`
  returns `default` until the player exists.
- **`PlayerPresenceManager`** — `Player` is now the object it is attached to. Freezing, unlocking and
  cursor handling moved out of `Awake`/`Start` into `BindPlayer`, which only the owning client calls;
  otherwise every remote copy of another player would seize this client's cursor. Added `BindPlayer`.
- **`PlayerManager`** — the static `Instance` accessor is gone.

## 2. Deferred player resolution (9 files)

Each cached the player in `Awake`/`Start`, which now runs before any player exists. Each resolves on
first use and tolerates the player being absent.

| File | What was deferred |
|---|---|
| `Misc/FlameFlicker.cs` | Player transform for the distance check |
| `Misc/GlareEffect.cs` | Player camera transform |
| `Utilities/Tools/CanvasLootAtCamera.cs` | Player camera |
| `Core/Dialogue/DialogueTrigger.cs` | Player transform for ranged dialogue |
| `Core/DynamicObject/DynamicObject.cs` | `Physics.IgnoreCollision` pairing, retried from `Update` |
| `Core/Game/GameManager.cs` | Stamina subscription, retried from `Update` |
| `Core/Puzzle/.../LockpickInteract.cs` | `PlayerManager`, resolved in `InteractStart` |
| `Core/Puzzle/.../SafePuzzle.cs` | `PlayerManager` + `ExamineController` |
| `Trigger/GhostHunting/ThermometerTemp.cs` | Thermometer lookup, retried at each use |

## 3. AI held until the player spawns (2 files)

UHFPS AI is entirely player-driven, and NPCs are **scene-placed** — they `Awake` before NGO spawns
anyone. Every state body and transition predicate reads the player, so before this fix two zombies in
`GameplayScene` threw a `NullReferenceException` *per NPC, per frame*, for the whole
scene-load-to-connect window. That is the null-reference spam this slice was expected to hit.

| File | Change |
|---|---|
| `Core/AI/NPCStateMachine.cs` | `Player`/`PlayerHealth`/`PlayerManager` are null-tolerant and re-resolve while null. `Update` returns early until a player exists. |
| `Core/AI/FSM/FSMAIState.cs` | The three cached player fields became lazy properties; sight and distance helpers gate on the new `HasPlayer`. |

The `FSMAIState` change is the load-bearing one. Those were **fields assigned in the constructor**,
which runs from `NPCStateMachine.Awake()` — so they captured null at scene load and were never
refreshed. Every NPC would have stayed permanently blind to the player *even after it spawned*. As
properties they resolve on first use, and `NPCStateMachine` caches the result once, so the steady-state
cost is unchanged. The six AI states only ever read them, so field-to-property is source-compatible.

The early return in `Update` is deliberate over guarding each predicate: it fixes every state body and
every transition lambda at once, and freezing player-driven AI when there is no player is the correct
behaviour rather than a workaround.

**This tracks the _local_ player only.** AI chasing the nearest of several players is unfinished — see
the world-state gap below.

## 4. Item actions for the third-person body (4 files, 2 new)

UHFPS items fire, swing and reload inside their own update logic and expose no event, so a remote
player's body could not react. One seam was added and raised at the four points where UHFPS commits to
an action — the same line that triggers its first-person animation, so a dry click on an empty magazine
or a swing still on cooldown raises nothing.

| File | Change |
|---|---|
| `Controllers/Items/PlayerItemBehaviour.cs` | Added `ItemAction` enum, `ActionPerformed` event and protected `RaiseActionPerformed`. |
| `Controllers/Items/GunItem.cs` | Raises `Shoot` in `FireOneBullet` and `Reload` in `ReloadGun`. |
| `Controllers/Items/AxeItem.cs` | Raises `Attack` on the swing. *(newly patched)* |
| `Controllers/Items/KnifeItem.cs` | Raises `Attack` on the slash. *(newly patched)* |

The bridge's `PlayerActionSync` subscribes on the owner only and forwards each action through an
owner-only RPC (`InvokePermission = Owner`), so no client can make another player appear to fire.
Remote copies never raise it themselves: every item returns early unless equipped, and only
`PlayerItemsManager` equips — a `PlayerComponent` that is disabled on remote copies.

If an update reverts these, weapons still work for the player using them; other players simply stop
seeing the body react.

## 5. World objects that cached the player at load (16 files, none new)

Section 2 deferred nine of these; sixteen more still read the local player in `Awake`/`Start`, where it does
not exist yet. The first time anyone ran the game, interacting surfaced three of them as
`NullReferenceException`s: storage (`ItemsStorage` → `InventoryContainer.inventory`), lockpicking
(`LockpickInteract.PlayerPresence`) and doors (`DynamicObject.gameManager`). The rest failed the same way
more quietly — `PuzzleBase` and `PuzzleBaseBlend` even dereferenced the null in `Awake`, so every puzzle threw
at level load and skipped the rest of its initialisation.

Found systematically (any assignment from `LocalPlayerContext` inside `Awake`/`Start`/`OnEnable`), and fixed
with one pattern: the cached field becomes a property of the same name that resolves on use. Existing reads
compile unchanged; the only writes were the `Awake` lines removed.

| File | Resolved on use |
|---|---|
| `Core/DynamicObject/DynamicObject.cs` | `inventory`, `gameManager` |
| `Core/DynamicObject/DynamicUnlock/DynamicBrokenFix.cs` | `gameManager` |
| `Core/Inventory/Behaviour/InventoryContainer.cs` | `inventory` (base of `ItemsStorage`, `ItemsContainer`) |
| `Core/Puzzle/PuzzleBase.cs` | `playerPresence`, `playerManager`, `gameManager` |
| `Core/Puzzle/PuzzleBaseBlend.cs` | the above, `playerItems`, the Cinemachine brain; the blend to restore is captured when the puzzle starts |
| `Core/Puzzle/Puzzles/Maze/MazePuzzle.cs` | `inventory` |
| `Core/Puzzle/Puzzles/Lockpick/LockpickInteract.cs` | `PlayerPresence`, `GameManager`; lockpick HUD wired on first use |
| `Core/Puzzle/Puzzles/Safe/SafeBig/SafePuzzle.cs` | `GameManager`, `PlayerPresence`; safe HUD wired on first use |
| `Interact/CCTV/CCTV_CameraSystem.cs` | `playerPresence`, `playerItems`, `gameManager`; CCTV overlay wired on first use |
| `Interact/Hiding/HideInteract.cs` | presence, managers, state machine, controllers, Cinemachine brain |
| `Interact/Other/CustomInteractEvent.cs` | `playerPresence` |
| `Interact/PowerGenerator/GeneratorFuelTank.cs` | `gameManager`, `inventory` |
| `Trigger/FeatureDisableTrigger.cs` | `gameManager`, `player` |
| `Trigger/HintTrigger.cs` | `gameManager` |
| `Trigger/LookAtTrigger.cs` | `playerPresence` |
| `Core/Inventory/Event/InventoryUseEvents.cs` | registers its use events on `LocalPlayerContext.Ready` instead of in `Start` |

The HUD panels (lockpick, safe, CCTV) are a variant of the same problem: their widgets live in the HUD on the
player prefab, so looking them up in `Start` found nothing. They are wired the first time they are needed.

## World state replication (no UHFPS changes)

Doors, pickups, props and puzzles replicate through `Assets/Modules/Multiplayer/Bridge/World/`, set up by
**Tools > Multiplayer > Set Up World Sync**. It hooks UHFPS only through interfaces UHFPS already calls
(`IInteractStart`) and data it already exposes (`OnSave`/`OnLoad`, `DynamicObject.target`), so it adds nothing
to this list and survives a UHFPS update as long as those stay.

## 6. AI simulated by the host for every player (6 files, 4 new)

UHFPS AI ran on every client against that client's own player, so each player was chased by a private copy of
the same zombie. Now only the host simulates NPCs: the bridge's `NetworkedNpc` switches the AI, navigation and
root motion off on clients, which show the host's zombie through NetworkTransform and NetworkAnimator.

The host needs a view of *every* player, and on the host a remote player's UHFPS components are switched off —
its `PlayerHealth` never changes and its state machine never enters Hiding. So the AI no longer reads those. It
asks `IAITarget` (implemented by the bridge's `PlayerAiTarget` on the player prefab), which answers from
replicated state: server-authoritative health, and hiding/invisibility as each player's own client reports it.

| File | Change |
|---|---|
| `Core/AI/NPCStateMachine.cs` | `SetTarget` (the host picks the pursued player) and `Target` (its `IAITarget`); target death via `Target.IsDead`. |
| `Core/AI/FSM/FSMAIState.cs` | `aiTarget` accessor; the sight check reads death and invisibility from it. |
| `Core/AI/AIStates/Zombie/ZombieChaseState.cs` | Hiding and death via `aiTarget`; attacks call `aiTarget.ApplyDamage`. *(newly patched)* |
| `Core/AI/AIStates/Zombie/ZombiePatrolState.cs` | Hiding via `aiTarget`. *(newly patched)* |
| `Core/AI/AIStates/Zombie/ZombiePlayerHideState.cs` | Hiding place, concealment, health and damage via `aiTarget`; pulling a player out calls `aiTarget.ForceUnhide`, which runs on that player's own client. *(newly patched)* |
| `Core/AI/NPCHealth.cs` | `DamageRelay`: a client's hit goes to the host instead of its own copy; an `ApplyDamageMax` override so one-hit kills are relayed too. *(newly patched)* |

Attack animations also play on clients, and their events reach the AI states there; `IAITarget.ApplyDamage`
does nothing off the server, so only the host's attack lands.

## 7. One examiner per object (2 files, none new)

| File | Change |
|---|---|
| `Controllers/Camera/ExamineController.cs` | `StartExamine` asks the object's `IExamineGate` first; if the host has not granted it yet, the examine starts from the grant's callback. Inventory examines skip it. |
| `Controllers/Camera/InteractController.cs` | `Interact` refuses to pick up an object another player is examining. |

The gate is the bridge's `SyncedExamineLock`: first come, first served, decided by the host, and released when
the examine ends, when the item is taken mid-examine, or when the examiner disconnects.

## 8. Player health decided by the server (1 file, none new)

The host already owned player health, but UHFPS still changed the local copy wherever damage or healing was
noticed — medkits, health/damage item attributes, damage zones, falls — and the host never heard about it.

| File | Change |
|---|---|
| `Controllers/Player/PlayerHealth.cs` | `Authority` (an `IPlayerHealthAuthority`): when set, `ApplyDamage`, `ApplyDamageMax`, `ApplyHeal` and `ApplyHealMax` send a request instead of changing health. `SetAuthoritativeHealth` applies the server's value and plays the blood and hurt-sound feedback the local call used to. `Awake` no longer dereferences the health volume. |

The authority is the bridge's `PlayerHealthSync`. Only the owner's requests go out, so an event seen by several
copies counts once and there is no friendly fire. Healing does not revive a dead player. Fall damage was forced
off while it could not reach the server; it is back to the prefab's setting.

The `Awake` fix repairs an older bug. The health volume is a scene reference the bridge assigns at spawn, after
`Awake`, so reading it there threw on every spawn and skipped `InitHealth`. `MaxEntityHealth` stayed 0, so every
heal clamped to zero and the inventory greyed out medkits. The eye blink is now looked up on first use.

## 9. No single-player scene changes in a session (2 files, none new)

UHFPS restarts, loads saves, changes level and quits to the main menu with plain `SceneManager` loads. In a
session that takes one player out of the room, or ends the game for everyone when it is the host, and a locked
room cannot be rejoined.

| File | Change |
|---|---|
| `Core/Game/GameManager.cs` | `SessionExit` (an `ISessionExit`): when set, `LoadNextLevel`, `LoadNextWorld`, `LoadWorldLastState` and `LoadGameState` (and so `RestartGame`) are refused with a hint, and `MainMenu` leaves the session instead. |
| `Interact/Other/LevelInteract.cs` | `SwitchLevel` checks `GameManager.BlockSceneChange` before saving, so a refused level exit writes no save either. |

The bridge's `SessionMenus` sets `SessionExit` on the local player and adapts the menus, finding each button by
the UHFPS method it calls: Save Game, Load Game and Restart are hidden; Quit and the death screen's Main Menu become
Leave Game (End Game on the host) and act on a second press. `SavesUILoader` still loads scenes itself, but its only
way in is the hidden Load Game button.

## 10. Dropped items shared (1 file, new)

| File | Change |
|---|---|
| `Core/Inventory/Behaviour/Inventory.ContextHandler.cs` | `DropSync` (an `IItemDropSync`): `DropItem` reports the object it just created, with its ObjectReference GUID and quantity. |

UHFPS creates a dropped item on the dropping client only, and World Sync only knew objects that were in the level
when it loaded. The bridge's `DroppedItems` asks the host for keys; the host broadcasts the drop, every other client
creates the same object at the same pose, and each copy gets the components Set Up World Sync gives a scene pickup
(`SyncedPickup`, `SyncedRigidbody`, `SyncedExamineLock`). The copies are held still until the dropper's physics
stream moves them, so the item falls once, as the dropper saw it, and lands in the same place for everyone. Taking
it follows the usual rule: the first taker gets it, and everyone else's copy is destroyed.

## Known gaps

- **Save/load is untested and likely broken.** `SaveGameManager` now lives on the player prefab, so it
  is instantiated once per player and disabled on all but the owner's copy. `Inventory` is
  `ISaveableCustom` and moved too. It went onto the prefab only because it holds a reference to the
  `SavingIcon` in the HUD — not because per-player saving is correct. Out of scope for this slice, and
  the first thing to revisit if saving matters.
- **World state is replicated, with limits.** Doors, pickups, dropped items, props, puzzles, switches, lights and NPCs sync
  (see above). Per-player triggers (cutscenes, dialogue, objectives, jumpscares) stay local by design. Events a
  saveable fires are not replayed on other clients; their effects arrive only through objects that replicate
  their own state. NPC ragdolls fall independently on each client once dead.
- **Death is permanent until the level restarts** (the bridge's `SessionDeathScreen`, no UHFPS changes). What a
  dead player carried is out of play with them, so a key they held can leave the level unfinishable.
- Backups of the pre-refactor scripts, scene and prefab are in this session's scratchpad.
