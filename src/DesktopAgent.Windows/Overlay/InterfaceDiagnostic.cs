using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Configuration;
using DesktopAgent.Windows.Diagnostics;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Presentation;

namespace DesktopAgent.Windows.Overlay;

internal static class InterfaceDiagnostic
{
    public static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var gate = new InputSafetyGate();
        var checks = new List<object>(); bool passed = true; string? failure = null;
        void Check(string name, bool value) { checks.Add(new { name, passed = value }); passed &= value; }
        var lifecycle = new ProductLifecycleLog(Path.Combine(directory, "lifecycle"));
        var window = new MainWindow(gate, new(false, 0, "UI_DIAGNOSTIC_NO_INPUT"), new LocalConfigurationStore(Path.Combine(directory, "isolated-" + Guid.NewGuid().ToString("N"))), lifecycle);
        using var overlay = new DesktopOverlayController(window.Dispatcher);
        bool closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); await Task.Delay(300);
            ThemeManager.Apply("light"); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("start_disabled_without_probe_or_hotkeys", !((Button)window.FindName("StartButton")).IsEnabled && !gate.Status.IsOpen);
            Check("precise_default_model", ((System.Windows.Controls.ComboBox)window.FindName("ProfileBox")).SelectedItem is ProviderProfile { Model: "deepseek-v4-flash-vision-exp" });
            Check("high_risk_default_off_and_new_task_available", ((CheckBox)window.FindName("HighRiskMode")).IsChecked == false && ((Button)window.FindName("NewTaskButton")).IsEnabled);
            Render(window, Path.Combine(directory, "chat.png"));
            Check("task_details_collapsed_by_default", !((Expander)window.FindName("TaskDetailsExpander")).IsExpanded);
            ThemeManager.Apply("dark"); window.UpdateLayout(); Render(window, Path.Combine(directory, "chat-dark.png"));
            var darkBackground = ((SolidColorBrush)Application.Current.Resources["PageBrush"]).Color;
            var darkText = ((SolidColorBrush)Application.Current.Resources["TextBrush"]).Color;
            Check("dark_theme_has_readable_contrast", darkText.R > 220 && darkBackground.R < 40);
            ThemeManager.Apply("light");
            window.Width = 580; window.Height = 640; window.UpdateLayout(); Render(window, Path.Combine(directory, "chat-minimum.png"));
            bool dialogReturned = false;
            var settings = new SettingsWindow(new LocalConfigurationStore(Path.Combine(directory, "modal-" + Guid.NewGuid().ToString("N"))), gate) { Owner = window };
            settings.Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Delay(50);
                    settings.UpdateLayout(); Render(settings, Path.Combine(directory, "settings-light.png"));
                    settings.PreviewTheme("dark"); settings.UpdateLayout(); Render(settings, Path.Combine(directory, "settings-dark.png")); settings.PreviewTheme("light");
                    using (settings.MinimizeForObservation())
                    {
                        await Task.Delay(80);
                        Check("minimized_settings_preserve_modal_loop", !dialogReturned && settings.WindowState == WindowState.Minimized);
                    }
                    await Task.Delay(50);
                    Check("restored_settings_preserve_modal_loop", !dialogReturned && settings.IsVisible && settings.WindowState == WindowState.Normal && window.WindowState == WindowState.Normal);
                }
                finally { settings.Close(); }
            };
            settings.ShowDialog(); dialogReturned = true;
            window.Hide();
            var env = new Win32DesktopEnvironment().Read(); var display = env.Displays.First(d => d.IsPrimary); overlay.SetDisplay(display);
            var lease = new Lease(Guid.NewGuid(), 0);
            await overlay.SetStateAsync(new(lease, TaskState.Running, "界面测试：找到并开启记事本自动换行", "正在核对当前选项", "这是界面诊断，没有模型或自动输入"), default);
            await Task.Delay(100); Render(overlay.Hud, Path.Combine(directory, "status-card.png"));
            Check("running_card_and_border_visible", overlay.Hud.IsVisible && overlay.Rainbow.IsVisible);
            Check("running_details_default_collapsed_and_compact_disabled", !overlay.Details.IsExpanded && !overlay.CompactButton.IsEnabled);
            ThemeManager.Apply("dark"); overlay.Hud.UpdateLayout(); Render(overlay.Hud, Path.Combine(directory, "status-card-dark.png")); ThemeManager.Apply("light");
            await using (var outer = await overlay.HideForCaptureAsync(lease, default))
            {
                Check("hidden_during_capture", !overlay.Hud.IsVisible && !overlay.Rainbow.IsVisible);
                await using (var inner = await overlay.HideForCaptureAsync(lease, default)) { }
                Check("nested_capture_stays_hidden", !overlay.Hud.IsVisible && !overlay.Rainbow.IsVisible);
                await overlay.SetStateAsync(new(lease, TaskState.Paused, "界面测试", "已暂停", "不会自动恢复输入"), default);
            }
            Check("capture_exit_respects_latest_pause", overlay.Hud.IsVisible && !overlay.Rainbow.IsVisible);
            var question = new UserQuestion(Guid.NewGuid(), "你要计算什么算式？直接在下方回复，我会接着操作计算器。", "需要算式");
            var interpretation = new TaskInterpretation("打开计算器，按用户指定的算式计算", "我会打开计算器计算。你明确要求先询问算式，我会在这里等你回复，然后继续原任务。");
            var task = new TaskContext(lease.TaskId, lease.Epoch, "打开计算器，询问算式后计算", DateTimeOffset.UtcNow,
                TaskState.WaitingUser, true, "diagnostic", display.Id, TaskBudget.Default, new(0, 1, 0, 0, 0), [], null, null)
                { PendingQuestion = question, Interpretation = interpretation };
            overlay.Update(new(task with { State = TaskState.Running, CleanupComplete = false, PendingQuestion = null }, "正在打开计算器", "随后核对界面", "", [], new(0, 0, 0, 0), 1), true);
            await overlay.SetStateAsync(new(lease, TaskState.Running, task.Goal, "正在请求模型", "等待下一步操作"), default);
            Check("collapsed_details_preserve_model_reply", !overlay.Details.IsExpanded && overlay.ModelReplyText.Text == interpretation.Reply);
            overlay.Details.IsExpanded = true; overlay.Hud.UpdateLayout();
            Check("model_reply_survives_transient_execution_status", overlay.ModelReplyText.IsVisible && overlay.ModelReplyText.Text == interpretation.Reply);
            await Task.Delay(50); Render(overlay.Hud, Path.Combine(directory, "model-reply-card.png"));
            var planInterpretation = new TaskInterpretation("界面诊断：打开设置，将系统改为浅色并核对", "我会逐步完成，并根据新画面核对结果。")
            {
                Steps = [new("s1", "打开设置", "看到 Windows 设置窗口"), new("s2", "切换浅色模式", "颜色选项显示浅色"), new("s3", "核对完整目标", "系统外观与选项均符合要求")],
                CompletionCheck = "设置显示浅色，且新画面中的系统外观已改变。"
            };
            var planTask = task with
            {
                Goal = planInterpretation.Goal, State = TaskState.Paused, PendingQuestion = null, Interpretation = planInterpretation,
                PlanProgress = [new("s1", "completed", "已看到设置窗口。", lease, "ui-plan-fixture"), new("s2", "active", "已读取当前颜色选项。", lease, "ui-plan-fixture")]
            };
            overlay.Update(new(planTask, "计划界面诊断，未执行真实操作", "继续后根据新画面推进", "", [], new(0, 0, 0, 0), 1), true);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            overlay.Hud.UpdateLayout();
            Check("structured_plan_contains_all_states_and_observations", overlay.ModelReplyText.IsVisible &&
                overlay.ModelReplyText.Text == TaskPlanTracking.Describe(planInterpretation, planTask.PlanProgress) &&
                new[] { "任务计划", "[已核对] 打开设置", "[进行中] 切换浅色模式", "[待完成] 核对完整目标", "已看到设置窗口。", "已读取当前颜色选项。" }
                    .All(text => overlay.ModelReplyText.Text.Contains(text, StringComparison.Ordinal)));
            var planOuterScroll = (ScrollViewer)((Border)overlay.Hud.Content).Child;
            var planPanel = (StackPanel)planOuterScroll.Content;
            var planScroll = ((StackPanel)overlay.Details.Content).Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<ScrollViewer>())
                .Single(scroll => ReferenceEquals(scroll.Content, overlay.ModelReplyText));
            Check("structured_plan_has_bounded_scroll_area", planScroll.IsVisible && planScroll.MaxHeight > 0 &&
                double.IsFinite(planScroll.MaxHeight) && planScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
                planScroll.ScrollableHeight > 0 && planOuterScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
            planScroll.ScrollToEnd();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("structured_plan_can_scroll_to_remaining_steps", planScroll.VerticalOffset > 0 &&
                Math.Abs(planScroll.VerticalOffset - planScroll.ScrollableHeight) < 1);
            var planButtons = planPanel.Children.OfType<StackPanel>().Single().Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
            Check("structured_plan_preserves_task_controls_without_input", overlay.NewTaskButton.IsVisible && overlay.ResumeTaskButton.IsVisible &&
                new[] { "返回主界面", "暂停", "停止" }.All(label => planButtons.Any(button => Equals(button.Content, label) && button.IsVisible)) &&
                overlay.Hud.IsVisible && !overlay.Rainbow.IsVisible && !gate.Status.IsOpen);
            Render(overlay.Hud, Path.Combine(directory, "task-plan-card.png"));
            planScroll.ScrollToTop();
            overlay.Update(new(task, "计算器已打开", "", question.Question, [], new(0, 0, 0, 0), 1), true);
            await Task.Delay(80); Render(overlay.Hud, Path.Combine(directory, "question-card.png"));
            Check("question_card_has_reply_without_input_armed", overlay.Hud.IsVisible && overlay.ReplyBox.IsVisible && !overlay.Rainbow.IsVisible && !gate.Status.IsOpen);
            Check("question_keeps_model_interpretation_and_separate_reply", overlay.ModelReplyText.IsVisible && overlay.ModelReplyText.Text == interpretation.Reply && overlay.ReplyBox.Text == "");
            var styles = GetWindowLongPtrW(new System.Windows.Interop.WindowInteropHelper(overlay.Hud).Handle, -20);
            Check("question_is_activatable_and_taskbar_reachable", overlay.Hud.ShowInTaskbar && (styles & 0x08000080) == 0 && (styles & 0x00040000) != 0);
            int stops = 0; overlay.Stop += () => stops++;
            overlay.Hud.Close();
            Check("question_close_requests_stop_without_orphaning_chat", stops == 1 && overlay.Hud.IsVisible);
            int replies = 0; string? answer = null;
            overlay.Reply += text => { replies++; answer = text; };
            overlay.ReplyBox.Text = "3+2+5";
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("reply_submits_once", replies == 1 && answer == "3+2+5" && !gate.Status.IsOpen);
            overlay.ClearQuestion();
            Check("resolved_question_hides_reply", !overlay.ReplyBox.IsVisible && !overlay.ReplyButton.IsEnabled);
            var completedTask = task with { State = TaskState.Succeeded, PendingQuestion = null };
            var completedProgress = new AgentProgress(completedTask, "桌面控制已释放", "", "本次任务已完成。", [], new(0, 0, 0, 0), 2);
            overlay.Update(completedProgress, true);
            await Task.Delay(80); Render(overlay.Hud, Path.Combine(directory, "completed-card.png"));
            Check("completion_keeps_input_and_result_without_rainbow", overlay.Hud.IsVisible && overlay.ReplyBox.IsVisible && !overlay.Rainbow.IsVisible && overlay.ReplyBox.Text == "");
            Check("completion_keeps_model_interpretation", overlay.ModelReplyText.IsVisible && overlay.ModelReplyText.Text == interpretation.Reply);
            int newTasks = 0; string? nextGoal = null;
            overlay.NewTask += goal => { newTasks++; nextGoal = goal; };
            overlay.ReplyBox.Text = "打开记事本";
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("completed_card_sends_one_new_goal_not_question_answer", newTasks == 1 && nextGoal == "打开记事本" && replies == 1);
            int returns = 0; overlay.Chat += () => returns++;
            overlay.Hud.Close();
            Check("completed_card_close_returns_to_main", returns == 1 && stops == 1);
            overlay.ClearQuestion();
            Check("starting_next_task_clears_old_input", !overlay.ReplyBox.IsVisible && overlay.ReplyBox.Text == "");
            overlay.Countdown(3);
            Check("next_task_countdown_clears_old_interpretation", !overlay.ModelReplyText.IsVisible && overlay.ModelReplyText.Text == "");
            int resumed = 0; overlay.ResumeRequested += () => resumed++;
            foreach (var stoppedState in new[] { TaskState.Paused, TaskState.WaitingUser, TaskState.Interrupted, TaskState.Partial, TaskState.Failed, TaskState.Cancelled })
            {
                overlay.Update(completedProgress with { Task = completedTask with { State = stoppedState, LastError = new("UI_DIAGNOSTIC_STOP", "固定测试原因") } }, true);
                bool resumable = stoppedState is TaskState.Paused or TaskState.WaitingUser or TaskState.Interrupted;
                Check("retained_" + stoppedState.ToString().ToLowerInvariant() + "_shows_reason_and_controls", overlay.Hud.IsVisible && !overlay.Rainbow.IsVisible &&
                    overlay.StatusReason.Contains("UI_DIAGNOSTIC_STOP", StringComparison.Ordinal) && overlay.NewTaskButton.IsVisible &&
                    overlay.ResumeTaskButton.IsVisible == resumable && resumed == 0);
            }
            Render(overlay.Hud, Path.Combine(directory, "stopped-card.png"));
            overlay.Update(completedProgress with { Task = completedTask with { State = TaskState.Paused } }, true);
            overlay.ResumeTaskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("retained_card_resumes_only_on_explicit_click", resumed == 1 && !gate.Status.IsOpen);
            var approval = new PendingActionApproval(Guid.NewGuid(), "这是一项离线界面确认测试。", "诊断用联系人", "测试消息显示为已发送", "单击当前可见的发送按钮")
                { OriginalGoal = "给诊断用联系人发一条测试消息", Recipient = "诊断用联系人", Message = "仅用于界面渲染的示例消息，不会发送。" };
            var approvalTask = task with { State = TaskState.AwaitingApproval, PendingQuestion = null, PendingActionApproval = approval, HighRiskEnabled = true };
            var approvalProgress = new AgentProgress(approvalTask, "桌面控制已释放", "等待最终批准", "", [], new(0, 0, 0, 0), 3);
            overlay.Update(approvalProgress, true);
            await Task.Delay(50); Render(overlay.Hud, Path.Combine(directory, "approval-card.png"));
            Check("approval_card_shows_exact_step_recipient_and_message", overlay.ApprovalDetails.IsVisible &&
                overlay.ApprovalDetails.Text.Contains(approval.ActionDescription, StringComparison.Ordinal) &&
                overlay.ApprovalDetails.Text.Contains(approval.Recipient!, StringComparison.Ordinal) &&
                overlay.ApprovalDetails.Text.Contains(approval.Message!, StringComparison.Ordinal));
            Check("approval_releases_input_and_locks_task_risk_mode", !overlay.Rainbow.IsVisible && !overlay.ReplyBox.IsVisible &&
                overlay.HighRiskMode.IsChecked == true && !overlay.HighRiskMode.IsEnabled && !gate.Status.IsOpen);
            int approvals = 0; Guid? answeredApproval = null; bool? approved = null;
            overlay.ActionApprovalAnswered += (id, allowed) => { approvals++; answeredApproval = id; approved = allowed; };
            overlay.ReplyBox.Text = "同意"; overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("ordinary_reply_cannot_approve_action", approvals == 0 && replies == 1);
            overlay.ApproveActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.ApproveActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("approval_submits_exact_step_once", approvals == 1 && answeredApproval == approval.Id && approved == true);
            var nextApproval = approval with { Id = Guid.NewGuid() };
            overlay.Update(approvalProgress with { Task = approvalTask with { PendingActionApproval = nextApproval }, Revision = 4 }, true);
            overlay.RejectActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.RejectActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("cancel_is_distinct_and_submits_once", approvals == 2 && answeredApproval == nextApproval.Id && approved == false);
            overlay.RetireTask(task.Id); overlay.ShowNewTaskDraft();
            Check("new_task_draft_clears_question_approval_and_risk_mode", overlay.IsNewTaskDraft && overlay.ReplyBox.IsVisible && overlay.ReplyBox.Text == "" &&
                !overlay.ApprovalDetails.IsVisible && overlay.HighRiskMode.IsChecked == false && overlay.HighRiskMode.IsEnabled && !overlay.Rainbow.IsVisible);
            overlay.Update(approvalProgress, true);
            await overlay.SetStateAsync(new(lease, TaskState.Running, "旧任务迟到状态", "不应再显示", "不应接管"), default);
            Check("retired_task_cannot_replace_new_task_draft", overlay.IsNewTaskDraft && !overlay.ApprovalDetails.IsVisible && !overlay.Rainbow.IsVisible);
            overlay.ReplyBox.Text = "新的独立任务";
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.ReplyBox.Text += "补充";
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("draft_sends_one_new_task_even_when_text_changes_after_submit", newTasks == 2 && nextGoal == "新的独立任务" && replies == 1);
            Render(overlay.Hud, Path.Combine(directory, "new-task-card.png"));
            var permissionLease = new Lease(Guid.NewGuid(), 0);
            var permissionQuestion = new UserQuestion(Guid.NewGuid(), "当前任务需要高风险权限。启用后继续原计划，提交前仍需逐次确认。", "需要任务权限");
            var permissionTask = planTask with
            {
                Id = permissionLease.TaskId, Epoch = permissionLease.Epoch, State = TaskState.WaitingUser, CleanupComplete = true,
                PendingQuestion = permissionQuestion, LastError = new("HIGH_IMPACT_MANUAL", "需要任务权限"), HighRiskEnabled = false,
                PlanProgress = [.. planTask.PlanProgress.Select(report => report with { Lease = permissionLease })]
            };
            var permissionProgress = new AgentProgress(permissionTask, "桌面控制已释放", "等待你决定是否启用本任务权限", "", [], new(0, 0, 0, 0), 1);
            foreach (var permissionCode in new[] { "HIGH_IMPACT_MANUAL", "MESSAGE_COMMIT_DISABLED", "ACCOUNT_ENTRY_PERMISSION_REQUIRED" })
            {
                overlay.Update(permissionProgress with { Task = permissionTask with { LastError = new(permissionCode, "需要任务权限") } }, true);
                Check("permission_button_for_" + permissionCode.ToLowerInvariant(), overlay.EnableHighRiskButton.IsVisible &&
                    overlay.EnableHighRiskButton.IsEnabled && !overlay.HighRiskMode.IsEnabled && !overlay.Rainbow.IsVisible && !gate.Status.IsOpen);
            }
            foreach (var unrelatedCode in new[] { "EMPTY_CONTENT", "CONTROLS_UNAVAILABLE", "NO_VERIFIABLE_CONTROLS", "UIA_TIMEOUT" })
            {
                overlay.Update(permissionProgress with { Task = permissionTask with { LastError = new(unrelatedCode, "需要任务权限") } }, true);
                Check("permission_button_hidden_for_" + unrelatedCode.ToLowerInvariant(), !overlay.EnableHighRiskButton.IsVisible && !overlay.EnableHighRiskButton.IsEnabled);
            }
            foreach (var ineligible in new[] { permissionTask with { State = TaskState.Paused }, permissionTask with { State = TaskState.Running },
                permissionTask with { CleanupComplete = false }, permissionTask with { HighRiskEnabled = true }, permissionTask with { LastError = null } })
            {
                overlay.Update(permissionProgress with { Task = ineligible }, true);
                Check("permission_button_rejects_ineligible_" + ineligible.State + "_" + ineligible.CleanupComplete + "_" + ineligible.HighRiskEnabled + "_" + (ineligible.LastError?.Code ?? "none"),
                    !overlay.EnableHighRiskButton.IsVisible && !overlay.EnableHighRiskButton.IsEnabled);
            }
            overlay.Update(permissionProgress, true);
            int permissionRequests = 0; Lease? requestedPermissionLease = null;
            overlay.EnableHighRiskRequested += requested => { permissionRequests++; requestedPermissionLease = requested; };
            overlay.ReplyBox.Text = "同意";
            overlay.ReplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.HighRiskMode.IsChecked = true;
            Check("ordinary_reply_and_checkbox_cannot_enable_task_permission", permissionRequests == 0 && !permissionTask.HighRiskEnabled);
            overlay.SetHighRiskMode(false, false); overlay.SetComposerBusy(false);
            overlay.EnableHighRiskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            overlay.EnableHighRiskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("permission_button_submits_one_explicit_bound_lease", permissionRequests == 1 && requestedPermissionLease == permissionLease &&
                !overlay.EnableHighRiskButton.IsEnabled && permissionTask.Goal == planInterpretation.Goal && permissionTask.Interpretation == planInterpretation && !gate.Status.IsOpen);
            overlay.Update(permissionProgress, true);
            Check("repeated_permission_progress_does_not_unlock_double_submission", !overlay.EnableHighRiskButton.IsEnabled && permissionRequests == 1);
            overlay.SetPermissionBusy(false); overlay.ReplyBox.Clear();
            await Task.Delay(50); Render(overlay.Hud, Path.Combine(directory, "permission-card.png"));
            Check("permission_card_keeps_plan_and_existing_task_controls", overlay.ModelReplyText.Text.Contains("任务计划", StringComparison.Ordinal) &&
                overlay.NewTaskButton.IsVisible && overlay.Hud.IsVisible && planButtons.Any(button => Equals(button.Content, "停止") && button.IsVisible) &&
                (string)overlay.EnableHighRiskButton.Content == "启用高风险并继续\n提交前仍需逐次确认");
            window.ReceiveProgress(permissionProgress);
            Check("main_permission_button_requires_verified_runtime_before_enabling", ((Button)window.FindName("EnableHighRiskButton")).Visibility == Visibility.Visible &&
                !((Button)window.FindName("EnableHighRiskButton")).IsEnabled && !gate.Status.IsOpen);
            overlay.ClearQuestion();
            overlay.EnableHighRiskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("leaving_permission_card_clears_old_permission_action", !overlay.EnableHighRiskButton.IsVisible && !overlay.EnableHighRiskButton.IsEnabled && permissionRequests == 1);
            overlay.HideAll();
            window.TaskOverlay.SetDisplay(display);
            window.ReceiveProgress(completedProgress);
            await Task.Delay(60);
            Check("main_stays_hidden_when_task_succeeds", !window.IsVisible && window.TaskOverlay.Hud.IsVisible && window.TaskOverlay.ReplyBox.IsVisible);
            var chat = (StackPanel)window.FindName("Messages"); int messageCount = chat.Children.Count;
            window.ReceiveProgress(completedProgress with { Revision = 3, Current = "桌面控制已释放" });
            Check("main_keeps_interpretation_without_duplicate_chat_messages", ((TextBlock)window.FindName("ModelReplyText")).Text == interpretation.Reply && chat.Children.Count == messageCount);
            window.ReturnToMain();
            Check("manual_return_shows_main_and_hides_card", window.IsVisible && !window.TaskOverlay.Hud.IsVisible && !gate.Status.IsOpen);
            Render(window, Path.Combine(directory, "chat-with-model-reply.png"));
            window.ReceiveProgress(new(completedTask with { Id = Guid.NewGuid(), State = TaskState.Running, CleanupComplete = false, Interpretation = null }, "正在理解新任务", "", "", [], new(0, 0, 0, 0), 4));
            Check("new_task_clears_prior_model_interpretation", ((TextBlock)window.FindName("ModelReplyText")).Text == "" && ((StackPanel)window.FindName("ModelReplyPanel")).Visibility == Visibility.Collapsed && window.TaskOverlay.ModelReplyText.Text == "");
            var oldMainProgress = completedProgress with { Revision = 5 };
            window.ReceiveProgress(oldMainProgress);
            ((TextBox)window.FindName("TaskBox")).Text = "旧任务目标";
            ((CheckBox)window.FindName("HighRiskMode")).IsChecked = true;
            await window.ResetForNewTaskAsync();
            Check("main_new_task_clears_goal_and_opens_empty_hud", !window.IsVisible && window.TaskOverlay.IsNewTaskDraft &&
                ((TextBox)window.FindName("TaskBox")).Text == "" && window.TaskOverlay.ReplyBox.Text == "" && ((CheckBox)window.FindName("HighRiskMode")).IsChecked == false);
            window.ReceiveProgress(oldMainProgress with { Revision = 100 });
            Check("late_old_progress_cannot_restore_main_task", window.TaskOverlay.IsNewTaskDraft && ((TextBox)window.FindName("TaskBox")).Text == "" &&
                ((TextBlock)window.FindName("ModelReplyText")).Text == "");
            var interrupted = completedProgress with { Task = completedTask with { Id = Guid.NewGuid(), State = TaskState.Interrupted,
                PendingQuestion = null, LastError = new("TEST_INTERRUPTION", "测试中断") }, Revision = 101 };
            window.ReceiveProgress(interrupted);
            Check("main_does_not_replace_interrupted_hud", !window.IsVisible && window.TaskOverlay.Hud.IsVisible &&
                window.TaskOverlay.StatusReason.Contains("TEST_INTERRUPTION", StringComparison.Ordinal));
            await window.HandleUnexpectedUiFailureAsync();
            Check("unexpected_ui_failure_keeps_visible_stopped_card", !window.IsVisible && window.TaskOverlay.Hud.IsVisible &&
                !window.TaskOverlay.Rainbow.IsVisible && window.TaskOverlay.StatusReason.Contains("UI_DISPATCHER_ERROR", StringComparison.Ordinal) && !gate.Status.IsOpen);
            Render(window.TaskOverlay.Hud, Path.Combine(directory, "error-card.png"));
            window.WindowState = WindowState.Minimized; window.ReturnToMain();
            Check("manual_return_restores_minimized_main", window.IsVisible && window.WindowState == WindowState.Normal && !window.TaskOverlay.Hud.IsVisible);
            const string privateMarker = "PRIVATE_DIAGNOSTIC_GOAL_KEY_MESSAGE_MUST_NOT_APPEAR";
            lifecycle.Progress(interrupted with { Task = interrupted.Task with { Goal = privateMarker,
                Interpretation = new(privateMarker, privateMarker) }, Current = privateMarker, Summary = privateMarker }, false, true);
            try { throw new InvalidOperationException(privateMarker); }
            catch (Exception error) { lifecycle.Fault("UI_DIAGNOSTIC_EXCEPTION", error); }
            string technicalLog = await File.ReadAllTextAsync(lifecycle.FilePath);
            Check("lifecycle_log_omits_private_text_and_exception_message", !technicalLog.Contains(privateMarker, StringComparison.Ordinal) &&
                technicalLog.Contains("System.InvalidOperationException", StringComparison.Ordinal) && technicalLog.Contains("TEST_INTERRUPTION", StringComparison.Ordinal));
            var providerLog = new ProductLifecycleLog(Path.Combine(directory, "lifecycle-provider"));
            var unknownMetadata = new ProviderResponseMetadata(200, privateMarker, 7, false, privateMarker, new(3, 4), true, 8, false)
            {
                Endpoint = privateMarker, RequestStage = privateMarker, ResponseStatus = privateMarker,
                IncompleteReason = privateMarker, RemoteErrorCode = privateMarker
            };
            providerLog.Fault("UI_DIAGNOSTIC_PROVIDER", new ProviderCallException(privateMarker, unknownMetadata));
            string unknownProviderLog = (await File.ReadAllLinesAsync(providerLog.FilePath))[^1];
            using (var row = JsonDocument.Parse(unknownProviderLog))
            {
                var details = row.RootElement.GetProperty("provider");
                Check("provider_log_replaces_unknown_ascii_metadata", !unknownProviderLog.Contains(privateMarker, StringComparison.Ordinal) &&
                    details.GetProperty("code").GetString() == "UNCLASSIFIED" &&
                    new[] { "endpoint", "requestStage", "status", "finishReason", "incompleteReason" }.All(name => details.GetProperty(name).GetString() == "unknown"));
            }
            providerLog.Fault("UI_DIAGNOSTIC_PROVIDER", new ProviderCallException("OUTPUT_TOKEN_LIMIT", unknownMetadata with
            {
                Endpoint = "responses", RequestStage = "search_continuation", ResponseStatus = "incomplete",
                FinishReason = "incomplete", IncompleteReason = "max_output_tokens"
            }));
            using (var row = JsonDocument.Parse((await File.ReadAllLinesAsync(providerLog.FilePath))[^1]))
            {
                var details = row.RootElement.GetProperty("provider");
                Check("provider_log_preserves_known_responses_enums", details.GetProperty("code").GetString() == "OUTPUT_TOKEN_LIMIT" &&
                    details.GetProperty("endpoint").GetString() == "responses" && details.GetProperty("requestStage").GetString() == "search_continuation" &&
                    details.GetProperty("status").GetString() == "incomplete" && details.GetProperty("finishReason").GetString() == "incomplete" &&
                    details.GetProperty("incompleteReason").GetString() == "max_output_tokens" && details.GetProperty("inputTokens").GetInt64() == 3);
            }
            providerLog.Fault("UI_DIAGNOSTIC_PROVIDER", new ProviderCallException("OUTPUT_TOKEN_LIMIT", unknownMetadata with
            {
                Endpoint = "chat/completions", RequestStage = "recovery", ResponseStatus = null, FinishReason = "length", IncompleteReason = null
            }));
            using (var row = JsonDocument.Parse((await File.ReadAllLinesAsync(providerLog.FilePath))[^1]))
            {
                var details = row.RootElement.GetProperty("provider");
                Check("provider_log_preserves_known_chat_enums_and_absent_fields", details.GetProperty("endpoint").GetString() == "chat/completions" &&
                    details.GetProperty("requestStage").GetString() == "recovery" && details.GetProperty("finishReason").GetString() == "length" &&
                    details.GetProperty("status").ValueKind == JsonValueKind.Null && details.GetProperty("incompleteReason").ValueKind == JsonValueKind.Null);
            }
            var rotationLog = new ProductLifecycleLog(Path.Combine(directory, "lifecycle-rotation"));
            rotationLog.TechnicalEvent("ROTATION_SETUP");
            await File.WriteAllBytesAsync(rotationLog.FilePath, new byte[256 * 1024]);
            rotationLog.TechnicalEvent("ROTATION_TEST");
            Check("lifecycle_log_rotates_at_bounded_size", File.Exists(rotationLog.FilePath + ".previous") && new FileInfo(rotationLog.FilePath).Length < 256 * 1024);
            Check("input_never_armed", !gate.Status.IsOpen && gate.Status.Lease is null);
            overlay.ShowNewTaskDraft(); overlay.CompactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(80); overlay.Hud.UpdateLayout();
            Render(overlay.Hud, Path.Combine(directory, "capsule.png"));
            Check("idle_capsule_can_expand_again", Equals(overlay.CompactButton.Content, "展开"));
            overlay.CompactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("capsule_restores_composer", overlay.ReplyBox.IsVisible && Equals(overlay.CompactButton.Content, "收起"));
            Check("native_tray_registered", window.InitializeTray());
            window.Show(); window.Close();
            Check("close_hides_to_tray_without_shutdown", !closed && !window.IsVisible && !gate.Status.IsOpen);
            window.ReturnToMain(); Check("tray_restore_keeps_main_reachable", window.IsVisible);
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; Check("completed", false); }
        finally
        {
            overlay.HideAll(); window.RequestExit();
            for (int i = 0; i < 100 && !closed; i++) await Task.Delay(20);
            Check("window_closed_and_input_closed", closed && !gate.Status.IsOpen);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "interface.json"), JsonSerializer.Serialize(new { passed, failure, checks, modelCalls = 0, inputEvents = 0,
            glassAttributeAccepted = overlay.GlassRequested, evidenceMode = "WPF_SELF_RENDER_AND_WINDOW_STATE_NOT_MODEL_OR_REAL_INPUT" }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
    private static void Render(Window window, string path)
    {
        var content = (FrameworkElement)window.Content; content.UpdateLayout();
        int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // Use explicit local bounds: auto VisualBrush bounds exclude padding and can stretch collapsed content.
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        { var bounds = new Rect(0, 0, width, height); dc.DrawRectangle(window.Background, null, bounds); dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill }, null, bounds); }
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);
}
