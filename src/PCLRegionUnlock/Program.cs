using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PclRegionUnlock;

internal static class Program
{
    private const string HookDllName = "PclRegionHook.dll";
    private const string StartupHookVariable = "DOTNET_STARTUP_HOOKS";
    private const string HookLogDirectoryVariable = "PCL_UNLOCK_LOG_DIRECTORY";
    private const string ShortcutFileName = "PCL2CE(unlock)";
    private const string Caption = "PCL_Unlock";
    private const string ShortcutLaunchArgument = "--launch-pcl2ce";
    private const string PclCeReleasesUrl = "https://github.com/PCL-Community/PCL-CE/releases";

    private static readonly string[] PreferredPclCeNames =
    [
        "PCL2_CE_Release_x64_2.exe",
        "PCL2_CE_Release_x64.exe",
        "PCL2_CE_Release_ARM64.exe"
    ];

    private static readonly string[] OriginalPclNames =
    [
        "Plain Craft Launcher 2.exe",
        "PlainCraftLauncher2.exe",
        "PCL2.exe"
    ];

    private static readonly Guid DesktopFolderId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var isSelfCheck = args.Contains("--self-check", StringComparer.OrdinalIgnoreCase);
            var launchesTarget = IsShortcutLaunch(args) || args.Length > 0 && LooksLikeExePath(args[0]);
            var target = ResolveTarget(args, baseDir, out var passthrough, out var guidanceShown, allowInteraction: !isSelfCheck);

            if (target is null)
            {
                if (isSelfCheck)
                {
                    WriteLauncherLog("No PCL2 CE target found; interactive selection skipped.");
                    return 2;
                }

                if (!guidanceShown)
                    ShowError("没有选择 PCL2 CE 主程序，启动器退出。\n\n本项目只支持 PCL2 CE，不支持原版 PCL2。");
                return 2;
            }

            var hookDll = ResolveHookDll(baseDir, out var hookSource);
            if (hookDll is null)
            {
                ShowError($"找不到启动钩子 {HookDllName}，启动器里也没有内嵌副本。\n\n" +
                          "请用 build.ps1 重新构建启动器，或在同目录 Hook\\ 下放入该 DLL。");
                return 3;
            }

            // --self-check 只做诊断，不写任何东西（不建快捷方式、不落盘记忆）。
            if (isSelfCheck)
            {
                WriteLauncherLog(string.Join(Environment.NewLine, new[]
                {
                    $"baseDir        = {baseDir}",
                    $"target         = {target}",
                    $"hook           = {hookDll}",
                    $"hookFrom       = {hookSource}",
                    $"hookExists     = {File.Exists(hookDll)}",
                    $"desktop        = {GetDesktopDirectory()}",
                    $"shortcutNew    = {EnsureDesktopShortcut(target, baseDir, dryRun: true)}"
                }));
                return 0;
            }

            if (!launchesTarget)
            {
                var shortcutCreated = EnsureDesktopShortcut(target, baseDir);

                if (shortcutCreated)
                {
                    ShowInfo("已创建 PCL2CE(unlock) 快捷方式！\n\n" +
                             $"桌面：{ShortcutFileName}\n" +
                             $"目标：{target}\n\n" +
                             "以后双击桌面这个快捷方式即可启动 PCL2 CE。");
                }
            }

            // 直接运行启动器只负责创建快捷方式；快捷方式本身才负责启动 CE。
            if (!launchesTarget) return 0;

            // 与原版 PCL2 CE 一致：已有同一路径实例时只激活它，不重复启动。
            if (TryBringExistingPclToFront(target)) return 0;

            var existingHooks = Environment.GetEnvironmentVariable(StartupHookVariable);
            Environment.SetEnvironmentVariable(
                StartupHookVariable,
                string.IsNullOrWhiteSpace(existingHooks) ? hookDll : hookDll + Path.PathSeparator + existingHooks);
            Environment.SetEnvironmentVariable(HookLogDirectoryVariable, baseDir);

            var startInfo = new ProcessStartInfo(target)
            {
                WorkingDirectory = Path.GetDirectoryName(target)!,
                UseShellExecute = false
            };
            foreach (var arg in passthrough) startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowError("启动失败：\n" + ex);
            return 1;
        }
    }

    // ---------- 目标 PCL 定位 ----------

    private static string? ResolveTarget(
        string[] args,
        string baseDir,
        out string[] passthrough,
        out bool guidanceShown,
        bool allowInteraction)
    {
        passthrough = args;
        guidanceShown = false;

        if (IsShortcutLaunch(args))
        {
            passthrough = args.Skip(2).ToArray();
            var shortcutTarget = args[1];
            if (File.Exists(shortcutTarget) && IsPclCeExecutable(shortcutTarget))
                return Path.GetFullPath(shortcutTarget);

            var shortcutDirectory = Path.GetDirectoryName(Path.GetFullPath(shortcutTarget));
            var siblingPclCe = shortcutDirectory is null ? null : FindPclCe(shortcutDirectory);
            if (siblingPclCe is not null) return siblingPclCe;

            if (allowInteraction)
            {
                GuidePclCeDownload("快捷方式记录的 PCL2 CE 路径不存在");
                guidanceShown = true;
            }

            return null;
        }

        // 快捷方式显式指定的目标也必须是 CE，不能退回或误启动原版。
        if (args.Length > 0 && LooksLikeExePath(args[0]))
        {
            passthrough = args.Skip(1).ToArray();
            if (File.Exists(args[0]))
            {
                if (!IsPclCeExecutable(args[0]))
                {
                    var originalDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]));
                    var siblingPclCe = originalDirectory is null ? null : FindPclCe(originalDirectory);
                    if (siblingPclCe is not null)
                    {
                        return siblingPclCe;
                    }

                    if (allowInteraction)
                    {
                        GuidePclCeDownload("传入的程序不是 PCL2 CE");
                        guidanceShown = true;
                    }
                    return null;
                }

                var explicitTarget = Path.GetFullPath(args[0]);
                return explicitTarget;
            }
        }

        var target = FindPclCe(baseDir);
        if (target is not null)
        {
            if (allowInteraction) NotifyCoLocatedPclVariants(target);
            return target;
        }

        if (FindOriginalPcl(baseDir).Count > 0)
        {
            if (allowInteraction)
            {
                GuidePclCeDownload("当前目录只检测到原版 PCL2");
                guidanceShown = true;
            }
            return null;
        }

        if (!allowInteraction) return null;

        var picked = PickTarget(baseDir);
        if (picked is null) return null;
        if (!IsPclCeExecutable(picked))
        {
            GuidePclCeDownload("所选程序不是 PCL2 CE");
            guidanceShown = true;
            return null;
        }

        var selectedTarget = Path.GetFullPath(picked);
        if (allowInteraction) NotifyCoLocatedPclVariants(selectedTarget);
        return selectedTarget;
    }

    private static bool IsPclCeExecutable(string path) =>
        Path.GetFileName(path).StartsWith("PCL2_CE", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    private static string? FindPclCe(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        foreach (var name in PreferredPclCeNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }

        try
        {
            return Directory.GetFiles(directory, "PCL2_CE*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(path => !string.Equals(
                    Path.GetFullPath(path),
                    Environment.ProcessPath,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static List<string> FindOriginalPcl(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        return OriginalPclNames
            .Select(name => Path.Combine(directory, name))
            .Where(File.Exists)
            .ToList();
    }

    private static void NotifyCoLocatedPclVariants(string target)
    {
        var directory = Path.GetDirectoryName(target);
        if (directory is null || FindOriginalPcl(directory).Count == 0) return;

        ShowInfo("检测到当前目录同时存在原版 PCL2 和 PCL2 CE。\n\n" +
                 "本项目只针对 PCL2 CE；本次启动和桌面快捷方式都会指向：\n" + target);
    }

    private static void GuidePclCeDownload(string reason)
    {
        var answer = ShowQuestion(
            $"{reason}。\n\n本项目只支持 PCL2 CE，不能启动原版 PCL2。\n" +
            "是否打开 PCL2 CE 官方 Releases 页面？下载并解压后，请将本工具放到 CE 程序所在目录再运行。",
            "需要 PCL2 CE");
        if (answer != 6) return;

        try
        {
            Process.Start(new ProcessStartInfo(PclCeReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("无法打开 PCL2 CE 官方下载页面：\n" + ex.Message + "\n\n" + PclCeReleasesUrl);
        }
    }

    private static bool LooksLikeExePath(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains('\\') || value.Contains('/') || value.Contains(':'));

    private static bool IsShortcutLaunch(string[] args) =>
        args.Length >= 2 &&
        string.Equals(args[0], ShortcutLaunchArgument, StringComparison.OrdinalIgnoreCase) &&
        LooksLikeExePath(args[1]);

    private static bool TryBringExistingPclToFront(string target)
    {
        var targetPath = Path.GetFullPath(target);
        var processName = Path.GetFileNameWithoutExtension(targetPath);

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || !IsSameExecutable(process, targetPath)) continue;

                try
                {
                    process.Refresh();
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero)
                    {
                        try { process.WaitForInputIdle(1500); } catch { }
                        process.Refresh();
                        window = process.MainWindowHandle;
                    }

                    if (window != IntPtr.Zero)
                    {
                        if (IsIconic(window)) ShowWindow(window, ShowWindowRestore);
                        BringWindowToTop(window);
                        SetForegroundWindow(window);
                        WriteLauncherLog($"existing PCL2 CE activated: pid={process.Id}, window=0x{window.ToInt64():X}");
                    }
                    else
                    {
                        WriteLauncherLog($"existing PCL2 CE found without a main window: pid={process.Id}");
                    }
                }
                catch (Exception ex)
                {
                    WriteLauncherLog($"activate existing PCL2 CE failed: pid={process.Id}, error={ex.Message}");
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsSameExecutable(Process process, string targetPath)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath) &&
                   string.Equals(Path.GetFullPath(processPath), targetPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            WriteLauncherLog($"read process path failed: pid={process.Id}, error={ex.Message}");
            return false;
        }
    }

    private static string? PickTarget(string baseDir)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "请选择 PCL2 CE 主程序",
            Filter = "PCL2 CE 启动器|PCL2_CE*.exe",
            InitialDirectory = Directory.Exists(baseDir) ? baseDir : GetDesktopDirectory(),
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCLRegionUnlock");

    // ---------- 桌面快捷方式 ----------

    private static string GetDesktopDirectory()
    {
        try
        {
            var id = DesktopFolderId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var buffer) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(buffer);
                    if (!string.IsNullOrWhiteSpace(path)) return path!;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
        }
        catch
        {
            // 退回 .NET 的实现。
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static bool EnsureDesktopShortcut(string target, string baseDir, bool dryRun = false)
    {
        try
        {
            var desktop = GetDesktopDirectory();
            if (string.IsNullOrWhiteSpace(desktop)) return false;

            var launcher = Environment.ProcessPath ?? Path.Combine(baseDir, "PCL_Unlock.exe");
            var shortcutPath = Path.Combine(desktop, ShortcutFileName + ".lnk");
            var arguments = ShortcutLaunchArgument + " \"" + target + "\"";

            if (File.Exists(shortcutPath) && ShortcutMatches(shortcutPath, launcher, arguments)) return false;

            if (dryRun) return true;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                WriteLauncherLog("WScript.Shell unavailable, shortcut skipped");
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = launcher;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(launcher) ?? baseDir;
            shortcut.IconLocation = target + ",0";
            shortcut.Description = "以日本设备区域启动 PCL2 CE（解除地区限制）";
            shortcut.Save();

            WriteLauncherLog($"shortcut written: {shortcutPath} -> {launcher} {arguments}");
            return true;
        }
        catch (Exception ex)
        {
            WriteLauncherLog("create shortcut failed: " + ex);
            return false;
        }
    }

    private static bool ShortcutMatches(string shortcutPath, string launcher, string arguments)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string existingTarget = shortcut.TargetPath;
            string existingArguments = shortcut.Arguments;
            return string.Equals(existingTarget, launcher, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(existingArguments, arguments, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---------- 启动钩子定位（内嵌 or 本地） ----------

    private static string? ResolveHookDll(string baseDir, out string source)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(baseDir, "Hook", HookDllName),
                     Path.Combine(baseDir, HookDllName)
                 })
        {
            if (File.Exists(path))
            {
                source = "local";
                return path;
            }
        }

        source = "embedded";
        return ExtractEmbeddedHook();
    }

    private static string? ExtractEmbeddedHook()
    {
        var targetDir = Path.Combine(StateDirectory, "Hook");
        string? hookPath = null;
        try
        {
            Directory.CreateDirectory(targetDir);

            foreach (var name in new[] { HookDllName, "0Harmony.dll" })
            {
                using var resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("PclRegionUnlock.Hook." + name);
                if (resource is null)
                {
                    WriteLauncherLog($"embedded resource missing: {name}");
                    return null;
                }

                var path = Path.Combine(targetDir, name);
                var needWrite = !File.Exists(path) || new FileInfo(path).Length != resource.Length;
                if (needWrite)
                {
                    var temp = path + ".tmp";
                    using (var output = File.Create(temp)) resource.CopyTo(output);
                    File.Move(temp, path, true);
                }

                if (name == HookDllName) hookPath = path;
            }

            return hookPath;
        }
        catch (Exception ex)
        {
            WriteLauncherLog("extract hook failed: " + ex);
            return null;
        }
    }

    // ---------- 杂项 ----------

    private static void WriteLauncherLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "PCLRegionUnlock.launcher.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 忽略日志失败。
        }
    }

    private static void ShowError(string message)
    {
        SystemSounds.Hand.Play();
        MsgBox(message, Caption, 0x00000010 /* MB_ICONERROR */);
    }

    private static void ShowInfo(string message) =>
        MsgBox(message, Caption, 0x00000040 /* MB_ICONINFORMATION */);

    private static int ShowQuestion(string message, string caption) =>
        MsgBox(message, caption, 0x00000004 | 0x00000020 /* MB_YESNO | MB_ICONQUESTION */);

    private static int MsgBox(string message, string caption, uint type) =>
        MessageBoxW(IntPtr.Zero, message, caption, type | 0x00040000 /* MB_TOPMOST */);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
