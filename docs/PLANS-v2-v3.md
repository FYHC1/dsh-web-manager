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
   "非 dsh 占用自动顺延"（ss 解析 + 非 dsh 判定）；localhostForwarding 探测为关闭时的
   WSL IP URL 回退（`hostname -I`）留待 v2.2（当前环境 localhostForwarding 正常）

## v3.0：systemd 托管 + 多实例 + 更新机制

1. **WSL 内 systemd 用户/系统服务托管 dsh**
   - `/etc/wsl.conf` 开 `[boot] systemd=true`（需一次性 `wsl --shutdown`，实施前与用户确认）
   - systemd unit：Restart=on-failure 自愈、journald 日志、随 WSL 启动自动拉起
   - fnm node PATH/env 用 wrapper 脚本包一层
   - EXE 只做 `wsl.exe -e systemctl --user start/stop/restart dsh-web` 编排

2. **多实例（Windows + WSL 两端同时配置运行）**
   - `Instances` 数组：每实例独立端口/状态/生命周期（配置模型预留）
   - 托盘多实例子菜单（每实例一组）

3. **Runtime Bridge 插件（权威状态/优雅停止）**
   - DSH 内注入 Cordis 补丁（plugins/ 目录已预留 cordis patch 结构）
   - versioned JSON 协议：ping / getStatus / getRuntimeInfo / shutdown
   - Windows 侧 named pipe；WSL 侧 loopback TCP + launch token

4. **更新机制**：dsh 版本检查（24h 节流）、托盘提示、一键更新

## 里程碑顺序

- v2.1：WslBackend + wsl-start.sh + 互装协议 + WSL 守护 + 真机 A–I 扩展矩阵
- v3.0：systemd 迁移（需用户同意 wsl --shutdown 一次）+ 多实例 + Bridge + 更新

## 红线（持续有效）

- 测试端口 3093/3095；3080 及其上的现有 Windows/WSL 服务与窗口一律不碰
- 图标只作用于端口限定的 app 窗口；任何改动过 A–I 验证矩阵
- 不抢占/不误杀外部服务（attached 模式不杀外部进程）