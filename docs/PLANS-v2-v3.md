# v2.1 / v3.0 实现计划（dsh web manager）

> 最终决策记录（截至 2026-08-21 会话）：
> 语言 C#/.NET Framework 4.8 + WinForms；新仓库 FYHC1/dsh-web-manager 独立交付；
> 退出托盘=停服务（默认，`exitKeepService` 可改）；端口非 dsh 占用自动顺延；
> 开机自启默认关、自启不弹窗；WSL 托管走 wrapper 路线（v2.1），systemd 路线留 v3.0。
>
> **v2.1 已于 2026-08-21 交付并真机验证**（见 TESTING.md J–Q 矩阵）。

## v2.1：WSL 后端 + 双向互装 ✅ 已交付

交付内容：

1. **WSL 服务托管（wrapper 路线）** ✅
   - 采用参考项目（kanneiren/dsh-windows-manager）的 `exec` 语义：
     `wsl.exe -d <distro> --cd <dir> -- bash -lc 'exec dsh --profile <p> --host 127.0.0.1 --port N'`
     （实现为 `wsl.exe -d <distro> -- bash -lc '~/.dsh-webui/wsl-start.sh <p> <port>'`，
     脚本自带自愈循环 + pidfile + TERM 陷阱）
   - Windows 侧 `wsl.exe` 进程生命周期 == WSL 内 DSH 生命周期 → 原生进程句柄可作 liveness，
     kill 进程树即停服务
   - `ServiceBackend` 接口：`WindowsBackend`（v2.0 现有）+ `WslBackend`（v2.1 新增）✅
   - distro 自动探测：`wsl.exe --list --quiet/--verbose`，过滤 Docker Desktop/Rancher/Podman
     辅助发行版；优先级：配置的 > 唯一候选 > **运行中** > 默认 > 名称打分
     （实机修正：默认发行版 Stopped 时优先选运行中的）
   - attached/managed 所有权模型：管理器拉起的=managed（完整生命周期控制），
     外部已运行的 WSL dsh=attached（只监控/受控停止）✅

2. **双向互装协议（Windows ↔ WSL）** ✅（基础版）
   - 共享配置位于 `%USERPROFILE%\.dsh-webui\config.json`（WSL 侧可见 `/mnt/c/Users/<user>/.dsh-webui/`）✅
   - WSL 侧 `wsl-bootstrap.sh` → 检查 Windows 侧 manager 进程（Get-Process 通配符）
     → 存在则转发动作；不存在则静默启动；未安装则经共享目录 `wsl-bootstrap\Install.ps1` 静默安装 ✅
   - Windows 侧安装插件 → 经 wsl.exe 物化 wsl-start.sh / wsl-bootstrap.sh 到 WSL home ✅
   - 竞态：共享目录 `bootstrap.lock`（先到先得 + 幂等重试）✅
   - 遗留：WSL→Windows 安装路径（第 3 分支）未在真机演练（避免卸载用户 manager）

3. **WSL 守护** ✅：Windows 侧端口探测（百毫秒级）+ 探测到崩溃按退避重拉；
   WSL 侧脚本自带崩溃循环做第一道自愈（manager 检测到 wrapper 存活时等待自愈，不争抢）

4. **端口策略** ✅：每模式独立端口记忆（Port / WslPort，顺延写回）；WSL 模式同样走
   "非 dsh 占用自动顺延"（ss 解析 + 非 dsh 判定）
   - v2.2 调整：`localhostForwarding` 关闭时 **不做 WSL IP URL 回退** —— dsh 出于安全
     拒绝 `--host 0.0.0.0`（RCE 防护），服务只能绑 127.0.0.1，forwarding 关闭时 Windows
     本就不可达；管理器改为：健康探测走 WSL 侧 ss 回退（守护不误判）+ 打开窗口时给出明确提示。

## v2.2：forwarding 感知 + 生命周期清理修复 ✅ 已交付（2026-08-21）

1. **后端感知健康探测**：`IServiceBackend.IsServiceUp(port)` —— Windows 探测失败时
   WSL 后端回退 `ss -tlnp` 解析（forwarding 关闭时守护/状态/等待就绪不误判）
2. **窗口 URL 策略**：`IServiceBackend.GetWindowUrl(port)`；WSL 不可达时返回空串，
   `OpenWindow` 弹提示（"localhostForwarding 关闭，Windows 无法访问"）而非打开打不开的窗口
3. **生命周期清理修复**：Stop/Restart 在后端已拉起 wrapper 但启动失败（Error/Starting）
   时仍调用 backend.Stop()，杜绝残留 wsl.exe/脚本空转
4. **可中断 sleep**：wsl-start.sh 用 `sleep N & wait $!`，TERM 陷阱不再被前台 sleep 延迟
5. **墙钟超时**：WaitReadyBackend 用墙钟截止（迭代计数会被 WSL 探测耗时拉长数倍）
6. **wsl-bootstrap 安装分支**真机演练仍待做（需临时移除用户 manager，谨慎）

## v3.0：systemd 托管 + 多实例 + 更新机制

1. **WSL 内 systemd 用户服务托管 dsh** ✅ 代码层已交付（2026-08-21，W–Z 矩阵）
   - `/etc/wsl.conf` 开 `[boot] systemd=true` —— **本机 FedoraLinux 已启用**（PID 1=systemd），
     无需 wsl --shutdown；若在未启用的发行版使用：改 wsl.conf 后需一次性 `wsl --shutdown`
   - systemd unit：`~/.config/systemd/user/dsh-web-<port>.service`（Type=simple /
     Restart=on-failure / RestartSec=3 / journald 日志 / WantedBy=default.target 随登录拉起）
   - `wsl-systemd-start.sh`：前台 exec dsh（fnm/toolchain bootstrap 一层）
   - EXE 编排：`systemctl --user start/stop daemon-reload`（XDG_RUNTIME_DIR 经 id -u 拼接）
   - `WslServiceMode` 配置（wrapper|systemd）+ 托盘「WSL 服务模式」子菜单 + `wslmode` 管道动作；
     systemd 不可用自动回退 wrapper（不破坏现有功能）
   - 遗留：`systemctl --user` 依赖登录会话的 user manager；无登录会话的发行版需
     `loginctl enable-linger`（记入交付说明）

2. **多实例（Windows + WSL 两端同时配置运行）** ✅ 已交付（2026-08-21）
   - `Instances` 数组（InstanceConfig：profile/backend/port/wsl*/window/enabled）；
     EffectiveInstances 回退 legacy 单实例字段（向后兼容）
   - 托盘「实例」子菜单（每实例 打开窗口/重启/状态）；沙箱验证 Windows+WSL 同开

3. **Runtime Bridge 插件（权威状态/优雅停止）** ✅ 已交付（2026-08-21）
   - 已重构为可安装的 dsh 插件包 `dsh-web-manager`（Cordis bundle 包：
     package.json `dsh.bundle.patch` + 根 `cordis.patch.yml` + `lib/index.js`），
     附 Windows 托盘 exe + WSL 脚本；`dsh plugin --profile <name> add dsh-web-manager` 一键安装
   - versioned line-JSON 协议：ping / getStatus / getRuntimeInfo / shutdown（SIGTERM 优雅停止），
     token 校验；监听 WSL 内 127.0.0.1:<port+100>，manager 经 localhostForwarding 直连
   - wsl-start.sh / wsl-systemd-start.sh 传 DSH_BRIDGE_* env；manager 启动传 token +
     ping 验证 + Stop 先 bridge shutdown 再 kill 兜底
   - 端到端验证：真实 dsh 加载插件 → 全协议通过
   - 已交付：注入用户 web profile（`dsh plugin --profile web add dsh-web-manager`，重启 dsh 生效）

4. **更新机制** ✅ 已交付（2026-08-21）：UpdateChecker（npmmirror registry 最新版 +
   dsh --version 当前版，LastVersionCheckUtc 24h 节流）；托盘「更新」子菜单
   （检查更新 / 更新 dsh = npm install -g @deepseek-ai/dsh@latest --registry=npmmirror）；
   启动后台节流检查，有新版 balloon 提示

## 里程碑顺序

- v2.1：WslBackend + wsl-start.sh + 互装协议 + WSL 守护 + 真机 A–I 扩展矩阵
- v3.0：systemd 迁移（需用户同意 wsl --shutdown 一次）+ 多实例 + Bridge + 更新

## 红线（持续有效）

- 测试端口 3093/3095；3080 及其上的现有 Windows/WSL 服务与窗口一律不碰
- 图标只作用于端口限定的 app 窗口；任何改动过 A–I 验证矩阵
- 不抢占/不误杀外部服务（attached 模式不杀外部进程）