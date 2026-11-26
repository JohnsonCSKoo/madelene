namespace Madelene.Storage;

/// <summary>
/// Retries file-system operations that fail transiently on Windows when another process (antivirus,
/// search indexer, backup agent) briefly opens freshly written files.
/// </summary>
internal static class FileRetry
{
    public static void Run(Action action, int attempts = 40)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(System.Math.Min(25 * attempt, 250));
            }
        }
    }
}
