using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using WinWaechter.Core.Logging;

namespace WinWaechter.App.Services;

public enum StartupMode { Normal, Check, Improve }

/// <summary>Command line: „--pruefen“ (weekly check, headless) or „winwaechter:verbessern“ (click on the notification).</summary>
public static class StartupArgs
{
    public const string Check = "--pruefen";

    public static StartupMode Parse(string[] args)
    {
        foreach (var a in args)
        {
            if (a.Equals(Check, StringComparison.OrdinalIgnoreCase)) return StartupMode.Check;
            if (a.StartsWith("winwaechter:", StringComparison.OrdinalIgnoreCase))
            {
                var verb = a["winwaechter:".Length..].Trim('/');
                if (verb.Length == 0 || verb.Equals(ActivationPipe.Verb, StringComparison.OrdinalIgnoreCase)) return StartupMode.Improve;
            }
            FileLog.Info($"Unbekanntes Startargument ignoriert: {a}");
        }
        return StartupMode.Normal;
    }
}

/// <summary>
/// A second start (e.g. the notification's „Ansehen“) hands its wish to the running window and exits.
/// The pipe is open to the current user only; anything but the one known verb is dropped.
/// </summary>
public static class ActivationPipe
{
    public const string Verb = "verbessern";

    public static string NameFor(string? userSid) => "WinWaechter.Activate." + (userSid ?? "user");

    public static bool IsValidVerb(string? v) => v == Verb;

    /// <summary>True when a listener took the message.</summary>
    public static bool Send(string name, string verb)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(1000);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(verb);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens until disposed; onVerb runs on a pool thread (dispatch to the UI yourself).</summary>
    public static IDisposable Listen(string name, Action<string> onVerb)
    {
        var cts = new CancellationTokenSource();
        var security = new PipeSecurity();
        using (var me = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, security);
                    await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                    using var r = new StreamReader(server);
                    var line = (await r.ReadLineAsync(cts.Token).ConfigureAwait(false))?.Trim();
                    if (IsValidVerb(line)) onVerb(line!);
                    else FileLog.Warn($"Aktivierung mit unbekanntem Wunsch verworfen: {line}");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileLog.Warn("Aktivierung: Pipe-Fehler", ex);
                    try { await Task.Delay(500, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
        });
        return new Stop(cts);
    }

    sealed class Stop(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
