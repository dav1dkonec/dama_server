namespace dama_klient_app.Services;

/// <summary>
/// Jednoduchý wrapper na služby používaný napříč aplikací.
/// </summary>
public static class AppServices
{
    public static IGameClient GameClient { get; private set; } = new GameClient();
    public static ILogger Logger { get; private set; } = new ConsoleLogger();

    public static void Initialize(IGameClient client, ILogger? logger = null)
    {
        GameClient = client;
        if (logger != null)
        {
            Logger = logger;
        }
    }
}
