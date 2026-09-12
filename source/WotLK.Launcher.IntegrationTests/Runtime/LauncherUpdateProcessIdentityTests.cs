using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WotLK.Launcher.Updater;

internal static class LauncherUpdateProcessIdentityTests
{
    internal static async Task<int> RunAsync()
    {
        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
                 {
                     "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                     "[Console]::WriteLine('ready'); [void][Console]::ReadLine()"
                 })
            start.ArgumentList.Add(argument);

        using Process child = Process.Start(start)
            ?? throw new InvalidOperationException("Processus de test absent.");
        try
        {
            Require(await child.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15)) == "ready", "Le processus de test doit être prêt.");
            DateTimeOffset startedAt = new(child.StartTime.ToUniversalTime());
            Require(LauncherUpdateParentWaiter.ProcessMatchesPath(child.Id, executable),
                "L'identité normale doit être reconnue avant de restreindre la lecture mémoire.");

            // Restrict only this disposable child. Like an elevated helper, it permits
            // limited process queries but denies the VM_READ used by .NET 8 MainModule.
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User!.Value;
            RawSecurityDescriptor descriptor = new(
                $"D:P(D;;0x0010;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{sid})");
            byte[] binary = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(binary, 0);
            SafeProcessHandle retainedHandle = child.SafeHandle;
            if (!SetKernelObjectSecurity(retainedHandle, 0x00000004, binary))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            bool legacyAccessDenied = false;
            using (Process probe = Process.GetProcessById(child.Id))
            {
                try { _ = probe.MainModule?.FileName; }
                catch (Win32Exception error) when (error.NativeErrorCode == 5)
                {
                    legacyAccessDenied = true;
                }
            }
            Require(legacyAccessDenied,
                "Le test doit reproduire le refus de lecture mémoire de l'ancien contrôle.");
            Require(LauncherUpdateParentWaiter.ProcessMatchesPath(child.Id, executable),
                "Le helper doit rester identifiable quand Windows refuse la lecture de sa mémoire.");
            Require(LauncherUpdateParentWaiter.ProcessMatchesIdentity(child.Id, executable, startedAt),
                "La date de démarrage doit rester vérifiée avec les droits limités.");
            Require(!LauncherUpdateParentWaiter.ProcessMatchesPath(child.Id, executable + ".wrong"),
                "Un autre chemin ne doit jamais être accepté.");
            Require(!LauncherUpdateParentWaiter.ProcessMatchesIdentity(
                child.Id, executable, startedAt.AddMinutes(-1)),
                "Un PID associé à une ancienne date doit être refusé.");
            Require(!LauncherUpdateParentWaiter.ProcessMatchesPath(-1, executable),
                "Un PID invalide doit être refusé.");
            Require(!LauncherUpdateParentWaiter.ProcessMatchesPath(int.MaxValue, executable),
                "Un processus absent doit être refusé.");

            await child.StandardInput.WriteLineAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(!LauncherUpdateParentWaiter.ProcessMatchesPath(child.Id, executable),
                "Un processus terminé doit être refusé.");
            Console.WriteLine("Launcher update process identity OK: VM_READ denied; limited query, path, start time and exited PID verified.");
            return 0;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeProcessHandle handle, uint securityInformation, byte[] securityDescriptor);
}
