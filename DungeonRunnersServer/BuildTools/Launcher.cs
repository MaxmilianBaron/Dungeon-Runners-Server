using System;
using System.Diagnostics;
using System.IO;

static class Launcher
{
    static int Main()
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string project = Path.Combine(root, "DungeonRunnersServer.csproj");
        string outDir = Path.Combine(root, "BuildTools", "_out", "server");

        int rc = Run(root, "dotnet", "publish \"" + project + "\" -c Release -r win-x64 --self-contained false -p:UseSharedCompilation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:AssemblyName=Server -o \"" + outDir + "\" -nologo");
        if (rc != 0)
        {
            Console.WriteLine("Build failed (exit " + rc + ").");
            Pause();
            return rc;
        }

        try
        {
            File.Copy(Path.Combine(outDir, "Server.exe"), Path.Combine(root, "Server.exe"), true);
            Console.WriteLine("Server.exe rebuilt in \"" + root + "\".");
        }
        catch (Exception e)
        {
            Console.WriteLine("Could not replace Server.exe: " + e.Message);
            Console.WriteLine("Stop a running Server.exe first, then rebuild.");
            rc = 1;
        }
        Pause();
        return rc;
    }

    static int Run(string workingDir, string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, WorkingDirectory = workingDir };
        using var process = Process.Start(psi);
        process.WaitForExit();
        return process.ExitCode;
    }

    static void Pause()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine("Press any key to close...");
        Console.ReadKey(true);
    }
}
