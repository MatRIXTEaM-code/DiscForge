// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.GameAudio;

/// <summary>
/// Raised when a game-audio file (PSF/SPC/VGM/NSF) is too short to hold its
/// header, or its magic signature does not match. These readers parse metadata
/// and container structure only — no audio is ever decoded or played.
/// </summary>
public sealed class GameAudioFormatException(string message) : Exception(message);
