// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.GameAudio;

/// <summary>
/// Raised when a game-audio file (PSF/SPC/VGM/NSF) is too short to hold its
/// header, or its magic signature does not match. These readers parse metadata
/// and container structure only — no audio is ever decoded or played.
/// </summary>
public sealed class GameAudioFormatException(string message) : Exception(message);
