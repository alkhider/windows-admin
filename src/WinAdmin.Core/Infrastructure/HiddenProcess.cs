using System.Diagnostics;
using System.Text;

namespace WinAdmin.Core.Infrastructure;

public static class HiddenProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        int timeoutMs = 180_000)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName.Trim().Trim('"'),
                Arguments = arguments ?? "",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };
            psi.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

            var directory = Path.GetDirectoryName(psi.FileName);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                psi.WorkingDirectory = directory;
            }

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var output = new StringBuilder();
            var error = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };

            if (!process.Start())
            {
                return (-1, "", $"Could not start {psi.FileName}.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (-1, "", "The task timed out.");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return (-1, "", ex.Message);
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }
}

public static class StaRunner
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
