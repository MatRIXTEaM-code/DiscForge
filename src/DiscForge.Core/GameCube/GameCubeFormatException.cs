// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.GameCube;

/// <summary>
/// Raised when a GameCube/Wii disc image or an RVZ/WIA container is malformed,
/// truncated, or asks for an operation DiscForge intentionally does not perform
/// (for example RVZ group decompression, which is deferred). Readers throw this
/// rather than a bare <see cref="Exception"/> or an index-out-of-range crash so
/// callers can distinguish a bad input from a genuine bug.
/// </summary>
public sealed class GameCubeFormatException(string message) : Exception(message);
