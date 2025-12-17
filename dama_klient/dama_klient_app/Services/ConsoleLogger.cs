using System;

namespace dama_klient_app.Services;

/// <summary>
/// Simple console logger for diagnostics.
/// </summary>
public class ConsoleLogger : ILogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");

    public void Error(string message) => Console.WriteLine($"[ERROR] {message}");
}
