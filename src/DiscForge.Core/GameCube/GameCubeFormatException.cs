// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.GameCube;

/// <summary>
/// Raised when a GameCube/Wii disc image or an RVZ/WIA container is malformed,
/// truncated, or asks for an operation DiscForge intentionally does not perform
/// (for example RVZ group decompression, which is deferred). Readers throw this
/// rather than a bare <see cref="Exception"/> or an index-out-of-range crash so
/// callers can distinguish a bad input from a genuine bug.
/// </summary>
public sealed class GameCubeFormatException(string message) : Exception(message);
