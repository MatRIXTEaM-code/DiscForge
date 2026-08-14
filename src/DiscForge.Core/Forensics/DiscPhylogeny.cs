// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>A node in a disc family tree: a leaf is one disc; an internal node is a split where two
/// lineages diverge, at a height equal to how different they are.</summary>
public sealed record PhyloNode
{
    public string? Name { get; init; }               // set on leaves
    public double Height { get; init; }              // divergence height (0 for leaves)
    public IReadOnlyList<PhyloNode> Children { get; init; } = System.Array.Empty<PhyloNode>();
    public bool IsLeaf => Name is not null;

    public IEnumerable<string> Leaves() =>
        IsLeaf ? new[] { Name! } : Children.SelectMany(c => c.Leaves());
}

/// <summary>
/// Disc phylogenetics — reconstruct the family tree of a title's releases from what actually changed
/// between them. Two discs' distance is one minus their content similarity (shared files by path and by
/// hash), and average-linkage (UPGMA) clustering over those distances builds a dendrogram: near-identical
/// variants sit as close siblings, a heavily-revised or different-region pressing branches higher up. It
/// turns a pile of regions, revisions and budget re-releases into a readable lineage — which version is a
/// tweak of which, and where the big divergences are — like a git history recovered from the discs alone.
/// Identification and structure only; it reads the discs and draws the tree.
/// </summary>
public static class DiscPhylogeny
{
    /// <summary>Build the family tree of a set of profiled discs.</summary>
    public static PhyloNode Build(IReadOnlyList<DiscProfile> discs)
    {
        ArgumentNullException.ThrowIfNull(discs);
        if (discs.Count == 0) throw new ArgumentException("Need at least one disc.", nameof(discs));
        if (discs.Count == 1) return new PhyloNode { Name = discs[0].Source };

        int n = discs.Count;
        var nodes = new List<PhyloNode>();
        var sizes = new List<int>();
        for (int i = 0; i < n; i++) { nodes.Add(new PhyloNode { Name = discs[i].Source }); sizes.Add(1); }

        // Pairwise distance = 1 - similarity.
        var dist = new List<List<double>>();
        for (int i = 0; i < n; i++)
        {
            dist.Add(new List<double>(new double[n]));
            for (int j = 0; j < n; j++)
                dist[i][j] = i == j ? 0 : 1 - DiscClustering.Similarity(discs[i], discs[j]).Score;
        }

        while (nodes.Count > 1)
        {
            // Closest pair.
            int bi = 0, bj = 1;
            double best = double.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
                for (int j = i + 1; j < nodes.Count; j++)
                    if (dist[i][j] < best) { best = dist[i][j]; bi = i; bj = j; }

            var parent = new PhyloNode
            {
                Height = best / 2.0,
                Children = new[] { nodes[bi], nodes[bj] },
            };
            int newSize = sizes[bi] + sizes[bj];

            // Average-linkage distances from the merged cluster to every other.
            var newRow = new List<double>();
            for (int k = 0; k < nodes.Count; k++)
                newRow.Add(k == bi || k == bj ? 0
                    : (sizes[bi] * dist[bi][k] + sizes[bj] * dist[bj][k]) / newSize);

            // Remove bj then bi (bj > bi), then append the merged cluster.
            RemoveIndex(dist, bj); RemoveIndex(dist, bi);
            nodes.RemoveAt(bj); nodes.RemoveAt(bi);
            sizes.RemoveAt(bj); sizes.RemoveAt(bi);
            newRow.RemoveAt(bj); newRow.RemoveAt(bi);

            foreach (var row in dist) row.Add(0);       // new column
            dist.Add(new List<double>(newRow) { 0 });   // new row (+ self 0)
            int m = dist.Count - 1;
            for (int k = 0; k < m; k++) { dist[m][k] = newRow[k]; dist[k][m] = newRow[k]; }

            nodes.Add(parent);
            sizes.Add(newSize);
        }

        return nodes[0];
    }

    /// <summary>An indented, human-readable tree.</summary>
    public static string RenderTree(PhyloNode root)
    {
        var sb = new StringBuilder();
        void Walk(PhyloNode node, int depth)
        {
            string indent = new string(' ', depth * 2);
            if (node.IsLeaf) sb.AppendLine($"{indent}- {node.Name}");
            else
            {
                sb.AppendLine($"{indent}* diverge @ {node.Height:0.###} ({node.Leaves().Count()} discs)");
                foreach (var c in node.Children) Walk(c, depth + 1);
            }
        }
        Walk(root, 0);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Newick format, for loading into standard tree viewers.</summary>
    public static string ToNewick(PhyloNode root)
    {
        string Node(PhyloNode n)
        {
            if (n.IsLeaf) return Safe(n.Name!);
            var parts = n.Children.Select(c =>
                Node(c) + ":" + (n.Height - c.Height).ToString("0.###", CultureInfo.InvariantCulture));
            return "(" + string.Join(",", parts) + ")";
        }
        return Node(root) + ";";
    }

    // ---- internals ----------------------------------------------------------

    private static void RemoveIndex(List<List<double>> matrix, int idx)
    {
        matrix.RemoveAt(idx);
        foreach (var row in matrix) row.RemoveAt(idx);
    }

    private static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c is ',' or '(' or ')' or ':' or ';' ? '_' : c);
        return sb.ToString();
    }
}
