// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cheat;

/// <summary>The console family a <see cref="CheatCode"/> targets.</summary>
public enum CheatPlatform
{
    Nes,
    Snes,
    Genesis,
    GameBoy,
    GameSharkPs1,
}

/// <summary>
/// Thrown when a cheat-code string cannot be parsed: wrong length, an illegal
/// character for the code's alphabet, or a malformed GameShark line.
/// </summary>
public sealed class CheatFormatException(string message) : Exception(message);

/// <summary>
/// A decoded cheat: the CPU/bus address it writes to, the value it writes and,
/// for Game Genie "compare" codes and GameShark conditionals, the byte the code
/// tests against. <see cref="Description"/> carries a human-readable note
/// (chiefly for GameShark, where the leading type byte selects the operation).
/// </summary>
public sealed record CheatCode
{
    /// <summary>Which console family this code is for.</summary>
    public required CheatPlatform Platform { get; init; }

    /// <summary>The target address (CPU/bus address; 0x8000-0xFFFF for NES Game Genie).</summary>
    public required long Address { get; init; }

    /// <summary>The value the code writes.</summary>
    public required long Value { get; init; }

    /// <summary>
    /// The "compare" / "old value" byte. Set for Game Genie compare codes (8-letter
    /// NES, 9-digit Game Boy) and GameShark equal/not-equal conditionals; null otherwise.
    /// </summary>
    public long? Compare { get; init; }

    /// <summary>A human-readable note (GameShark operation type, etc.).</summary>
    public string? Description { get; init; }
}
