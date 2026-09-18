using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GoldenTicket.CompanionHost.Security;

/// <summary>The material a run needs: the server certificate, and the CA to hand to a device.</summary>
public sealed record LocalTrustMaterial(
    X509Certificate2 ServerCertificate,
    byte[] AuthorityCertificateDer,
    string AuthorityFingerprint,
    string Hostname,
    DateTimeOffset ServerCertificateExpiry);

/// <summary>
/// Generates and stores a unique local certificate authority per installation (DESIGN 18.5).
///
/// The private keys never leave this machine: they are written DPAPI-protected for the current user,
/// and only the public CA certificate is ever exported for a device to trust. No shared private key
/// is shipped, and no client is ever asked to disable TLS validation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalCertificateAuthority
{
    /// <summary>
    /// Apple caps manually trusted server certificates at 825 days and rejects long-lived leaves.
    /// A year keeps well inside that and makes renewal a routine, tested path (DESIGN 18.5).
    /// </summary>
    public const int ServerCertificateDays = 365;

    /// <summary>The CA outlives many leaf renewals; replacing it costs every device its trust.</summary>
    public const int AuthorityYears = 10;

    /// <summary>Renew this far ahead of expiry rather than on the day.</summary>
    public const int RenewWithinDays = 30;

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("GoldenTicket.CompanionHost.v1");

    private readonly string _directory;
    private readonly Func<byte[], byte[]> _protectKey;
    private readonly Func<byte[], byte[]> _unprotectKey;
    private readonly X509KeyStorageFlags _keyStorageFlags;

    public LocalCertificateAuthority(string directory)
        : this(directory, ProtectWithDpapi, UnprotectWithDpapi, X509KeyStorageFlags.Exportable) { }

    // Certificate tests use an in-memory key vault so they can run without a Windows user profile.
    internal LocalCertificateAuthority(string directory, Func<byte[], byte[]> protectKey,
        Func<byte[], byte[]> unprotectKey,
        X509KeyStorageFlags keyStorageFlags = X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable)
    {
        _directory = directory;
        _protectKey = protectKey;
        _unprotectKey = unprotectKey;
        _keyStorageFlags = keyStorageFlags;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>DESIGN 19.1: host material lives beside the settings, not in the install directory.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoldenTicket", "companion-host");

    private string InstallationIdPath => Path.Combine(_directory, "installation-id");
    private string AuthorityCertPath => Path.Combine(_directory, "authority.crt");
    private string AuthorityKeyPath => Path.Combine(_directory, "authority.key.dpapi");
    private string ServerPath => Path.Combine(_directory, "server.pfx.dpapi");

    /// <summary>
    /// The stable installation identifier used in the local hostname. Kept on disk so a device that
    /// has already trusted this installation keeps working across restarts.
    /// </summary>
    public string InstallationId
    {
        get
        {
            if (File.Exists(InstallationIdPath))
            {
                var existing = File.ReadAllText(InstallationIdPath).Trim();
                if (existing.Length == 8 && existing.All(char.IsAsciiLetterOrDigit)) return existing;
                throw new CryptographicException("The installation identifier is damaged. Restore the host material before continuing.");
            }

            var generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            File.WriteAllText(InstallationIdPath, generated);
            return generated;
        }
    }

    /// <summary>The advertised local name, e.g. <c>gt-1a2b3c4d.local</c> (DESIGN 18.5).</summary>
    public string Hostname => $"gt-{InstallationId}.local";

    /// <summary>
    /// Returns usable trust material, creating or renewing what is missing. The CA is reused
    /// whenever possible; only the leaf is reissued, so devices keep their trust.
    /// </summary>
    public LocalTrustMaterial EnsureMaterial(IPAddress lanAddress)
    {
        // Prevent two launches from mixing certificates and keys during first setup/renewal.
        using var materialLock = new FileStream(Path.Combine(_directory, "material.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var authority = LoadAuthority() ?? CreateAuthority();
        var server = LoadServer();

        if (server is null || NeedsReissue(server, authority, lanAddress))
        {
            server?.Dispose();
            server = IssueServerCertificate(authority, lanAddress);
        }

        var authorityDer = authority.Export(X509ContentType.Cert);

        return new LocalTrustMaterial(
            server,
            authorityDer,
            FormatFingerprint(authority),
            Hostname,
            server.NotAfter);
    }

    /// <summary>
    /// A leaf is reissued when it is expiring, when it does not cover the address now being served,
    /// or when the hostname has changed. DESIGN 18.5 requires the SANs to match the address actually
    /// used; a mismatched certificate must never be worked around by disabling validation.
    /// </summary>
    private bool NeedsReissue(X509Certificate2 server, X509Certificate2 authority, IPAddress lanAddress)
    {
        if (!server.HasPrivateKey || server.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            server.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(RenewWithinDays)) return true;

        // SANs alone do not prove this leaf belongs to the CA being exported. A restored or
        // replaced CA must never leave the host serving a certificate signed by the old key.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (!chain.Build(server)) return true;

        var names = ReadSubjectAlternativeNames(server);
        return !names.Contains(Hostname, StringComparer.OrdinalIgnoreCase) ||
               !names.Contains(lanAddress.ToString(), StringComparer.Ordinal);
    }

    /// <summary>Every DNS name and IP address the certificate covers, as text.</summary>
    public static IReadOnlyList<string> ReadSubjectAlternativeNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san) continue;

            return [.. san.EnumerateDnsNames(), .. san.EnumerateIPAddresses().Select(ip => ip.ToString())];
        }

        return [];
    }

    public static string FormatFingerprint(X509Certificate2 certificate)
    {
        var digest = SHA256.HashData(certificate.RawData);
        return string.Join(' ', Enumerable.Range(0, digest.Length)
            .Select(i => digest[i].ToString("X2"))
            .Chunk(4)
            .Select(chunk => string.Concat(chunk)));
    }

    // ---- Creation ---------------------------------------------------------------------------

    private X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=GoldenTicket local authority {InstallationId}, O=GoldenTicket"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(AuthorityYears));

        Protect(AuthorityKeyPath, key.ExportPkcs8PrivateKey());
        File.WriteAllBytes(AuthorityCertPath, certificate.Export(X509ContentType.Cert));

        return certificate;
    }

    private X509Certificate2 IssueServerCertificate(X509Certificate2 authority, IPAddress lanAddress)
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={Hostname}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        // iOS requires the server-authentication purpose on the leaf.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], critical: false));

        // DESIGN 18.5: the names must match whatever the device actually types.
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Hostname);
        names.AddDnsName("localhost");
        names.AddIpAddress(lanAddress);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(ServerCertificateDays);
        if (notAfter > authority.NotAfter.ToUniversalTime()) notAfter = authority.NotAfter.ToUniversalTime();

        // Both freshly created and restored authorities have their matching private key attached.
        {
            var issued = request.Create(
                authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));

            // Whether Create returns the private key attached depends on the runtime; take it as it
            // comes rather than assuming, because attaching a second time throws.
            var withKey = issued.HasPrivateKey ? issued : issued.CopyWithPrivateKey(key);

            try
            {
                // Schannel needs a persisted key, so round-trip through PKCS#12 for Kestrel.
                var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
                var pfx = withKey.Export(X509ContentType.Pfx, password);

                try
                {
                    Protect(ServerPath, Combine(password, pfx));
                    return X509CertificateLoader.LoadPkcs12(pfx, password, _keyStorageFlags);
                }
                finally { CryptographicOperations.ZeroMemory(pfx); }
            }
            finally
            {
                if (!ReferenceEquals(withKey, issued)) withKey.Dispose();
                issued.Dispose();
            }
        }
    }

    // ---- Loading ----------------------------------------------------------------------------

    private X509Certificate2? LoadAuthority()
    {
        if (!File.Exists(AuthorityCertPath) && !File.Exists(AuthorityKeyPath)) return null;
        if (!File.Exists(AuthorityCertPath) || !File.Exists(AuthorityKeyPath))
            throw new CryptographicException("The local authority is incomplete. Restore its certificate and key; replacing it requires trusting a new CA on every device.");

        using var certificate = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(AuthorityCertPath));
        if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
            !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
            throw new CryptographicException("The local authority is invalid or expired. Check the clock; CA replacement requires new device trust.");
        using var key = LoadAuthorityKey();
        return certificate.CopyWithPrivateKey(key); // also verifies that the stored keys match
    }

    private RSA LoadAuthorityKey()
    {
        var key = RSA.Create();
        var plaintext = Unprotect(AuthorityKeyPath);
        try
        {
            key.ImportPkcs8PrivateKey(plaintext, out _);
            return key;
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private X509Certificate2? LoadServer()
    {
        if (!File.Exists(ServerPath)) return null;

        try
        {
            var plaintext = Unprotect(ServerPath);
            try
            {
                var (password, pfx) = Split(plaintext);
                try { return X509CertificateLoader.LoadPkcs12(pfx, password, _keyStorageFlags); }
                finally { CryptographicOperations.ZeroMemory(pfx); }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---- DPAPI storage ------------------------------------------------------------------------

    private void Protect(string path, byte[] plaintext)
    {
        byte[] protectedBytes;
        try { protectedBytes = _protectKey(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);
    }

    private byte[] Unprotect(string path) => _unprotectKey(File.ReadAllBytes(path));

    private static byte[] ProtectWithDpapi(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, DpapiEntropy, DataProtectionScope.CurrentUser);

    private static byte[] UnprotectWithDpapi(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);

    private static byte[] Combine(string password, byte[] pfx)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var buffer = new byte[4 + passwordBytes.Length + pfx.Length];

        BitConverter.TryWriteBytes(buffer, passwordBytes.Length);
        passwordBytes.CopyTo(buffer, 4);
        pfx.CopyTo(buffer, 4 + passwordBytes.Length);
        return buffer;
    }

    private static (string Password, byte[] Pfx) Split(byte[] buffer)
    {
        if (buffer.Length < sizeof(int)) throw new CryptographicException("Malformed key file.");
        var length = BitConverter.ToInt32(buffer);
        if (length < 0 || length > buffer.Length - 4) throw new CryptographicException("Malformed key file.");

        return (Encoding.UTF8.GetString(buffer, 4, length), buffer[(4 + length)..]);
    }
}
