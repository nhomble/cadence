// Minimal PASS/FAIL harness: no test framework, no NuGet (mirrors wsl-tray tests\SelfTest.cs).
// Usage: tests are `public static class XxxTests` with `public static void Method()`; call T.Eq/T.True/T.Throws.
using System.Reflection;

namespace Cadence.TestHarness;

public static class T
{
    static int _total, _fails;
    static string _current = "";
    static readonly List<string> _known = [];
    static readonly List<string> _skipped = [];

    /// <summary>
    /// Records a check that could not be carried out because of a documented, external known issue (not a defect of the code under test).
    /// It does not count as a pass or a failure and does not change the exit code, but it is printed loudly on its own line and again in
    /// the final summary. Use it ONLY behind a precise detection of the known cause, so the check runs normally (and can fail) once the
    /// cause is gone; <paramref name="reason"/> must say where the issue is documented.
    /// </summary>
    public static void KnownIssue(string name, string reason)
    {
        string line = _current + name + "  -- " + reason;
        _known.Add(line);
        Console.WriteLine("KNOWN-ISSUE " + line);
    }

    /// <summary>
    /// Records a check that is switched off ON PURPOSE by default (for example because it would pop a window on the owner's desktop) and says how to turn it on.
    /// Like <see cref="KnownIssue"/> it is neither a pass nor a failure and is listed again in the summary, so a skipped check can never be mistaken for a passed one.
    /// </summary>
    public static void Skipped(string name, string reason)
    {
        string line = _current + name + "  -- " + reason;
        _skipped.Add(line);
        Console.WriteLine("SKIPPED " + line);
    }

    public static void True(string name, bool ok, string? detail = null)
    {
        _total++;
        if (!ok) _fails++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + _current + name + (ok || detail is null ? "" : "  -- " + detail));
    }

    public static void Eq<TV>(string name, TV expected, TV actual) =>
        True(name, EqualityComparer<TV>.Default.Equals(expected, actual), $"expected <{expected}> actual <{actual}>");

    public static void Throws<TEx>(string name, Action action) where TEx : Exception
    {
        try { action(); True(name, false, $"expected {typeof(TEx).Name}, nothing thrown"); }
        catch (TEx) { True(name, true); }
        catch (Exception ex) { True(name, false, $"expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
    }

    public static int RunAll(string[] args)
    {
        string? filter = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--filter") filter = args[i + 1];
        var tests = Assembly.GetEntryAssembly()!.GetTypes()
            .Where(t => t.IsPublic && t.IsAbstract && t.IsSealed && t.Name.EndsWith("Tests", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void))
                .OrderBy(m => m.MetadataToken)
                .Select(m => (Type: t, Method: m)));
        foreach (var (type, method) in tests)
        {
            string full = type.Name + "." + method.Name;
            if (filter is not null && !full.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _current = full + ": ";
            try { method.Invoke(null, null); }
            catch (TargetInvocationException ex) { True("threw", false, ex.InnerException?.ToString()); }
        }
        _current = "";
        Console.WriteLine($"{_total - _fails}/{_total} passed");
        if (_known.Count > 0)
        {
            Console.WriteLine($"{_known.Count} KNOWN-ISSUE check(s) were NOT verified (not counted above; they do not fail the run):");
            foreach (string k in _known) Console.WriteLine("  KNOWN-ISSUE " + k);
        }
        if (_skipped.Count > 0)
        {
            Console.WriteLine($"{_skipped.Count} check(s) were SKIPPED on purpose (not counted above; they do not fail the run):");
            foreach (string k in _skipped) Console.WriteLine("  SKIPPED " + k);
        }
        return _fails == 0 && _total > 0 ? 0 : 1;   // zero tests run (bad filter) is a failure
    }
}
