using System.Text.Json;

namespace ClaudeWatch;

public sealed class ConsoleUi(Pipeline pipeline, TriggerServer server, CancellationTokenSource shutdown)
{
    public void RunKeyLoop()
    {
        while (!shutdown.IsCancellationRequested)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return; } // no interactive console (redirected)

            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'R':
                    Log.Info("manual rebuild requested");
                    pipeline.Post(new Trigger(TriggerKind.Manual));
                    break;
                case 'S':
                    Console.WriteLine(JsonSerializer.Serialize(server.BuildStatus(),
                        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                    break;
                case 'C':
                    Console.Clear();
                    break;
                case 'Q':
                    Log.Info("shutting down...");
                    shutdown.Cancel();
                    return;
            }
        }
    }
}
