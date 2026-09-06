# 开发说明

## 结构

- `src/DesktopAgent.Core`：不可变任务、观察协议、提供商、计划与批准状态机。
- `src/DesktopAgent.Windows`：WPF 界面、主题与托盘、Win32 输入、截图、只读控件辅助、设置存储。
- `tests/DesktopAgent.Core.Tests`：离线协议与运行时回归。
- `tests/DesktopAgent.NativeFixture`：明确的自有原生测试窗口。
- `scripts/Build.ps1`：构建、测试、便携包；不启动付费模型任务。

`DesktopAgent` 是兼容保留的内部程序集名称，公开品牌为 Prism。不要仅复制 exe；Windows 发布包包含依赖和 .NET 运行时。

## 构建

SDK 版本精确记录在 `global.json`。`Build.ps1` 默认使用 PATH 中的 dotnet，也支持 `-DotnetPath` 指向自备 SDK。离线构建可加 `-Offline -PackageCache <已有NuGet缓存路径>`；缓存不完整时会失败，不会下载或安装工具链。

```powershell
pwsh -File scripts/Build.ps1 -Task Build
pwsh -File scripts/Build.ps1 -Task Test
pwsh -File scripts/Build.ps1 -Task Package
```

Package 输出新目录并计算 zip 的 SHA256，不覆盖旧发布。发布包不嵌入 API 配置。仓库保留的早期工程诊断入口不是产品流程，部分需要专门准备的本地测试状态；不要随意执行带 run/prepare 的入口。

## 界面与输入边界

ThemeManager 使用共享动态资源，默认跟随系统；固定主题覆盖只影响本程序。悬浮卡默认收起说明，任务问题与批准区直接可见。托盘退出沿用停止、取消、清理路径；收到旧任务回调不能覆盖新任务。

点击和文本输入仍通过真实系统输入执行器，UIA 仅辅助只读定位。每个动作绑定任务、租约和新帧；暂停/停止使旧结果失效。不要用 UIA 写操作、脚本或浏览器模拟替代真实输入，也不要将界面诊断当作视觉模型实测。
