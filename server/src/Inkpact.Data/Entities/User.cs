namespace Inkpact.Data.Entities;

public class User
{
    private string _email = "";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The address as the user typed it. Setting it also sets <see cref="NormalizedEmail"/>.</summary>
    public string Email
    {
        get => _email;
        set
        {
            _email = value.Trim();
            NormalizedEmail = _email.ToLowerInvariant();
        }
    }

    /// <summary>Lower-case address with a unique index, so logins are case-insensitive.</summary>
    public string NormalizedEmail { get; private set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Argon2id hash. Null for accounts that only sign in through OIDC.</summary>
    public string? PasswordHash { get; set; }

    public string? OidcSubject { get; set; }

    /// <summary>The account created at first-run setup. Admin pages arrive in phase 2.</summary>
    public bool IsAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
