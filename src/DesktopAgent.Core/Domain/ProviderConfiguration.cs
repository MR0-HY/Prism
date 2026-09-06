using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopAgent.Core.Domain;

public static class ProviderConfiguration
{
    public static ProviderProfile DefaultDeepSeek() => new(Guid.NewGuid(), "DeepSeek", ProviderKind.DeepSeek,
        new("https://api.deepseek.com"), "deepseek-v4-flash-vision-exp", Guid.NewGuid().ToString("N"),
        ImageEncoding.DataUrl, 45000, 90000, null, null, true, [], null);

    public static Uri Endpoint(ProviderProfile profile)
    {
        Validate(profile);
        return new Uri(profile.BaseUrl.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }
    public static void Validate(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty || !Enum.IsDefined(profile.ProviderKind) || !Enum.IsDefined(profile.ImageEncoding) ||
            string.IsNullOrWhiteSpace(profile.Label) || profile.Label.Length > 100 ||
            string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 200 || profile.Model.Any(char.IsControl) ||
            !Guid.TryParseExact(profile.SecretRef, "N", out _) || profile.Capabilities.IsDefault)
            throw new ArgumentException("配置名称、型号或密钥引用无效。");
        if (!profile.BaseUrl.IsAbsoluteUri || (profile.BaseUrl.Scheme != "https" && !(profile.BaseUrl.Scheme == "http" && profile.BaseUrl.IsLoopback)) ||
            !string.IsNullOrEmpty(profile.BaseUrl.UserInfo) || !string.IsNullOrEmpty(profile.BaseUrl.Query) || !string.IsNullOrEmpty(profile.BaseUrl.Fragment) ||
            profile.BaseUrl.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请填写 HTTPS Base URL（本机服务可用 HTTP），不要包含密钥、查询参数或 /chat/completions。");
        if (profile.RequestTimeoutMs is < 10000 or > 120000 || profile.FrameTtlMs < profile.RequestTimeoutMs + 5000 || profile.FrameTtlMs > 180000)
            throw new ArgumentException("请求时限或图像有效期超出范围。");
        if (profile.Thinking is not (null or "enabled" or "disabled") ||
            (profile.ReasoningEffort is not null && (profile.ReasoningEffort.Length > 40 || profile.ReasoningEffort.Any(char.IsControl))))
            throw new ArgumentException("模型可选参数无效。");
    }

    // SecretRef is rotated whenever the UI replaces a key. No plaintext key enters this metadata.
    public static string Fingerprint(ProviderProfile profile)
    {
        Validate(profile);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.ProviderKind, baseUrl = profile.BaseUrl.AbsoluteUri.TrimEnd('/'), profile.Model, profile.SecretRef,
            profile.ImageEncoding, profile.Thinking, profile.ReasoningEffort, profile.JsonMode,
            profile.RequestTimeoutMs, profile.FrameTtlMs
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
