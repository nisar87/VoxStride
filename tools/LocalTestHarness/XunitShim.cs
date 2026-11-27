// Minimal xUnit-compatible shim used only by the offline local harness. CI runs the real xUnit.
using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace Xunit
{
    [AttributeUsage(AttributeTargets.Method)]
    public class FactAttribute : Attribute { public string? Skip { get; set; } public int Timeout { get; set; } }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TheoryAttribute : FactAttribute { }
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class InlineDataAttribute(params object?[] data) : Attribute { public object?[] Data { get; } = data; }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CollectionAttribute(string name) : Attribute { public string Name { get; } = name; } // shim runs tests sequentially anyway

    public sealed class AssertException(string m) : Exception(m);

    public static class Assert
    {
        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new AssertException($"Expected: {expected}\nActual:   {actual}");
        }
        public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        {
            if (!expected.SequenceEqual(actual)) throw new AssertException($"Sequences differ. Expected [{string.Join(",", expected)}] Actual [{string.Join(",", actual)}]");
        }
        public static void NotEqual<T>(T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) throw new AssertException($"Values should differ: {actual}");
        }
        public static void True(bool c, string? m = null) { if (!c) throw new AssertException(m ?? "Expected true"); }
        public static void False(bool c, string? m = null) { if (c) throw new AssertException(m ?? "Expected false"); }
        public static T IsType<T>(object? o) => o is T t && o.GetType() == typeof(T) ? t : throw new AssertException($"Expected {typeof(T).Name}, got {o?.GetType().Name}");
        public static void Null(object? o) { if (o is not null) throw new AssertException($"Expected null, got {o}"); }
        public static T NotNull<T>(T? o) where T : class => o ?? throw new AssertException("Expected not null");
        public static void NotNull(object? o) { if (o is null) throw new AssertException("Expected not null"); }
        public static void Empty(IEnumerable e) { if (e.Cast<object>().Any()) throw new AssertException("Expected empty"); }
        public static void NotEmpty(IEnumerable e) { if (!e.Cast<object>().Any()) throw new AssertException("Expected not empty"); }
        public static T Single<T>(IEnumerable<T> e, Predicate<T> pred) => Single(e.Where(x => pred(x)));
        public static T Single<T>(IEnumerable<T> e) { var l = e.ToList(); if (l.Count != 1) throw new AssertException($"Expected single, got {l.Count}"); return l[0]; }
        public static void Contains(string sub, string? s, StringComparison c) { if (s is null || !s.Contains(sub, c)) throw new AssertException($"'{s}' does not contain '{sub}'"); }
        public static void DoesNotContain(string sub, string? s, StringComparison c) { if (s is not null && s.Contains(sub, c)) throw new AssertException($"'{s}' contains '{sub}'"); }
        public static void StartsWith(string p, string? s, StringComparison c) { if (s is null || !s.StartsWith(p, c)) throw new AssertException($"'{s}' does not start with '{p}'"); }
        public static void Contains(string sub, string? s) { if (s is null || !s.Contains(sub, StringComparison.Ordinal)) throw new AssertException($"'{s}' does not contain '{sub}'"); }
        public static void DoesNotContain(string sub, string? s) { if (s is not null && s.Contains(sub, StringComparison.Ordinal)) throw new AssertException($"'{s}' contains '{sub}'"); }
        public static void Contains<T>(T item, IEnumerable<T> e) { if (!e.Contains(item)) throw new AssertException($"Collection does not contain {item}"); }
        public static void Contains<T>(IEnumerable<T> e, Predicate<T> pred) { if (!e.Any(x => pred(x))) throw new AssertException("No item matches predicate"); }
        public static void DoesNotContain<T>(IEnumerable<T> e, Predicate<T> pred) { if (e.Any(x => pred(x))) throw new AssertException("An item matches predicate"); }
        public static void DoesNotContain<T>(T item, IEnumerable<T> e) { if (e.Contains(item)) throw new AssertException($"Collection contains {item}"); }
        public static void EndsWith(string p, string? s, StringComparison c = StringComparison.Ordinal) { if (s is null || !s.EndsWith(p, c)) throw new AssertException($"'{s}' does not end with '{p}'"); }
        public static void All<T>(IEnumerable<T> e, Action<T> a) { foreach (var x in e) a(x); }
        public static void StartsWith(string p, string? s) { if (s is null || !s.StartsWith(p, StringComparison.Ordinal)) throw new AssertException($"'{s}' does not start with '{p}'"); }
        public static void InRange<T>(T v, T lo, T hi) where T : IComparable<T> { if (v.CompareTo(lo) < 0 || v.CompareTo(hi) > 0) throw new AssertException($"{v} not in [{lo},{hi}]"); }
        public static T Throws<T>(Action a) where T : Exception
        {
            try { a(); } catch (T ex) { return ex; } catch (Exception ex) { throw new AssertException($"Expected {typeof(T).Name}, got {ex.GetType().Name}"); }
            throw new AssertException($"Expected {typeof(T).Name}, nothing thrown");
        }
        public static async Task<T> ThrowsAsync<T>(Func<Task> a) where T : Exception
        {
            try { await a(); } catch (T ex) { return ex; } catch (Exception ex) { throw new AssertException($"Expected {typeof(T).Name}, got {ex.GetType().Name}"); }
            throw new AssertException($"Expected {typeof(T).Name}, nothing thrown");
        }
    }
}

namespace VoxStride.Tests.LocalHarness
{
    using Xunit;

    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var filter = args.FirstOrDefault();
            int pass = 0, fail = 0, skip = 0;
            var sw = Stopwatch.StartNew();
            var types = typeof(Program).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.Namespace?.StartsWith("VoxStride.Tests", StringComparison.Ordinal) == true && t.Namespace != "VoxStride.Tests.LocalHarness");
            foreach (var t in types.OrderBy(t => t.FullName))
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<FactAttribute>() is not null))
                {
                    var name = $"{t.Name}.{m.Name}";
                    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    var fact = m.GetCustomAttribute<FactAttribute>()!;
                    if (fact.Skip is not null) { skip++; continue; }
                    var datas = m.GetCustomAttributes<InlineDataAttribute>().Select(d => d.Data).ToList();
                    if (datas.Count == 0) datas.Add([]);
                    foreach (var d in datas)
                    {
                        object? inst = null;
                        try
                        {
                            inst = Activator.CreateInstance(t);
                            var ps = m.GetParameters();
                            var callArgs = d.Select((v, i) => v is null ? null : Convert.ChangeType(v, Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType)).ToArray();
                            var r = m.Invoke(inst, callArgs);
                            if (r is Task task) await task;
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            var e = ex is TargetInvocationException tie ? tie.InnerException! : ex;
                            Console.WriteLine($"FAIL {name}({string.Join(",", d)}): {e.GetType().Name}: {e.Message}");
                            Console.WriteLine("   " + e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("VoxStride", StringComparison.Ordinal))?.Trim());
                        }
                        finally
                        {
                            if (inst is IAsyncDisposable ad) await ad.DisposeAsync();
                            else if (inst is IDisposable di) di.Dispose();
                        }
                    }
                }
            }
            Console.WriteLine($"Local harness: {pass} passed, {fail} failed, {skip} skipped in {sw.Elapsed.TotalSeconds:0.0}s");
            return fail == 0 ? 0 : 1;
        }
    }
}
