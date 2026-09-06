using System.IO;
using System.Text;
using System.Text.Json;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Windows.Configuration;

/// <summary>Explicit local diagnostic using invented secret data, never an existing user profile.</summary>
internal static class ConfigurationDiagnostic
{
    public static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        string temporaryStore = Path.Combine(directory, "store-" + Guid.NewGuid().ToString("N"));
        var store = new LocalConfigurationStore(temporaryStore);
        var profile = ProviderConfiguration.DefaultDeepSeek();
        var checks = new List<object>();
        bool passed = true;
        string? failureType = null;
        int? failureCode = null;
        void Check(string name, bool value) { checks.Add(new { name, passed = value }); passed &= value; }
        try
        {
            using var invented = new SecretValue("fixture-only-key-中文".AsSpan());
            await store.WriteAsync(profile.SecretRef, invented, default);
            await store.SaveAsync(profile, default);
            var reopened = new LocalConfigurationStore(temporaryStore);
            var loaded = (await reopened.ReadAsync(default)).Single();
            Check("profile_reopen", loaded.Model == profile.Model && loaded.BaseUrl == profile.BaseUrl && loaded.SecretRef == profile.SecretRef);
            using var secret = await reopened.ReadAsync(loaded.SecretRef, default);
            Check("dpapi_roundtrip", secret is not null && secret.Value.Span.SequenceEqual(invented.Value.Span));
            var encrypted = await File.ReadAllBytesAsync(Path.Combine(temporaryStore, "secrets", profile.SecretRef + ".dpapi"));
            Check("ciphertext_not_plaintext", !Encoding.UTF8.GetString(encrypted).Contains("fixture-only-key"));
            Check("metadata_does_not_contain_secret", !(await File.ReadAllTextAsync(Path.Combine(temporaryStore, "profiles.json"))).Contains("fixture-only-key"));
            await store.DeleteAsync(profile.Id, default);
            await store.DeleteAsync(profile.SecretRef, default);
            Check("profile_delete", (await store.ReadAsync(default)).IsEmpty);
            Check("secret_delete", await store.ReadAsync(profile.SecretRef, default) is null);
        }
        catch (Exception error)
        {
            failureType = error.GetType().Name;
            failureCode = error.HResult;
            Check("local_store_completed", false);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "configuration.json"), JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow, passed, checks, failureType, failureCode, modelCalls = 0, data = "INVENTED_SECRET_ONLY",
            evidenceMode = "REAL_WINDOWS_CURRENT_USER_DPAPI_AND_LOCAL_FILES"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
