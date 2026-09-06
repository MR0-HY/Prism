using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Presentation;

namespace DesktopAgent.Windows.Overlay;

internal sealed class DesktopOverlayController : IOverlayController, IDisposable
{
    internal sealed record ActionApprovalCard(Guid Id, string Details);
    private readonly Dispatcher _dispatcher;
    private readonly Window _hud, _border;
    internal readonly Expander Details = new() { Header = "计划与说明", IsExpanded = false, Margin = new(0, 6, 0, 12) };
    internal readonly Button CompactButton = new() { Content = "收起", Padding = new(8, 4, 8, 4), MinHeight = 26, FontSize = 11, Focusable = false };
    private readonly StackPanel _body = new();
    private readonly bool _persistAppearance;
    private double? _preferredX, _preferredY;
    private bool _compact;
    private readonly TextBlock _state = Label(15), _goal = Label(14), _current = Label(12), _next = Label(12);
    private readonly StackPanel _modelReplyPanel = new() { Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 10) };
    internal readonly TextBlock ModelReplyText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = ColorBrush("#29364D") };
    private readonly StackPanel _replyPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _question = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = ColorBrush("#18233A") };
    internal readonly TextBox ReplyBox = new() { Height = 64, MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal readonly Button ReplyButton = new() { Content = "发送并继续", Padding = new(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
    internal readonly Button NewTaskButton = new() { Content = "新任务", Padding = new(10, 7, 10, 7), Margin = new(0, 0, 6, 0), Focusable = false };
    internal readonly Button ResumeTaskButton = new() { Content = "继续", Padding = new(10, 7, 10, 7), Margin = new(0, 0, 6, 0), Visibility = Visibility.Collapsed, Focusable = false };
    internal readonly Button EnableHighRiskButton = new() { Content = "启用高风险并继续\n提交前仍需逐次确认", Padding = new(10, 7, 10, 7), Margin = new(0, 0, 0, 8), Visibility = Visibility.Collapsed, Focusable = false };
    internal readonly CheckBox HighRiskMode = new() { Content = "高风险操作（逐次确认）", IsChecked = false, Margin = new(0, 6, 0, 8) };
    internal readonly Button ApproveActionButton = new() { Content = "批准这一步", Padding = new(12, 7, 12, 7), Margin = new(8, 0, 0, 0) };
    internal readonly Button RejectActionButton = new() { Content = "取消", Padding = new(12, 7, 12, 7) };
    internal readonly TextBlock ApprovalDetails = new() { TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush("#503A16"), FontSize = 13 };
    private readonly Border _approvalPanel = new() { Visibility = Visibility.Collapsed, Padding = new(10), Margin = new(0, 4, 0, 10), CornerRadius = new(8), Background = ColorBrush("#FFF6E6"), BorderBrush = ColorBrush("#E4B969"), BorderThickness = new(1) };
    private readonly HashSet<Guid> _retiredTasks = [];
    private Guid? _questionId;
    private Guid? _completedTaskId;
    private Guid? _approvalId;
    private Guid? _retainedTaskId;
    private Lease? _permissionLease;
    private bool _permissionPending;
    private bool _retainedCanResume;
    private bool _newTaskDraft, _submissionPending, _approvalPending, _updatingMode, _modeChangeAllowed;
    private readonly List<Button> _executionButtons = [];
    private bool HasComposer => _questionId.HasValue || _completedTaskId.HasValue || _newTaskDraft;
    private bool Interactive => HasComposer || _approvalId.HasValue || _retainedTaskId.HasValue;
    internal bool IsNewTaskDraft => _newTaskDraft;
    private DisplayInfo? _display;
    private Lease _lease;
    private bool _visible, _running, _disposed;
    private int _suppressed;
    internal Window Hud => _hud;
    internal Window Rainbow => _border;
    internal string StatusReason => _next.Text;
    public event Action? Pause, Stop, Chat;
    public event Action? NewTaskRequested;
    public event Action? ResumeRequested;
    public event Action<Lease>? EnableHighRiskRequested;
    public event Action<bool>? HighRiskModeChanged;
    public event Action<Guid, bool>? ActionApprovalAnswered;
    public event Action<string>? Reply;
    public event Action<string>? NewTask;
    public bool GlassRequested { get; private set; }

    public DesktopOverlayController(Dispatcher dispatcher, bool persistAppearance = false)
    {
        _dispatcher = dispatcher; _persistAppearance = persistAppearance;
        if (persistAppearance) { var preferences = AppearancePreferences.Read(); _preferredX = preferences.HudX; _preferredY = preferences.HudY; }
        var panel = new StackPanel();
        _modelReplyPanel.Children.Add(new TextBlock { Text = "模型理解", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ColorBrush("#67738A"), Margin = new(0, 0, 0, 4) });
        _modelReplyPanel.Children.Add(new ScrollViewer { Content = ModelReplyText, MaxHeight = 82, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        System.Windows.Automation.AutomationProperties.SetAutomationId(ModelReplyText, "ModelInterpretation");
        var header = new DockPanel { Margin = new(0, 0, 0, 12), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll };
        var brand = new TextBlock { Text = "◈", FontSize = 22, Margin = new(0, 0, 9, 0) }; brand.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(CompactButton, Dock.Right); header.Children.Add(CompactButton); header.Children.Add(brand); header.Children.Add(_state);
        panel.Children.Add(header); panel.Children.Add(_body);
        _current.FontSize = 15; _current.FontWeight = FontWeights.Medium; _current.MaxHeight = 72;
        var detailContent = new StackPanel(); detailContent.Children.Add(_goal); detailContent.Children.Add(_modelReplyPanel); Details.Content = detailContent;
        _body.Children.Add(_current); _body.Children.Add(Details); _body.Children.Add(_next);
        _replyPanel.Children.Add(new ScrollViewer { Content = _question, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 10) });
        _replyPanel.Children.Add(ReplyBox); _replyPanel.Children.Add(ReplyButton);
        _replyPanel.Children.Add(new TextBlock { Text = "等待回复时键鼠已释放 · Ctrl+Enter发送", FontSize = 11, Foreground = Brushes.DimGray, Margin = new(0, 5, 0, 8) });
        var approvalContent = new StackPanel();
        approvalContent.Children.Add(new TextBlock { Text = "请确认这一步操作", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) });
        approvalContent.Children.Add(new ScrollViewer { Content = ApprovalDetails, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var approvalButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        approvalButtons.Children.Add(RejectActionButton); approvalButtons.Children.Add(ApproveActionButton);
        approvalContent.Children.Add(approvalButtons);
        approvalContent.Children.Add(new TextBlock { Text = "批准仅用于所显示的这一步；画面或目标变化后会重新核对。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray, Margin = new(0, 7, 0, 0) });
        _approvalPanel.Child = approvalContent;
        _body.Children.Add(_approvalPanel);
        _body.Children.Add(_replyPanel);
        _body.Children.Add(HighRiskMode);
        _body.Children.Add(EnableHighRiskButton);
        System.Windows.Automation.AutomationProperties.SetAutomationId(EnableHighRiskButton, "EnableHighRiskAndResume");
        EnableHighRiskButton.Click += (_, _) =>
        {
            if (_permissionLease is not { } permissionLease || _permissionPending || !EnableHighRiskButton.IsEnabled) return;
            _permissionPending = true; EnableHighRiskButton.IsEnabled = false;
            EnableHighRiskRequested?.Invoke(permissionLease);
        };
        HighRiskMode.Checked += (_, _) => ChangeHighRiskMode(); HighRiskMode.Unchecked += (_, _) => ChangeHighRiskMode();
        ApproveActionButton.Click += (_, _) => SubmitApproval(true);
        RejectActionButton.Click += (_, _) => SubmitApproval(false);
        ReplyButton.Click += (_, _) => SubmitReply();
        ReplyBox.TextChanged += (_, _) => ReplyButton.IsEnabled = HasComposer && !_submissionPending && !string.IsNullOrWhiteSpace(ReplyBox.Text);
        ReplyBox.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; SubmitReply(); } };
        var buttons = new WrapPanel { Margin = new(0, 8, 0, 5) };
        NewTaskButton.Click += (_, _) => NewTaskRequested?.Invoke(); buttons.Children.Add(NewTaskButton);
        ResumeTaskButton.Click += (_, _) => { if (ResumeTaskButton.IsEnabled) ResumeRequested?.Invoke(); }; buttons.Children.Add(ResumeTaskButton);
        foreach (var item in new (string Text, Action Action)[] { ("返回主界面", () => Chat?.Invoke()), ("暂停", () => Pause?.Invoke()), ("停止", () => Stop?.Invoke()) })
        {
            var button = new Button { Content = item.Text, Padding = new(10, 7, 10, 7), Margin = new(0, 0, 6, 0), Focusable = false };
            button.Click += (_, _) => item.Action(); buttons.Children.Add(button);
            if (item.Text != "返回主界面") _executionButtons.Add(button);
        }
        _body.Children.Add(buttons);
        _body.Children.Add(new TextBlock { Text = "Ctrl+Alt+F8 暂停 · Ctrl+Alt+F9 停止", FontSize = 11 });
        _hud = Window(370, 280);
        _hud.Title = "Prism · 任务";
        _hud.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; if (_completedTaskId.HasValue || _newTaskDraft ||
            _retainedTaskId.HasValue && !_questionId.HasValue && !_approvalId.HasValue) Chat?.Invoke(); else Stop?.Invoke(); } };
        var shell = new Border { Padding = new(20), CornerRadius = new(18), BorderThickness = new(1), Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        shell.SetResourceReference(Border.BackgroundProperty, "GlassBrush"); shell.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); _hud.Content = shell;
        BindTheme(panel);
        _approvalPanel.SetResourceReference(Border.BackgroundProperty, "WarningBrush"); _approvalPanel.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        ReplyButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton"); ApproveActionButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        CompactButton.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        Details.Expanded += (_, _) => ResizeCard(); Details.Collapsed += (_, _) => ResizeCard();
        CompactButton.Click += (_, _) => { if (CompactButton.IsEnabled) { _compact = !_compact; ApplyVisibility(); ResizeCard(); } };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBlock || _running) return;
            try { _hud.DragMove(); RememberPosition(); } catch (InvalidOperationException) { }
        };
        _hud.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_hud).Handle;
            SetWindowLongPtrW(hwnd, -20, GetWindowLongPtrW(hwnd, -20) | 0x08000080);
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Margins(-1, -1, -1, -1);
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int backdrop = 3, corner = 2;
            GlassRequested = DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4) == 0;
            DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
            ThemeManager.Chrome(_hud);
        };
        _border = Window(100, 100); _border.AllowsTransparency = true; _border.IsHitTestVisible = false;
        var colors = new[] { "#FF6584", "#FFBB70", "#F5E77C", "#7FDCA0", "#72CEFA", "#A68CFF", "#F29ADA" };
        var gradient = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        for (int i = 0; i < colors.Length; i++) gradient.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(colors[i]), i / 6d));
        var edge = new Border { BorderThickness = new(3), BorderBrush = gradient };
        if (SystemParameters.ClientAreaAnimation) edge.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.65, 1, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _border.Content = edge;
        _border.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_border).Handle;
            SetWindowLongPtrW(hwnd, -20, GetWindowLongPtrW(hwnd, -20) | 0x080800A0);
            HwndSource.FromHwnd(hwnd).AddHook((nint h, int message, nint w, nint l, ref bool handled) =>
            { if (message == 0x84) { handled = true; return -1; } return 0; });
        };
    }
    private static void BindTheme(DependencyObject node)
    {
        if (node is TextBlock text) ThemeManager.BindText(text);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) BindTheme(child);
    }
    private void ResizeCard() { if (!_disposed && _display is not null) Position(_hud, Corners(_display)[0]); }
    private void RememberPosition()
    {
        if (_display is null || !GetWindowRect(new WindowInteropHelper(_hud).Handle, out var rect)) return;
        var area = _display.WorkArea;
        _preferredX = Math.Clamp((rect.Left - area.Left) / (double)Math.Max(1, area.Width - (rect.Right - rect.Left)), 0, 1);
        _preferredY = Math.Clamp((rect.Top - area.Top) / (double)Math.Max(1, area.Height - (rect.Bottom - rect.Top)), 0, 1);
        if (_persistAppearance) (AppearancePreferences.Read() with { HudX = _preferredX, HudY = _preferredY }).Save();
    }
    private static TextBlock Label(double size) => new() { FontSize = size, TextWrapping = TextWrapping.Wrap, MaxHeight = size * 3.3, Margin = new(0, 0, 0, 9), Foreground = ColorBrush("#29364D") };
    private static Brush ColorBrush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private static Window Window(double width, double height) => new() { Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = Brushes.Transparent, FontFamily = new("Microsoft YaHei UI") };

    public void SetDisplay(DisplayInfo display)
    {
        _dispatcher.VerifyAccess(); _display = display;
        Position(_border, display.Bounds); Position(_hud, Corners(display)[0]);
    }
    private PhysicalRect[] Corners(DisplayInfo d)
    {
        _body.Measure(new Size(328, double.PositiveInfinity));
        int height = _compact ? 84 : Math.Max(215, (int)Math.Ceiling(_body.DesiredSize.Height) + 94);
        int w = (int)Math.Ceiling(370 * d.DpiX / 96d), h = (int)Math.Ceiling(height * d.DpiY / 96d), pad = 18;
        var r = d.WorkArea;
        w = Math.Min(w, r.Width - 2 * pad); h = Math.Min(h, r.Height - 2 * pad);
        var corners = new PhysicalRect[] { new((int)r.Right - w - pad, r.Top + pad, w, h), new(r.Left + pad, r.Top + pad, w, h),
            new((int)r.Right - w - pad, (int)r.Bottom - h - pad, w, h), new(r.Left + pad, (int)r.Bottom - h - pad, w, h) };
        if (_preferredX is { } x && _preferredY is { } y && double.IsFinite(x) && double.IsFinite(y))
            return [new(r.Left + pad + (int)(Math.Clamp(x, 0, 1) * Math.Max(0, r.Width - w - 2 * pad)), r.Top + pad + (int)(Math.Clamp(y, 0, 1) * Math.Max(0, r.Height - h - 2 * pad)), w, h), .. corners];
        return corners;
    }
    private static void Position(Window window, PhysicalRect rect)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, -1, rect.Left, rect.Top, rect.Width, rect.Height, 0x0010)) throw new InvalidOperationException("OVERLAY_POSITION_FAILED");
    }
    public void Update(AgentProgress progress, bool visible)
    {
        _dispatcher.VerifyAccess(); if (_retiredTasks.Contains(progress.Task.Id)) return;
        if (_lease.TaskId != progress.Task.Id) { Details.IsExpanded = false; _compact = false; }
        _lease = progress.Task.Lease; _visible = visible; _running = progress.Task.State == TaskState.Running;
        _state.Text = StateName(progress.Task.State); _goal.Text = progress.Task.Goal; _current.Text = progress.Current; _next.Text = progress.Summary.Length > 0 ? progress.Summary : progress.Next;
        SetModelReply(TaskPlanTracking.Describe(progress.Task.Interpretation, progress.Task.PlanProgress));
        SetQuestion(progress.Task.State == TaskState.WaitingUser && progress.Task.CleanupComplete ? progress.Task.PendingQuestion : null,
            progress.Task.State == TaskState.Succeeded && progress.Task.CleanupComplete ? progress.Task.Id : null);
        bool retained = progress.Task.State is (TaskState.Paused or TaskState.WaitingUser or TaskState.Interrupted or TaskState.Partial or TaskState.Failed or TaskState.Cancelled);
        _retainedTaskId = retained ? progress.Task.Id : null;
        _retainedCanResume = retained && progress.Task.CleanupComplete && progress.Task.PendingQuestion is null &&
            progress.Task.PendingActionApproval is null && progress.Task.State is (TaskState.Paused or TaskState.WaitingUser or TaskState.Interrupted) &&
            progress.Task.Usage.ActiveMs < progress.Task.Budget.ActiveMsLimit && progress.Task.Usage.ApiAttempts < progress.Task.Budget.RequestLimit &&
            progress.Task.Usage.ActionsAttempted < progress.Task.Budget.ActionLimit;
        ResumeTaskButton.Visibility = _retainedCanResume ? Visibility.Visible : Visibility.Collapsed;
        ResumeTaskButton.IsEnabled = _retainedCanResume;
        _next.MaxHeight = retained ? double.PositiveInfinity : 40;
        if (retained && progress.Task.LastError is { } error) _next.Text += "\n原因代码：" + error.Code;
        SetActionApproval(progress.Task.State == TaskState.AwaitingApproval && progress.Task.CleanupComplete && progress.Task.PendingActionApproval is { } approval
            ? new(approval.Id, DescribeApproval(approval)) : null);
        SetHighRiskMode(progress.Task.HighRiskEnabled, progress.Task.CleanupComplete && progress.Task.State is
            (TaskState.Succeeded or TaskState.Partial or TaskState.Failed or TaskState.Cancelled));
        SetPermissionTask(progress.Task);
        ApplyInteractionStyle();
        ApplyVisibility();
    }
    private void SetModelReply(string? reply)
    {
        bool previouslyVisible = _modelReplyPanel.Visibility == Visibility.Visible;
        ModelReplyText.Text = reply ?? "";
        _modelReplyPanel.Visibility = string.IsNullOrWhiteSpace(reply) ? Visibility.Collapsed : Visibility.Visible;
        if (previouslyVisible != (_modelReplyPanel.Visibility == Visibility.Visible) && _display is not null) Position(_hud, Corners(_display)[0]);
    }
    private void SubmitReply()
    {
        if (!HasComposer || !ReplyButton.IsEnabled || string.IsNullOrWhiteSpace(ReplyBox.Text)) return;
        _submissionPending = true; ReplyButton.IsEnabled = false;
        if (_completedTaskId.HasValue || _newTaskDraft) NewTask?.Invoke(ReplyBox.Text.Trim());
        else Reply?.Invoke(ReplyBox.Text.Trim());
    }
    private void SetQuestion(UserQuestion? question, Guid? completedTaskId = null)
    {
        bool changed = _questionId != question?.Id || _completedTaskId != completedTaskId || _newTaskDraft;
        _newTaskDraft = false;
        if (changed) { _submissionPending = false; ReplyBox.Clear(); }
        _questionId = question?.Id;
        _completedTaskId = completedTaskId;
        _replyPanel.Visibility = HasComposer ? Visibility.Visible : Visibility.Collapsed;
        // A question is an interactive window: make it reachable from the taskbar/Alt+Tab too.
        _hud.ShowInTaskbar = Interactive;
        _next.Visibility = question is null ? Visibility.Visible : Visibility.Collapsed;
        if (question is not null) { _question.Text = question.Question; _state.Text = "等你回复"; }
        if (completedTaskId.HasValue) { _question.Text = "接下来想让我做什么？"; _state.Text = "本次任务完成，等待提示词输入"; }
        ReplyButton.Content = completedTaskId.HasValue ? "发送新任务" : "发送并继续";
        ReplyBox.MaxLength = completedTaskId.HasValue ? 4000 : 1000;
        foreach (var button in _executionButtons) button.Visibility = completedTaskId.HasValue ? Visibility.Collapsed : Visibility.Visible;
        ReplyButton.IsEnabled = HasComposer && !_submissionPending && !string.IsNullOrWhiteSpace(ReplyBox.Text);
        ApplyInteractionStyle();
        if (changed && _display is not null) Position(_hud, Corners(_display)[0]);
    }
    private void ApplyInteractionStyle()
    {
        _hud.ShowInTaskbar = Interactive;
        var hwnd = new WindowInteropHelper(_hud).EnsureHandle();
        var style = GetWindowLongPtrW(hwnd, -20);
        SetWindowLongPtrW(hwnd, -20, !Interactive
            ? (style | 0x08000080) & ~((nint)0x00040000)
            : (style & ~((nint)0x08000080)) | 0x00040000);
    }
    private void ChangeHighRiskMode()
    { if (!_updatingMode && _modeChangeAllowed) HighRiskModeChanged?.Invoke(HighRiskMode.IsChecked == true); }
    internal void SetHighRiskMode(bool enabled, bool canChange)
    {
        _updatingMode = true;
        try { HighRiskMode.IsChecked = enabled; HighRiskMode.IsEnabled = _modeChangeAllowed = canChange; }
        finally { _updatingMode = false; }
    }
    internal static bool CanEnableHighRisk(TaskContext? task) => task is
        { State: TaskState.WaitingUser, CleanupComplete: true, HighRiskEnabled: false, PendingActionApproval: null, MessageCommitAttempted: false } &&
        task.LastError?.Code is "HIGH_IMPACT_MANUAL" or "MESSAGE_COMMIT_DISABLED" or "ACCOUNT_ENTRY_PERMISSION_REQUIRED";
    private void SetPermissionTask(TaskContext? task)
    {
        Lease? lease = CanEnableHighRisk(task) ? task!.Lease : null;
        if (_permissionLease != lease) _permissionPending = false;
        _permissionLease = lease;
        EnableHighRiskButton.Visibility = lease.HasValue ? Visibility.Visible : Visibility.Collapsed;
        EnableHighRiskButton.IsEnabled = lease.HasValue && !_permissionPending;
    }
    internal void SetPermissionAvailability(bool available) => EnableHighRiskButton.IsEnabled =
        _permissionLease.HasValue && !_permissionPending && available;
    internal void SetPermissionBusy(bool busy)
    { _permissionPending = busy; EnableHighRiskButton.IsEnabled = _permissionLease.HasValue && !busy; }
    internal void SetActionApproval(ActionApprovalCard? approval, bool canAnswer = true)
    {
        if (approval is not null && _approvalId != approval.Id) Details.IsExpanded = false;
        if (_approvalId != approval?.Id) _approvalPending = false;
        _approvalId = approval?.Id;
        ApprovalDetails.Text = approval?.Details ?? "";
        _approvalPanel.Visibility = approval is null ? Visibility.Collapsed : Visibility.Visible;
        ApproveActionButton.IsEnabled = RejectActionButton.IsEnabled = approval is not null && canAnswer && !_approvalPending;
        if (approval is not null) { _state.Text = "等待你确认这一步"; _running = false; }
        ApplyInteractionStyle();
        if (_display is not null) Position(_hud, Corners(_display)[0]);
        ApplyVisibility();
    }
    internal static string DescribeApproval(PendingActionApproval approval)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(approval.OriginalGoal)) lines.Add("本次请求：" + approval.OriginalGoal);
        if (!string.IsNullOrWhiteSpace(approval.Summary)) lines.Add(approval.Summary);
        lines.Add("这一步：" + approval.ActionDescription);
        lines.Add("对象：" + approval.Target);
        if (!string.IsNullOrWhiteSpace(approval.Recipient)) lines.Add("收件人：" + approval.Recipient);
        if (!string.IsNullOrWhiteSpace(approval.Message)) lines.Add("完整消息：\n" + approval.Message);
        lines.Add("预期变化：" + approval.Expected);
        return string.Join("\n\n", lines);
    }
    private void SubmitApproval(bool approve)
    {
        if (_approvalId is not { } id || _approvalPending || !ApproveActionButton.IsEnabled || !RejectActionButton.IsEnabled) return;
        _approvalPending = true; ApproveActionButton.IsEnabled = RejectActionButton.IsEnabled = false;
        ActionApprovalAnswered?.Invoke(id, approve);
    }
    internal void SetComposerBusy(bool busy)
    { _submissionPending = busy; ReplyButton.IsEnabled = HasComposer && !busy && !string.IsNullOrWhiteSpace(ReplyBox.Text); }
    internal void SetApprovalBusy(bool busy)
    { _approvalPending = busy; ApproveActionButton.IsEnabled = RejectActionButton.IsEnabled = _approvalId.HasValue && !busy; }
    internal void SetApprovalAvailability(bool available) =>
        ApproveActionButton.IsEnabled = RejectActionButton.IsEnabled = _approvalId.HasValue && available && !_approvalPending;
    internal void SetResumeAvailability(bool available) => ResumeTaskButton.IsEnabled = _retainedCanResume && available;
    internal void RetireTask(Guid id)
    {
        if (id != Guid.Empty) _retiredTasks.Add(id);
        if (_retiredTasks.Count > 128) _retiredTasks.Remove(_retiredTasks.First());
    }
    internal void ShowNewTaskResetting()
    {
        ClearQuestion(); SetModelReply(null); _visible = true; _running = false;
        _state.Text = "正在结束上一任务"; _goal.Text = "准备新任务";
        _current.Text = "已停止输入，等待后台收尾"; _next.Text = "完成清理后即可输入新的任务";
        NewTaskButton.IsEnabled = false; SetHighRiskMode(false, false); ApplyVisibility();
    }
    internal void ShowNewTaskDraft()
    {
        ClearQuestion(); SetModelReply(null); _newTaskDraft = true; _submissionPending = false; _lease = default;
        _visible = true; _running = false; _state.Text = "新任务 · 等待提示词输入"; _goal.Text = "上一任务已结束";
        _current.Text = "桌面控制已释放"; _next.Text = ""; _next.Visibility = Visibility.Collapsed;
        _question.Text = "接下来想让我做什么？"; _replyPanel.Visibility = Visibility.Visible;
        ReplyBox.Clear(); ReplyBox.MaxLength = 4000; ReplyButton.Content = "发送新任务"; ReplyButton.IsEnabled = false;
        SetHighRiskMode(false, true);
        NewTaskButton.IsEnabled = true;
        foreach (var button in _executionButtons) button.Visibility = Visibility.Collapsed;
        ApplyInteractionStyle(); if (_display is not null) Position(_hud, Corners(_display)[0]); ApplyVisibility();
        _hud.Activate(); ReplyBox.Focus();
    }
    internal void ShowComposerError(string message)
    { _current.Text = message; NewTaskButton.IsEnabled = true; SetComposerBusy(false); }
    internal void ShowStoppedStatus(string state, string reason, string code)
    {
        ClearQuestion(); _retainedTaskId = _lease.TaskId == Guid.Empty ? Guid.NewGuid() : _lease.TaskId;
        _retainedCanResume = false; ResumeTaskButton.Visibility = Visibility.Collapsed;
        _state.Text = state; _current.Text = "已停止自动输入"; _next.Text = reason + "\n原因代码：" + code;
        _next.Visibility = Visibility.Visible; _next.MaxHeight = double.PositiveInfinity;
        _visible = true; _running = false;
        ApplyInteractionStyle(); if (_display is not null) Position(_hud, Corners(_display)[0]); ApplyVisibility();
    }
    internal void ClearQuestion()
    {
        _retainedTaskId = null; _retainedCanResume = false; ResumeTaskButton.Visibility = Visibility.Collapsed;
        SetPermissionTask(null);
        SetQuestion(null); SetActionApproval(null);
    }
    internal static string StateName(TaskState state) => state switch { TaskState.Running => "正在操作桌面", TaskState.Pausing => "正在暂停并收尾", TaskState.Paused => "已暂停", TaskState.WaitingUser => "等待你处理", TaskState.AwaitingApproval => "等待你确认这一步", TaskState.Interrupted => "已中断", TaskState.Succeeded => "任务完成", TaskState.Cancelled => "已停止", TaskState.Failed => "执行失败", TaskState.Partial => "部分完成", _ => "正在核对" };
    public void Countdown(int seconds) { _visible = true; _running = false; SetPermissionTask(null); SetModelReply(null); _state.Text = $"{seconds} 秒后开始操作"; _goal.Text = "请切换到目标软件，并暂时松开键鼠"; _current.Text = "将使用真实系统鼠标与键盘"; _next.Text = "随时可以暂停或停止"; ApplyVisibility(); }
    public void HideAll() { _visible = _running = false; ApplyVisibility(); }
    private void ApplyVisibility()
    {
        if (_disposed) return;
        CompactButton.IsEnabled = !_running && !_questionId.HasValue && !_approvalId.HasValue;
        if (!CompactButton.IsEnabled) _compact = false;
        CompactButton.Content = _compact ? "展开" : "收起";
        _body.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        if (_running) _next.Visibility = Visibility.Collapsed;
        HighRiskMode.Visibility = _modeChangeAllowed && !_running ? Visibility.Visible : Visibility.Collapsed;
        if (_visible && _suppressed == 0) _hud.Show(); else _hud.Hide();
        if (_visible && _running && _suppressed == 0) _border.Show(); else _border.Hide();
    }
    public async Task SetStateAsync(OverlayState state, CancellationToken ct) => await _dispatcher.InvokeAsync(() =>
    { ct.ThrowIfCancellationRequested(); if (_retiredTasks.Contains(state.Lease.TaskId)) return; if (_lease.TaskId != state.Lease.TaskId) SetModelReply(null); if (_permissionLease != state.Lease || state.State != TaskState.WaitingUser) SetPermissionTask(null); _lease = state.Lease; _visible = true; _running = state.State == TaskState.Running; _state.Text = StateName(state.State); _goal.Text = state.GoalSummary; _current.Text = state.Current; _next.Text = state.Next; ApplyVisibility(); });
    public async Task<IAsyncDisposable> HideForCaptureAsync(Lease lease, CancellationToken ct)
    {
        await _dispatcher.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); _suppressed++; ApplyVisibility(); });
        var scope = new Suppression(this);
        try { await Task.Run(() => DwmFlush(), ct); ct.ThrowIfCancellationRequested(); return scope; }
        catch { await scope.DisposeAsync(); throw; }
    }
    private sealed class Suppression(DesktopOverlayController owner) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref _disposed, 1) == 0) await owner._dispatcher.InvokeAsync(() => { owner._suppressed--; owner.ApplyVisibility(); }); }
    }
    public async Task RelocateAsync(Lease lease, PhysicalRect excludedRegion, CancellationToken ct) => await _dispatcher.InvokeAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed || lease != _lease || _display is null) return;
        foreach (var corner in Corners(_display)) if (!FrameChecks.Overlaps(corner, excludedRegion)) { Position(_hud, corner); return; }
    });
    public void Dispose() { _dispatcher.VerifyAccess(); if (_disposed) return; _disposed = true; _hud.Close(); _border.Close(); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private record struct Margins(int Left, int Right, int Top, int Bottom);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint h, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint h, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint h, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
