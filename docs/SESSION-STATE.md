﻿﻿﻿# 会话状态保留（dsh web manager —— 关键上下文）

> 本文件用途：会话压缩/恢复时读取，重建关键事实与未完成事项。
> 最后更新：2026-08-21 v2.2 交付后

## 项目现状

- **v2.0**：Windows 后端全量交付（托盘/窗口/图标/尺寸/守护/配置/日志/迁移），A–I 矩阵通过。
- **v2.1**：WSL 后端交付（J–Q 矩阵），commit 0998cbc。
- **v2.2**：forwarding 感知 + 生命周期清理（R–V 矩阵），commit 待推送。
  - `IsServiceUp`：Windows 探测 + WSL `ss` 双通道（forwarding 关闭守护不误判）
  - `GetWindowUrl`：WSL 不可达返回空串 → 托盘提示（**dsh 拒绝 --host 0.0.0.0**，放弃 WSL-IP 回退）
  - Stop/Restart 清理 Error/Starting 残留 wrapper；wsl-start.sh 可中断 sleep；WaitReady 墙钟
  - 用户 manager 已于本会话采纳新 exe 并重启（见「当前环境状态」）

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

- 用户真实 manager：**已采纳 v2.2 新 exe**（克隆目录 dist/dsh-web-manager.exe 已替换并重启，
  托盘「后端」菜单可用）；用户 11:04–11:10 曾自行退出/重启过旧 manager（3081 服务随之停/起）
- 用户 Windows 3080 服务（PID 25080）；3081 由重启后的 v2.2 manager 管理
- 用户 WSL 3080 服务（PID 30556）；用户 Edge（pid 11076）
- 真实 config `C:\Users\hgl\.dsh-webui\config.json`：Port 3080 / windows / 2.0.0 / 945x1020,10,10
  （v2.1 测试全程未动）
- TaskbarGlomLevel = 2；WSL 侧 `~/.dsh-webui/`（wsl-start.sh + 日志 + pidfile）为 v2.1 运行时产物
- 测试沙箱：`C:\Users\hgl\AppData\Local\Temp\dwm-sandbox\`（DSH_WEB_MANAGER_HOME 指向它）
- 编译目录：`C:\Users\hgl\AppData\Local\Temp\dwm-build2\`（旧 dwm-build 被上一会话残留文件污染，勿用）

## 真实故障与修复记录（2026-08-21 同日）

### 故障 1：发行版自动选择选错（FedoraLinux44）
- 现象：用户关闭 WSL 后重启 manager，dsh 起不来，误报"localhostForwarding 关闭"
- 根因：WSL 全关时无"运行中"候选，自动选择落到默认标记的 **FedoraLinux44**
  （不可用镜像发行版：`Failed to start the systemd user session`、`command -v dsh`
  命中 Windows interop 的 /mnt/c/nvm4w/nodejs/dsh）；forwarding 实际正常
- 修复：`LastWslDistro` 记忆上次成功（managed/attached）的发行版，选择优先级
  配置 > 上次成功 > 运行中 > 唯一 > 默认 > 打分；通知文案区分"forwarding 关"与"服务未就绪"

### 故障 2：WSL 冷启动 forwarding 时序竞态
- 现象：修复后一段时间，manager 突弹"服务未就绪"/"forwarding 关闭"通知，窗口不可用；
  重启 manager 仍复现
- 根因：WSL 重启/冷启动后 localhostForwarding 需数秒~数十秒才建立；manager 在
  forwarding 就绪前判定"关闭"并开空窗/弹通知（WSL 侧 ss 显示服务在跑）
- 修复：OpenWindow 后台重试 4×5s=20s 宽限（成功即开窗，最终失败才按 IsServiceUp
  区分通知文案）；StartSystemd 前 WaitSystemdUserReady（探测
  /run/user/<uid>/systemd/private，最多 30s）

## 未完成事项

1. 用户侧采纳已基本完成（新 exe 已替换并重启）；桌面快捷方式仍指向克隆 exe（未重跑 Install.ps1，
   无碍——同路径）。建议用户日后确认托盘菜单出现「后端」「WSL 服务模式」项
2. wsl-bootstrap.sh 的「未安装→静默安装」分支真机演练（需临时移除 manager，谨慎）
   - （已修复的真实故障）发行版自动选择记忆：LastWslDistro=FedoraLinux 已持久化，
     FedoraLinux44 为不可用镜像发行版（勿选），用户 config 可显式 WslDistro=FedoraLinux
3. v3.0 剩余：Runtime Bridge 插件（权威状态/优雅停止）、多实例（两端同开）、更新机制
   （**systemd 托管代码层已交付**：本机 /etc/wsl.conf 已含 systemd=true，无需 wsl --shutdown；
   托盘「WSL 服务模式 → systemd」即可启用）

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
