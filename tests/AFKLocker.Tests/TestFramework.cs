using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace AFKLocker.Tests
{
    /// <summary>Marks a parameterless static method as a test.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute
    {
        public string Description { get; private set; }

        public TestAttribute(string description)
        {
            Description = description;
        }
    }

    internal sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    /// <summary>Assertions used by the test suite.</summary>
    internal static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new AssertionException("Expected true: " + message);
        }

        public static void False(bool condition, string message)
        {
            if (condition) throw new AssertionException("Expected false: " + message);
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException(string.Format(
                    "{0}\n      expected: {1}\n      actual:   {2}", message, expected, actual));
        }

        public static void NotNull(object value, string message)
        {
            if (value == null) throw new AssertionException("Expected not null: " + message);
        }

        public static void Null(object value, string message)
        {
            if (value != null) throw new AssertionException("Expected null: " + message);
        }

        public static TException Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                throw new AssertionException(string.Format(
                    "{0}\n      expected {1}, got {2}: {3}",
                    message, typeof(TException).Name, other.GetType().Name, other.Message));
            }

            throw new AssertionException(string.Format(
                "{0}\n      expected {1}, but nothing was thrown", message, typeof(TException).Name));
        }
    }

    /// <summary>
    /// A deliberately small test runner.
    ///
    /// AFKLocker builds with the C# compiler that ships with Windows and has no
    /// package restore step, which keeps the build reproducible on any machine
    /// and on CI without a toolchain install. Pulling in a test framework would
    /// have meant adding one. For a project this size, reflection over
    /// [Test] methods is enough.
    /// </summary>
    internal static class TestRunner
    {
        public static int Main(string[] args)
        {
            bool verbose = args != null && args.Any(a =>
                string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase));

            var tests = Assembly.GetExecutingAssembly()
                .GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Select(m => new
                {
                    Method = m,
                    Attribute = (TestAttribute)m.GetCustomAttributes(typeof(TestAttribute), false).FirstOrDefault()
                })
                .Where(x => x.Attribute != null)
                .OrderBy(x => x.Method.DeclaringType.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Method.Name, StringComparer.Ordinal)
                .ToList();

            if (tests.Count == 0)
            {
                Console.Error.WriteLine("No tests found.");
                return 1;
            }

            var stopwatch = Stopwatch.StartNew();
            var failures = new List<string>();
            string currentGroup = null;

            foreach (var test in tests)
            {
                string group = test.Method.DeclaringType.Name;
                if (group != currentGroup)
                {
                    Console.WriteLine();
                    Console.WriteLine(group);
                    currentGroup = group;
                }

                try
                {
                    test.Method.Invoke(null, null);
                    Console.WriteLine("  PASS  " + test.Attribute.Description);
                }
                catch (TargetInvocationException wrapped)
                {
                    Exception inner = wrapped.InnerException ?? wrapped;
                    Console.WriteLine("  FAIL  " + test.Attribute.Description);
                    Console.WriteLine("        " + inner.Message.Replace("\n", "\n        "));
                    if (verbose) Console.WriteLine(inner.StackTrace);
                    failures.Add(group + "." + test.Method.Name);
                }
            }

            stopwatch.Stop();
            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine(string.Format("All tests passed ({0}ms).", stopwatch.ElapsedMilliseconds));
                return 0;
            }

            Console.WriteLine("FAILED:");
            foreach (string failure in failures)
                Console.WriteLine("  " + failure);
            return 1;
        }
    }
}
