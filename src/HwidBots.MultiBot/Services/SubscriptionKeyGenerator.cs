namespace HwidBots.Shared.Services;

public static class SubscriptionKeyGenerator
{
    // Alphabet without confusing characters (0/O, 1/I/l)
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string Prefix = "SENTINEL";

    /// <summary>
    /// Generates a beautiful subscription key in format: SENTINEL-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX
    /// Example: SENTINEL-A7K9-X2M4-Q8N6-P3H5-R9W2-T5Y7
    /// </summary>
    public static string Generate()
    {
        // Generate 6 segments of 4 characters each
        var segments = new string[6];

        for (int segment = 0; segment < 6; segment++)
        {
            Span<char> buffer = stackalloc char[4];
            Span<byte> randomBytes = stackalloc byte[4];

            RandomNumberGenerator.Fill(randomBytes);

            for (var i = 0; i < 4; i++)
            {
                buffer[i] = Alphabet[randomBytes[i] % Alphabet.Length];
            }

            segments[segment] = new string(buffer);
        }

        return $"{Prefix}-{segments[0]}-{segments[1]}-{segments[2]}-{segments[3]}-{segments[4]}-{segments[5]}";
    }

    /// <summary>
    /// Legacy method for backward compatibility
    /// </summary>
    public static string Generate(int length)
    {
        return Generate();
    }
}
