using System.Security.Cryptography;

namespace MtgEngine.Api.Services;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2)
            return false;
        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expectedHash = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            // A corrupt or sentinel hash (the seeder writes "SEEDED_NO_LOGIN") is a
            // failed login, not a 500.
            return false;
        }
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    /// <summary>
    /// Burns the same PBKDF2 cost as a real verification and always fails. Login calls
    /// this when the account does not exist, so response timing no longer separates
    /// "wrong password" from "no such user".
    /// </summary>
    public static void VerifyDummy(string password)
    {
        Rfc2898DeriveBytes.Pbkdf2(password, DummySalt, Iterations, Algorithm, HashSize);
    }

    private static readonly byte[] DummySalt = new byte[SaltSize];
}
