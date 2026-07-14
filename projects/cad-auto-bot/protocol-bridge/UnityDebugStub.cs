namespace UnityEngine;

public static class Debug
{
    public static void LogError(object? message)
    {
        Console.Error.WriteLine(message);
    }
}
