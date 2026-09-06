# Prism 0.2.0 Preview 1 · Windows x64

首个 GitHub 预览发行版 / First GitHub preview release.

## 下载与启动 / Downloads

- **Prism-0.2.0-preview.1-win-x64.exe**：单文件启动，内含 .NET 10.0.11，无需预装 .NET。首次运行会把依赖释放到 Windows 用户临时目录，需可写空间；不是“完全不落盘”。
- **Prism-0.2.0-preview.1-win-x64.zip**：完整便携目录，解压全部文件后运行 Prism.cmd，不能只取包里的 DesktopAgent.exe。
- **SHA256SUMS.txt**：EXE 与 ZIP 的校验值。

Standalone EXE includes .NET and extracts bundled dependencies to the user's temporary directory on first launch. The ZIP contains a complete portable folder; extract everything and run Prism.cmd. Both builds use the same application source. Neither includes an API key.

## 此版本变化 / Highlights

- Prism紧凑主页、浅色/深色/跟随系统、玻璃任务卡和彩虹边框。
- 当前步骤默认显示，理解、计划和反馈按需展开；任务内回复、确认与新任务。
- 完成后继续输入，胶囊和托盘待命；暂停与紧急停止。
- 单文件EXE与完整ZIP两种分发形式，修正单文件/重命名启动时的托盘图标来源。

Compact WPF interface, light/dark/system themes, floating task card, expandable plans and feedback, in-task replies and approvals, new-task control, capsule and system tray. This release adds standalone EXE distribution and preserves native mouse/keyboard execution.

## 已知缺陷与限制 / Known issues and limitations

1. **复杂任务仍可能卡住**：陌生页面、右键子菜单、创建文件后的重命名与文字替换可能定位失败、重复尝试或暂停。完整“创建文件夹→325.txt→写入→保存退出”没有完成统一验收。
2. **微信只完成部分启动验证**：进入账户、查看新消息、选定联系人及最终发送的完整流程未验收；不能把启动成功当作完整微信任务可用。
3. **模型响应不稳定**：可能出现超时、空响应、无效结构或误解模糊意图。恢复与计划反馈已存在，但不保证自动恢复所有情况；没有任务成功率或速度保证。
4. **依赖可靠控件辅助定位**：自绘/无障碍信息不足的界面可能不可用；纯视觉通用定位尚未完成。实际操作会占用前台键鼠，多实例可能争用热键。
5. **高风险操作默认关闭**：允许启用后继续，但具体提交仍需逐次确认；不绕过扫码、UAC、验证码或系统权限。
6. **未代码签名、无安装器或自动更新**：Windows可能提示未知发布者。只支持本版验证范围的Windows x64；不保证所有机器兼容。单文件首次解包较慢，配置仍保存在用户目录。
7. **验收范围有限**：历史本地1030核心测试和84界面检查不是模型真实任务成功率。此前GitHub CI出现一次测试失败，具体原因尚未定位；本次包检查会在Release正文单独记录，不能据此宣称CI已修复。

Complex tasks can still stall on unfamiliar screens, submenus, renaming, or text replacement. End-to-end file creation and full WeChat workflows remain unverified. Model timeouts, empty/invalid responses, intent errors, and limited UI Automation coverage remain possible. Foreground input is shared with the user; avoid running multiple instances. High-impact submissions require specific approval. This is unsigned preview software without an installer or auto-update. A previous CI test failure is unresolved; local checks do not establish general task reliability.

## 首次配置与退出 / Setup and exit

在设置页输入自己的API地址、精确模型名和密钥，验证辅助定位后开始。默认预设 https://api.deepseek.com / deepseek-v4-flash-vision-exp；另有兼容服务配置入口，需分别验证。不自动更换模型或计费服务。任务截图、控件文字与指令发送到你配置的服务。密钥按Windows当前用户加密存储。

Configure your own endpoint, exact model, and API key in Settings. Screenshots, control text, and task instructions are sent to your configured provider. API charges may apply. Keys are encrypted for the current Windows user.

**Ctrl+Alt+F8 暂停 / Pause · Ctrl+Alt+F9 紧急停止 / Emergency stop**。关闭主窗口进入托盘；彻底退出使用托盘菜单。Closing the main window keeps Prism in the tray; choose Exit Prism from the tray menu to quit.

## 许可 / License

MIT；运行时第三方许可随ZIP提供，并内嵌EXE的distribution目录中，启动解包后可查阅。Runtime notices ship with the ZIP and are embedded in the EXE's extracted distribution directory.
