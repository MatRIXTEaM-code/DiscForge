// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Cdg;

/// <summary>Thrown when CD+G input is malformed in a way that matters
/// (a genuinely broken container, not merely a short or empty stream —
/// those decode to an empty picture).</summary>
public sealed class CdgFormatException : Exception
{
    public CdgFormatException(string message) : base(message) { }
    public CdgFormatException(string message, Exception inner) : base(message, inner) { }
}
