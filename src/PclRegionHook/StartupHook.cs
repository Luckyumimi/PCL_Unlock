using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using HarmonyLib;

// .NET 启动钩子要求：全局命名空间下的 StartupHook 类型 + 无参静态 Initialize 方法。
public static class StartupHook
{
    private const int JapanGeoId = 122;          // Windows GeoID: 日本
    private const string DeviceRegionValue = "DeviceRegion";
    private const string HookLogDirectoryVariable = "PCL_UNLOCK_LOG_DIRECTORY";
    private static readonly string LogPath = ResolveLogPath();
    private static readonly object LogLock = new();
    private static string _hookDir = string.Empty;

    private static string ResolveLogPath()
    {
        var directory = Environment.GetEnvironmentVariable(HookLogDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.GetTempPath();

        return Path.Combine(directory, "PCLRegionUnlock.log");
    }

    public static void Initialize()
    {
        try
        {
            _hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location) ?? string.Empty;
            ResetLog();
            Log($"=== PCL2 Region Unlock hook loaded (pid {Environment.ProcessId}) ===");
            AssemblyLoadContext.Default.Resolving += ResolveHookDependency;
            Run();
            StartSelfTest();
        }
        catch (Exception ex)
        {
            Log("Initialize failed: " + ex);
        }
    }

    private static Assembly? ResolveHookDependency(AssemblyLoadContext context, AssemblyName name)
    {
        try
        {
            var candidate = Path.Combine(_hookDir, (name.Name ?? string.Empty) + ".dll");
            if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        }
        catch (Exception ex)
        {
            Log($"resolve {name.Name} failed: {ex.Message}");
        }

        return null;
    }

    // 单独的方法，保证 Harmony 类型在 ALC 解析器挂好之后才被 JIT 解析。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var harmony = new Harmony("PCLRegionUnlock");

        try
        {
            PatchRegistryLayer(harmony);
        }
        catch (Exception ex)
        {
            Log("patch Microsoft.Win32.Registry failed: " + ex.Message);
        }

        // PCL 自己的程序集在启动钩子阶段还没加载，等它们加载时再打补丁。
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            try
            {
                PatchPclAssembly(harmony, e.LoadedAssembly);
            }
            catch (Exception ex)
            {
                Log("patch on AssemblyLoad failed: " + ex.Message);
            }
        };

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                PatchPclAssembly(harmony, assembly);
            }
            catch (Exception ex)
            {
                Log("patch loaded assembly failed: " + ex.Message);
            }
        }
    }

    private static void PatchRegistryLayer(Harmony harmony)
    {
        var assembly = Assembly.Load("Microsoft.Win32.Registry");
        var registryKey = assembly.GetType("Microsoft.Win32.RegistryKey");
        if (registryKey is null)
        {
            Log("RegistryKey type not found -> registry layer patch skipped");
            return;
        }

        var prefix = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.RegistryKeyGetValue),
            BindingFlags.Static | BindingFlags.NonPublic)!);

        var patched = 0;
        foreach (var signature in new[]
                 {
                     new[] { typeof(string) },
                     new[] { typeof(string), typeof(object) },
                     new[] { typeof(string), typeof(object), typeof(Microsoft.Win32.RegistryValueOptions) }
                 })
        {
            var method = registryKey.GetMethod("GetValue", signature);
            if (method is null)
            {
                Log($"RegistryKey.GetValue({signature.Length} args) not found");
                continue;
            }

            try
            {
                harmony.Patch(method, prefix: prefix);
                patched++;
                Log($"patched RegistryKey.GetValue/{signature.Length} args");
            }
            catch (Exception ex)
            {
                Log($"RegistryKey.GetValue/{signature.Length} patch failed: {ex.Message}");
            }
        }

        Log(patched > 0
            ? "registry layer patch OK"
            : "registry layer patch FAILED (falling back to PCL internals only)");
    }

    private static void PatchPclAssembly(Harmony harmony, Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name is not ("PCL.Core" or "Plain Craft Launcher 2")) return;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = Array.FindAll(ex.Types, t => t is not null)!;
        }

        foreach (var type in types)
        {
            switch (type.FullName)
            {
                case "PCL.Core.App.Region.RegionAssessment":
                    PatchBoolGetter(harmony, type, "IsRestricted", false);
                    break;

                case "PCL.Core.App.Region.RegionPolicy":
                    // 让区域判定读到的语言/locale 变成日语，从而得到 Other（不受限）而不是 Mainland。
                    PatchTryReadUserLocale(harmony, type);
                    PatchTryReadPreferredUiLanguages(harmony, type);
                    break;

                case "PCL.RestrictedFeaturePolicy":
                    PatchBoolGetter(harmony, type, "IsRestricted", false);
                    PatchBoolMethod(harmony, type, "Check", true);
                    PatchVoidMethod(harmony, type, "ThrowIfRestricted");
                    break;

                case "PCL.Core.App.Localization.Lang":
                    // 让"正版购买提示"走原有分支，避免区域解除后弹出账号验证/演示模式提示。
                    PatchBoolGetter(harmony, type, "IsFeaturesUnrestricted", true);
                    break;
            }
        }
    }

    private static void PatchTryReadUserLocale(Harmony harmony, Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var method = type.GetMethods(flags).FirstOrDefault(m =>
            m.Name == "TryReadUserLocale" && m.ReturnType == typeof(string));
        if (method is null)
        {
            Log("skip RegionPolicy.TryReadUserLocale: not found");
            return;
        }

        var prefix = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.LocaleJa),
            BindingFlags.Static | BindingFlags.NonPublic)!);
        harmony.Patch(method, prefix: prefix);
        Log("patched RegionPolicy.TryReadUserLocale -> ja-JP");
    }

    private static void PatchTryReadPreferredUiLanguages(Harmony harmony, Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var method = type.GetMethods(flags).FirstOrDefault(m =>
            m.Name == "TryReadPreferredUiLanguages" &&
            typeof(System.Collections.Generic.IReadOnlyList<string>).IsAssignableFrom(m.ReturnType));
        if (method is null)
        {
            Log("skip RegionPolicy.TryReadPreferredUiLanguages: not found");
            return;
        }

        var prefix = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.PreferredUiJa),
            BindingFlags.Static | BindingFlags.NonPublic)!);
        harmony.Patch(method, prefix: prefix);
        Log("patched RegionPolicy.TryReadPreferredUiLanguages -> [ja-JP]");
    }

    private static void PatchBoolGetter(Harmony harmony, Type type, string propertyName, bool value)
    {
        var getter = type.GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)?.GetGetMethod(true);
        if (getter is null)
        {
            Log($"skip {type.FullName}.{propertyName}: getter not found");
            return;
        }

        var prefix = new HarmonyMethod((value ? typeof(Hooks).GetMethod(nameof(Hooks.AlwaysTrueGet),
            BindingFlags.Static | BindingFlags.NonPublic) : typeof(Hooks).GetMethod(nameof(Hooks.AlwaysFalseGet),
            BindingFlags.Static | BindingFlags.NonPublic))!);
        harmony.Patch(getter, prefix: prefix);
        Log($"patched {type.FullName}.{propertyName} -> {value}");
    }

    private static void PatchBoolMethod(Harmony harmony, Type type, string methodName, bool value)
    {
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null || method.ReturnType != typeof(bool))
        {
            Log($"skip {type.FullName}.{methodName}: bool method not found");
            return;
        }

        var prefix = new HarmonyMethod((value ? typeof(Hooks).GetMethod(nameof(Hooks.AlwaysTrueGet),
            BindingFlags.Static | BindingFlags.NonPublic) : typeof(Hooks).GetMethod(nameof(Hooks.AlwaysFalseGet),
            BindingFlags.Static | BindingFlags.NonPublic))!);
        harmony.Patch(method, prefix: prefix);
        Log($"patched {type.FullName}.{methodName} -> {value}");
    }

    private static void PatchVoidMethod(Harmony harmony, Type type, string methodName)
    {
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null || method.ReturnType != typeof(void))
        {
            Log($"skip {type.FullName}.{methodName}: void method not found");
            return;
        }

        var prefix = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.SkipVoid),
            BindingFlags.Static | BindingFlags.NonPublic)!);
        harmony.Patch(method, prefix: prefix);
        Log($"patched {type.FullName}.{methodName} (skipped)");
    }

    private static void StartSelfTest()
    {
        var thread = new Thread(() =>
        {
            for (var i = 0; i < 180; i++)
            {
                Thread.Sleep(1000);
                bool done;
                try
                {
                    done = RunSelfTest();
                }
                catch (Exception ex)
                {
                    Log("self-test error: " + ex.Message);
                    return;
                }

                if (done) return;
            }

            Log("self-test: PCL assemblies never appeared, giving up");
        })
        {
            IsBackground = true,
            Name = "PCLRegionUnlock.SelfTest"
        };
        thread.Start();
    }

    private static bool RunSelfTest()
    {
        var core = FindAssembly("PCL.Core");
        if (core is null) return false;

        var policyType = core.GetType("PCL.Core.App.Region.RegionPolicy");
        if (policyType is null) return false;

        var readSignals = policyType.GetMethod("ReadWindowsSignals", BindingFlags.Public | BindingFlags.Static);
        var evaluate = policyType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static);
        if (readSignals is null || evaluate is null) return false;

        var signals = readSignals.Invoke(null, null);
        if (signals is null) return false;
        var assessment = evaluate.Invoke(null, new[] { signals });
        if (assessment is null) return false;

        var assessmentType = assessment.GetType();
        var classification = assessmentType.GetProperty("Classification")?.GetValue(assessment);
        var reason = assessmentType.GetProperty("Reason")?.GetValue(assessment);
        var restricted = assessmentType.GetProperty("IsRestricted")?.GetValue(assessment);

        Log($"self-test: RegionPolicy.Evaluate -> classification={classification}, reason={reason}, IsRestricted={restricted}");

        var app = FindAssembly("Plain Craft Launcher 2");
        if (app is not null)
        {
            var policy = app.GetType("PCL.RestrictedFeaturePolicy");
            var value = policy?.GetProperty("IsRestricted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            if (value is not null) Log($"self-test: RestrictedFeaturePolicy.IsRestricted = {value}");
        }

        var verdict = restricted is false ? "PASS" : "FAIL";
        Log($"self-test: region restriction removed -> {verdict}");
        return true;
    }

    private static Assembly? FindAssembly(string simpleName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return assembly;
        }

        return null;
    }

    private static void ResetLog()
    {
        try
        {
            File.WriteAllText(LogPath, string.Empty);
        }
        catch
        {
            // 日志写不了不影响功能。
        }
    }

    internal static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 忽略日志失败。
        }
    }

    internal static bool IsDeviceRegionQuery(string? name) =>
        string.Equals(name, DeviceRegionValue, StringComparison.OrdinalIgnoreCase);

    internal static int JapanGeoIdValue => JapanGeoId;
}

internal static class Hooks
{
    internal static bool RegistryKeyGetValue(string? name, ref object? __result)
    {
        if (!StartupHook.IsDeviceRegionQuery(name)) return true;

        __result = StartupHook.JapanGeoIdValue;
        StartupHook.Log($"intercepted RegistryKey.GetValue(\"{name}\") -> {StartupHook.JapanGeoIdValue}");
        return false;
    }

    internal static bool AlwaysFalseGet(ref bool __result)
    {
        __result = false;
        return false;
    }

    internal static bool AlwaysTrueGet(ref bool __result)
    {
        __result = true;
        return false;
    }

    internal static bool SkipVoid() => false;

    internal static bool LocaleJa(ref bool available, ref string? __result)
    {
        available = true;
        __result = "ja-JP";
        StartupHook.Log("intercepted RegionPolicy.TryReadUserLocale -> ja-JP");
        return false;
    }

    internal static bool PreferredUiJa(ref bool available, ref System.Collections.Generic.IReadOnlyList<string> __result)
    {
        available = true;
        __result = new[] { "ja-JP" };
        StartupHook.Log("intercepted RegionPolicy.TryReadPreferredUiLanguages -> [ja-JP]");
        return false;
    }
}
