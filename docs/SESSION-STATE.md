# 会话状态保留（dsh web manager —— 关键上下文）

> 本文件用途：会话压缩/恢复时读取，重建关键事实与未完成事项。
> 最后更新：2026-08-21 v2.1 交付后

## 项目现状

- **v2.0**：Windows 后端全量交付（托盘/窗口/图标/尺寸/守护/配置/日志/迁移），A–I 矩阵通过。
- **v2.1**：WSL 后端已交付并真机验证（J–Q 矩阵），commit 待推送。
  - `IServiceBackend` 抽象：`WindowsBackend` + `WslBackend` + `BackendFactory`
  - WSL 侧 `wsl-start.sh`（自愈循环 + pidfile + TERM 陷阱，物化到 `~/.dsh-webui/`）
  - 互装：`wsl-bootstrap.sh`（WSL→Windows，Get-Process 探测 + 静默拉起/安装）
  - 每后端独立端口（`Port` / `WslPort`）+ 顺延写回；托盘「后端」菜单 + `backend wsl` 管道动作
  - 测试沙箱：`DSH_WEB_MANAGER_HOME` 环境变量隔离 config/日志/mutex/管道
  - 待办遗留：v2.2 WSL IP URL 回退（localhostForwarding 关闭时）；wsl-bootstrap 安装分支真机演练

## 用户原始诉求（任务栏图标，已闭环）

任务栏上新拉起的 dsh web 窗口曾显示 Edge 图标。结论：**代码层 AUMID 不可能**
（Chromium `--app` 窗口 `SetValue` 抛 0x80070002；notepad 对照组成功证明是窗口拒绝），
按用户指示保留**从不合并**（`TaskbarGlomLevel=2`，已生效），README 已写明。

## 已确认事实（全部技术验证）

1. 系统 = Windows 10 Pro 22H2（Build 19045）；`TaskbarGlomLevel=2`（从不合并，已生效）
2. manager 拉起的窗口图标一直正确：`WM_GETICON` 像素 sha = `000000000000FFFFFFFFFFFF`
   == 官方 `dsh-webui.ico` 32px 采样（比对像素 sha，绝不比 HICON 句柄）
3. 3080 旧脚本窗口 sha = `000000070707C6C6C6EFEFEF`（Edge 特征，无 watcher 设置）
4. 合并（GlomLevel 0/1）时同 EXE 无 AUMID 窗口合并成单按钮显示进程图标（Edge）；
   从不合并显示各自窗口图标
5. **wsl.exe 参数透传二次解析**：内联 bash 命令含 `$(...)`/`$VAR`/`\(` 会被 wsl.exe→sh 链
   损坏（sed 收到字面引号）；简单命令（pkill/mkdir/cp/wslpath）不受影响。
   修复：`ss -tlnp` 纯参数 + C# 侧解析；复杂逻辑走脚本文件
6. `Start-Process -ArgumentList 'backend wsl'` 拆分带空格参数 → Program.cs 需
   `String.Join(" ", args)`；`tasklist.exe //FI` 在 WSL interop 不转换；`Get-Process -Name`
   不接受 `.exe` 后缀（用 `dsh-web-manager*` 通配）
7. WSL 有两个发行版：**FedoraLinux**（Running，当前会话所在，用户 WSL dsh 3080 在此）、
   **FedoraLinux44**（默认标记但 Stopped）→ 自动探测必须优先运行中
8. 用户 WSL 3080 服务由旧安装器脚本 `/home/hgl/.local/share/dsh-webui/start-dsh-webui.sh`
   拉起（node `dsh web --port 3080`，PID 30556）—— 与 v2.1 的 `~/.dsh-webui/wsl-start.sh` 并存，
   互不干扰

## 当前环境状态

- 用户真实 manager 在跑：PID 24476（`C:\D\CodeProjects\Github\dsh-web-manager\dist\` 旧 v2.0 exe，
  **该 exe 文件被锁定，v2.1 新 exe 已就位待用户重启 manager 后替换/重装**）
- 3081 服务监听（PID 20912）；用户 Windows 3080 服务（PID 25080）
- 用户 WSL 3080 服务（PID 30556）；用户 Edge（pid 11076）
- 真实 config `C:\Users\hgl\.dsh-webui\config.json`：Port 3080 / windows / 2.0.0 / 945x1020,10,10
  （v2.1 测试全程未动）
- TaskbarGlomLevel = 2；WSL 侧 `~/.dsh-webui/`（wsl-start.sh + 日志 + pidfile）为 v2.1 运行时产物
- 测试沙箱：`C:\Users\hgl\AppData\Local\Temp\dwm-sandbox\`（DSH_WEB_MANAGER_HOME 指向它）
- 编译目录：`C:\Users\hgl\AppData\Local\Temp\dwm-build2\`（旧 dwm-build 被上一会话残留文件污染，勿用）

## 未完成事项

1. **用户侧采纳 v2.1**：停掉旧 manager → 用新 exe 替换克隆目录 → （可选）重跑 Install.ps1
   使桌面快捷方式指向新版本 + 物化 WSL 伴侣脚本
2. v2.2：localhostForwarding 关闭时的 WSL IP URL 回退（`wsl.exe hostname -I`）
3. wsl-bootstrap.sh 的「未安装→静默安装」分支真机演练（需临时卸载/改名 manager，谨慎）
4. v3.0：systemd 托管（需用户同意一次 `wsl --shutdown`）+ 多实例 + Runtime Bridge + 更新机制

## 仓库路径速查

- 新仓库 `/home/hgl/projects/dsh/dsh-web-manager`（GitHub FYHC1/dsh-web-manager，main）
- 用户克隆 `C:\D\CodeProjects\Github\dsh-web-manager\`（dist 已同步除 exe 外全部）
- 编译临时目录 `C:\Users\hgl\AppData\Local\Temp\dwm-build2\`
- 共享配置 `C:\Users\hgl\.dsh-webui\config.json`
- 日志（真实）`C:\Users\hgl\AppData\Local\dsh-web-manager\logs\manager.log`
- 官方图标 `C:\Users\hgl\.dsh-webui\dsh-webui.ico`（sha a821…）
- WSL 侧状态 `~/.dsh-webui/`（wsl-start.sh / wsl-dsh.log / wsl-dsh.pid / wsl-bootstrap.sh）

## 开发环境速查

- 编译：`powershell -File scripts\Build.ps1`（系统 csc.exe，目标 .NET 4.8）；
  编辑后 .cs/.ps1 必须带 UTF-8 BOM（`printf '\xef\xbb\xbf' | cat - f`）
- 从 WSL 调 Windows：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File <win路径>`；
  避免 UNC 路径；先复制到 C:\ 临时目录
- WSL 发行版：FedoraLinux（运行中）；dsh = `~/.local/share/fnm/.../installation/bin/dsh` v0.1.0-rc.7
- 用户 Windows dsh = `C:\nvm4w\nodejs\dsh.cmd`
- dsh 正确语法：`dsh --profile <name> --host 127.0.0.1 --port N`（不是 `dsh web --port`）
- Windows npmjs 不通 → 必须 npmmirror；dsh 插件元数据拉取也失败 → 手动 npm install

## 测试资产

- 测试端口：3093/3095（禁止 3080）；测试沙箱 `DSH_WEB_MANAGER_HOME=C:\...\Temp\dwm-sandbox`
- 识图工具链：python3 + freebuff2api（192.168.2.14:8877/v1，FREEBUFF2API_API_KEY 在
  `/home/hgl/.dsh/.credentials.yaml`）；oc2api（:18080）不支持图像，勿用
- v2.1 关键修复记录见 docs/TESTING.md「关键发现与修复」
