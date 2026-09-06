using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;

namespace DesktopAgent.Windows.Configuration;

public sealed class LocalConfigurationStore : IProfileStore, ISecretStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _profilesLock = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopAgent.Secrets.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAgent");
    public LocalConfigurationStore(string root) => _root = Path.GetFullPath(root);
    private string ProfilesPath => Path.Combine(_root, "profiles.json");
    public async Task<string> SaveProbeReportAsync(ProbeReport report)
    {
        string path = Path.Combine(_root, "probe-reports", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions), default);
        return path;
    }
    // Explicit synthetic probes only. No desktop capture, secret, HTTP envelope or reasoning log.
    // Write images/truth before spending a request so a cancelled run keeps its inputs.
    public async Task<string> BeginSyntheticProbeEvidenceAsync(ImmutableArray<GeneratedProbeImage> cases, bool diagnosticOnly, CancellationToken ct)
    {
        if (cases.IsDefaultOrEmpty || cases.Length > 8 || cases.Any(c => c.Expected is null) ||
            cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length)
            throw new ArgumentException("INVALID_SYNTHETIC_EVIDENCE");
        string directory = Path.Combine(_root, "probe-evidence", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var items = new List<object>();
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            string imageName = $"case-{i:D2}.png", overviewName = $"overview-{i:D2}.png";
            await WriteAtomicAsync(Path.Combine(directory, imageName), item.Image.Bytes.ToArray(), ct);
            if (item.Overview is not null) await WriteAtomicAsync(Path.Combine(directory, overviewName), item.Overview.Bytes.ToArray(), ct);
            var request = Core.Providers.VisualProbe.RequestForAttempt(item, diagnosticOnly, i);
            items.Add(new { item.Id, image = imageName, item.Image.Width, item.Image.Height, expected = item.Expected,
                overview = item.Overview is null ? null : overviewName,
                promptVariant = Core.Providers.VisualProbe.PromptVariantForAttempt(item, diagnosticOnly, i),
                request = new { request.SystemPrompt, request.UserPrompt } });
        }
        await WriteAtomicAsync(Path.Combine(directory, "cases.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { schemaVersion = 1, diagnosticOnly, evidenceMode = "INVENTED_SYNTHETIC_IMAGES_ONLY", modelCallsAtCreation = 0, cases = items }, JsonOptions), ct);
        return directory;
    }
    public async Task<string> CompleteSyntheticProbeEvidenceAsync(string directory, ProbeReport report)
    {
        string root = Path.GetFullPath(Path.Combine(_root, "probe-evidence")) + Path.DirectorySeparatorChar;
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory, "cases.json")))
            throw new ArgumentException("INVALID_EVIDENCE_DIRECTORY");
        string path = Path.Combine(directory, "report.json");
        await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions), default);
        return path;
    }
    public async Task SaveSyntheticProbeCheckpointAsync(string directory, ProbeCheckpoint checkpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Index is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(checkpoint));
        string evidenceRoot = Path.GetFullPath(Path.Combine(_root, "probe-evidence"));
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string casesPath = Path.Combine(directory, "cases.json");
        if (!string.Equals(Path.GetDirectoryName(directory), evidenceRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(directory) || !File.Exists(casesPath) ||
            (File.GetAttributes(evidenceRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(casesPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("INVALID_EVIDENCE_DIRECTORY", nameof(directory));
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(casesPath, ct));
        var cases = evidence.RootElement.GetProperty("cases");
        if (checkpoint.Index >= cases.GetArrayLength() ||
            cases[checkpoint.Index].GetProperty("Id").GetString() != checkpoint.Id ||
            cases[checkpoint.Index].GetProperty("promptVariant").GetString() != checkpoint.PromptVariant ||
            checkpoint.Attempt is { } attempt && (attempt.Id != checkpoint.Id || attempt.PromptVariant != checkpoint.PromptVariant))
            throw new ArgumentException("CHECKPOINT_DOES_NOT_MATCH_SYNTHETIC_CASE", nameof(checkpoint));
        string path = Path.Combine(directory, $"checkpoint-{checkpoint.Index:D2}-{checkpoint.Stage}.json");
        await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions), ct, flushToDisk: true);
    }
    private string SecretPath(string secretRef)
    {
        if (!Guid.TryParseExact(secretRef, "N", out _)) throw new ArgumentException("Invalid secret reference.");
        return Path.Combine(_root, "secrets", secretRef + ".dpapi");
    }

    public async Task<ImmutableArray<ProviderProfile>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(ProfilesPath)) return [];
        if (new FileInfo(ProfilesPath).Length > 256 * 1024) throw new InvalidDataException("配置文件过大。");
        var profiles = JsonSerializer.Deserialize<ImmutableArray<ProviderProfile>>(await File.ReadAllTextAsync(ProfilesPath, ct), JsonOptions);
        if (profiles.IsDefault || profiles.Length > 16 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length)
            throw new InvalidDataException("配置文件格式无效。");
        foreach (var profile in profiles) ProviderConfiguration.Validate(profile);
        return profiles;
    }
    public async Task SaveAsync(ProviderProfile profile, CancellationToken ct)
    {
        ProviderConfiguration.Validate(profile);
        await _profilesLock.WaitAsync(ct);
        try
        {
            var profiles = (await ReadAsync(ct)).Where(p => p.Id != profile.Id).Append(profile).ToImmutableArray();
            if (profiles.Length > 16) throw new InvalidOperationException("最多保存 16 个模型配置。");
            await WriteAtomicAsync(ProfilesPath, JsonSerializer.SerializeToUtf8Bytes(profiles, JsonOptions), ct);
        }
        finally { _profilesLock.Release(); }
    }
    public async Task DeleteAsync(Guid profileId, CancellationToken ct)
    {
        await _profilesLock.WaitAsync(ct);
        try
        {
            var profiles = (await ReadAsync(ct)).Where(p => p.Id != profileId).ToImmutableArray();
            await WriteAtomicAsync(ProfilesPath, JsonSerializer.SerializeToUtf8Bytes(profiles, JsonOptions), ct);
        }
        finally { _profilesLock.Release(); }
    }
    public async Task<SecretValue?> ReadAsync(string secretRef, CancellationToken ct)
    {
        string path = SecretPath(secretRef);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("密钥文件格式无效。");
        byte[] encrypted = await File.ReadAllBytesAsync(path, ct);
        byte[] plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        char[]? characters = null;
        try
        {
            characters = new UTF8Encoding(false, true).GetChars(plaintext);
            return new SecretValue(characters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (characters is not null) Array.Clear(characters);
        }
    }
    public async Task WriteAsync(string secretRef, SecretValue value, CancellationToken ct)
    {
        string path = SecretPath(secretRef);
        if (value.Value.Length is < 1 or > 8192 || value.Value.Span.ContainsAny('\r', '\n')) throw new ArgumentException("密钥为空或格式无效。");
        byte[] plaintext = new byte[Encoding.UTF8.GetByteCount(value.Value.Span)];
        Encoding.UTF8.GetBytes(value.Value.Span, plaintext);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        await WriteAtomicAsync(path, encrypted, ct);
    }
    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(SecretPath(secretRef));
        return Task.CompletedTask;
    }
    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct, bool flushToDisk = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (flushToDisk)
            {
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, ct);
                ct.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            else await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
