using System.Diagnostics;
using System.Text.Json;

namespace UnchartedOptions.Alpaca;

/// <summary>Raised when the CLI is missing, times out, or reports a structured error.</summary>
public sealed class AlpacaCliException : Exception
{
    public AlpacaCliException(string message, string? hint = null)
        : base(string.IsNullOrWhiteSpace(hint) ? message : $"{message} (hint: {hint})")
    {
    }
}

/// <summary>
/// Runs the Alpaca CLI and returns parsed JSON.
/// </summary>
/// <remarks>
/// <para>
/// Every argument goes through <see cref="ProcessStartInfo.ArgumentList"/>, which hands each
/// one to the child process as a discrete string with no shell in between. This matters for
/// the <c>--legs</c> JSON array: passing it through PowerShell requires escaping every
/// interior quote, and getting it wrong yields the misleading error
/// <c>invalid character 's' looking for beginning of object key string</c>. The agent never
/// touches a shell, so it never has that problem -- but the hand-tested path and the agent
/// path therefore differ, which is why the agent path has its own test.
/// </para>
/// <para>
/// <c>--quiet</c> is appended to every call so stdout carries JSON and nothing else.
/// </para>
/// </remarks>
public sealed class CliRunner
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly string? _profile;

    /// <param name="profile">
    /// Alpaca CLI profile name, passed as <c>-p</c>. Every call is pinned to it explicitly
    /// rather than relying on whichever profile happens to be active, so switching the
    /// active profile at a terminal cannot silently redirect the agent's orders.
    /// </param>
    public CliRunner(string executable = "alpaca", TimeSpan? timeout = null, string? profile = null)
    {
        _executable = executable;
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
        _profile = profile;
    }

    public async Task<JsonDocument> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        ProcessStartInfo psi = new()
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add("--quiet");

        if (!string.IsNullOrWhiteSpace(_profile))
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(_profile);
        }

        using Process process = new() { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new AlpacaCliException(
                $"Could not start '{_executable}'. Is the Alpaca CLI installed and on PATH? ({ex.Message})");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new AlpacaCliException($"Alpaca CLI timed out after {_timeout.TotalSeconds:F0}s.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new AlpacaCliException(
                $"Alpaca CLI returned no output (exit {process.ExitCode}). {stderr.Trim()}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new AlpacaCliException($"Alpaca CLI returned unparseable output: {ex.Message}");
        }

        // Failures come back as a JSON object carrying a non-empty "error", not a non-zero
        // exit code -- e.g. {"code":0,"error":"--underlying-symbol required","status":0}.
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            string message = error.GetString() ?? "unknown error";
            string? hint = doc.RootElement.TryGetProperty("hint", out JsonElement h) ? h.GetString() : null;
            doc.Dispose();
            throw new AlpacaCliException(message, hint);
        }

        return doc;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
