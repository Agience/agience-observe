using System.Diagnostics;
using System.Text;

namespace Agience.Core;

/// <summary>What one command did.</summary>
/// <param name="ExitCode">-1 when the command could not be started at all.</param>
/// <param name="Output">stdout and stderr, interleaved in the order they arrived.</param>
public sealed record ShellResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Runs one command to completion and reports what it said.
/// </summary>
/// <remarks>
/// <para>
/// For setup only, never for a service. A service is a long-lived process with a port, a log
/// and a stop verb; that is <see cref="ServiceProcess"/>. This is for the short commands that build
/// the environment: <c>python -m venv</c>, <c>pip install</c>, <c>python --version</c>.
/// </para>
/// <para>
/// stdout and stderr, interleaved, because pip puts the answer in both. Resolution progress goes
/// to stdout and the failure that explains it goes to stderr, so a caller reading one of them gets
/// a transcript that stops just before the reason.
/// </para>
/// </remarks>
public static class Shell
{
    /// <summary>
    /// Run <paramref name="exe"/> and wait.
    /// </summary>
    /// <param name="progress">Each output line as it arrives, for a window that shows progress.</param>
    /// <param name="timeout">Null for the default of ten minutes — a cold pip install is slow.</param>
    public static async Task<ShellResult> RunAsync(
        string exe,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IProgress<string>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // pip is told it has no terminal. Left to guess it emits carriage-return progress bars,
        // which arrive here as one enormous line and render in a text box as a single unreadable
        // smear of every percentage it ever printed.
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PIP_NO_INPUT"] = "1";
        psi.Environment["PIP_PROGRESS_BAR"] = "off";
        psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";

        var output = new StringBuilder();

        void Take(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }

            progress?.Report(line);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new ShellResult(-1, $"could not start {exe}");
            }

            process.OutputDataReceived += (_, e) => Take(e.Data);
            process.ErrorDataReceived += (_, e) => Take(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));

            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone.
                }

                Take(ct.IsCancellationRequested ? "--- cancelled ---" : "--- timed out ---");
                lock (output)
                {
                    return new ShellResult(-1, output.ToString());
                }
            }

            lock (output)
            {
                return new ShellResult(process.ExitCode, output.ToString());
            }
        }
        catch (Exception exc)
        {
            return new ShellResult(-1, $"{exc.Message}{Environment.NewLine}{output}");
        }
    }
}
