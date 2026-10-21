using System.Text.Json;

namespace Whisper.Recorder.Core.Access;

/// <summary>Common configuration holds only the address. The key belongs to ICredentialVault.</summary>
public sealed class ApiConfigurationStore(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whisper", "Recorder", "settings.json");

    public Uri? Read()
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("apiBaseAddress", out var value)
            || value.ValueKind != JsonValueKind.String
            || !TryNormalizeAddress(value.GetString(), out var address))
            throw new InvalidDataException("Configuração da API inválida.");
        return address;
    }

    public void Save(Uri address)
    {
        if (!TryNormalizeAddress(address.AbsoluteUri, out var normalized))
            throw new InvalidDataException("Endereço da API inválido.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new { apiBaseAddress = normalized!.AbsoluteUri });
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool TryNormalizeAddress(string? input, out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        var basePath = parsed.AbsolutePath.TrimEnd('/');
        if (!basePath.EndsWith("/v1", StringComparison.Ordinal)) basePath += "/v1";
        address = new UriBuilder(parsed) { Path = basePath }.Uri;
        return true;
    }
}
