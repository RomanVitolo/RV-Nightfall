namespace QuietVillage
{
    /// <summary>
    /// Where the project's own assets live, for every setup tool. The one place to change when folders move.
    /// </summary>
    /// <remarks>
    /// Tools that create or look up assets by path read these rather than spelling paths out, so the layout below is
    /// defined once. Assets referenced from other assets (scenes, prefabs) are found by GUID and survive moves on their
    /// own; only tools need paths.
    ///
    /// <code>
    /// Assets/QuietVillage/
    ///   Scenes/            every scene: lobby, levels, the UHFPS demo level; NavMesh/ beside them
    ///   Scripts/
    ///     Multiplayer/Runtime   QuietVillage.Multiplayer.Runtime asmdef (no UHFPS)
    ///     Multiplayer/Bridge    UHFPS to Netcode glue (Assembly-CSharp)
    ///     Gameplay/             the game itself, e.g. Survival (Assembly-CSharp)
    ///     Editor/               setup tools (Assembly-CSharp-Editor)
    ///   Prefabs/           Player, Multiplayer, Creatures, Items/Drops
    ///   Data/              LevelCatalog; Resources/ for assets loaded at runtime
    ///   Art/               Animations, Materials, Skybox
    ///   UI/                Multiplayer lobby and overlay
    ///   Docs/              UHFPS-PATCHES.md
    /// </code>
    /// Outside it: vendor folders, and <c>Assets/DefaultNetworkPrefabs.asset</c>, which Netcode expects at that path.
    /// </remarks>
    public static class ProjectPaths
    {
        public const string Root = "Assets/QuietVillage";

        public const string Scenes = Root + "/Scenes";
        public const string NavMeshes = Scenes + "/NavMesh";
        public const string LobbyScene = Scenes + "/LobbyScene.unity";

        /// <summary>UHFPS's demo level, adapted for multiplayer; also the template new levels copy their systems from.</summary>
        public const string DemoLevelScene = Scenes + "/GameplayScene.unity";

        public const string Data = Root + "/Data";
        public const string RuntimeResources = Data + "/Resources";
        public const string LevelCatalog = Data + "/LevelCatalog.asset";
        public const string CharacterCatalog = Data + "/CharacterCatalog.asset";

        public const string Prefabs = Root + "/Prefabs";
        public const string PlayerPrefab = Prefabs + "/Player/NetworkedWorkerPlayer.prefab";
        public const string MultiplayerPrefabs = Prefabs + "/Multiplayer";
        public const string CreaturePrefabs = Prefabs + "/Creatures";
        public const string ItemDropPrefabs = Prefabs + "/Items/Drops";

        public const string Art = Root + "/Art";
        public const string WorkerAnimations = Art + "/Animations/Worker";
        public const string GreyboxMaterials = Art + "/Materials/Greybox";

        public const string MultiplayerUI = Root + "/UI/Multiplayer";

        /// <summary>Netcode's default network prefab list; it looks for it here, so it stays at the Assets root.</summary>
        public const string NetworkPrefabsList = "Assets/DefaultNetworkPrefabs.asset";
    }
}
