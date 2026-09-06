namespace DesktopAgent.Core.Runtime;

/// <summary>Actionable local observations, not invented rewards or certification of model claims.</summary>
public static class ExecutionFeedback
{
    public static string ModelFailure(string code) => code switch
    {
        "EMPTY_CONTENT" => "模型返回了空回复，没有可执行提议；本轮未执行输入。这不是控件定位或权限判断失败。",
        "EMPTY_REASONING_ONLY" => "模型仅返回思考数据，没有最终操作回复；本轮未执行输入。请给出简短完整的操作JSON。",
        "EMPTY_SEARCH_ANSWER" => "模型完成了检索但没有最终操作回复；本轮未执行输入。请综合已有资料和新画面给出操作。",
        "OUTPUT_TOKEN_LIMIT" => "模型输出达到上限，未得到完整操作回复；本轮未执行输入。请减少解释并给出完整简短JSON。",
        "RESPONSE_REFUSAL" => "模型拒绝了本轮请求，未执行输入；开启应用权限不能修复模型拒绝。",
        "CONTENT_FILTERED" => "模型服务限制了本轮输出，未执行输入。",
        _ => "模型请求未成功（" + code + "），本轮未执行输入；请核对服务状态后继续。"
    };

    public static string ForRejection(string code) => code switch
    {
        "CONTROL_MEANING_CONFLICT" => "控件名称与提议用途冲突。结合名称、同组相邻项和局部图重新识别，不要再次点击同一错误目标。",
        "VISUAL_ACTION_LOOP" or "REPEATED_ACTION" => "相同操作未带来可证明的新进展。检查是否已生效、焦点/模式是否正确，选择有依据的不同路径。",
        "ASSISTED_FOCUS_REQUIRED" or "ASSISTED_FOCUS_CHANGED" => "当前键盘焦点没有可靠落在目标控件。先核对实际聚焦项；必要时点击正确输入框，重新观察再输入。",
        "CONTROL_CHANGED" or "CONTROL_UNAVAILABLE" or "CONTROL_OCCLUDED_OR_GONE" => "目标在重新定位时变化、被遮挡或无法命中。重新读取当前候选/局部图，不能沿用旧控件ID或猜坐标。",
        "SHELL_RENAME_EDIT_CHANGED" or "REPLACEMENT_VALUE_CHANGED" => "文件名编辑框或其完整文字发生变化。重新读取当前完整值和选区，纠错应完整替换而非追加。",
        "INVALID_PLAN_UPDATE" => "计划步骤引用或状态格式无效。仅更新当前计划中已有的步骤ID，不凭预期报告完成。",
        _ => "本地核对未通过（" + code + "）。请结合当前画面修正提议；预期效果不是执行结果。"
    };
}
