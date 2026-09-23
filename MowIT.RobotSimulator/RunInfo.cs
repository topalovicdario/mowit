using System.Diagnostics;
using System.Text;

namespace MowIT.RobotSimulator;

internal static class RunInfo
{
    public static void Write(string resultsDir, string mode, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        string slug = new string(mode.TakeWhile(c => c != ' ').ToArray());
        string path = Path.Combine(resultsDir, $"run_info_{slug}.txt");
        using var w = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        w.WriteLine($"# MowIT.RobotSimulator - {mode}");
        w.WriteLine($"datum_vrijeme_utc: {DateTime.UtcNow:O}");
        w.WriteLine($"dotnet_verzija: {Environment.Version}");
        w.WriteLine($"git_commit: {GitCommitHash()}");
        foreach (var kv in parameters)
            w.WriteLine($"{kv.Key}: {kv.Value}");
    }

    private static string GitCommitHash()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory       = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi);
            if (p is null) return "unknown";

            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return p.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
