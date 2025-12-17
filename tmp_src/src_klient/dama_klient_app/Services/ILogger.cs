namespace dama_klient_app.Services;

/// <summary>
/// Minimal logging abstraction for the client.
/// </summary>
public interface ILogger
{
    void Info(string message);
    void Error(string message);
}
