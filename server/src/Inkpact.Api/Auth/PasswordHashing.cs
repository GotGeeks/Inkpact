using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Geralt;

namespace Inkpact.Api.Auth;

public interface IPasswordHasher
{
    /// <summary>Returns the encoded Argon2id hash ("$argon2id$v=19$m=...").</summary>
    string Hash(string password);

    bool Verify(string hash, string password);

    /// <summary>True when the hash was made with weaker parameters than the current ones.</summary>
    bool NeedsRehash(string hash);

    /// <summary>
    /// Burns the same time as a real verification. Called when the account does not exist or has no password,
    /// so the response time does not reveal which emails are registered.
    /// </summary>
    void VerifyDummy(string password);
}

/// <summary>
/// Argon2id with the first option of the OWASP password storage cheat sheet: 46 MiB of memory, one iteration,
/// one lane. libsodium (through Geralt) does the work.
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    public const int Iterations = 1;
    public const int MemoryBytes = 47_104 * 1024;

    // At most this many hashes at once, so a burst of logins cannot take gigabytes of memory.
    // Login rate limiting arrives in run 1.06.
    private const int MaxConcurrent = 4;

    private readonly SemaphoreSlim _concurrency = new(MaxConcurrent, MaxConcurrent);
    private readonly Lazy<string> _dummyHash;

    public Argon2PasswordHasher()
    {
        _dummyHash = new Lazy<string>(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));
    }

    public string Hash(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        Span<char> hash = stackalloc char[Argon2id.HashSize];
        _concurrency.Wait();
        try
        {
            Argon2id.ComputeHash(hash, passwordBytes, Iterations, MemoryBytes);
        }
        finally
        {
            _concurrency.Release();
            CryptographicOperations.ZeroMemory(passwordBytes);
        }

        return hash[..hash.IndexOf('\0')].ToString();
    }

    public bool Verify(string hash, string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        _concurrency.Wait();
        try
        {
            return Argon2id.VerifyHash(hash, passwordBytes);
        }
        finally
        {
            _concurrency.Release();
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public bool NeedsRehash(string hash) => Argon2id.NeedsRehash(hash, Iterations, MemoryBytes);

    public void VerifyDummy(string password) => Verify(_dummyHash.Value, password);
}

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <summary>
    /// Length only, no composition rules (NIST 800-63B). Returns an error code, or null when the password is fine.
    /// Length counts characters, not bytes, so a passphrase in any language gets the same allowance.
    /// </summary>
    public static string? Validate(string password, string email)
    {
        var length = new StringInfo(password).LengthInTextElements;
        if (length < MinLength)
        {
            return "password_too_short";
        }

        if (length > MaxLength)
        {
            return "password_too_long";
        }

        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "password_same_as_email";
        }

        return null;
    }
}
