using System.Windows;
using System.Windows.Controls;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Providers;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Diagnostics;
using DesktopAgent.Windows.Presentation;

namespace DesktopAgent.Windows.Configuration;

public partial class SettingsWindow : Window
{
    private bool _themeReady;
    internal void PreviewTheme(string theme)
    {
        _themeReady = false;
        try { ThemeManager.Apply(theme); ThemeBox.SelectedIndex = theme == "light" ? 1 : theme == "dark" ? 2 : 0; }
        finally { _themeReady = true; }
    }
    private void Done_Click(object sender, RoutedEventArgs e) => Close();
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        ThemeManager.Apply(theme); (AppearancePreferences.Read() with { Theme = theme }).Save();
    }
    private readonly LocalConfigurationStore _store;
    private readonly InputSafetyGate? _gate;
    private ProviderProfile? _loaded;
    private readonly Dictionary<ProviderKind, ProviderProfile> _profiles = [];
    private bool _initializing = true;
    private CancellationTokenSource? _probeCancellation;
    private bool _closed;
    public SettingsWindow(LocalConfigurationStore store, InputSafetyGate? gate = null)
    {
        _store = store;
        _gate = gate;
        InitializeComponent();
        ThemeBox.SelectedIndex = ThemeManager.Mode == "light" ? 1 : ThemeManager.Mode == "dark" ? 2 : 0;
        _themeReady = true;
        SourceInitialized += (_, _) => ThemeManager.Chrome(this);
        ProviderBox.ItemsSource = new[] { "DeepSeek", "OpenAI-compatible", "Kimi", "GLM" };
        Loaded += async (_, _) =>
        {
            try { foreach (var p in await _store.ReadAsync(default)) _profiles[p.ProviderKind] = p; }
            catch { ResultText.Text = "现有配置无法读取，请检查本机配置文件。"; SaveButton.IsEnabled = false; }
            _initializing = false;
            ProviderBox.SelectedIndex = 0;
        };
        Closed += (_, _) => { _closed = true; _probeCancellation?.Cancel(); KeyBox.Clear(); };
    }
    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ProviderBox.SelectedIndex < 0) return;
        var kind = (ProviderKind)ProviderBox.SelectedIndex;
        _profiles.TryGetValue(kind, out _loaded);
        BaseBox.Text = _loaded?.BaseUrl.AbsoluteUri ?? kind switch
        {
            ProviderKind.DeepSeek => "https://api.deepseek.com", ProviderKind.Kimi => "https://api.moonshot.cn/v1",
            ProviderKind.Glm => "https://open.bigmodel.cn/api/paas/v4", _ => ""
        };
        ModelBox.Text = _loaded?.Model ?? (kind == ProviderKind.DeepSeek ? "deepseek-v4-flash-vision-exp" : "");
        KeyBox.Clear();
        KeyHint.Text = _loaded is null ? "请在此填写，不要粘贴到聊天。" : "已保存的密钥不会显示。留空保留，填写新值则替换。";
        ResultText.Text = _loaded is not null && DesktopTaskCoordinator.IsProfileVerified(_loaded, controlsRequired: true)
            ? "已通过定位前置测试，真实任务仍须单独验收。" : "辅助定位尚未通过，自动执行未启用。";
    }
    private void Endpoint_Changed(object sender, TextChangedEventArgs e)
    {
        if (EndpointText is not null) EndpointText.Text = BaseBox.Text.Trim().TrimEnd('/') + "/chat/completions";
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        string? pendingSecretRef = null;
        bool profileSaved = false;
        try
        {
            var kind = (ProviderKind)ProviderBox.SelectedIndex;
            using var securePassword = KeyBox.SecurePassword;
            bool replaceKey = securePassword.Length > 0;
            if (!replaceKey && _loaded is null) throw new ArgumentException("请填写 API Key。");
            var profile = new ProviderProfile(_loaded?.Id ?? Guid.NewGuid(), ProviderBox.SelectedItem.ToString()!, kind,
                new Uri(BaseBox.Text.Trim(), UriKind.Absolute), ModelBox.Text.Trim(),
                replaceKey ? Guid.NewGuid().ToString("N") : _loaded!.SecretRef,
                kind == ProviderKind.Glm ? ImageEncoding.RawBase64 : ImageEncoding.DataUrl,
                _loaded?.RequestTimeoutMs ?? 45000, _loaded?.FrameTtlMs ?? 90000,
                _loaded?.Thinking, _loaded?.ReasoningEffort, _loaded?.JsonMode ?? kind == ProviderKind.DeepSeek, [], null);
            ProviderConfiguration.Validate(profile);
            if (replaceKey)
            {
                pendingSecretRef = profile.SecretRef;
                using var value = new SecretValue(KeyBox.Password.AsSpan());
                await _store.WriteAsync(profile.SecretRef, value, default);
            }
            else
            {
                using var existing = await _store.ReadAsync(profile.SecretRef, default);
                if (existing is null) throw new ArgumentException("本机密钥不存在，请重新填写。");
            }
            if (_loaded is not null && ProviderConfiguration.Fingerprint(_loaded) == ProviderConfiguration.Fingerprint(profile))
                profile = profile with { Capabilities = _loaded.Capabilities, ProbeFingerprint = _loaded.ProbeFingerprint };
            await _store.SaveAsync(profile, default);
            profileSaved = true;
            var old = _loaded;
            _loaded = profile;
            _profiles[kind] = profile;
            KeyBox.Clear();
            if (replaceKey && old is not null)
            {
                try { await _store.DeleteAsync(old.SecretRef, default); }
                catch { /* An obsolete encrypted blob must not invalidate the successfully saved profile. */ }
            }
            KeyHint.Text = "密钥已按当前 Windows 用户加密保存，留空可保留。";
            ResultText.Text = "已保存。可点击“测试辅助定位（2次）”验证此配置。";
        }
        catch (UriFormatException) { ResultText.Text = "Base URL 格式不正确。"; }
        catch (ArgumentException error) { ResultText.Text = error.Message; }
        catch { ResultText.Text = "保存失败，请检查本机存储权限；没有发送 API 请求。"; }
        finally
        {
            if (!profileSaved && pendingSecretRef is not null)
            {
                try { await _store.DeleteAsync(pendingSecretRef, default); } catch { }
            }
            IsEnabled = true;
        }
    }

    private void CancelProbe_Click(object sender, RoutedEventArgs e) => _probeCancellation?.Cancel();

    internal IDisposable MinimizeForObservation() => new ObservationWindows(this);
    private sealed class ObservationWindows : IDisposable
    {
        private readonly SettingsWindow _settings;
        private readonly Window? _owner;
        private readonly WindowState _state, _ownerState;
        public ObservationWindows(SettingsWindow settings)
        {
            _settings = settings; _owner = settings.Owner;
            _state = settings.WindowState; _ownerState = _owner?.WindowState ?? WindowState.Normal;
            // Hide ends WPF's modal loop. Minimize keeps it alive until Settings closes,
            // so MainWindow reloads the final profile rather than an in-progress copy.
            if (_owner is not null) _owner.WindowState = WindowState.Minimized;
            settings.WindowState = WindowState.Minimized;
        }
        public void Dispose()
        {
            if (_settings._closed) return;
            if (_owner is not null) _owner.WindowState = _ownerState;
            _settings.WindowState = _state;
            _settings.Activate();
        }
    }

    private async void Hybrid_Click(object sender, RoutedEventArgs e)
    {
        using var password = KeyBox.SecurePassword;
        if (_loaded is null || password.Length != 0 || BaseBox.Text.Trim().TrimEnd('/') != _loaded.BaseUrl.AbsoluteUri.TrimEnd('/') || ModelBox.Text.Trim() != _loaded.Model)
        { ResultText.Text = "请先保存当前配置，再开始测试。"; return; }
        if (_probeCancellation is not null) return;
        var profile = _loaded;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(145));
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        long? revision = _gate?.Status.Revision;
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!watching.IsCancellationRequested)
                {
                    await Task.Delay(40, watching.Token);
                    if (_gate is not null && _gate.Status.Revision != revision) { await cancellation.CancelAsync(); break; }
                }
            }
            catch (OperationCanceledException) { }
        });
        _probeCancellation = cancellation;
        ConfigFields.IsEnabled = SaveButton.IsEnabled = ProbeButton.IsEnabled = DiagnosticButton.IsEnabled = HybridButton.IsEnabled = false;
        CancelProbeButton.IsEnabled = true;
        IDisposable? observationWindows = null;
        ProviderProfile? unverifiedProfile = null;
        bool publishingCapabilities = false;
        void ThrowIfInterrupted()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed || (_gate is not null && _gate.Status.Revision != revision))
                throw new OperationCanceledException(cancellation.Token);
        }
        string directory = Path.Combine(LocalConfigurationStore.DefaultRoot, "hybrid-probe-evidence", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        string summary = "辅助测试未完成，自动执行未授权。";
        try
        {
            profile = profile with { Capabilities = profile.Capabilities.Where(c => !c.Name.StartsWith("hybrid_", StringComparison.Ordinal)).ToImmutableArray() };
            await _store.SaveAsync(profile, cancellation.Token);
            unverifiedProfile = profile;
            _loaded = profile; _profiles[profile.ProviderKind] = profile;
            observationWindows = MinimizeForObservation();
            var report = await HybridControlDiagnostic.RunAsync(profile, _store, directory, cancellation.Token);
            await _store.SaveProbeReportAsync(report);
            ThrowIfInterrupted();
            profile = profile with { Capabilities = profile.Capabilities.Concat(report.Results.Select(r =>
                new CapabilityRecord(r.Name, r.Status, "native-controls-v1", report.TestedAtUtc, report.ProfileFingerprint, r.PublicSummary))).ToImmutableArray() };
            publishingCapabilities = true;
            await _store.SaveAsync(profile, cancellation.Token);
            ThrowIfInterrupted();
            _loaded = profile; _profiles[profile.ProviderKind] = profile;
            publishingCapabilities = false;
            bool passed = !report.Cancelled && report.Results.Length == 3 && report.Results.All(r => r.Status == CapabilityStatus.ProbePassed && r.Name.StartsWith("hybrid_", StringComparison.Ordinal));
            summary = string.Join("；", report.Results.Select(r => r.PublicSummary)) + $"。共{report.ApiAttempts}次请求、{report.Attempts.Sum(a => a.ElapsedMs) / 1000d:F1}秒。" +
                (passed ? "辅助定位前置通过；先在测试窗口验证真实操作，尚不代表设置任务通过。" : "辅助定位未通过，不启用自动操作。") +
                $"用量：输入{report.Usage.InputTokens?.ToString() ?? "未知"} / 输出{report.Usage.OutputTokens?.ToString() ?? "未知"} tokens。证据：" + directory;
        }
        catch (OperationCanceledException) { summary = "辅助测试已取消；已发请求可能计费，不自动重试。已有逐项记录：" + directory; }
        catch { summary = "辅助测试或记录保存失败；没有启用自动操作。请查看已有逐项记录：" + directory; }
        finally
        {
            await watching.CancelAsync(); await watcher;
            _probeCancellation = null;
            if (publishingCapabilities && unverifiedProfile is not null)
            {
                _loaded = unverifiedProfile; _profiles[unverifiedProfile.ProviderKind] = unverifiedProfile;
                try { await _store.SaveAsync(unverifiedProfile, CancellationToken.None); }
                catch { summary = "测试结果撤销保存失败，请关闭程序并检查本机配置；本轮未完成。证据：" + directory; }
            }
            if (!_closed)
            {
                ConfigFields.IsEnabled = SaveButton.IsEnabled = ProbeButton.IsEnabled = DiagnosticButton.IsEnabled = HybridButton.IsEnabled = true;
                CancelProbeButton.IsEnabled = false;
                ResultText.Text = summary;
            }
            observationWindows?.Dispose();
        }
    }

    private async void Probe_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(false);
    private async void Diagnostic_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(true);
    private async Task RunProbeAsync(bool diagnosticOnly)
    {
        using var password = KeyBox.SecurePassword;
        if (_loaded is null || password.Length != 0 || BaseBox.Text.Trim().TrimEnd('/') != _loaded.BaseUrl.AbsoluteUri.TrimEnd('/') || ModelBox.Text.Trim() != _loaded.Model)
        { ResultText.Text = "请先保存当前配置，再开始测试。"; return; }
        if (_probeCancellation is not null) return;
        var profile = _loaded;
        string? savedReportPath = null;
        string? evidenceDirectory = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(18));
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        long? gateRevision = _gate?.Status.Revision;
        var watchGate = Task.Run(async () =>
        {
            try
            {
                while (!stopWatching.IsCancellationRequested)
                {
                    await Task.Delay(40, stopWatching.Token);
                    if (_gate is not null && _gate.Status.Revision != gateRevision) { await cancellation.CancelAsync(); break; }
                }
            }
            catch (OperationCanceledException) { }
        });
        _probeCancellation = cancellation;
        ConfigFields.IsEnabled = SaveButton.IsEnabled = ProbeButton.IsEnabled = DiagnosticButton.IsEnabled = HybridButton.IsEnabled = false;
        CancelProbeButton.IsEnabled = true;
        ResultText.Text = diagnosticOnly ? "正在对同一张随机图作两次对照：旧提示、新提示各一次。只排查原因，不启用自动操作。可取消，无重试。"
            : "正在测试随机合成图，共 8 项，无自动重试。可随时取消；取消前已发送的请求仍可能计费。";
        try
        {
            // Invalidate old approval before spending any request; a cancelled/failed retest cannot retain it.
            if (!diagnosticOnly)
            {
                profile = profile with { Capabilities = profile.Capabilities.Where(c => c.Name.StartsWith("hybrid_", StringComparison.Ordinal)).ToImmutableArray(), ProbeFingerprint = null };
                await _store.SaveAsync(profile, cancellation.Token);
                _loaded = profile;
                _profiles[profile.ProviderKind] = profile;
            }
            ImmutableArray<GeneratedProbeImage> images = diagnosticOnly ? SyntheticProbeImages.CreateGroundingDiagnostic() : SyntheticProbeImages.Create();
            evidenceDirectory = await _store.BeginSyntheticProbeEvidenceAsync(images, diagnosticOnly, cancellation.Token);
            Task SaveCheckpoint(ProbeCheckpoint progress, CancellationToken token)
                => _store.SaveSyntheticProbeCheckpointAsync(evidenceDirectory, progress, token);
            using var provider = new ChatCompletionProvider(profile, _store);
            var report = diagnosticOnly ? await provider.DiagnoseGroundingAsync(profile, images, cancellation.Token, SaveCheckpoint)
                : await provider.ProbeAsync(profile, images, cancellation.Token, SaveCheckpoint);
            string reportPath = await _store.SaveProbeReportAsync(report);
            savedReportPath = reportPath;
            string evidenceReport = await _store.CompleteSyntheticProbeEvidenceAsync(evidenceDirectory, report);
            if (_closed || cancellation.IsCancellationRequested) return;
            bool passed = !diagnosticOnly && !report.DiagnosticOnly && report.Results.Length == 3 && report.Results.All(r => r.Status == CapabilityStatus.ProbePassed) && !report.Cancelled;
            if (!diagnosticOnly)
            {
                profile = profile with
                {
                    Capabilities = profile.Capabilities.Where(c => c.Name.StartsWith("hybrid_", StringComparison.Ordinal)).Concat(
                        report.Results.Select(r => new CapabilityRecord(r.Name, r.Status, null, report.TestedAtUtc,
                        report.ProfileFingerprint, r.PublicSummary))).ToImmutableArray(),
                    ProbeFingerprint = passed ? report.ProfileFingerprint : null
                };
                await _store.SaveAsync(profile, cancellation.Token);
                _loaded = profile;
                _profiles[profile.ProviderKind] = profile;
            }
            string error = report.Attempts.FirstOrDefault(a => a.ErrorCode is not null)?.ErrorCode ?? "";
            var points = report.Attempts.Where(a => a.Kind == ProbeKind.GroundPoint).ToArray();
            string pointDetails = points.Length == 0 ? "" : $"定位项分开核对：文字正确 {points.Count(a => a.Diagnostic?.CodeMatched == true)}/{points.Length}，坐标命中 {points.Count(a => a.Diagnostic?.PointInsideHitBox == true)}/{points.Length}。";
            if (diagnosticOnly)
                pointDetails += string.Join("；", points.Select(a => (a.PromptVariant == "baseline" ? "旧提示" : "新提示") + "：" +
                    (a.Passed ? "文字与定位均正确" : !a.SchemaValid ? "回复格式未通过" : a.Diagnostic?.CodeMatched != true ? "文字未读对" : "定位未命中"))) + "。";
            ResultText.Text = string.Join("；", report.Results.Select(r => r.PublicSummary)) +
                $"。{report.ApiAttempts} 次请求，耗时 {report.Attempts.Sum(a => a.ElapsedMs) / 1000d:F1} 秒。" +
                $"用量：输入 {report.Usage.InputTokens?.ToString() ?? "未知"} / 输出 {report.Usage.OutputTokens?.ToString() ?? "未知"} tokens。" +
                pointDetails + (diagnosticOnly ? "本次仅诊断，不改变完整验收结果。" : passed ? "有限探针通过。" : "未通过：" + error + "。") + "图像与逐项报告：" + evidenceReport;
        }
        catch (OperationCanceledException) { if (!_closed) ResultText.Text = "测试已取消，此次未授权自动执行。"; }
        catch { if (!_closed) ResultText.Text = "测试或报告保存失败，此次未授权自动执行。请检查本机存储和配置。" +
            (evidenceDirectory is null ? "" : "已有逐项记录：" + evidenceDirectory); }
        finally
        {
            await stopWatching.CancelAsync();
            await watchGate;
            _probeCancellation = null;
            if (!_closed)
            {
                ConfigFields.IsEnabled = SaveButton.IsEnabled = ProbeButton.IsEnabled = DiagnosticButton.IsEnabled = HybridButton.IsEnabled = true;
                CancelProbeButton.IsEnabled = false;
                if (cancellation.IsCancellationRequested) ResultText.Text = "测试已取消，此次未授权自动执行。" +
                    (savedReportPath is null ? evidenceDirectory is null ? "本次未保存报告。" : "未保存最终报告，已有逐项记录：" + evidenceDirectory : "报告：" + savedReportPath);
            }
        }
    }
}
