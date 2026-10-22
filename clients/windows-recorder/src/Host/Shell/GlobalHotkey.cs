using System.Runtime.InteropServices;

namespace Whisper.Recorder.Host.Shell;

public sealed record Hotkey(uint Modifiers, uint VirtualKey, string DisplayName)
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, NoRepeat = 0x4000;
}

/// <summary>Registro do atalho no sistema (RegisterHotKey), substituível em teste.</summary>
public interface IHotkeyRegistrar
{
    bool Register(int id, uint modifiers, uint virtualKey);
    void Unregister(int id);
}

public sealed class Win32HotkeyRegistrar(nint window) : IHotkeyRegistrar
{
    public bool Register(int id, uint modifiers, uint virtualKey) => RegisterHotKey(window, id, modifiers, virtualKey);
    public void Unregister(int id) => UnregisterHotKey(window, id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}

/// <summary>
/// Atalho global para iniciar e parar. Tenta os candidatos em ordem e fica com o primeiro que o sistema aceitar,
/// porque outro programa pode já ter reservado a combinação (RegisterHotKey falha nesse caso).
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5748;

    public static readonly IReadOnlyList<Hotkey> Candidates =
    [
        new(Hotkey.Control | Hotkey.Alt | Hotkey.NoRepeat, 0x52, "Ctrl+Alt+R"),
        new(Hotkey.Control | Hotkey.Alt | Hotkey.Shift | Hotkey.NoRepeat, 0x52, "Ctrl+Alt+Shift+R"),
        new(Hotkey.Control | Hotkey.Alt | Hotkey.NoRepeat, 0x78, "Ctrl+Alt+F9"),
        new(Hotkey.Control | Hotkey.Shift | Hotkey.NoRepeat, 0x78, "Ctrl+Shift+F9")
    ];

    private readonly IHotkeyRegistrar registrar;

    public GlobalHotkey(IHotkeyRegistrar registrar, IEnumerable<Hotkey>? candidates = null)
    {
        this.registrar = registrar;
        foreach (var candidate in candidates ?? Candidates)
        {
            if (registrar.Register(HotkeyId, candidate.Modifiers, candidate.VirtualKey))
            {
                Registered = candidate;
                break;
            }
        }
    }

    /// <summary>O atalho em uso; nulo se nenhum candidato pôde ser registrado.</summary>
    public Hotkey? Registered { get; }
    public event Action? Pressed;

    /// <summary>Repasse as mensagens da janela; devolve verdadeiro quando a mensagem era o atalho.</summary>
    public bool HandleMessage(int message, nint wParam)
    {
        if (message != WmHotkey || Registered is null || wParam != HotkeyId)
            return false;
        Pressed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (Registered is not null)
            registrar.Unregister(HotkeyId);
    }
}
