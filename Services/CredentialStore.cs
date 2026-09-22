using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BrainFuel.Services;

public enum CredentialBackend
{
    None,
    WindowsDpapi,
    MacOSKeychain,
    LinuxSecretService,
}

/// <summary>
/// Stores the GLM API key outside settings.json when the platform provides a
/// suitable per-user secret store. Failure is deliberately non-destructive:
/// callers may keep the legacy settings-file value as a compatibility fallback.
/// </summary>
public static class CredentialStore
{
    private const string ServiceName = "BrainFuel";
    private const string AccountName = "glm-coding-plan-api-key";
    private const int ErrSecItemNotFound = -25300;
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("BrainFuel|GLM Coding Plan API Key|v1");

    public static CredentialBackend Backend => OperatingSystem.IsWindows()
        ? CredentialBackend.WindowsDpapi
        : OperatingSystem.IsMacOS()
            ? CredentialBackend.MacOSKeychain
            : OperatingSystem.IsLinux()
                ? CredentialBackend.LinuxSecretService
                : CredentialBackend.None;

    public static string BackendDisplayName => Backend switch
    {
        CredentialBackend.WindowsDpapi => "Windows DPAPI",
        CredentialBackend.MacOSKeychain => "macOS Keychain",
        CredentialBackend.LinuxSecretService => "Linux Secret Service",
        _ => "local settings file",
    };

    public static bool TryRead(string appDirectory, out string? value)
    {
        value = null;
        try
        {
            return Backend switch
            {
                CredentialBackend.WindowsDpapi => TryReadWindows(appDirectory, out value),
                CredentialBackend.MacOSKeychain => TryReadMac(out value),
                CredentialBackend.LinuxSecretService => TryReadLinux(out value),
                _ => false,
            };
        }
        catch
        {
            value = null;
            return false;
        }
    }

    public static bool TryWrite(string appDirectory, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TryDelete(appDirectory);

        try
        {
            return Backend switch
            {
                CredentialBackend.WindowsDpapi => TryWriteWindows(appDirectory, value),
                CredentialBackend.MacOSKeychain => TryWriteMac(value),
                CredentialBackend.LinuxSecretService => TryWriteLinux(value),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDelete(string appDirectory)
    {
        try
        {
            return Backend switch
            {
                CredentialBackend.WindowsDpapi => TryDeleteWindows(appDirectory),
                CredentialBackend.MacOSKeychain => TryDeleteMac(),
                CredentialBackend.LinuxSecretService => TryDeleteLinux(),
                _ => true,
            };
        }
        catch
        {
            return false;
        }
    }

    // Windows: DPAPI protects the blob to the current OS user. The ciphertext
    // may safely live beside settings.json without revealing the API key.
    private static string DpapiPath(string appDirectory) => Path.Combine(appDirectory, "credentials.dat");

    private static bool TryReadWindows(string appDirectory, out string? value)
    {
        value = null;
        var path = DpapiPath(appDirectory);
        if (!File.Exists(path))
            return false;

        var protectedBytes = File.ReadAllBytes(path);
        var clear = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        value = Encoding.UTF8.GetString(clear);
        CryptographicOperations.ZeroMemory(clear);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryWriteWindows(string appDirectory, string value)
    {
        Directory.CreateDirectory(appDirectory);
        var clear = Encoding.UTF8.GetBytes(value);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, DpapiEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(DpapiPath(appDirectory), protectedBytes);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static bool TryDeleteWindows(string appDirectory)
    {
        var path = DpapiPath(appDirectory);
        if (File.Exists(path))
            File.Delete(path);
        return true;
    }

    // macOS: use the user's login Keychain through Security.framework so the
    // secret never needs to be passed on a command line.
    private static readonly byte[] MacService = Encoding.UTF8.GetBytes(ServiceName);
    private static readonly byte[] MacAccount = Encoding.UTF8.GetBytes(AccountName);

    private static bool TryReadMac(out string? value)
    {
        value = null;
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)MacService.Length, MacService,
            (uint)MacAccount.Length, MacAccount,
            out var passwordLength, out var passwordData, out var itemRef);

        if (status == ErrSecItemNotFound)
            return false;
        if (status != 0)
            return false;

        try
        {
            var bytes = new byte[passwordLength];
            Marshal.Copy(passwordData, bytes, 0, bytes.Length);
            value = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return !string.IsNullOrWhiteSpace(value);
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
                SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (itemRef != IntPtr.Zero)
                CFRelease(itemRef);
        }
    }

    private static bool TryWriteMac(string value)
    {
        var secret = Encoding.UTF8.GetBytes(value);
        IntPtr itemRef = IntPtr.Zero;
        IntPtr passwordData = IntPtr.Zero;
        try
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)MacService.Length, MacService,
                (uint)MacAccount.Length, MacAccount,
                out _, out passwordData, out itemRef);

            if (passwordData != IntPtr.Zero)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                passwordData = IntPtr.Zero;
            }

            if (status == 0 && itemRef != IntPtr.Zero)
                return SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero, (uint)secret.Length, secret) == 0;

            if (status != ErrSecItemNotFound)
                return false;

            return SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)MacService.Length, MacService,
                (uint)MacAccount.Length, MacAccount,
                (uint)secret.Length, secret,
                out itemRef) == 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (itemRef != IntPtr.Zero)
                CFRelease(itemRef);
        }
    }

    private static bool TryDeleteMac()
    {
        var status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)MacService.Length, MacService,
            (uint)MacAccount.Length, MacAccount,
            out _, out var passwordData, out var itemRef);

        try
        {
            if (passwordData != IntPtr.Zero)
                SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (status == ErrSecItemNotFound)
                return true;
            if (status != 0 || itemRef == IntPtr.Zero)
                return false;
            return SecKeychainItemDelete(itemRef) == 0;
        }
        finally
        {
            if (itemRef != IntPtr.Zero)
                CFRelease(itemRef);
        }
    }

    // Linux: secret-tool is the standard CLI frontend to freedesktop Secret
    // Service (GNOME Keyring, KWallet-compatible providers, etc.). We pass the
    // secret over stdin rather than command-line arguments.
    private static bool TryReadLinux(out string? value)
    {
        value = null;
        var result = RunSecretTool(new[] { "lookup", "application", ServiceName, "credential", AccountName }, null);
        if (result.ExitCode != 0)
            return false;
        value = result.Output.TrimEnd('\r', '\n');
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryWriteLinux(string value)
    {
        var result = RunSecretTool(
            new[] { "store", "--label=BrainFuel GLM Coding Plan API Key", "application", ServiceName, "credential", AccountName },
            value + Environment.NewLine);
        return result.ExitCode == 0;
    }

    private static bool TryDeleteLinux()
    {
        var result = RunSecretTool(new[] { "clear", "application", ServiceName, "credential", AccountName }, null);
        return result.ExitCode == 0 || result.ExitCode == 1;
    }

    private static (int ExitCode, string Output) RunSecretTool(string[] arguments, string? stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "secret-tool",
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start secret-tool");
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, string.Empty);
        }
        return (process.ExitCode, output);
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData,
        out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
