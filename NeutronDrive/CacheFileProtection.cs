using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NeutronDrive;

/// <summary>
/// Provides transparent encryption and file permission hardening for the persistent cache file.
/// <para>
/// Encryption uses AES-256-GCM with a key derived (via PBKDF2) from machine-specific entropy
/// (machine ID on Linux, IOPlatformUUID on macOS, or the machine name as a fallback)
/// combined with the current user's name. No password is required.
/// </para>
/// <para>
/// On Linux/macOS the cache file permissions are restricted to owner-only (chmod 600).
/// On Windows, DPAPI (DataProtectionScope.CurrentUser) is used instead, which is already
/// scoped to the logged-in user.
/// </para>
/// </summary>
internal static class CacheFileProtection
{
    // 16-byte fixed salt – not secret; only purpose is to make PBKDF2 output domain-separated.
    private static readonly byte[] DerivationSalt =
        [0x4E, 0x65, 0x75, 0x74, 0x72, 0x6F, 0x6E, 0x44, 0x72, 0x69, 0x76, 0x65, 0x43, 0x61, 0x63, 0x68];

    private const int KeySizeBytes = 32; // AES-256
    private const int NonceSizeBytes = 12; // AES-GCM standard nonce
    private const int TagSizeBytes = 16; // AES-GCM standard tag

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns [nonce (12) | tag (16) | ciphertext].
    /// </summary>
    public static byte[] Encrypt(string plaintext)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return EncryptDpapi(plaintext);
        }

        return EncryptAesGcm(plaintext);
    }

    /// <summary>
    /// Decrypts data previously produced by <see cref="Encrypt"/>.
    /// </summary>
    public static string Decrypt(byte[] data)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DecryptDpapi(data);
        }

        return DecryptAesGcm(data);
    }

    /// <summary>
    /// Restricts the file at <paramref name="path"/> to owner-only read/write (chmod 600).
    /// On Windows this is a no-op because DPAPI already scopes protection to the current user.
    /// </summary>
    public static void RestrictFilePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // DPAPI provides per-user protection
        }

        if (!File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ─── AES-256-GCM (Linux / macOS) ────────────────────────────────────────

    private static byte[] EncryptAesGcm(string plaintext)
    {
        var key = DeriveKey();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: [nonce | tag | ciphertext]
        var result = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSizeBytes);
        ciphertext.CopyTo(result, NonceSizeBytes + TagSizeBytes);

        CryptographicOperations.ZeroMemory(key);
        return result;
    }

    private static string DecryptAesGcm(byte[] data)
    {
        if (data.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Encrypted data is too short.");
        }

        var key = DeriveKey();

        var nonce = data.AsSpan(0, NonceSizeBytes);
        var tag = data.AsSpan(NonceSizeBytes, TagSizeBytes);
        var ciphertext = data.AsSpan(NonceSizeBytes + TagSizeBytes);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        CryptographicOperations.ZeroMemory(key);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey()
    {
        var entropy = GetMachineEntropy();
        return Rfc2898DeriveBytes.Pbkdf2(
            entropy,
            DerivationSalt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
    }

    /// <summary>
    /// Builds a machine+user-specific entropy string without requiring user input.
    /// </summary>
    private static byte[] GetMachineEntropy()
    {
        var machineId = GetMachineId();
        var userName = Environment.UserName;
        var combined = $"NeutronDrive|{machineId}|{userName}";
        return Encoding.UTF8.GetBytes(combined);
    }

    private static string GetMachineId()
    {
        // Linux: /etc/machine-id (systemd) or /var/lib/dbus/machine-id
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (File.Exists(path))
                {
                    var id = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrEmpty(id))
                    {
                        return id;
                    }
                }
            }
        }

        // macOS: IOPlatformUUID via ioreg
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "/usr/sbin/ioreg";
                process.StartInfo.Arguments = "-rd1 -c IOPlatformExpertDevice";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // Parse IOPlatformUUID from the output
                const string key = "\"IOPlatformUUID\"";
                var idx = output.IndexOf(key, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var eqIdx = output.IndexOf('=', idx);
                    if (eqIdx >= 0)
                    {
                        var startQuote = output.IndexOf('"', eqIdx + 1);
                        var endQuote = output.IndexOf('"', startQuote + 1);
                        if (startQuote >= 0 && endQuote > startQuote)
                        {
                            return output.Substring(startQuote + 1, endQuote - startQuote - 1);
                        }
                    }
                }
            }
            catch
            {
                // Fall through to fallback
            }
        }

        // Fallback: machine name (less unique, but always available)
        return Environment.MachineName;
    }

    // ─── DPAPI (Windows) ────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static byte[] EncryptDpapi(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        return ProtectedData.Protect(plaintextBytes, DerivationSalt, DataProtectionScope.CurrentUser);
    }

    [SupportedOSPlatform("windows")]
    private static string DecryptDpapi(byte[] data)
    {
        byte[] plaintextBytes = ProtectedData.Unprotect(data, DerivationSalt, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plaintextBytes, 0, plaintextBytes.Length);
    }
}





