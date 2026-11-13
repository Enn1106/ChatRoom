namespace ChatServer;

/// <summary>Ghi log ra console (an toàn khi nhiều task cùng ghi).</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message, ConsoleColor.Gray);
    public static void Warn(string message) => Write("WARN ", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color)
    {
        lock (Gate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {level} {message}");
            Console.ForegroundColor = old;
        }
    }
}
