using System.Runtime.InteropServices;
using System.Text;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host.Access;

/// <summary>Falha do Credential Manager. Só carrega o código do Windows: nunca a chave.</summary>
public sealed class CredentialVaultException(int win32Error)
    : Exception("Não foi possível acessar o cofre de credenciais do Windows (código " + win32Error + ").")
{
    public int Win32Error { get; } = win32Error;
}

/// <summary>
/// Porta <see cref="ICredentialVault"/> sobre o Windows Credential Manager (advapi32), credencial genérica.
/// Persistência LOCAL_MACHINE: sobrevive ao reinício e ao logoff, fica restrita ao usuário atual e não é
/// replicada para outros computadores (ENTERPRISE só serviria a perfis de domínio roaming, o que a chave não deve fazer).
/// O valor sai do cofre como bytes UTF-8, que são zerados assim que viram a string entregue ao núcleo.
/// </summary>
public sealed class WindowsCredentialVault : ICredentialVault
{
    /// <summary>Alvo do app. Os testes usam alvos próprios, nunca este.</summary>
    public const string ProductionTarget = "Whisper.Recorder/ApiKey";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlobBytes = 5 * 512;
    private const string UserName = "api-key";

    private readonly string target;

    public WindowsCredentialVault(string target = ProductionTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        this.target = target;
    }

    public ValueTask SaveAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var blob = IntPtr.Zero;
        try
        {
            if (bytes.Length > MaxBlobBytes)
                throw new ArgumentException("A chave é maior do que o cofre aceita.", nameof(apiKey));
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new CredentialVaultException(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Array.Clear(bytes);
            if (blob != IntPtr.Zero)
            {
                for (var i = 0; i < bytes.Length; i++)
                    Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? ValueTask.FromResult<string?>(null) : throw new CredentialVaultException(error);
        }
        byte[]? bytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return ValueTask.FromResult<string?>(null);
            bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return ValueTask.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (bytes is not null)
                Array.Clear(bytes);
            CredFree(handle);
        }
    }

    public ValueTask RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            // Remover o que não existe já é o estado desejado.
            if (error != ErrorNotFound)
                throw new CredentialVaultException(error);
        }
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
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

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
