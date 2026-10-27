using System;
using System.Reflection;
using NUnit.Framework;
using VideoToUnity.Tests;

internal static class PureUsabilityRunner
{
    private static int Main()
    {
        int passed = 0, failed = 0;
        var suite = new BackgroundUsabilityGeometryTests();
        foreach (var method in suite.GetType().GetMethods())
        {
            if (method.GetCustomAttribute<TestAttribute>() == null) continue;
            try { method.Invoke(suite, null); Console.WriteLine("PASS " + method.Name); passed++; }
            catch (TargetInvocationException error)
            { Console.WriteLine("FAIL " + method.Name + ": " + error.InnerException); failed++; }
        }
        Console.WriteLine($"Pure geometry: {passed} passed, {failed} failed. No Unity process launched.");
        return failed == 0 && passed > 0 ? 0 : 1;
    }
}
