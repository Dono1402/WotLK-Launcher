using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WotLK.Launcher.Updater;

// A headless, disposable requester/helper/child. Links the actual updater code;
// never starts, replaces, stops or reads settings from an installed launcher.
internal static class Program
{
    private record Identity(string Sid, int Session, bool Elevated);

    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string mode = args[0];
        string job = Path.GetFullPath(args[1]);
        try
        {
            switch (mode)
            {
                case "coordinate":
                    if (ReadIdentity().Elevated) throw new InvalidOperationException("Start the coordinator unelevated.");
                    if (Directory.Exists(job)) throw new InvalidOperationException("Use a new result directory.");
                    Directory.CreateDirectory(job);
                    using (Process requester = Start("requester", job, elevated: false))
                    {
                        WaitForFile(Path.Combine(job, "requester.json"));
                        using Process helper = Start("helper", job, elevated: true);
                        if (!helper.WaitForExit(120000)) throw new TimeoutException("Helper did not finish.");
                        return helper.ExitCode;
                    }
                case "requester":
                    Write(job, "requester", new { pid = Environment.ProcessId, identity = ReadIdentity() });
                    WaitForFile(Path.Combine(job, "captured.json"));
                    return 0;
                case "child":
                    Write(job, "child", ReadIdentity());
                    return 0;
                case "helper":
                    if (!ReadIdentity().Elevated) throw new InvalidOperationException("Helper must be elevated.");
                    using (JsonDocument request = JsonDocument.Parse(File.ReadAllText(Path.Combine(job, "requester.json"))))
                    {
                        int pid = request.RootElement.GetProperty("pid").GetInt32();
                        Identity expected = request.RootElement.GetProperty("identity").Deserialize<Identity>()!;
                        using Process parent = Process.GetProcessById(pid);
                        using LauncherUpdateRequesterImpersonation captured = LauncherUpdateRequesterImpersonation.Capture(pid);
                        captured.DemandMatchesRequester(pid, Environment.ProcessPath!);
                        Write(job, "captured", new { captured = true });
                        if (!parent.WaitForExit(15000)) throw new TimeoutException("Requester did not exit.");
                        captured.LaunchProcess(Environment.ProcessPath!, "child \"" + job + "\"", AppContext.BaseDirectory);
                        WaitForFile(Path.Combine(job, "child.json"));
                        Identity actual = JsonSerializer.Deserialize<Identity>(File.ReadAllText(Path.Combine(job, "child.json")))!;
                        if (actual != expected || actual.Elevated)
                            throw new InvalidOperationException("Child identity, session or elevation differs from requester.");
                        Write(job, "report", new { passed = true, requesterExited = parent.HasExited, sameUser = true, sameSession = true, childElevated = actual.Elevated });
                        return 0;
                    }
                default:
                    throw new InvalidOperationException("Unknown probe mode.");
            }
        }
        catch (Exception ex)
        {
            if (Directory.Exists(job)) Write(job, mode + "-error", new
            {
                passed = false, type = ex.GetType().Name,
                nativeCode = (ex as Win32Exception)?.NativeErrorCode, message = ex.Message
            });
            return 1;
        }
    }

    private static Identity ReadIdentity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using Process process = Process.GetCurrentProcess();
        return new(identity.User!.Value, process.SessionId,
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
    }

    private static Process Start(string mode, string job, bool elevated) => Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!, Arguments = mode + " \"" + job + "\"",
        UseShellExecute = elevated, Verb = elevated ? "runas" : "",
        CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
    }) ?? throw new InvalidOperationException("Probe process did not start.");

    private static void WaitForFile(string path)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("Missing probe signal.");
            Thread.Sleep(100);
        }
    }

    private static void Write(string job, string name, object data)
    {
        string destination = Path.Combine(job, name + ".json");
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination);
    }
}

namespace WotLK.Launcher
{
    internal static class LauncherBuildFlavor
    {
        internal const string SettingsDirectoryName = "Atlas Update Token Probe";
    }
}
