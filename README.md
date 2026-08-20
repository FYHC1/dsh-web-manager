# dsh web manager

Windows 侧常驻托盘的 **DeepSeek Harness WebUI 管理器**：负责启动 dsh web、拉起 Edge 应用窗口、守护服务进程、常驻系统托盘，并接管窗口图标与窗口尺寸记忆。

- **跨 Windows / WSL2**：管理器本体只运行在 Windows；WSL 内的 dsh web 由管理器通过 `wsl.exe` 托管（v2.1）。
- **窗口只是视图**：关闭浏览器窗口不会结束 dsh web；点托盘图标随时重新唤起窗口。
- **图标与尺寸**：以官方 `DeepSeek Harness.ico` 通过 `WM_SETICON` 设置到应用窗口并持续维持；窗口尺寸按记忆复现。

> 与旧项目的关系：本仓库为 v2.0+ 的独立实现（独立交付），旧项目
> `FYHC1/dsh-webui-installer`（v1.4.x 脚本链路）收尾于 v1.4.2，不再大改。

## 用法

Windows PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Install.ps1
```

安装内容：

- 编译或复制 `dist\dsh-web-manager.exe`（.NET Framework 4.8，Win10/11 自带运行时）到 `%LOCALAPPDATA%\dsh-web-manager\app\`
- 创建桌面/开始菜单快捷方式（指向 `dsh-web-manager.exe open`）
- 冒泡式初始化共享配置 `%USERPROFILE%\.dsh-webui\config.json`（Windows 与 WSL 两侧可见）
- 可选：开机自启（仅托盘、不弹窗）

首次双击快捷方式 → 管理器以单实例启动：

1. 探测端口（默认 3080，被非 dsh 进程占用时自动顺延 3080+n，可配置关闭）
2. 未监听 → 隐藏拉起 `dsh web --port N` 并接管（managed）；已监听 → 附着（attached，不抢不杀）
3. 打开 Edge `--app=http://127.0.0.1:N`（沿用记忆的窗口尺寸/位置，独立浏览器数据目录）
4. 常驻托盘：关窗不结束服务，点托盘/菜单「打开窗口」随时重新唤起
5. 心跳循环：端口健康、图标维持（`WM_SETICON` 32/16px）、窗口尺寸采集、崩溃检测与退避重启

托盘右键菜单：打开窗口 / 重启服务 / 退出（停服务）/ 开机自启 / 状态。

## 状态与配置

`config.json` 关键字段（`%USERPROFILE%\.dsh-webui\config.json`）：

| 字段 | 说明 | 默认 |
| --- | --- | --- |
| `port` | 首选端口 | `3080` |
| `autoFallback` | 非 dsh 占用时自动顺延空闲端口 | `true` |
| `dataDir` | Edge 独立浏览器数据目录（留空用默认） | `""` |
| `closeStopsService` | 关闭窗口时同时停止服务（旧行为开关） | `false` |
| `exitKeepService` | 退出托盘时保留服务 | `false` |
| `autoStart` | 开机自启（托盘不弹窗） | `false` |
| `window.size` / `window.position` | 记忆的窗口尺寸与位置 | 空（Edge 默认） |

## 开发

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Build.ps1   # 系统 csc.exe 编译 + 测试程序
```

要求：任意 Windows 10/11（自带 .NET Framework 4.8 与 C# 编译器 `csc.exe`），无需安装 Visual Studio。

## 里程碑

- **v2.0**：Windows 后端全量（本目录当前代码）——托盘、窗口、图标、尺寸、守护、配置、日志、迁移、A–I 验证
- **v2.1**：WSL 后端（`wsl.exe` exec 语义托管 WSL 内 dsh web + distro 自动探测 + attached/managed 所有权 + 双向互装）
- **v3.0**：Runtime Bridge 插件（权威状态/优雅停止）、多实例（两端同开）、更新机制、设置界面

## 许可

MIT