using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Configuration;
using DesktopAgent.Windows.Diagnostics;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Overlay;
using DesktopAgent.Windows.Safety;
using DesktopAgent.Windows.Presentation;

namespace DesktopAgent.Windows;

public partial class MainWindow : Window
{
    private readonly InputSafetyGate _gate;
    private readonly LocalConfigurationStore _store;
    private readonly DesktopOverlayController _overlay;
    private readonly ProductLifecycleLog _lifecycle;
    private readonly WindowsDesktopObserver _desktop;
    private readonly WindowsControlObserver _controls;
    private readonly TaskLeaseRegistry _registry = new();
    private readonly DispatcherTimer _timer;
    private DesktopTaskCoordinator? _coordinator;
    private ChatCompletionProvider? _provider;
    private CancellationTokenSource? _starting;
    private TaskCompletionSource? _startFinished;
    private Task _clearPixels = Task.CompletedTask;
    private long _receivedRevision;
    private string? _lastSummary;
    private string? _lastInterpretation;
    private string? _lastLifecycleKey;
    private Dictionary<string, DisplayInfo> _displays = new(StringComparer.Ordinal);
    private string? _overlayMonitorId;
    private bool _closing, _allowClose, _ready;
    private TrayIcon? _tray;
    private bool _exitRequested;
    private bool _answering, _resetting, _updatingHighRisk, _reportingFailure;
    private long _interactionGeneration;
    private Guid? _displayedTaskId;
    private readonly HashSet<Guid> _retiredTaskIds = [];
    private HostedWindowIdentity? _replyTarget;
    private Guid? _shownQuestion;
    private Guid? _shownApproval;
    private long? _questionGateRevision;
    public MainWindow(InputSafetyGate gate, HotkeyRegistrationResult hotkeys, LocalConfigurationStore? store = null)
        : this(gate, hotkeys, store, ProductLifecycleLog.Default) { }
    internal MainWindow(InputSafetyGate gate, HotkeyRegistrationResult hotkeys, LocalConfigurationStore? store, ProductLifecycleLog lifecycle)
    {
        _gate = gate; _store = store ?? new(LocalConfigurationStore.DefaultRoot); _lifecycle = lifecycle;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.Chrome(this);
        _overlay = new(Dispatcher, persistAppearance: store is null); _desktop = new(_overlay.HideForCaptureAsync, preferForegroundWindow: true); _controls = new(_desktop);
        _overlay.Pause += PauseTask; _overlay.Stop += StopTask; _overlay.Chat += ReturnToMain;
        _overlay.Reply += answer => _ = AnswerQuestionAsync(answer);
        _overlay.NewTask += goal => _ = StartNextTaskAsync(goal);
        _overlay.NewTaskRequested += () => _ = ResetForNewTaskAsync();
        _overlay.ResumeRequested += () => _ = ResumeFromCardAsync();
        _overlay.EnableHighRiskRequested += lease => _ = EnableHighRiskAndResumeAsync(lease);
        _overlay.HighRiskModeChanged += enabled => { if (HighRiskMode.IsEnabled) HighRiskMode.IsChecked = enabled; };
        _overlay.ActionApprovalAnswered += (id, approve) => _ = AnswerActionApprovalAsync(id, approve);
        IsVisibleChanged += (_, _) => _lifecycle.TechnicalEvent(IsVisible ? "MAIN_VISIBLE" : "MAIN_HIDDEN");
        StateChanged += (_, _) => _lifecycle.TechnicalEvent(WindowState == WindowState.Minimized ? "MAIN_MINIMIZED" : "MAIN_NOT_MINIMIZED");
        _overlay.Hud.IsVisibleChanged += (_, _) => _lifecycle.TechnicalEvent(_overlay.Hud.IsVisible ? "HUD_VISIBLE" : "HUD_HIDDEN");
        Closed += (_, _) => _lifecycle.TechnicalEvent("MAIN_CLOSED");
        HotkeyText.Text = hotkeys.Registered ? "Ctrl+Alt+F8 暂停 · Ctrl+Alt+F9 紧急停止（已注册）" : "紧急热键未就绪，自动输入禁用";
        _timer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => RefreshButtons(), Dispatcher);
        _ready = true;
        Loaded += async (_, _) => await LoadProfilesAsync();
        Closing += ClosingAsync;
        if (store is null) Loaded += (_, _) => InitializeTray();
    }
    internal bool InitializeTray() { _tray ??= new TrayIcon(this, ReturnToMain, PauseTask, StopTask, RequestExit); return _tray.Available; }
    internal void RequestExit() { _exitRequested = true; Close(); }
    private ProviderProfile? Selected => ProfileBox.SelectedItem as ProviderProfile;
    internal DesktopOverlayController TaskOverlay => _overlay;
    internal void ReturnToMain()
    {
        PauseTask(); _overlay.HideAll();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); _lifecycle.TechnicalEvent("USER_RETURN_TO_MAIN");
    }
    private bool Clean => (_coordinator is null || (_coordinator.Completion.IsCompleted && (_coordinator.Progress is null || _coordinator.Progress.Task.CleanupComplete))) && _gate.Status.Lease is null;
    private static bool Terminal(TaskState? state) => state is null or TaskState.Succeeded or TaskState.Partial or TaskState.Failed or TaskState.Cancelled;
    private async Task LoadProfilesAsync()
    {
        try
        {
            var previous = Selected?.Id; var profiles = await _store.ReadAsync(CancellationToken.None);
            ProfileBox.ItemsSource = profiles.Length == 0 ? new[] { ProviderConfiguration.DefaultDeepSeek() } : profiles;
            ProfileBox.SelectedItem = profiles.FirstOrDefault(p => p.Id == previous); if (ProfileBox.SelectedIndex < 0) ProfileBox.SelectedIndex = 0;
        }
        catch { ProfileBox.ItemsSource = null; StatusText.Text = "读取本机配置失败，请在设置中检查。"; }
        RefreshButtons();
    }
    private void RefreshButtons()
    {
        if (!_ready) return;
        var profile = Selected; var task = _coordinator?.Progress?.Task;
        if ((task?.PendingQuestion is not null || task?.PendingActionApproval is not null) && _questionGateRevision is { } questionRevision &&
            _gate.Status.Revision != questionRevision && _gate.Status.Reason == InputStopReason.Stopped)
        { _questionGateRevision = null; _ = StopSafelyAsync(); }
        bool assisted = UseControls.IsChecked == true;
        bool verified = profile is not null && DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: assisted);
        bool available = !_closing && !_resetting && !_answering && _starting is null && Clean && _gate.Status.HotkeysReady && verified && !string.IsNullOrWhiteSpace(TaskBox.Text);
        StartButton.IsEnabled = available && Terminal(task?.State);
        PauseButton.IsEnabled = StopButton.IsEnabled = !_closing && (_starting is not null || !Terminal(task?.State));
        ResumeButton.IsEnabled = available && task?.PendingQuestion is null && task?.State is (TaskState.Paused or TaskState.WaitingUser or TaskState.Interrupted) && task.ProviderProfileFingerprint == ProviderConfiguration.Fingerprint(profile!);
        _overlay.SetResumeAvailability(ResumeButton.IsEnabled);
        ReplyButton.IsEnabled = !_answering && !_closing && !_resetting && Clean && task?.State == TaskState.WaitingUser && task.PendingQuestion is not null;
        bool canConfigure = !_closing && !_resetting && !_answering && _starting is null && Clean && Terminal(task?.State);
        ProfileBox.IsEnabled = UseControls.IsEnabled = HighRiskMode.IsEnabled = canConfigure;
        if (!canConfigure && task is not null && _starting is null)
        { _updatingHighRisk = true; try { HighRiskMode.IsChecked = task.HighRiskEnabled; } finally { _updatingHighRisk = false; } }
        _overlay.SetHighRiskMode(HighRiskMode.IsChecked == true, canConfigure);
        NewTaskButton.IsEnabled = _overlay.NewTaskButton.IsEnabled = !_closing && !_resetting;
        bool canApprove = !_answering && !_closing && !_resetting && _starting is null && Clean &&
            task is { State: TaskState.AwaitingApproval, PendingActionApproval: not null, HighRiskEnabled: true };
        ApproveActionButton.IsEnabled = RejectActionButton.IsEnabled = canApprove;
        _overlay.SetApprovalAvailability(canApprove);
        bool canEnableHighRisk = available && DesktopOverlayController.CanEnableHighRisk(task) &&
            task!.ProviderProfileFingerprint == ProviderConfiguration.Fingerprint(profile!);
        EnableHighRiskButton.IsEnabled = canEnableHighRisk;
        _overlay.SetPermissionAvailability(canEnableHighRisk);
        ProfileStatus.Text = profile is null ? "请添加本机模型配置。" : $"{profile.Model} · {(verified ? assisted ? "辅助定位模式 · 先理解目标，再定位操作" : "纯视觉模式" : assisted ? "请在设置通过辅助定位测试" : "纯视觉定位尚未通过")} · 最多 50 次请求 / 30 个动作 / 10 分钟";
        ConnectionText.Text = verified ? "模型已连接" : "设置模型";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, verified ? "AccentBrush" : "MutedBrush");
        ConnectionButton.ToolTip = verified ? "模型验证已通过，点击管理连接" : "连接并验证你的模型后即可开始";
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e) => RefreshButtons();
    private void Task_Changed(object sender, TextChangedEventArgs e) => RefreshButtons();
    private void Example_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string example } && Terminal(_coordinator?.Progress?.Task.State) && _starting is null) { TaskBox.Text = example; TaskBox.Focus(); TaskBox.CaretIndex = TaskBox.Text.Length; } }
    private async void Task_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && StartButton.IsEnabled) { e.Handled = true; await BeginAsync(false); } }
    private void HighRiskMode_Changed(object sender, RoutedEventArgs e)
    { if (_ready && !_updatingHighRisk) RefreshButtons(); }
    private async void Start_Click(object sender, RoutedEventArgs e) => await BeginAsync(false);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await BeginAsync(true);
    private async Task BeginAsync(bool resume, HostedWindowIdentity? replyTarget = null)
    {
        var profile = Selected;
        var resumeCoordinator = resume ? _coordinator : null;
        if (_starting is not null || _closing || _resetting || _reportingFailure || !Clean || profile is null || !DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: UseControls.IsChecked == true) || string.IsNullOrWhiteSpace(TaskBox.Text))
        { await RevokeResumeApprovalAsync(resumeCoordinator, resume, _gate.Status.Reason); return; }
        bool highRiskEnabled = resume ? _coordinator!.Progress!.Task.HighRiskEnabled : HighRiskMode.IsChecked == true;
        using var starting = new CancellationTokenSource(); _starting = starting;
        var startFinished = _startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); RefreshButtons();
        try
        {
            await _clearPixels;
            starting.Token.ThrowIfCancellationRequested();
            if (!resume)
            {
                if (!Terminal(_coordinator?.Progress?.Task.State)) throw new InvalidOperationException("ACTIVE_TASK");
                _provider?.Dispose(); _provider = new(profile, _store);
                var ttl = TimeSpan.FromMilliseconds(profile.FrameTtlMs);
                var coordinator = new DesktopTaskCoordinator(_gate, _desktop, _controls, _provider, profile, new DesktopPolicyValidator(ttl, Environment.ProcessId),
                    run => new WindowsInputExecutor(_desktop, _gate, run, ttl), _registry, _overlay, controlsRequired: UseControls.IsChecked == true,
                    prepareDesktop: new CalculatorWindowLayout(_gate).PrepareAsync);
                _coordinator = coordinator; _receivedRevision = 0; _lastSummary = null; _lastInterpretation = null;
                _replyTarget = null;
                coordinator.DiagnosticFault += error => _lifecycle.Fault("COORDINATOR_FAILURE", error);
                coordinator.Changed += progress => Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(_coordinator, coordinator)) ReceiveProgress(progress); }));
                AddMessage("你", TaskBox.Text.Trim());
            }
            else
            {
                var old = _coordinator!.Progress!.Task;
                if (old.ProviderProfileFingerprint != ProviderConfiguration.Fingerprint(profile)) throw new InvalidOperationException("PROFILE_CHANGED");
                if (old.Goal != TaskBox.Text.Trim()) { await _coordinator.CorrectAsync(old.Id, TaskBox.Text.Trim()); AddMessage("你", TaskBox.Text.Trim()); }
            }
            _controls.Enabled = UseControls.IsChecked == true;
            _gate.Trip(InputStopReason.Paused); long revision = _gate.Status.Revision;
            var environment = await _desktop.GetEnvironmentAsync(starting.Token);
            starting.Token.ThrowIfCancellationRequested();
            _displays = environment.Displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
            SetOverlayDisplay(environment.Displays.First(d => d.IsPrimary)); _overlay.ClearQuestion(); Hide(); _overlay.HideAll();
            if (replyTarget is not null && (!replyTarget.IsCurrent() || !SetForegroundWindow(replyTarget.HostHwnd)))
                throw new InvalidOperationException("REPLY_TARGET_NOT_READY");
            for (int seconds = 3; seconds > 0; seconds--)
            {
                _overlay.Countdown(seconds); await Task.Delay(1000, starting.Token);
                if (_gate.Status.Revision != revision) throw new OperationCanceledException();
            }
            environment = await _desktop.GetEnvironmentAsync(starting.Token);
            if (environment.SessionState != DesktopSessionState.Available || environment.Foreground is null || environment.Foreground.ProcessId == Environment.ProcessId)
                throw new InvalidOperationException("TARGET_NOT_READY");
            var display = resume ? environment.Displays.First(d => d.Id == _coordinator!.Progress!.Task.SelectedMonitorId)
                : environment.Displays.OrderByDescending(d => Intersection(d.Bounds, environment.Foreground.WindowRect)).First();
            _displays = environment.Displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
            SetOverlayDisplay(display);
            if (resume) await _coordinator!.ResumeWithGateRevisionAsync(_coordinator.Progress!.Task.Id, revision, starting.Token);
            else await _coordinator!.StartAsync(new(TaskBox.Text.Trim(), ProviderConfiguration.Fingerprint(profile), display.Id, TaskBudget.Default, revision)
                { HighRiskEnabled = highRiskEnabled }, starting.Token);
        }
        catch (OperationCanceledException)
        {
            await RevokeResumeApprovalAsync(resumeCoordinator, resume, _gate.Status.Reason);
            _lifecycle.TechnicalEvent("BEGIN_CANCELLED");
            if (!_resetting && !_closing && !_reportingFailure)
            {
                StatusText.Text = "启动已取消 · 本次批准已撤销";
                _overlay.ShowStoppedStatus("启动已取消", "本次批准已撤销。可查看原任务，或直接开始新任务。", "BEGIN_CANCELLED");
            }
        }
        catch (Exception error)
        {
            await RevokeResumeApprovalAsync(resumeCoordinator, resume, _gate.Status.Reason);
            _lifecycle.Fault("BEGIN_FAILED", error);
            if (!_resetting && !_closing && !_reportingFailure)
            {
                StatusText.Text = "暂不能开始，本次批准已撤销；请检查目标软件、剩余预算和配置。";
                _overlay.ShowStoppedStatus("暂不能开始", StatusText.Text, "BEGIN_FAILED");
            }
        }
        finally { _starting = null; startFinished.TrySetResult(); if (ReferenceEquals(_startFinished, startFinished)) _startFinished = null; RefreshButtons(); }
    }
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private void SetOverlayDisplay(DisplayInfo display) { _overlay.SetDisplay(display); _overlayMonitorId = display.Id; }
    internal static async Task RevokeResumeApprovalAsync(DesktopTaskCoordinator? coordinator, bool resume, InputStopReason reason)
    {
        if (!resume || coordinator?.Progress is not { } progress) return;
        // Before ResumeAsync there is no active run for the gate's cancellation callback to revoke.
        // Cancel the local grant explicitly even when the countdown or foreground restoration fails.
        if (reason is InputStopReason.Stopped or InputStopReason.Shutdown)
            await coordinator.StopAsync(progress.Task.Id);
        else await coordinator.PauseAsync(progress.Task.Id);
    }
    internal void ReceiveProgress(AgentProgress p)
    {
        if (_closing || _resetting || _reportingFailure || _retiredTaskIds.Contains(p.Task.Id) || p.Revision < _receivedRevision) return;
        _receivedRevision = p.Revision; _displayedTaskId = p.Task.Id;
        StatusText.Text = DesktopOverlayController.StateName(p.Task.State);
        var modelReply = TaskPlanTracking.Describe(p.Task.Interpretation, p.Task.PlanProgress);
        ModelReplyText.Text = modelReply ?? "";
        ModelReplyPanel.Visibility = string.IsNullOrWhiteSpace(modelReply) ? Visibility.Collapsed : Visibility.Visible;
        string? interpretationReply = p.Task.Interpretation?.Reply;
        string interpretationKey = $"{p.Task.Id}:{interpretationReply}";
        if (!string.IsNullOrWhiteSpace(interpretationReply) && interpretationKey != _lastInterpretation)
        { _lastInterpretation = interpretationKey; AddMessage("模型理解", interpretationReply); }
        CurrentText.Text = string.IsNullOrWhiteSpace(p.Summary) ? $"{p.Current} · {p.Next}" : p.Summary;
        StatsText.Text = $"请求 {p.Task.Usage.ApiAttempts} · 动作 {p.Task.Usage.ActionsAttempted} · 观察 {p.Timings.ObservationMs} ms · 定位 {p.Timings.ControlsMs} ms · 模型 {p.Timings.ModelMs} ms · 输入 {p.Timings.InputMs} ms";
        bool question = p.Task.State == TaskState.WaitingUser && p.Task.CleanupComplete && p.Task.PendingQuestion is not null;
        bool approval = p.Task.State == TaskState.AwaitingApproval && p.Task.CleanupComplete && p.Task.PendingActionApproval is not null;
        ApprovalPanel.Visibility = approval ? Visibility.Visible : Visibility.Collapsed;
        ApprovalDetails.Text = approval ? DesktopOverlayController.DescribeApproval(p.Task.PendingActionApproval!) : "";
        ReplyPanel.Visibility = question ? Visibility.Visible : Visibility.Collapsed;
        EnableHighRiskButton.Visibility = DesktopOverlayController.CanEnableHighRisk(p.Task) ? Visibility.Visible : Visibility.Collapsed;
        if (_shownQuestion != p.Task.PendingQuestion?.Id) { ReplyBox.Clear(); _shownQuestion = p.Task.PendingQuestion?.Id; _questionGateRevision = null; }
        if (_shownApproval != p.Task.PendingActionApproval?.Id) { _shownApproval = p.Task.PendingActionApproval?.Id; _questionGateRevision = null; }
        if (question || approval) _questionGateRevision ??= _gate.Status.Revision;
        if (_overlayMonitorId != p.Task.SelectedMonitorId && _displays.TryGetValue(p.Task.SelectedMonitorId, out var display)) SetOverlayDisplay(display);
        _overlay.Update(p, !IsVisible || question || approval);
        if (p.Task.CleanupComplete)
        {
            if (_desktop.CurrentFrame is { } observed) _replyTarget = HostedWindowIdentity.TryCapture(observed.Foreground);
            _clearPixels = _desktop.ClearAsync();
            string key = $"{p.Task.Epoch}:{p.Task.State}:{p.Summary}";
            if (!string.IsNullOrWhiteSpace(p.Summary) && key != _lastSummary) { _lastSummary = key; AddMessage("助手", p.Summary); }
            // Keep interruptions and failures in the same card; only the user opens the main window.
        }
        string lifecycleKey = $"{p.Task.Id}:{p.Task.Epoch}:{p.Task.State}:{p.Task.CleanupComplete}:{p.Task.LastError?.Code}:{p.Task.Usage.ApiAttempts}:{p.Task.Usage.ActionsAttempted}";
        if (lifecycleKey != _lastLifecycleKey) { _lastLifecycleKey = lifecycleKey; _lifecycle.Progress(p, IsVisible, _overlay.Hud.IsVisible,
            WindowState == WindowState.Minimized, _overlay.Hud.WindowState == WindowState.Minimized); }
        RefreshButtons();
    }
    private void AddMessage(string who, string text)
    {
        var message = new TextBlock { Text = $"{who}\n{text}", TextWrapping = TextWrapping.Wrap, FontSize = 13 }; ThemeManager.BindText(message);
        var bubble = new Border { CornerRadius = new(10), Padding = new(12), Margin = new(0, 8, 0, 0), Child = message };
        bubble.SetResourceReference(Border.BackgroundProperty, who == "你" ? "AccentSoftBrush" : "SurfaceAltBrush");
        Messages.Children.Add(bubble);
        while (Messages.Children.Count > 60) Messages.Children.RemoveAt(0); ChatScroll.ScrollToEnd();
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => PauseTask();
    private async void Reply_Click(object sender, RoutedEventArgs e) => await AnswerQuestionAsync(ReplyBox.Text);
    private async Task ResumeFromCardAsync()
    {
        if (_answering || _starting is not null || _closing || _resetting || _reportingFailure || !ResumeButton.IsEnabled ||
            _coordinator?.Progress?.Task is not { } task) return;
        TaskBox.Text = task.Goal;
        var target = _replyTarget is { } previous && previous.IsCurrent() ? previous : null;
        await BeginAsync(true, target);
    }
    private async void EnableHighRisk_Click(object sender, RoutedEventArgs e)
    { if (_coordinator?.Progress?.Task is { } task) await EnableHighRiskAndResumeAsync(task.Lease); }
    private async Task EnableHighRiskAndResumeAsync(Lease lease)
    {
        if (_answering || _starting is not null || _closing || _resetting || _reportingFailure || !Clean ||
            _coordinator?.Progress?.Task is not { } task || task.Lease != lease || !EnableHighRiskButton.IsEnabled ||
            !DesktopOverlayController.CanEnableHighRisk(task))
        { _overlay.SetPermissionBusy(false); RefreshButtons(); return; }
        _answering = true; _overlay.SetPermissionBusy(true); RefreshButtons();
        long generation = _interactionGeneration, gateRevision = _gate.Status.Revision;
        var coordinator = _coordinator;
        var target = _replyTarget is { } previous && previous.IsCurrent() ? previous : null;
        try
        {
            await coordinator.Completion;
            if (generation != _interactionGeneration || !ReferenceEquals(_coordinator, coordinator) || _closing || _resetting ||
                _gate.Status.Revision != gateRevision) return;
            bool enabled = await coordinator.EnableHighRiskAsync(lease, CancellationToken.None);
            if (generation != _interactionGeneration || !ReferenceEquals(_coordinator, coordinator) || _closing || _resetting) return;
            if (!enabled)
            {
                if (coordinator.Progress is { } unchanged) ReceiveProgress(unchanged);
                _overlay.ShowComposerError("当前任务状态已变化，尚未启用权限。请核对原任务后再继续。");
                return;
            }
            if (_gate.Status.Revision != gateRevision)
            { await RevokeResumeApprovalAsync(coordinator, true, _gate.Status.Reason); return; }
            AddMessage("你", "为本任务启用高风险操作并继续；提交前仍需逐次确认。");
            TaskBox.Text = task.Goal;
            await BeginAsync(true, target);
        }
        catch (Exception error)
        {
            _lifecycle.Fault("ENABLE_HIGH_RISK_FAILED", error);
            if (generation == _interactionGeneration && !_closing && !_resetting)
            {
                if (coordinator.Progress is { } progress) ReceiveProgress(progress);
                _overlay.ShowComposerError("暂未能继续，原任务和计划已保留。请核对当前状态后重试。");
            }
        }
        finally
        {
            if (generation == _interactionGeneration)
            { _answering = false; _overlay.SetPermissionBusy(false); RefreshButtons(); }
        }
    }
    private async Task StartNextTaskAsync(string goal)
    {
        if (_answering || _starting is not null || _closing || _resetting || string.IsNullOrWhiteSpace(goal) ||
            !(_overlay.IsNewTaskDraft && _coordinator is null && Clean) && _coordinator?.Progress?.Task is not { State: TaskState.Succeeded, CleanupComplete: true }) return;
        if (Selected is not { } selected || !_gate.Status.HotkeysReady || !DesktopTaskCoordinator.IsProfileVerified(selected, controlsRequired: UseControls.IsChecked == true))
        { _overlay.ShowComposerError("模型配置或紧急热键尚未就绪，请在主界面设置中检查。输入已保留。"); return; }
        _answering = true;
        long generation = _interactionGeneration;
        var coordinator = _coordinator;
        var target = _replyTarget is { } previous && previous.IsCurrent() ? previous : null;
        try
        {
            if (coordinator is not null) await coordinator.Completion;
            if (generation != _interactionGeneration || _resetting) return;
            TaskBox.Text = goal.Trim();
            await BeginAsync(false, target);
        }
        finally { if (generation == _interactionGeneration) { _answering = false; _overlay.SetComposerBusy(false); RefreshButtons(); } }
    }
    private async Task AnswerQuestionAsync(string answer)
    {
        if (_answering || _closing || _resetting || string.IsNullOrWhiteSpace(answer) || _coordinator?.Progress?.Task is not { PendingQuestion: { } question } task) return;
        _answering = true; RefreshButtons();
        long generation = _interactionGeneration;
        var coordinator = _coordinator;
        var target = _replyTarget;
        try
        {
            await coordinator.Completion;
            if (generation != _interactionGeneration || !ReferenceEquals(_coordinator, coordinator) || _resetting) return;
            await coordinator.AnswerAsync(task.Lease, question.Id, answer);
            if (generation != _interactionGeneration || _resetting) return;
            AddMessage("你", answer.Trim());
            TaskBox.Text = task.Goal;
            await BeginAsync(true, target);
        }
        catch
        {
            if (generation == _interactionGeneration && !_resetting)
            {
                StatusText.Text = "回复未能继续，请核对目标软件后重试；原任务已保留。";
                if (_coordinator?.Progress is { } current) ReceiveProgress(current);
            }
        }
        finally { if (generation == _interactionGeneration) { _answering = false; _overlay.SetComposerBusy(false); RefreshButtons(); } }
    }
    private async void ApproveAction_Click(object sender, RoutedEventArgs e)
    { if (_coordinator?.Progress?.Task.PendingActionApproval is { } approval) await AnswerActionApprovalAsync(approval.Id, true); }
    private async void RejectAction_Click(object sender, RoutedEventArgs e)
    { if (_coordinator?.Progress?.Task.PendingActionApproval is { } approval) await AnswerActionApprovalAsync(approval.Id, false); }
    private async Task AnswerActionApprovalAsync(Guid approvalId, bool approve)
    {
        if (_answering || _closing || _resetting || _starting is not null ||
            _coordinator?.Progress?.Task is not { State: TaskState.AwaitingApproval, HighRiskEnabled: true, PendingActionApproval: { } approval } task ||
            approval.Id != approvalId) return;
        _answering = true; RefreshButtons();
        long generation = _interactionGeneration;
        var coordinator = _coordinator;
        var target = _replyTarget;
        try
        {
            await coordinator.Completion;
            if (generation != _interactionGeneration || !ReferenceEquals(_coordinator, coordinator) || _resetting) return;
            if (approve) await coordinator.ApproveActionAsync(task.Lease, approvalId);
            else await coordinator.RejectActionAsync(task.Lease, approvalId);
            if (generation != _interactionGeneration || _resetting) return;
            AddMessage("你", approve ? "批准这一步：" + approval.ActionDescription : "取消这一步操作。");
            TaskBox.Text = task.Goal;
            if (approve) await BeginAsync(true, target);
            else if (coordinator.Progress is { } progress) ReceiveProgress(progress);
        }
        catch
        {
            if (generation == _interactionGeneration && !_resetting)
            {
                StatusText.Text = "确认未能继续，尚未通过本次确认执行输入；请核对当前任务状态。";
                if (coordinator.Progress is { } progress) ReceiveProgress(progress);
                _overlay.SetApprovalBusy(false);
            }
        }
        finally { if (generation == _interactionGeneration) { _answering = false; RefreshButtons(); } }
    }
    private async void NewTask_Click(object sender, RoutedEventArgs e) => await ResetForNewTaskAsync();
    internal async Task ResetForNewTaskAsync()
    {
        if (_closing || _resetting) return;
        _resetting = true; ++_interactionGeneration;
        var coordinator = _coordinator;
        var startingFinished = _startFinished?.Task;
        _starting?.Cancel(); _gate.Trip(InputStopReason.Stopped);
        if ((coordinator?.Progress?.Task.Id ?? _displayedTaskId) is { } retired)
        {
            _retiredTaskIds.Add(retired); _overlay.RetireTask(retired);
            if (_retiredTaskIds.Count > 128) _retiredTaskIds.Remove(_retiredTaskIds.First());
        }
        _overlay.ShowNewTaskResetting(); RefreshButtons();
        try
        {
            if (coordinator?.Progress is { } current) await coordinator.StopAsync(current.Task.Id);
            if (startingFinished is not null) await startingFinished;
            if (coordinator is not null)
            {
                await coordinator.Completion;
                if (coordinator.Progress is { } stopped) await coordinator.StopAsync(stopped.Task.Id);
            }
            if (!Clean) throw new InvalidOperationException("TASK_CLEANUP_REQUIRED");
            await _clearPixels; await _desktop.ClearAsync();
            if (_closing) return;
            _coordinator = null; _provider?.Dispose(); _provider = null;
            _receivedRevision = 0; _lastSummary = _lastInterpretation = _lastLifecycleKey = null; _displayedTaskId = null;
            _shownQuestion = _shownApproval = null; _questionGateRevision = null; _replyTarget = null; _answering = false;
            TaskBox.Clear(); ReplyBox.Clear(); ReplyPanel.Visibility = ApprovalPanel.Visibility = ModelReplyPanel.Visibility = EnableHighRiskButton.Visibility = Visibility.Collapsed;
            ApprovalDetails.Text = ModelReplyText.Text = CurrentText.Text = StatsText.Text = "";
            StatusText.Text = "新任务 · 等待提示词输入";
            Messages.Children.Clear(); AddMessage("助手", "上一任务已结束。请输入新的任务。");
            HighRiskMode.IsChecked = false;
            if (_overlayMonitorId is null)
            {
                var environment = await _desktop.GetEnvironmentAsync(CancellationToken.None);
                _displays = environment.Displays.ToDictionary(d => d.Id, StringComparer.Ordinal);
                SetOverlayDisplay(environment.Displays.First(d => d.IsPrimary));
            }
            if (_closing) return;
            Hide(); _overlay.ShowNewTaskDraft();
            _lifecycle.TechnicalEvent("NEW_TASK_DRAFT_READY");
        }
        catch
        {
            if (!_closing)
            {
                StatusText.Text = "已停止输入，上一任务仍在收尾；稍后点击“新任务”重试。";
                _overlay.ShowComposerError(StatusText.Text);
            }
        }
        finally { _resetting = false; RefreshButtons(); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
    internal async Task HandleUnexpectedUiFailureAsync()
    {
        if (_closing || _reportingFailure) return;
        _reportingFailure = true; ++_interactionGeneration; _starting?.Cancel(); _gate.Trip(InputStopReason.InputFault);
        var coordinator = _coordinator;
        if ((coordinator?.Progress?.Task.Id ?? _displayedTaskId) is { } id)
        { _retiredTaskIds.Add(id); _overlay.RetireTask(id); }
        try
        {
            if (coordinator?.Progress is { } progress) await coordinator.StopAsync(progress.Task.Id);
            if (coordinator is not null)
            {
                await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                if (coordinator.Progress is { } stopped) await coordinator.StopAsync(stopped.Task.Id);
            }
        }
        catch (Exception error) { _lifecycle.Fault("UI_FAILURE_CLEANUP", error); }
        finally
        {
            _reportingFailure = false; _answering = false;
            if (!_closing)
            {
                StatusText.Text = "界面运行异常，已停止自动输入。技术诊断已记录，可开始新任务。";
                CurrentText.Text = "原因代码：UI_DISPATCHER_ERROR";
                _overlay.ShowStoppedStatus("运行已中断", StatusText.Text, "UI_DISPATCHER_ERROR");
                _lifecycle.TechnicalEvent("UI_FAILURE_CARD_VISIBLE"); RefreshButtons();
            }
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopTask();
    private void PauseTask() { _starting?.Cancel(); _gate.Trip(InputStopReason.Paused); if (_coordinator?.Progress is { } p) _ = _coordinator.PauseAsync(p.Task.Id); }
    private void StopTask() { _starting?.Cancel(); _gate.Trip(InputStopReason.Stopped); _ = StopSafelyAsync(); }
    private async Task StopSafelyAsync()
    {
        long generation = _interactionGeneration; var coordinator = _coordinator;
        try { if (coordinator?.Progress is { } p) await coordinator.StopAsync(p.Task.Id); }
        catch { if (generation == _interactionGeneration && !_resetting && !_closing) StatusText.Text = "输入已停止，任务仍在收尾。"; }
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _resetting || _starting is not null) return;
        long generation = _interactionGeneration;
        var coordinator = _coordinator;
        PauseTask();
        try
        {
            if (coordinator is not null) await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            if (generation != _interactionGeneration || _resetting || _closing) return;
            await StopSafelyAsync();
            if (generation != _interactionGeneration || _resetting || _closing) return;
            if (!Clean) throw new InvalidOperationException();
            _overlay.HideAll(); new SettingsWindow(_store, _gate) { Owner = this }.ShowDialog(); await LoadProfilesAsync();
        }
        catch { if (generation == _interactionGeneration && !_resetting && !_closing) StatusText.Text = "输入已停止；等待在途任务收尾后再打开设置。"; }
    }
    private async void ClosingAsync(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !_exitRequested && _tray?.Available == true) { e.Cancel = true; Hide(); return; }
        if (_allowClose) return; e.Cancel = true; if (_closing) return; _closing = true;
        _lifecycle.TechnicalEvent("MAIN_CLOSE_REQUESTED");
        _starting?.Cancel(); _gate.Trip(InputStopReason.Shutdown); RefreshButtons();
        try
        {
            await StopSafelyAsync(); if (_coordinator is not null) await _coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(4));
            if (!Clean) throw new InvalidOperationException();
            await _clearPixels; _controls.Dispose(); await _desktop.DisposeAsync(); _provider?.Dispose(); _overlay.Dispose(); _tray?.Dispose(); _timer.Stop(); _allowClose = true;
            // A clean idle task can finish synchronously inside Closing. Close on the next
            // dispatcher turn so WPF has left its current close notification first.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch { _closing = false; StatusText.Text = "输入已停止，后台仍在收尾；请稍后再次关闭。"; RefreshButtons(); }
    }
}
