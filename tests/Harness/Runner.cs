// Reflection-based test runner for the offline harness. Finds [Fact]/[Theory]
// methods in this assembly and runs them. NOT part of DiscForge — never commit.

using System.Reflection;
using Xunit;

int passed = 0, failed = 0, skipped = 0;
var failures = new List<string>();
string? filter = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("DFORGE_TEST_FILTER");

var types = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && t.GetMethods()
        .Any(m => m.GetCustomAttribute<FactAttribute>() is not null))
    .Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .OrderBy(t => t.Name)
    .ToList();

foreach (var type in types)
{
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
    {
        var fact = method.GetCustomAttribute<FactAttribute>();
        if (fact is null) continue;
        if (fact.Skip is not null) { skipped++; continue; }

        var cases = new List<object?[]?>();
        if (fact is TheoryAttribute)
            foreach (var data in method.GetCustomAttributes<InlineDataAttribute>())
                cases.Add(data.Data);
        else
            cases.Add(null);

        foreach (var caseArgs in cases)
        {
            string name = $"{type.Name}.{method.Name}" +
                (caseArgs is null ? "" : "(" + string.Join(", ", caseArgs.Select(a => a?.ToString() ?? "null")) + ")");
            try
            {
                object? instance = method.IsStatic ? null : Activator.CreateInstance(type);
                var pars = method.GetParameters();
                object?[]? converted = caseArgs;
                if (caseArgs is not null)
                {
                    converted = new object?[caseArgs.Length];
                    for (int i = 0; i < caseArgs.Length; i++)
                    {
                        var target = pars[i].ParameterType;
                        var v = caseArgs[i];
                        if (v is not null && v.GetType() != target && !target.IsEnum &&
                            v is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
                            v = Convert.ChangeType(v, target);
                        converted[i] = v;
                    }
                }
                var result = method.Invoke(instance, converted);
                if (result is Task task) task.GetAwaiter().GetResult();
                passed++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                failed++;
                failures.Add($"FAIL {name}\n     {inner.GetType().Name}: {inner.Message.Replace("\n", "\n     ")}");
            }
        }
    }
}

Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped " +
                  $"({types.Count} test classes)");
foreach (var f in failures) Console.WriteLine("\n" + f);
return failed == 0 ? 0 : 1;
