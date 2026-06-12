using UnityEditor;

/// <summary>
/// One-click multiplayer validation menu (Tools → RTS → Multiplayer → …).
/// Thin wrappers over the BridgeOps PvP ops so the same verified code paths
/// are reachable from the editor UI without the file bridge.
///
/// Typical manual two-client session:
///   1. Build Two-Client Test Player (once per code change).
///   2. Enter Play Mode in MainMenuScene → Connect → Create Test Room.
///   3. Launch the build with -autotest (it joins and drives itself).
///   4. Print Photon Status until players=2 → Start Match (Blue).
///   5. Print Corner Report / Attack Nearest Enemy / Leave Room as needed.
/// </summary>
public static class MultiplayerValidationTools
{
    private const string Menu = "Tools/RTS/Multiplayer/";

    [MenuItem(Menu + "Print Photon Status")]
    public static void PrintStatus() => BridgeOps.PvpStatus();

    [MenuItem(Menu + "Print Corner + Ownership Report")]
    public static void PrintCorners() => BridgeOps.PvpCornerReport();

    [MenuItem(Menu + "Connect To Photon")]
    public static void Connect() => BridgeOps.PvpConnect();

    [MenuItem(Menu + "Create Test Room (BridgeSmokeTest)")]
    public static void CreateRoom() => BridgeOps.PvpCreateRoom();

    [MenuItem(Menu + "Start Match (Blue)")]
    public static void StartMatch() => BridgeOps.PvpStartMatch();

    [MenuItem(Menu + "Print Entity Positions (census)")]
    public static void PrintEntities() => BridgeOps.PvpEntityPositions();

    [MenuItem(Menu + "Produce Worker (networked command)")]
    public static void ProduceWorker() => BridgeOps.PvpProduceWorker();

    [MenuItem(Menu + "Move Own Unit +8,+8 (networked command)")]
    public static void MoveOwnUnit() => BridgeOps.PvpMoveOwnUnit();

    [MenuItem(Menu + "Damage Nearest Enemy Unit (−30)")]
    public static void DamageEnemy() => BridgeOps.PvpDamageEnemyUnit();

    [MenuItem(Menu + "Attack Nearest Enemy-Owned Unit")]
    public static void AttackNearest() => BridgeOps.PvpAttackNearestEnemy();

    [MenuItem(Menu + "Leave Room")]
    public static void Leave() => BridgeOps.PvpLeave();

    [MenuItem(Menu + "Build Two-Client Test Player")]
    public static void Build() => BridgeOps.BuildTwoClientPlayer();
}
