# UHFPS source patches for multiplayer

UHFPS is third-party code. A package update **will overwrite** these edits. **56 of its .cs files** are
now modified. Find what survived an update with:

```bash
grep -rl "MULTIPLAYER PATCH\|LocalPlayerContext" "Assets/ThunderWire Studio" --include=*.cs | wc -l
```

That must report **56**. A lower number means an update reverted some of them.

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

## Known gaps

- **Save/load is untested and likely broken.** `SaveGameManager` now lives on the player prefab, so it
  is instantiated once per player and disabled on all but the owner's copy. `Inventory` is
  `ISaveableCustom` and moved too. It went onto the prefab only because it holds a reference to the
  `SavingIcon` in the HUD — not because per-player saving is correct. Out of scope for this slice, and
  the first thing to revisit if saving matters.
- Backups of the pre-refactor scripts, scene and prefab are in this session's scratchpad.
