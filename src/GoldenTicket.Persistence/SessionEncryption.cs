using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace GoldenTicket.Persistence;

/// <summary>
/// Protects the parts of a save that must not be readable from a copied database file: deck
/// permutations, hands, private offers and the random continuation (DESIGN 19.2).
///
/// This prevents casual reading of a copied database. It does not protect secrets from the running
/// Windows account or a process debugger, and DESIGN 19.2 is explicit that pass-and-hide must not be
/// sold as adversarial security.
/// </summary>
public sealed class SessionEncryption
{
    /// <summary>Format marker stored with every ciphertext so the scheme can be versioned.</summary>
    public const int FormatVersion = 1;

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("GoldenTicket.SessionDataKey.v1");

    private readonly byte[] _key;

    private SessionEncryption(byte[] key) => _key = key;

    /// <summary>Creates a fresh per-session data key and the DPAPI-protected form to store.</summary>
    [SupportedOSPlatform("windows")]
    public static (SessionEncryption Encryption, byte[] ProtectedKey) CreateForNewSession()
    {
        RequireWindows();

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var protectedKey = System.Security.Cryptography.ProtectedData.Protect(
            key, DpapiEntropy, DataProtectionScope.CurrentUser);

        return (new SessionEncryption(key), protectedKey);
    }

    /// <summary>Recovers the data key for the current Windows user.</summary>
    [SupportedOSPlatform("windows")]
    public static SessionEncryption Open(byte[] protectedKey)
    {
        RequireWindows();

        var key = System.Security.Cryptography.ProtectedData.Unprotect(
            protectedKey, DpapiEntropy, DataProtectionScope.CurrentUser);

        if (key.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The saved data key has an unsupported length.");
        }

        return new SessionEncryption(key);
    }

    /// <summary>Encrypts with AES-GCM. The nonce is stored beside the ciphertext.</summary>
    public byte[] Encrypt(byte[] plaintext, out byte[] nonce)
    {
        nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[ciphertext.Length + TagSize];
        ciphertext.CopyTo(result, 0);
        tag.CopyTo(result, ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(byte[] payload, byte[] nonce)
    {
        if (payload.Length < TagSize || nonce.Length != NonceSize)
            throw new CryptographicException("The stored payload or nonce has an invalid length.");

        var ciphertext = payload.AsSpan(0, payload.Length - TagSize);
        var tag = payload.AsSpan(payload.Length - TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "GoldenTicket protects its saves with Windows DPAPI for the current user. " +
                "The application targets Windows 11 (DESIGN 17.1).");
        }
    }
}
