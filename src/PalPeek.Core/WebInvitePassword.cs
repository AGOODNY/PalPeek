using System.Security.Cryptography;

namespace PalPeek.Core;

public static class WebInvitePassword
{
    public const int Iterations = 600_000;
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public static (string Salt, string Hash) Hash(string password)
    {
        Validate(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashLength);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(WebInvite invite, string password)
    {
        if (password.Length is < MinimumLength or > MaximumLength)
            return false;

        try
        {
            var salt = Convert.FromBase64String(invite.PasswordSalt);
            var expected = Convert.FromBase64String(invite.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                invite.PasswordIterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string CreateInviteId() =>
        Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string CreateSessionId() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    public static void Validate(string password)
    {
        if (password.Length is < MinimumLength or > MaximumLength)
            throw new ArgumentException("口令长度必须为 8–128 个字符。", nameof(password));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
