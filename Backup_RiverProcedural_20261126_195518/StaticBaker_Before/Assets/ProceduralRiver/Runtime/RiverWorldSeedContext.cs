using UnityEngine;

/// <summary>
/// Carries the host's room code from the Main Menu into the gameplay scene.
/// A multiplayer room code deterministically maps to one world seed; solo sessions
/// deliberately leave this empty and receive a fresh random seed from GameSpawner.
/// </summary>
public static class RiverWorldSeedContext
{
    private static string roomCode = string.Empty;

    public static bool HasRoomCode => !string.IsNullOrWhiteSpace(roomCode);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        roomCode = string.Empty;
    }

    public static void SetRoomCode(string code)
    {
        roomCode = string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();
    }

    public static void ClearRoomCode()
    {
        roomCode = string.Empty;
    }

    public static bool TryGetRoomSeed(out int seed)
    {
        seed = 0;
        if (string.IsNullOrWhiteSpace(roomCode)) return false;

        // FNV-1a 32-bit: stable across platforms and .NET runtime versions.
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;
        uint hash = offsetBasis;

        for (int i = 0; i < roomCode.Length; i++)
        {
            hash ^= roomCode[i];
            hash = unchecked(hash * prime);
        }

        seed = (int)(hash & 0x7FFFFFFFu);
        if (seed == 0) seed = 1;
        return true;
    }
}
