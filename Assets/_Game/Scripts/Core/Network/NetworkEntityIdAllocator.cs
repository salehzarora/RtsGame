using UnityEngine;

/// <summary>
/// Per-client deterministic id factory for entities spawned in response to
/// a <see cref="PlayerCommand"/>. Two clients running the same command
/// (one as the originator, one as the network replay) MUST end up with
/// the same id on the spawned object — otherwise commands referencing it
/// won't resolve through <see cref="EntityRegistry"/>.
///
/// Namespacing scheme: ids are <c>"p{LocalPlayerId}-{counter}"</c>. The
/// counter is local to this client and increments per allocation. Because
/// the counter is paired with the local player id (different on each
/// client), two clients never produce colliding ids even though they each
/// keep their own counter.
///
/// How the issuer/receiver agree:
///   • Local issuer: mints "p0-7" (LocalPlayerId 0), embeds it in the
///     command, executes locally.
///   • Network relay: serialises the command (with "p0-7" inside) and
///     sends to the remote client.
///   • Remote client: deserialises, calls
///     <see cref="CommandDispatcher.IssueRemote"/>. The dispatcher reads
///     <c>spawnEntityId = "p0-7"</c> from the command and uses it via
///     <see cref="GameEntity.SetNextSpawnId"/> instead of allocating a
///     fresh one. Both clients' entities now agree on the id.
///
/// Single-player: <see cref="NetworkManagerRTS.LocalPlayerId"/> returns -1
/// when not in a room. We treat -1 as player 0 so the namespace is still
/// usable; collisions don't matter because there's no other client.
/// </summary>
public static class NetworkEntityIdAllocator
{
    private static int s_counter;

    /// <summary>
    /// Mint the next id for this client. Format: <c>"p{playerId}-{n}"</c>.
    /// Thread-affinity: Unity main thread only — counter is not interlocked.
    /// </summary>
    public static string Allocate()
    {
        int pid = NetworkManagerRTS.LocalPlayerId;
        if (pid < 0) pid = 0;     // single-player fallback

        int n = ++s_counter;
        string id = $"p{pid}-{n}";
        return id;
    }

    /// <summary>
    /// Allocate <paramref name="count"/> ids in one call. Useful for the
    /// Build command which mints two ids (construction site + final
    /// building) atomically so they don't interleave with other allocations.
    /// </summary>
    public static string[] AllocateBatch(int count)
    {
        if (count <= 0) return System.Array.Empty<string>();
        var arr = new string[count];
        for (int i = 0; i < count; i++) arr[i] = Allocate();
        return arr;
    }

    /// <summary>
    /// Editor-only — reset the counter to zero. Useful when domain-reload
    /// behaviour leaves residual state between Play sessions.
    /// </summary>
    public static void ResetForTests()
    {
        s_counter = 0;
    }
}
