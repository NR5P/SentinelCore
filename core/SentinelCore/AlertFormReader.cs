namespace SentinelCore;

// Reading a multipart body can fail for reasons that are entirely the sender's, and on this
// service the sender is a camera or a Blue Iris instance on the far side of a home network: a
// dropped connection, a flaky access point, or a service restarting mid-post all leave a body
// that simply stops halfway.
//
// Both upload routes already had a considered 400 for a request with no usable image in it, and
// neither could reach it - ReadFormAsync threw first, so the caller got a 500 and this service
// logged an unhandled exception, for something that is not a fault on this side at all.
public static class AlertFormReader
{
    public const string MalformedBodyMessage =
        "The multipart body could not be read - it looks truncated or malformed.";

    // Null means the body was unreadable; every caller answers that with MalformedBodyMessage.
    public static async Task<IFormCollection?> TryReadAsync(
        HttpRequest request, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Debug rather than Warning: a truncated upload says nothing about this server's
            // health, and one flapping sender would otherwise fill the journal with a line
            // nobody can act on. The 400 is what tells the sender.
            logger.LogDebug(ex, "Rejected an upload whose multipart body could not be read");
            return null;
        }
    }
}
