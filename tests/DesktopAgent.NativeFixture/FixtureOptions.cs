using System.IO;

namespace DesktopAgent.NativeFixture;

internal sealed record FixtureOptions(bool SelfCheck, string? ReportPath, string? RenderPath, string? ReportOnExitPath, string? InputProbePath, string? CaptureProbePath,
    string? AssistedLoopPath = null, int AssistedLoopLayout = 0)
{
    public static FixtureOptions Parse(string[] args)
    {
        bool selfCheck = false;
        string? report = null;
        string? render = null;
        string? reportOnExit = null;
        string? inputProbe = null;
        string? captureProbe = null;
        string? assistedLoop = null;
        int assistedLayout = 0;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--self-check" when !selfCheck:
                    selfCheck = true;
                    break;
                case "--report" when report is null:
                    report = ReadPath(args, ref index);
                    break;
                case "--render" when render is null:
                    render = ReadPath(args, ref index);
                    break;
                case "--report-on-exit" when reportOnExit is null:
                    reportOnExit = ReadPath(args, ref index);
                    break;
                case "--input-probe" when inputProbe is null:
                    inputProbe = ReadPath(args, ref index);
                    break;
                case "--capture-probe" when captureProbe is null:
                    captureProbe = ReadPath(args, ref index);
                    break;
                case "--assisted-loop-fixture" when assistedLoop is null:
                    assistedLoop = ReadPath(args, ref index);
                    if (++index >= args.Length || !int.TryParse(args[index], out assistedLayout) || assistedLayout is < 0 or > 1)
                        throw new ArgumentException("--assisted-loop-fixture 需要绝对报告路径及布局 0 或 1。");
                    break;
                default:
                    throw new ArgumentException("未知或重复参数。用法：--self-check --report <JSON绝对路径> [--render <PNG绝对路径>]；或 --report-on-exit <JSON绝对路径>；不带参数启动手工测试窗口。");
            }
        }
        if (selfCheck != (report is not null) || (render is not null && !selfCheck))
        {
            throw new ArgumentException("自检必须同时提供 --self-check 和 --report <绝对路径>；--render 仅限自检使用。");
        }
        if (report is not null && string.Equals(report, render, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("JSON 报告与 PNG 渲染必须使用不同路径。");
        }
        if (reportOnExit is not null && selfCheck)
        {
            throw new ArgumentException("--report-on-exit 仅记录交互窗口收到的事件，不能与 --self-check 同时使用。");
        }
        if (inputProbe is not null && (selfCheck || reportOnExit is not null))
            throw new ArgumentException("--input-probe 是独立只读测试报告模式，不能混用其他测试模式。");
        if (captureProbe is not null && (selfCheck || reportOnExit is not null || inputProbe is not null))
            throw new ArgumentException("--capture-probe 必须单独使用。");
        if (assistedLoop is not null && (selfCheck || report is not null || render is not null || reportOnExit is not null || inputProbe is not null || captureProbe is not null))
            throw new ArgumentException("--assisted-loop-fixture 必须单独使用。");
        return new FixtureOptions(selfCheck, report, render, reportOnExit, inputProbe, captureProbe, assistedLoop, assistedLayout);
    }

    private static string ReadPath(string[] args, ref int index)
    {
        if (++index >= args.Length || !Path.IsPathFullyQualified(args[index]))
        {
            throw new ArgumentException("输出参数必须提供完整绝对文件路径。");
        }
        return Path.GetFullPath(args[index]);
    }
}
