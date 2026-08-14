// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Net.Http;

namespace DiscForge.Core.Sources;

/// <summary>One file offered by a source: the on-disc path it should take, and its size (−1 when
/// the source can't know the size without opening it, e.g. a chunked HTTP response).</summary>
public sealed record SourceEntry(string Path, long SizeBytes);

/// <summary>
/// A place files can be pulled from to build or burn a disc — a local folder, an HTTP(S) URL list,
/// or (via the same interface, added later) a cloud drive such as Google Drive / OneDrive /
/// Dropbox. This abstraction is the thing legacy burners never had: it lets the image builders and
/// the spanning planner consume files that don't already live on the local disk. Cloud providers
/// implement <see cref="IFileSource"/> with their own auth; nothing else in the pipeline changes.
/// </summary>
public interface IFileSource
{
    /// <summary>A short human label for the source (shown in progress/logs).</summary>
    string Name { get; }

    /// <summary>List the files this source offers, with the on-disc path each should take.</summary>
    IEnumerable<SourceEntry> Enumerate();

    /// <summary>Open a read stream for one entry.</summary>
    Stream Open(SourceEntry entry);
}

/// <summary>A source backed by a local folder. Paths are relative to the folder root.</summary>
public sealed class LocalFileSource : IFileSource
{
    private readonly string _root;
    public LocalFileSource(string root) => _root = Path.GetFullPath(root);
    public string Name => $"local:{_root}";

    public IEnumerable<SourceEntry> Enumerate()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            yield return new SourceEntry(Path.GetRelativePath(_root, f).Replace('\\', '/'),
                                         new FileInfo(f).Length);
    }

    public Stream Open(SourceEntry entry)
        => File.OpenRead(Path.Combine(_root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>A source backed by HTTP(S) URLs, each mapped to an on-disc path. Sizes come from a HEAD
/// request (or Content-Length on GET) and are −1 when the server doesn't report one.</summary>
public sealed class HttpFileSource : IFileSource
{
    private readonly IReadOnlyList<(string path, Uri url)> _entries;
    private readonly Func<HttpClient> _clientFactory;

    public HttpFileSource(IEnumerable<(string path, Uri url)> entries, Func<HttpClient>? clientFactory = null)
    {
        _entries = entries.ToList();
        _clientFactory = clientFactory ?? (() => new HttpClient());
    }

    public string Name => $"http:{_entries.Count} url(s)";

    public IEnumerable<SourceEntry> Enumerate()
    {
        using var client = _clientFactory();
        foreach (var (path, url) in _entries)
        {
            long size = -1;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = client.Send(req);
                if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is { } len) size = len;
            }
            catch (HttpRequestException) { /* size stays unknown */ }
            yield return new SourceEntry(path, size);
        }
    }

    public Stream Open(SourceEntry entry)
    {
        var match = _entries.FirstOrDefault(e => e.path == entry.Path);
        if (match.url is null) throw new FileNotFoundException($"No URL for '{entry.Path}'.");
        var client = _clientFactory();
        var stream = client.GetStreamAsync(match.url).GetAwaiter().GetResult();
        return new HttpOwnedStream(stream, client);
    }

    /// <summary>Keeps the HttpClient alive for the lifetime of the response stream.</summary>
    private sealed class HttpOwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpClient _client;
        public HttpOwnedStream(Stream inner, HttpClient client) { _inner = inner; _client = client; }
        public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _client.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
