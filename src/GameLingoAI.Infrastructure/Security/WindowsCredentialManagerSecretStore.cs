using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GameLingoAI.Core.Abstractions;

namespace GameLingoAI.Infrastructure.Security;

/// <summary>Stores secrets as generic credentials encrypted for the current Windows user.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerSecretStore : ISecretStore
{
    private const int ErrorNotFound = 1168;
    private const uint CredentialTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int MaximumSecretCharacters = 2_560;
    private const string TargetPrefix = "GameLingoAI/";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var target = GetTarget(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the secret.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var characterCount = checked((int)credential.CredentialBlobSize / sizeof(char));
            var secret = Marshal.PtrToStringUni(credential.CredentialBlob, characterCount);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken)
    {
        var target = GetTarget(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length > MaximumSecretCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Secret exceeds Credential Manager limits.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var secretPointer = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = checked((uint)(secret.Length * sizeof(char))),
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows Credential Manager could not store the secret.");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(secretPointer);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var target = GetTarget(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDelete(target, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Windows Credential Manager could not delete the secret.");
            }
        }

        return Task.CompletedTask;
    }

    private static string GetTarget(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128 || key.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Secret key must be 1-128 letters, digits, dots, dashes, or underscores.", nameof(key));
        }

        return TargetPrefix + key;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }
}

