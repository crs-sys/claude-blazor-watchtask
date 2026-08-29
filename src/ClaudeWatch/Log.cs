namespace ClaudeWatch;

public static class Log
{
    private static readonly Lock ConsoleLock = new();

    public static void Info(string message) => Write(message, null);
    public static void Detail(string message) => Write("  " + message, ConsoleColor.DarkGray);
    public static void Success(string message) => Write(message, ConsoleColor.Green);
    public static void Warn(string message) => Write(message, ConsoleColor.Yellow);
    public static void Error(string message) => Write(message, ConsoleColor.Red);

    public static void App(string line)
    {
        lock (ConsoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  │ " + line);
            Console.ResetColor();
        }
    }

    private static void Write(string message, ConsoleColor? color)
    {
        lock (ConsoleLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ResetColor();
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}
