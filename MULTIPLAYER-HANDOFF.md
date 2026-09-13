# Handoff: The Quiet Village — UHFPS multiplayer conversion

I'm continuing work on my Unity game with you. Read this whole brief before doing anything, then read `Assets/Modules/Multiplayer/UHFPS-PATCHES.md`, which is the authoritative record of every change made to UHFPS. Verify anything below against the code before relying on it: this is a summary, and the code wins where they disagree.

## The project

- **Repo:** `C:\UnityProjects\VyR\RV-NightFall`, Unity project in `The Quiet Village/`. GitHub: `RomanVitolo/RV-Nightfall` (public). Branch `main`, everything committed and pushed (latest `2e1f1c74`).
- **Engine:** Unity 6000.4.1f1. Netcode for GameObjects 2.13.2, Unity Multiplayer Services (sessions/Relay), Cinemachine 3.1.7, TextMeshPro, UI Toolkit for the lobby.
- **Game:** a first-person co-op horror game, 2–4 players, built on **UHFPS** (ThunderWire Studio's single-player horror template) converted to multiplayer. Level: `GameplayScene`; lobby: `LobbyScene`.
- **Git LFS:** `Assets/Sci_Fi_Super_Pack` (~25 GB of textures and models, including files over 100 MB) is stored in LFS. Cloning needs Git LFS installed. A local-only branch `backup/before-lfs-migrate` still exists and holds ~24 GB of old data; it can be deleted.

## Architecture (important rules)

- **UHFPS is third-party code**, in `Assets/ThunderWire Studio/UHFPS` (Assembly-CSharp). It's patched in **65 files**, each marked `MULTIPLAYER PATCH` or using `LocalPlayerContext`. Check with:
  `grep -rl "MULTIPLAYER PATCH\|LocalPlayerContext" "Assets/ThunderWire Studio" --include=*.cs | wc -l` → must be **65**.
  Prefer adding code in the bridge over patching UHFPS. Every new UHFPS patch must be documented in `UHFPS-PATCHES.md`, with the count updated.
- **Bridge** (`Assets/Modules/Multiplayer/Bridge/`, Assembly-CSharp) glues UHFPS to Netcode. It may reference UHFPS and the module.
- **Module** (`Assets/Modules/Multiplayer/Scripts/Runtime/`, asmdef `Modules.Multiplayer.Runtime`) holds sessions, lobby, flow and spawning. **It cannot reference UHFPS or the bridge** (asmdef → Assembly-CSharp is impossible). When the module needs something from the bridge, define an interface plus a static holder in the module and have the bridge register (examples: `ISaveCatalog`/`SaveCatalog`, `ISpawnGate`/`SpawnGate`).
- **Authority:** the owner runs its own player (UHFPS first-person stack). Player health is server-authoritative (`PlayerHealthSync`). The host simulates NPCs (`NetworkedNpc`). World objects replicate through one in-scene `WorldSync` NetworkBehaviour; each object is a `WorldSyncEntity` with a stable id (a GlobalObjectId written by **Tools > Multiplayer > Set Up World Sync**; re-run it after adding objects to the level).
- **Players:** `GameManager`, `Inventory`, `PlayerPresenceManager`, `SaveGameManager` and other managers live on the player prefab (`Prefabs/NetworkedWorkerPlayer.prefab`, which nests UHFPS's `HEROPLAYER`). `LocalPlayerContext` is the one global for "this client's player". `HeroPlayerNetworkSetup` splits owner from remote copies: remote copies have UHFPS components, audio, cameras and HUD switched off, and a third-person avatar (Sci-Fi Worker, humanoid rig) shown instead.
- **Runtime-added components:** most bridge features are added in code by `HeroPlayerNetworkSetup`, not placed on the prefab, so the prefab rarely needs editing.
- **Rooms lock when the game starts:** there's no joining mid-game and no rejoining.

## Features done (all compile; none has been playtested with two or more players)

1. **Health through the host.** Healing, damage zones, fall damage and item attributes all request changes from the host (`IPlayerHealthAuthority`, patch in `PlayerHealth`). Also fixed a `PlayerHealth.Awake` crash that had broken medkits.
2. **Menus safe in multiplayer** (`SessionMenus`). Load and Restart are hidden. Quit/Main Menu become Leave Game (End Game for the host), with a confirm on the second press. The level exit (`LevelInteract`) and UHFPS scene loads are blocked with a hint (`ISessionExit`, patch in `GameManager`).
3. **Death is permanent until restart** (`SessionDeathScreen`, `SpectatorCamera`). Dead players watch a living teammate, with Next Player to switch. When everyone is dead, the host gets Restart Level (`SessionFlow.RestartLevel`, a networked reload) and clients see "Waiting for the host". Player display names are synced (`HeroPlayerNetworkSetup.DisplayName`).
4. **Dropped items shared** (`DroppedItems`, patch in `Inventory.ContextHandler`). The host allocates World Sync keys, everyone creates a copy, the dropper's physics streams the fall, and the first taker wins.
5. **Carried items survive death or disconnect** (`CarriedItems`). A dying player drops everything; the host drops a disconnected player's items where they last stood.
6. **Teammate sounds** (`AvatarSounds`): footsteps (the player's own FootstepsSystem settings and the surface underfoot), gunshots, melee swings, draw/holster sounds, flashlight clicks, and reload and other item-animation sounds (patch in `AnimationSoundEvent`, forwarded by name through `PlayerActionSync`).
7. **Held items visible:** `PlayerActionSync` publishes the equipped item index and whether its light is on. `AvatarHeldLight` shines a teammate's flashlight, lantern or candle; `AvatarHeldItem` puts the item's pickup model in the avatar's right hand. **The grip offsets are untuned guesses**: `GripPosition`/`GripRotation` at the top of `AvatarHeldItem.cs`.
8. **Name tags** (`AvatarNameTag`): world-space TMP text above teammates, hidden by walls, fading out by 25 m, and hidden when that player is dead.
9. **Crouch synced** (`PlayerLocomotionSync.IsCrouching`). Remote copies lower their camera holder, so zombie sight and the spectator camera are correct and footsteps use the real crouch state. The avatar body still stands, because the pack has no crouch clips.
10. **"Host left" notice:** `SessionService.SessionEndedReason` plus a dismissible banner in the lobby (`LobbyScreen`, `LobbyScreen.uxml`, `Multiplayer.uss`) when a room ends without the player choosing it.
11. **Co-op save/resume** (`Bridge/Saves/*`, `WorldSync.Saves.cs`, no UHFPS changes).
    - **Saving:** host-only Save Game in the pause menu. The host gathers each client's inventory, objectives, health and position (`PlayerSaveState`), keyed by **Unity account id** (`SessionService.LocalPlayerId`). It saves the world keyed by World Sync ids (`WorldSyncEntity.SaveTarget`), plus dropped items, into UHFPS's save folder format.
    - **Resuming:** in the lobby's Create Room dialog, pick New game or a save. Each client is sent the world and its own slice before its player spawns (`ISpawnGate`). Newcomers start empty-handed. Restart Level after a wipe returns to the latest save.
    - **Why not UHFPS's own save path:** it can't work, because `SaveGameManager`'s world-saveable list is scene references that a prefab can't hold.
    - **Not saved:** NPCs, so zombies reset alive when a save is resumed.

## Known gaps and possible next work

- **Playtest pass with 2–3 players.** Nothing above has been verified in play. Highest priority.
- Tune the held-item grip offsets.
- Save NPC state (zombie alive/dead and position).
- Level transitions over the network (the `GameplayScene` level exit currently refuses).
- Rejoining after a disconnect (needs a full world snapshot for returning players).
- Voice chat (Vivox), optional.
- Crouch animation for the avatar (needs clips).
- Custom item data (for example a gun's loaded ammo) is lost on drop, as in UHFPS.
- New UI strings are English-only constants.

## How to work in this project

- **Compile check** (Unity closed; the timeout message at the end is normal):
  ```
  "/c/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor/Unity.exe" -batchmode -nographics -quit -projectPath "C:/UnityProjects/VyR/RV-NightFall/The Quiet Village" -logFile <scratchpad>/compile.log
  ```
  Then `grep -c "error CS" compile.log` → must be 0.
- **Editing files from shell:** long bash heredocs containing C# have failed to parse here. Use the file write/edit tools, or Python (`py`) with anchored string replacements. Python writes LF line endings. Don't leave whole-file line-ending churn on untouched files; revert with `git checkout -- <file>`.
- **Code style:** match the existing bridge code. XML doc comments explain *why* (authority, ordering, what breaks otherwise), `m_` fields, clear early returns. Components are added at runtime where possible.
- **Before big features, confirm design choices with me.** So far I chose:
  - host-only saving;
  - saves keyed by player account;
  - resume chosen in the lobby;
  - permadeath with spectating;
  - host restarts when everyone is dead.
- **Don't commit or push unless I ask.** When you do, end commit messages with the attribution line I give you.
- **Always tell me what's untested,** and give concrete two-player test steps.
