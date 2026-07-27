using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace BrainFuel.Services;

/// <summary>
/// Single-instance coordination. The first running instance owns a named pipe
/// server; a second launch connects to it, sends "show", and exits. On receiving
/// "show" the running instance brings its window to the foreground.
/// </summary>
internal static class SingleInstanceActivation
{
    private const string PipeName = "BrainFuel.App.SingleInstance";

    /// <summary>Called by a second launch: poke the already-running instance, then return.</summary>
    public static void ActivateRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500); // best-effort; if it times out we just exit silently
            using var writer = new StreamWriter(client);
            writer.WriteLine("show");
            writer.Flush();
        }
        catch
        {
            // If we can't reach the running instance there's nothing useful to do;
            // exit silently rather than spawning a duplicate window.
        }
    }

    /// <summary>Called once by the first instance to start listening for activation pokes.</summary>
    public static async Task RunServerAsync(Action onShow, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                await server.WaitForConnectionAsync(ct);

                using (server)
                using (var reader = new StreamReader(server))
                {
                    var line = reader.ReadLine();
                    if (line == "show")
                        onShow();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe error: dispose and accept the next connection.
                try { server?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
