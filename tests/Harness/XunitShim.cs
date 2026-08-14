// Minimal xunit-compatible shim so the DiscForge test suite can compile and run
// in an offline environment (no NuGet access). Implements only the surface the
// suite actually uses. NOT part of DiscForge — never commit this.

using System.Collections;

namespace Xunit;

[AttributeUsage(AttributeTargets.Method)]
public class FactAttribute : Attribute
{
    public string? Skip { get; set; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : FactAttribute { }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute(params object?[] data) : Attribute
{
    public object?[] Data { get; } = data;
}

public sealed class XunitException(string message) : Exception(message);

public static class Assert
{
    private static void Fail(string msg) => throw new XunitException(msg);

    private static bool StructurallyEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string || b is string) return a.Equals(b);
        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var ia = ea.Cast<object?>().ToList();
            var ib = eb.Cast<object?>().ToList();
            if (ia.Count != ib.Count) return false;
            for (int i = 0; i < ia.Count; i++)
                if (!StructurallyEqual(ia[i], ib[i])) return false;
            return true;
        }
        return a.Equals(b);
    }

    private static string Show(object? o) => o switch
    {
        null => "(null)",
        string s => $"\"{s}\"",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show).Take(24)) + "]",
        _ => o.ToString() ?? "(null)",
    };

    public static void Equal<T>(T expected, T actual)
    {
        if (!StructurallyEqual(expected, actual))
            Fail($"Assert.Equal failure.\n  Expected: {Show(expected)}\n  Actual:   {Show(actual)}");
    }

    public static void Equal(double expected, double actual, int precision)
    {
        if (Math.Round(expected, precision) != Math.Round(actual, precision))
            Fail($"Assert.Equal failure (precision {precision}). Expected {expected}, got {actual}");
    }

    public static void NotEqual<T>(T expected, T actual)
    {
        if (StructurallyEqual(expected, actual))
            Fail($"Assert.NotEqual failure — both were {Show(actual)}");
    }

    public static void True(bool condition, string? message = null)
    {
        if (!condition) Fail(message ?? "Assert.True failure — condition was false");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) Fail(message ?? "Assert.False failure — condition was true");
    }

    public static void True(bool? condition) => True(condition == true);

    public static void False(bool? condition) => False(condition != false);

    public static void Null(object? value)
    {
        if (value is not null) Fail($"Assert.Null failure — was {Show(value)}");
    }

    public static void NotNull(object? value)
    {
        if (value is null) Fail("Assert.NotNull failure — was null");
    }

    public static void Contains(string expectedSubstring, string? actual)
    {
        if (actual is null || !actual.Contains(expectedSubstring))
            Fail($"Assert.Contains failure.\n  Not found: {Show(expectedSubstring)}\n  In:        {Show(actual)}");
    }

    public static void Contains(string expectedSubstring, string? actual, StringComparison comparison)
    {
        if (actual is null || !actual.Contains(expectedSubstring, comparison))
            Fail($"Assert.Contains failure.\n  Not found: {Show(expectedSubstring)}\n  In:        {Show(actual)}");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
    {
        if (!collection.Any(x => StructurallyEqual(x, expected)))
            Fail($"Assert.Contains failure.\n  Not found: {Show(expected)}\n  In:        {Show(collection)}");
    }

    public static T Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        foreach (var item in collection)
            if (filter(item)) return item;
        Fail($"Assert.Contains failure — no item matched the filter in {Show(collection)}");
        throw new InvalidOperationException();  // unreachable
    }

    public static void DoesNotContain(string unexpectedSubstring, string? actual)
    {
        if (actual is not null && actual.Contains(unexpectedSubstring))
            Fail($"Assert.DoesNotContain failure — found {Show(unexpectedSubstring)} in {Show(actual)}");
    }

    public static void DoesNotContain<T>(T unexpected, IEnumerable<T> collection)
    {
        if (collection.Any(x => StructurallyEqual(x, unexpected)))
            Fail($"Assert.DoesNotContain failure — found {Show(unexpected)}");
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        if (collection.Any(x => filter(x)))
            Fail("Assert.DoesNotContain failure — an item matched the filter");
    }

    public static void Empty(IEnumerable collection)
    {
        if (collection.Cast<object?>().Any())
            Fail($"Assert.Empty failure — {Show(collection)}");
    }

    public static void NotEmpty(IEnumerable collection)
    {
        if (!collection.Cast<object?>().Any())
            Fail("Assert.NotEmpty failure — collection was empty");
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        var list = collection.ToList();
        if (list.Count != 1)
            Fail($"Assert.Single failure — {list.Count} item(s): {Show(list)}");
        return list[0];
    }

    public static T Single<T>(IEnumerable<T> collection, Predicate<T> filter)
    {
        var matches = collection.Where(x => filter(x)).ToList();
        if (matches.Count != 1)
            Fail($"Assert.Single failure — {matches.Count} item(s) matched the filter");
        return matches[0];
    }

    public static void All<T>(IEnumerable<T> collection, Action<T> inspector)
    {
        int i = 0;
        foreach (var item in collection)
        {
            try { inspector(item); }
            catch (XunitException ex) { Fail($"Assert.All failure at item {i}: {ex.Message}"); }
            i++;
        }
    }

    public static T Throws<T>(Action testCode) where T : Exception
    {
        try { testCode(); }
        catch (T expected) { return expected; }
        catch (Exception other)
        {
            Fail($"Assert.Throws failure — expected {typeof(T).Name}, got {other.GetType().Name}: {other.Message}");
        }
        Fail($"Assert.Throws failure — expected {typeof(T).Name}, nothing was thrown");
        throw new InvalidOperationException();  // unreachable
    }

    public static T Throws<T>(Func<object?> testCode) where T : Exception
        => Throws<T>(() => { _ = testCode(); });

    public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
    {
        if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0)
            Fail($"Assert.InRange failure — {actual} not in [{low}, {high}]");
    }

    public static void StartsWith(string expectedStart, string? actual)
    {
        if (actual is null || !actual.StartsWith(expectedStart))
            Fail($"Assert.StartsWith failure.\n  Expected start: {Show(expectedStart)}\n  Actual:         {Show(actual)}");
    }

    public static void EndsWith(string expectedEnd, string? actual)
    {
        if (actual is null || !actual.EndsWith(expectedEnd))
            Fail($"Assert.EndsWith failure.\n  Expected end: {Show(expectedEnd)}\n  Actual:       {Show(actual)}");
    }

    public static T IsType<T>(object? value)
    {
        if (value is not T t || value.GetType() != typeof(T))
        {
            Fail($"Assert.IsType failure — expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"}");
            throw new InvalidOperationException();  // unreachable
        }
        return t;
    }
}
