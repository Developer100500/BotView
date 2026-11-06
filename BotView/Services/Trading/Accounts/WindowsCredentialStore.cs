using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BotView.Models;

namespace BotView.Services.Trading.Accounts;

/// <summary>Persists keys in the current Windows user's Credential Manager on this computer.</summary>
public sealed class WindowsCredentialStore : ICredentialStore, ICredentialNotifications
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumBlobSize = 2560; // 2KB
    private const int HeaderSize = 8;
    private const int FormatVersion = 1;
    private static readonly Encoding KeyEncoding = new UTF8Encoding(false, true);

    private readonly object _sync = new();
    private bool _disposed;

    public event Action<Guid>? CredentialsChanged;

    /// <summary>Checks the same entry that will be used for a subsequent connection.</summary>
    public bool Contains(Guid profileId)
    {
        using var credentials = Read(profileId);
        return credentials is not null;
    }

    /// <summary>Reads an independent copy and clears temporary native and managed secret buffers.</summary>
    public AccountCredentials? Read(Guid profileId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!CredRead(GetTargetName(profileId), GenericCredential, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return null;
                }

                throw new Win32Exception(error, "Could not read exchange keys from Windows Credential Manager.");
            }

            var native = default(NativeCredential);
            byte[]? blob = null;

            try
            {
                native = Marshal.PtrToStructure<NativeCredential>(pointer);
                if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize <= HeaderSize
                    || native.CredentialBlobSize > MaximumBlobSize)
                {
                    throw new InvalidDataException("Invalid stored exchange credentials.");
                }

                blob = new byte[(int)native.CredentialBlobSize];
                Marshal.Copy(native.CredentialBlob, blob, 0, blob.Length);
                return Decode(blob);
            }
            finally
            {
                if (blob is not null)
                {
                    CryptographicOperations.ZeroMemory(blob);
                }

                ClearNativeBlob(native.CredentialBlob, native.CredentialBlobSize);
                CredFree(pointer);
            }
        }
    }

    /// <summary>Atomically replaces a generic credential; both keys are inside its protected blob.</summary>
    public void Write(Guid profileId, AccountCredentials credentials)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(credentials);
            var targetName = GetTargetName(profileId);
            var blob = Encode(credentials);
            var pointer = IntPtr.Zero;

            try
            {
                pointer = Marshal.AllocHGlobal(blob.Length);
                Marshal.Copy(blob, 0, pointer, blob.Length);

                var native = new NativeCredential
                {
                    Type = GenericCredential,
                    TargetName = targetName,
                    UserName = "BotView",
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = pointer,
                    Persist = PersistLocalMachine
                };

                if (!CredWrite(ref native, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not save exchange keys in Windows Credential Manager.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
                ClearNativeBlob(pointer, (uint)blob.Length);
                Marshal.FreeHGlobal(pointer);
            }
        }

        CredentialsChanged?.Invoke(profileId);
    }

    /// <summary>Deletes only this profile's entry and invalidates sessions using its keys.</summary>
    public void Delete(Guid profileId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!CredDelete(GetTargetName(profileId), GenericCredential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                {
                    throw new Win32Exception(error, "Could not delete exchange keys from Windows Credential Manager.");
                }
            }
        }

        CredentialsChanged?.Invoke(profileId);
    }

    /// <summary>Closes this store without deleting persisted keys.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    /// <summary>Uses a stable, application-specific name containing no key material or editable metadata.</summary>
    private static string GetTargetName(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        return $"BotView/TradingAccounts/{profileId:N}";
    }

    /// <summary>Encodes version, API key byte length, API key, and secret into one bounded UTF-8 blob.</summary>
    private static byte[] Encode(AccountCredentials credentials)
    {
        var apiKey = credentials.ApiKey;
        var secret = credentials.Secret;
        var keyLength = KeyEncoding.GetByteCount(apiKey);
        var secretLength = KeyEncoding.GetByteCount(secret);

        if ((long)HeaderSize + keyLength + secretLength > MaximumBlobSize)
        {
            throw new ArgumentException("Exchange keys exceed the Windows Credential Manager entry size limit.",
                nameof(credentials));
        }

        var blob = new byte[HeaderSize + keyLength + secretLength];

        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(blob, FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), keyLength);
            KeyEncoding.GetBytes(apiKey.AsSpan(), blob.AsSpan(HeaderSize, keyLength));
            KeyEncoding.GetBytes(secret.AsSpan(), blob.AsSpan(HeaderSize + keyLength));
            return blob;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(blob);
            throw;
        }
    }

    /// <summary>Validates the payload before constructing disposable key material.</summary>
    private static AccountCredentials Decode(byte[] blob)
    {
        var version = BinaryPrimitives.ReadInt32LittleEndian(blob);
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4));

        if (version != FormatVersion || keyLength <= 0 || keyLength >= blob.Length - HeaderSize)
        {
            throw new InvalidDataException("Invalid stored exchange credentials.");
        }

        return new AccountCredentials(
            KeyEncoding.GetString(blob.AsSpan(HeaderSize, keyLength)),
            KeyEncoding.GetString(blob.AsSpan(HeaderSize + keyLength)));
    }

    /// <summary>Erases secret bytes before releasing an unmanaged allocation.</summary>
    private static void ClearNativeBlob(IntPtr pointer, uint length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (var offset = 0; offset < length; offset++)
        {
            Marshal.WriteByte(pointer, offset, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    /// <summary>Reads a Windows generic credential allocated by the operating system.</summary>
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    /// <summary>Writes the complete entry in one Windows API operation.</summary>
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    /// <summary>Removes a Windows generic credential.</summary>
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    /// <summary>Releases the allocation returned by CredReadW.</summary>
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
