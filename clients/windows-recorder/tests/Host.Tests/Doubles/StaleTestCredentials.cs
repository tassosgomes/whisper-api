using System.Runtime.InteropServices;

namespace Whisper.Recorder.Host.Tests.Doubles;

/// <summary>
/// O <c>Dispose</c> dos testes do cofre não roda quando o processo de teste é encerrado no meio,
/// e o alvo ficaria no Credential Manager do usuário. Remove alvos de teste antigos; os recentes
/// podem pertencer a uma classe rodando em paralelo e são preservados.
/// </summary>
internal static class StaleTestCredentials
{
    private const string Filter = "Whisper.Recorder.Test/*";
    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);
    private static int swept;

    public static void SweepOnce()
    {
        if (Interlocked.Exchange(ref swept, 1) == 1 || !CredEnumerate(Filter, 0, out var count, out var list))
            return;
        var stale = new List<string>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var credential = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                var written = DateTime.FromFileTimeUtc(((long)credential.LastWrittenHigh << 32) | credential.LastWrittenLow);
                if (DateTime.UtcNow - written > MinimumAge)
                    stale.Add(credential.TargetName);
            }
        }
        finally
        {
            CredFree(list);
        }
        foreach (var target in stale)
            CredDelete(target, CredTypeGeneric, 0);
    }

    private const uint CredTypeGeneric = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
