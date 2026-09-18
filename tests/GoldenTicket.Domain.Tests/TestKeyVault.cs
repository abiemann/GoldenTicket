using System.Security.Cryptography;

namespace GoldenTicket.Domain.Tests;

/// <summary>
/// Stores test-only certificate keys in memory and writes opaque tokens to the fixture directory.
/// Certificate behavior can then be tested without calling Windows data protection.
/// </summary>
internal sealed class TestKeyVault : IDisposable
{
    private readonly Dictionary<string, byte[]> _keys = [];

    public byte[] Protect(byte[] plaintext)
    {
        var token = RandomNumberGenerator.GetBytes(32);
        _keys.Add(Convert.ToHexString(token), plaintext.ToArray());
        return token;
    }

    public byte[] Unprotect(byte[] token)
    {
        if (!_keys.TryGetValue(Convert.ToHexString(token), out var plaintext))
            throw new CryptographicException("Unknown test key token.");
        return plaintext.ToArray();
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
    }
}
