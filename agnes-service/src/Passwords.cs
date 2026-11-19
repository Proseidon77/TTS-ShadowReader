using System.Security.Cryptography;
using System.Text;

namespace AgnesService;

/// <summary>
/// dbapi-compatible password hashes: <c>pbkdf2-sha512$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c>,
/// 16-byte salt, 64-byte key, 210 000 iterations. Same as the Python round (python-legacy/auth.py).
/// Verification also accepts url-safe / unpadded base64, since dbapi's exact b64 flavour could not be
/// confirmed from its source.
/// </summary>
public static class Passwords
{
    public const string Scheme = "pbkdf2-sha512";
    public const int Iterations = 210_000;
    public const int SaltLen = 16;
    public const int HashLen = 64;
    public const int MinLength = 10;
    public const int MaxLength = 1024;

    public static string Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var dk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA512, HashLen);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(dk)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], out var iters) || iters < 1000 || iters > 5_000_000) return false;
        var salt = B64(parts[2]);
        var expected = B64(parts[3]);
        if (salt == null || expected == null || expected.Length == 0) return false;
        var dk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iters,
            HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(dk, expected);
    }

    private static byte[]? B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    private static readonly Lazy<string> DummyHash = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
    /// <summary>Spend the same PBKDF2 time on unknown usernames (no enumeration by timing).</summary>
    public static void BurnTime(string password) => Verify(password, DummyHash.Value);

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password12", "password123", "password1!", "passw0rd12", "1234567890", "0123456789", "12345678910",
        "qwertyuiop", "qwerty1234", "qwerty12345", "1q2w3e4r5t", "1qaz2wsx3edc", "abc1234567", "abcd123456",
        "admin12345", "admin123456", "iloveyou12", "letmein123", "welcome123", "a123456789", "11111111111",
    };

    /// <summary>
    /// Password-strength rule (dbapi-style <c>weak_password</c>), shared by the admin API, change-password
    /// and the CLI: 10–1024 characters; at least 4 distinct characters; at least two character classes out
    /// of {ASCII lowercase, ASCII uppercase, digits, anything else (symbols, spaces, CJK…)}; must not contain
    /// the username (case-insensitive); not on a small list of very common passwords. Returns null when OK.
    /// </summary>
    public static string? PolicyProblem(string password, string? username = null)
    {
        if (password.Length < MinLength) return $"password must be at least {MinLength} characters";
        if (password.Length > MaxLength) return "password too long";
        if (password.Distinct().Count() < 4) return "password is too simple (needs at least 4 different characters)";
        var classes = (password.Any(char.IsAsciiLetterLower) ? 1 : 0) + (password.Any(char.IsAsciiLetterUpper) ? 1 : 0)
                    + (password.Any(char.IsAsciiDigit) ? 1 : 0) + (password.Any(ch => !char.IsAsciiLetterOrDigit(ch)) ? 1 : 0);
        if (classes < 2) return "password must mix at least two of: lowercase letters, uppercase letters, digits, other characters";
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            return "password must not contain the username";
        if (Common.Contains(password)) return "password is too common";
        return null;
    }
}
