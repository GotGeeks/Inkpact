namespace Inkpact.Data.Entities;

/// <summary>
/// Failed logins for one email address, whether or not an account exists for it. The key is a hash of the
/// normalized address, so addresses that strangers typed are not kept here.
/// </summary>
public class LoginThrottle
{
    /// <summary>SHA-256 of the normalized email, in hex.</summary>
    public string Key { get; set; } = "";

    public int Failures { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset LastFailureAt { get; set; }
}
