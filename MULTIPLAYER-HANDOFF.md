# Handoff: The Quiet Village — UHFPS multiplayer conversion

I'm continuing work on my Unity game with you. Read this whole brief before doing anything, then read `Assets/Modules/Multiplayer/UHFPS-PATCHES.md`, which is the authoritative record of every change made to UHFPS. Verify anything below against the code before relying on it: this is a summary, and the code wins where they disagree.

## The project

- **Repo:** `C:\UnityProjects\VyR\RV-NightFall`, Unity project in `The Quiet Village/`. GitHub: `RomanVitolo/RV-Nightfall` (public). Branch `main`, everything committed and pushed (latest `2e1f1c74`).
- **Engine:** Unity 6000.4.1f1. Netcode for GameObjects 2.13.2, Unity Multiplayer Services (sessions/Relay), Cinemachine 3.1.7, TextMeshPro, UI Toolkit for the lobby.
- **Game:** a first-person co-op horror game, 2–4 players, built on **UHFPS** (ThunderWire Studio's single-player horror template) converted to multiplayer. Levels: listed in `Assets/Game/Levels/LevelCatalog.asset` (currently UHFPS's demo `GameplayScene` and a greybox `ForestVillage`); lobby: `LobbyScene`.
- **Git LFS:** `Assets/Sci_Fi_Super_Pack` (~25 GB of textures and models, including files over 100 MB) is stored in LFS. Cloning needs Git LFS installed. A local-only branch `backup/before-lfs-migrate` still exists and holds ~24 GB of old data; it can be deleted.

## Architecture (important rules)

- **UHFPS is third-party code**, in `Assets/ThunderWire Studio/UHFPS` (Assembly-CSharp). It's patched in **67 files**, each marked `MULTIPLAYER PATCH` or using `LocalPlayerContext`. Check with:
  `grep -rl "MULTIPLAYER PATCH\|LocalPlayerContext" "Assets/ThunderWire Studio" --include=*.cs | wc -l` → must be **67**.
  Prefer adding code in the bridge over patching UHFPS. Every new UHFPS patch must be documented in `UHFPS-PATCHES.md`, with the count updated.
- **Bridge** (`Assets/Modules/Multiplayer/Bridge/`, Assembly-CSharp) glues UHFPS to Netcode. It may reference UHFPS and the module.
- **Module** (`Assets/Modules/Multiplayer/Scripts/Runtime/`, asmdef `Modules.Multiplayer.Runtime`) holds sessions, lobby, flow and spawning. **It cannot reference UHFPS or the bridge** (asmdef → Assembly-CSharp is impossible). When the module needs something from the bridge, define an interface plus a static holder in the module and have the bridge register (examples: `ISaveCatalog`/`SaveCatalog`, `ISpawnGate`/`SpawnGate`).
- **Authority:** the owner runs its own player (UHFPS first-person stack). Player health is server-authoritative (`PlayerHealthSync`). The host simulates NPCs (`NetworkedNpc`). World objects replicate through one in-scene `WorldSync` NetworkBehaviour; each object is a `WorldSyncEntity` with a stable id (a GlobalObjectId written by **Tools > Multiplayer > Set Up World Sync**, which works on the open level; re-run it after adding objects to the level).
- **Levels:** one scene per settlement. `LevelCatalog` (module, ScriptableObject) is the single list of playable levels; a scene not in it is never treated as a level. The host picks the level in the lobby's Create Room dialog (or it follows the chosen save), it's published as a session property (`SessionService.RoomLevel`), and `SessionFlow` loads it through Netcode. Restart reloads the active level; `NetworkPlayerSpawner` spawns into any catalog level. Levels are identified by **scene name**, so names must be unique.
  - **Tools > Multiplayer > Levels > Create New Level...** makes a scene with GameplayScene's `GAMEMANAGER` systems copied in, a 100 m greybox floor and a moonlight, then sets it up.
  - **Tools > Multiplayer > Levels > Set Up Open Scene As Level** (idempotent): session guard, removes stray NetworkManager/SessionService, SceneGameReferences, spawn points, World Sync, Build Settings, catalog entry.
  - Rename levels or add descriptions via **Select Level Catalog**.
- **Players:** `GameManager`, `Inventory`, `PlayerPresenceManager`, `SaveGameManager` and other managers live on the player prefab (`Prefabs/NetworkedWorkerPlayer.prefab`, which nests UHFPS's `HEROPLAYER`). `LocalPlayerContext` is the one global for "this client's player". `HeroPlayerNetworkSetup` splits owner from remote copies: remote copies have UHFPS components, audio, cameras and HUD switched off, and a third-person avatar (Sci-Fi Worker, humanoid rig) shown instead.
- **Runtime-added components:** most bridge features are added in code by `HeroPlayerNetworkSetup`, not placed on the prefab, so the prefab rarely needs editing.
- **Rooms lock when the game starts:** there's no joining mid-game and no rejoining.

## Features done (all compile; none has been playtested with two or more players)

1. **Health through the host.** Healing, damage zones, fall damage and item attributes all request changes from the host (`IPlayerHealthAuthority`, patch in `PlayerHealth`). Also fixed a `PlayerHealth.Awake` crash that had broken medkits.
2. **Menus safe in multiplayer** (`SessionMenus`). Load and Restart are hidden. Quit/Main Menu become Leave Game (End Game for the host), with a confirm on the second press. The level exit (`LevelInteract`) and UHFPS scene loads are blocked with a hint (`ISessionExit`, patch in `GameManager`).
3. **Death is permanent until restart** (`SessionDeathScreen`, `SpectatorCamera`). Dead players watch a living teammate, with Next Player to switch. When everyone is dead, the host gets Restart Level (`SessionFlow.RestartLevel`, a networked reload) and clients see "Waiting for the host". Player display names are synced (`HeroPlayerNetworkSetup.DisplayName`).
4. **Dropped items shared** (`DroppedItems`, patch in `Inventory.ContextHandler`). The host allocates World Sync keys, everyone creates a copy, the dropper's physics streams the fall, and the first taker wins.
5. **Carried items survive death or disconnect** (`CarriedItems`). A dying player drops everything droppable; the host drops a disconnected player's items where they last stood. Requires item drop data: **Tools > Multiplayer > Set Up Droppable Items** (already run) gave 22 non-player items a drop prefab (`Assets/Game/Prefabs/Drops`) and Object References entry. Player items (flashlight, weapons) are not droppable, so they are lost on death.
6. **Teammate sounds** (`AvatarSounds`): footsteps (the player's own FootstepsSystem settings and the surface underfoot), gunshots, melee swings, draw/holster sounds, flashlight clicks, and reload and other item-animation sounds (patch in `AnimationSoundEvent`, forwarded by name through `PlayerActionSync`).
7. **Held items visible:** `PlayerActionSync` publishes the equipped item index and whether its light is on. `AvatarHeldLight` shines a teammate's flashlight, lantern or candle; `AvatarHeldItem` puts a mesh-only copy of the item's pickup prefab in the avatar's right hand. Models and per-item grips live in `Assets/Modules/Multiplayer/Resources/AvatarHeldItemModels.asset`, built by **Tools > Multiplayer > Set Up Held Item Models** (UHFPS's demo database leaves `Item.ItemObject` empty, so it can't be the source). **The grip offsets are untuned guesses**: edit them per item in that asset.
8. **Name tags** (`AvatarNameTag`): world-space TMP text above teammates, hidden by walls, fading out by 25 m, and hidden when that player is dead.
9. **Crouch synced** (`PlayerLocomotionSync.IsCrouching`). Remote copies lower their camera holder, so zombie sight and the spectator camera are correct and footsteps use the real crouch state. The avatar body still stands, because the pack has no crouch clips.
10. **"Host left" notice:** `SessionService.SessionEndedReason` plus a dismissible banner in the lobby (`LobbyScreen`, `LobbyScreen.uxml`, `Multiplayer.uss`) when a room ends without the player choosing it.
11. **Co-op save/resume** (`Bridge/Saves/*`, `WorldSync.Saves.cs`, no UHFPS changes).
    - **Saving:** host-only Save Game in the pause menu. The host gathers each client's inventory, objectives, health and position (`PlayerSaveState`), keyed by **Unity account id** (`SessionService.LocalPlayerId`). It saves the world keyed by World Sync ids (`WorldSyncEntity.SaveTarget`), plus dropped items, into UHFPS's save folder format.
    - **Resuming:** in the lobby's Create Room dialog, pick New game or a save. Each client is sent the world and its own slice before its player spawns (`ISpawnGate`). Newcomers start empty-handed. Restart Level after a wipe returns to the latest save.
    - **Why not UHFPS's own save path:** it can't work, because `SaveGameManager`'s world-saveable list is scene references that a prefab can't hold.
    - **Health** resumes server-side: `PlayerHealthSync` starts each player at their saved health (minimum 1) via `WorldSync.TryGetSavedHealth`.
    - **Other game systems** join the save through `IWorldSaveParticipant` (register with `WorldSync.RegisterSaveParticipant` on the host; read back with `WorldSync.TryGetResumedState`). Stored under `participants` in the data file.
    - **Not saved:** NPCs, so zombies reset alive when a save is resumed.
12. **Survival loop prototype** (`Assets/Game/Scripts/Survival/`, Assembly-CSharp, namespace `QuietVillage.Survival`, no UHFPS changes).
    - `SurvivalDirector` (one in-scene NetworkObject per level, server-authoritative): day/night clock replicated as phase + server end time; barricade health as one `NetworkList<int>` indexed by `Barricade.Order`; spawns `NightCreature`s at night (base + per living player) and despawns them at dawn. Dawn = the night was survived. Host can press **F8** to skip the current phase (testing).
    - `Barricade`: UHFPS timed interaction (hold Use). Client asks the host; host adds 50 health (max 100) and tells the builder's client to spend 1 **Scrap** (UHFPS's existing item, standing in for wood). Standing = solid collider + carving `NavMeshObstacle`.
    - `NightCreature`: host-only NavMeshAgent. Chases the nearest player with a complete NavMesh path; if barricades cut every path, attacks the nearest standing barricade. Invulnerable, placeholder capsule, no audio yet.
    - `DayNightLighting` (sun angle, dusk, fog/ambient) and `SurvivalHud` (clock, scrap, barricades standing) run on every client from replicated state.
    - **Greybox settlements:** **Tools > Quiet Village > Build Greybox > Forest Village / Coastal Town** (`SettlementGreyboxBuilder`, layouts in code, rebuildable). Builds buildings, shelter openings with barricades, trees, scrap, creature/player spawns, the director, a baked NavMesh (`NavMesh-<Level>.asset`), creates `Assets/Game/Prefabs/NightCreature.prefab` (registered in DefaultNetworkPrefabs), then runs level setup.
    - **Saved:** phase, time left and barricade health (`SurvivalDirector` is an `IWorldSaveParticipant`). Creatures are not; a night resumes with a fresh set at the spawn points.

## Known gaps and possible next work

- **Playtest pass with 2–3 players.** Nothing above has been verified in play. Highest priority.
- Tune the held-item grip offsets.
- Save NPC state (zombie alive/dead and position).
- Level transitions mid-session (UHFPS level exits still refuse; levels are chosen in the lobby only).
- UHFPS's `MainMenu`, `LevelManager`, `Elevator` and `Raining` scenes are still in Build Settings, unused.
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
