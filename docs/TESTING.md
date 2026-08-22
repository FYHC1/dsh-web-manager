# 验证矩阵（v2.0 + v2.1 真机实测记录）

测试机：Windows 11 22H2 + WSL2（FedoraLinux 运行中 / FedoraLinux44 默认但 Stopped）
dsh 命令：Windows `C:\nvm4w\nodejs\dsh.cmd`；WSL `/home/hgl/.local/share/fnm/.../dsh`
测试端口：3093 / 3095（绝不用 3080 做破坏性实验）
红线：用户 Windows 3080 服务（PID 25080）、3081 服务/窗口（PID 20912）、
用户 WSL 3080 服务（PID 30556）、正常 Edge（pid 11076）、真实 config 全程不动。

## v2.0 矩阵（Windows 后端）

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| A | 启动 | `dsh-web-manager.exe open` → 拉起 dsh web → 弹 Edge 窗口 | ✅ 服务监听；窗口图标 sha 与官方 ico 一致；窗口尺寸已保存 |
| B | 关窗常驻 | 关闭 app 窗口 | ✅ 服务存活（默认 closeStopsService=false） |
| C | 托盘唤起 | 再次 `exe open`（单实例转发） | ✅ 新窗口重现；管理器进程唯一 |
| D | 单实例 | 多次启动 | ✅ 恒为 1 个 dsh-web-manager 进程（Mutex + 命名管道转发） |
| E | 崩溃守护 | 杀 managed node 进程 | ✅ crash #1 检测 → 自动重启（1/3 退避）→ 服务恢复 |
| F | 外部附着 | 先起外部 3095 dsh web，再启动 manager | ✅ attached 识别（不抢占）；不杀外部服务 |
| G | 退出停服 | `exe exit`（managed 模式） | ✅ 停服务、taskkill 树、端口释放 |
| H | 关窗停服开关 | closeStopsService=true + 关窗 | ✅ 服务随窗口关闭而停止 |
| I | 3080 红线 | 全程监听 3080 | ✅ 用户服务与窗口全程不受影响 |

## v2.1 矩阵（WSL 后端 + 互装 + 守护）— 2026-08-21 实测

测试沙箱：`DSH_WEB_MANAGER_HOME=C:\Users\hgl\AppData\Local\Temp\dwm-sandbox`
（隔离 config/日志/mutex/管道，与真实安装完全互不干扰）

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| J | WSL 托管启动 | 配置 BackendType=wsl, WslPort=3095, WslDistro="" → `open` | ✅ 自动选中运行中的 **FedoraLinux**（未选默认但 Stopped 的 FedoraLinux44）；wsl-start.sh 物化到 `~/.dsh-webui/`；dsh 在 WSL 内 3095 监听；Windows 经 localhost 转发可达；状态 Managed；Edge 窗口打开 |
| K | WSL 崩溃自愈 | `kill -9 <dsh pid>` | ✅ wsl-start.sh 3 秒内重启（新 pid）；manager 日志 `wrapper alive; waiting for self-heal`（不误计崩溃、不与之争抢） |
| L | 退出停 WSL 服务 | `exe exit` | ✅ TERM 陷阱 `received TERM, stopping dsh` → 脚本退出、pidfile 清除、3095 释放 |
| M | WSL attach | 手动 `dsh web --port 3095` 后启动 manager | ✅ `已附着现有 dsh 服务 (WSL (FedoraLinux), port 3095)`；外部进程不杀、不起第二实例 |
| N | Windows 回归 | BackendType=windows, Port=3093 | ✅ dsh.cmd 拉起、Managed、监听 3093 |
| O | 后端热切换 | 运行中 `backend wsl`（控制管道） | ✅ 停 Windows 3093 → 切 WSL → 3095 启动，状态/窗口跟随 |
| P | WSL→Windows 互装 | `wsl-bootstrap.sh tray` | ✅ 探测到 manager 运行 → 转发动作；bootstrap.lock 先到先得并清理 |
| Q | 沙箱隔离 | 全程对照真实 config/进程 | ✅ 真实 config.json 原封不动（Port 3080/windows/2.0.0）；真实 manager(24476)、3080(30556)、3081(20912) 全程无损 |

## v2.2 矩阵（forwarding 感知 + 清理修复）— 2026-08-21 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| R | WSL 托管回归 | v2.2 构建 WSL 后端 3095 | ✅ 127.0.0.1 绑定（dsh 拒绝 0.0.0.0）、Windows 可达、URL=http://127.0.0.1:3095/、崩溃自愈、后端切换均正常 |
| S | Error 状态清理 | 不存在的 profile 启动失败 → Error/Starting + 存活 wrapper → exit | ✅ `Stopping managed dsh` 触发后端清理；退避中的 wsl-start.sh 被 TERM 秒杀、无残留（修复前会残留 wrapper/脚本） |
| T | 可中断 sleep | 脚本进入 60s 退避后发 TERM | ✅ `sleep_int`（后台 sleep + wait）使陷阱立即触发（修复前前台 sleep 60 延迟陷阱最多 60s） |
| U | 墙钟超时 | WSL 后端启动失败等待超时 | ✅ WaitReadyBackend 改用墙钟截止（修复前按迭代计数，WSL 探测每轮 ~1.5s 导致实际超时 ~2min） |
| V | dsh 安全限制 | `--host 0.0.0.0` | ✅ **dsh 主动拒绝**（"intentionally not supported yet for safety"，防 RCE 暴露）→ v2.2 放弃 0.0.0.0/WSL-IP 方案，forwarding 关闭时 GetWindowUrl 返回空串 + 托盘提示，不做无谓的 URL 回退 |

## v3.0 systemd 矩阵（代码层交付）— 2026-08-21 实测

> 关键前提：**本机 FedoraLinux 的 /etc/wsl.conf 已含 [boot] systemd=true**（PID 1=systemd），
> 因此 systemd 模式可完整真机验证，**无需 wsl --shutdown**。

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| W | systemd 托管启动 | WslServiceMode=systemd + WSL 后端 3095 | ✅ 探测到 systemd → unit dsh-web-3095.service active (running)，Main PID = 3095 监听者；unit 生成到 ~/.config/systemd/user/（%h/ExecStart 正确） |
| X | systemd 崩溃自愈 | `kill -9` dsh 进程 | ✅ systemd `Scheduled restart job, restart counter at 1` → 3s 拉起（Restart=on-failure/RestartSec=3）；journald 完整记录（wsl-systemd-start.sh 输出入 journal） |
| Y | systemd 停服 | `exe exit` | ✅ `systemctl --user stop` → unit inactive (dead)、端口释放 |
| Z | 模式热切换 | systemd ⇄ wrapper（wslmode 管道） | ✅ 双向切换正常；systemctl stop 后等待端口释放，端口保持 3095 不顺延 |
| AA | systemd 不可用回退 | （本机 systemd 可用，未触发） | 代码路径：SystemdAvailable=false 时回退 wrapper + 日志提示，不破坏现有功能 |
| BB | wrapper 回归 | 切回 wrapper 模式 | ✅ 服务正常、窗口正常 |

## 关键发现与修复（开发过程记录）

- `dsh web` 是错误用法；正确语法为 `dsh --profile <profile> [--host 127.0.0.1] --port <port>`。
- Windows 侧访问 npmjs.org 不通：dsh 构建 profile 依赖时须走 npmmirror 镜像，或直接用既有 profile 目录。
- 用户 web profile 的 `dsh-better-sidebar` 插件曾缺依赖：已用
  `npm install --prefix ...\dsh-better-sidebar --registry=https://registry.npmmirror.com --ignore-scripts`
  补齐全部依赖（schemastery 等 207 包），并恢复 patch 中该插件为启用状态；
  dsh web 完整启动验证通过（HTTP 200）。
- 启动子进程用 `cmd /d /s /c ""dsh.cmd" args"` + 异步流捕获（重定向到文件在 UNC 工作目录下会失败）。
- 二次实例经命名管道转发动作；`exit` 用 `Environment.Exit` 保证托盘进程必然终止。
- **wsl.exe 参数透传会二次解析内联脚本**：含 `$(...)` / `$VAR` / `\(` 的 bash 内联命令会被
  wsl.exe→sh 链损坏（sed 收到字面引号）。修复：`ss -tlnp` 纯参数输出 + C# 侧解析（零元字符）。
  简单命令（pkill / mkdir / cp / wslpath）不受影响。
- `Start-Process -ArgumentList 'backend wsl'` 会把带空格参数拆成两个 → Program.cs 需
  `String.Join(" ", args)` 再转发，否则控制动作被截断为 `backend`（no-op）。
- `tasklist.exe //FI` 在 WSL interop 下不被转换（报"无效参数"）；`Get-Process -Name` 不接受
  `.exe` 后缀 → 用 `Get-Process -Name 'dsh-web-manager*'`（剥离扩展名 + 通配符）。
- WSL 发行版自动选择优先级：配置 > 唯一候选 > 运行中(唯一) > 默认 > 名称打分
  （若默认发行版处于 Stopped 而另有运行中的，选运行中的那个）。
- **dsh 拒绝 `--host 0.0.0.0`**（安全设计防 RCE）→ WSL 服务只能绑 127.0.0.1，
  依赖 localhostForwarding 供 Windows 访问；forwarding 关闭时管理器给出明确提示而非打开打不开的窗口。
- **wsl.exe 透传 + bash 陷阱延迟**：前台 `sleep 60` 期间收到 TERM 会等 sleep 结束才执行陷阱
  → 用 `sleep N & wait $!` 使陷阱立即触发。
- **迭代计数超时陷阱**：等待就绪/崩溃检测若按"轮数×500ms"计时会因 WSL 探测耗时（wsl.exe 拉起 ~1.5s）
  把实际超时拉长数倍 → 一律用墙钟截止。
- **Error/Starting 状态的清理**：后端已拉起 wrapper 但启动失败（如 profile 不存在）时，
  Stop/Exit 必须仍调用 backend.Stop() 清理 wsl.exe/脚本，否则残留进程继续空转。
- **本机 systemd 早已启用**：FedoraLinux 的 /etc/wsl.conf 含 `[boot] systemd=true`（PID 1=systemd），
  v3.0 systemd 托管无需 wsl --shutdown；`systemctl --user` 直接可用（WSL 登录会话提供 user manager）。
- **systemctl stop 是异步的**：停服后端口需短暂释放，紧跟的 Start 会误判占用而顺延端口
  → Stop 后轮询 WslPortOwnerPid 归零（最多 3s）再返回。
- **systemd unit 生成**：manager 物化 wsl-systemd-start.sh（前台 exec dsh）+ 写
  ~/.config/systemd/user/dsh-web-<port>.service（Type=simple / Restart=on-failure /
  journald 日志），文件写入不依赖 systemd 运行，可在启用前预置。
- **真实故障 2（2026-08-21，已修复）**：WSL 重启/冷启动后 localhostForwarding 需数秒~数十秒
  才建立；manager 在 forwarding 就绪前判定"关闭"并弹通知/开空窗，重启 manager 仍复现。
  修复：OpenWindow 后台重试（4×5s=20s 宽限，成功即开窗，最终失败才按 IsServiceUp 区分
  通知文案）；StartSystemd 前 WaitSystemdUserReady（探测 /run/user/<uid>/systemd/private，
  最多 30s），WSL 冷启动时 systemctl start 不再立即失败。
- **真实故障（2026-08-21，已修复）**：用户关闭 WSL 后重启 manager，自动发行版选择在
  无"运行中"候选时落到默认标记的 **FedoraLinux44**（不可用镜像发行版：`Failed to start
  the systemd user session`、`command -v dsh` 命中 Windows interop 的 /mnt/c/nvm4w），
  systemctl start 起不来 → 超时 → 误报 "localhostForwarding 关闭"。
  修复：`LastWslDistro` 记忆上次成功（managed/attached）的发行版，选择优先级
  配置 > 上次成功 > 运行中 > 唯一 > 默认 > 打分；通知文案区分"服务在跑但 forwarding 关"
  与"服务未就绪（查发行版/dsh）"。

## v3.0 Runtime Bridge 矩阵（权威状态 + 优雅停止）— 2026-08-21 实测

> 注入方式：`dsh plugin --profile web add dsh-web-manager`（或 `file:`/`github:` 本地/仓库安装）。
> 该包经 `package.json` 的 `dsh.bundle.patch` 自动追加进 `dsh.profile.bundles`，桥插件自动参与组合
> （**WSL 与 Windows 两侧 profile 都装**）。旧的 `plugins/dsh-runtime-bridge` 手动拷贝注入已废弃。
> 管理器启动 dsh 时注入 `DSH_BRIDGE_PORT=<port+100>` / `DSH_BRIDGE_TOKEN` / `DSH_PROFILE` / `DSH_WEB_PORT`。

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| CC | WSL bridge 监听 | WSL 后端 3080 → 查询 3180 | ✅ `getStatus` 返回 `{"v":1,"ok":true,"status":{"running":true,"pid":...,"profile":"web","webPort":3080,"host":"127.0.0.1"}}` |
| DD | WSL bridge 权威版本 | 查询 3180 `getRuntimeInfo` | ✅ `info.dshVersion`=0.1.0-rc.7、`node`、`platform:linux` 与 WSL 实际一致 |
| EE | bridge 优雅停止 | 查询 3180 `shutdown` | ✅ 返回 `{ok:true,shuttingDown:true}` → dsh SIGTERM 退出（wrapper 模式 kill 前先优雅） |
| FF | Windows bridge（P1-3） | Windows 后端 3081 → 查询 3181 | ✅ `getRuntimeInfo` 返回 `dshVersion:0.1.0-rc.7`、`node:v24.16.0`、`platform:win32`、`pid:33992` |
| GG | 托盘版本显示（P1-2） | Tick 每 10s RefreshRuntime | ✅ 状态文本附加 `· dsh 0.1.0-rc.7 · node v24.16.0 · 运行 12m`；检查更新优先用 bridge 版本（不再 spawn wsl.exe） |

## v3.0 多实例矩阵 — 2026-08-21 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| HH | 双实例同开 | `Instances=[windows:3081, wsl:3080(systemd)]` | ✅ Windows 3081=managed（bridge 3181）；WSL 3080 同时在线；各自独立生命周期 |
| II | 每实例窗口管理 | 两个实例窗口分别缩放/移动 | ✅ 每个 `InstanceConfig.Window` 独立记忆尺寸/位置，Tick 逐实例捕获 |
| JJ | 每实例图标 | 两个窗口 | ✅ `HandleInstanceWindow` 逐实例 `ApplyIconToWindow`，互不干扰 |
| KK | 实例菜单 | 托盘「实例」子菜单 | ✅ 每个实例有打开/重启/状态子项（P2-2 后列出所有实例） |
| LL | 添加实例（P2-2） | 托盘「实例 → 添加实例」 | 代码交付：对话框（Id/后端/Profile/端口/WSL 发行版下拉/WSL 模式）→ `AddInstance` 即时启动 |
| MM | 删除实例（P2-2） | 托盘「实例 → 删除实例」 | 代码交付：下拉选择 → `RemoveInstance` 停止并移除；至少保留一个 |

## v3.0 更新机制矩阵 — 2026-08-21 交付

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| NN | 版本检查 | 托盘「更新 → 检查更新」 | ✅ 24h 节流 + npmmirror `@deepseek-ai/dsh/latest` + 比对 bridge 当前版本，仅新版本弹通知 |
| OO | 一键更新 | 托盘「更新 → 更新 dsh」 | ✅ `npm i -g @deepseek-ai/dsh@latest --registry=https://registry.npmmirror.com`，完成后提示新版本号 |

## P2 修复矩阵 — 2026-08-21

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| PP | FindAppWindow 卡死（P2-1） | exit 时 `CloseWindow` 曾卡在 WMI（"调用已取消"） | ✅ msedge 进程快照缓存（TTL 2s）+ `EnumerationOptions.Timeout=3s` + 失败降级上次快照；exit 不再挂死 |
| QQ | 添加实例对话框（P2-2） | 打开「添加实例」 | ✅ 修复确认按钮不可见（改为底部固定尺寸面板）+ WSL 发行版下拉（自动探测真实发行版） |

## P2 后续 UI/根因修复矩阵 — 2026-08-21/22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| RR | 顶部横向切换按钮 | 托盘顶部 Windows/WSL 并排按钮 | ✅ 横向排布（ToolStripControlHost）；选中蓝色高亮；切换后「WSL 服务模式」项按后端显隐 |
| SS | 菜单底部锚定 | 打开菜单 + 切换后端（WSL 模式项显隐致高度变化） | ✅ 底部固定在屏幕底/托盘图标，只有顶部伸缩 |
| TT | 状态两行 | 运行中 / 未运行分别查看 | ✅ 运行中=「运行中 (WSL, 3080)」+「dsh 0.1.0-rc.7 · 12m」；未运行=「未启动」+「未知版本」；高度稳定不跳动 |
| UU | 端口全局独占 | 添加实例默认端口 + 手动填已有端口 | ✅ 默认端口=全局最大已用+1(+2)；`AddInstance` 校验跨后端重复端口并拒绝 |
| VV | 实例「关闭窗口」 | 实例子菜单「关闭窗口」 | ✅ `EdgeWindow.CloseWindow` 只关该实例窗口（WM_CLOSE），不停服务 |
| WW | 每实例独立 profile | 多实例各开窗口 | ✅ `--user-data-dir` 按端口后缀（`dsh-web-manager-browser-3081`），窗口不合并 |
| XX | FindAppWindow 只匹配独立窗口 | 打开窗口（存在旧无后缀残留窗口时） | ✅ 额外要求 dataDir 带 `-端口"` 后缀，旧共享 profile 残留窗口不再被误匹配恢复 |
| YY | 默认启动后端 | 「默认启动后端」选 WSL → 重启 manager（open） | ✅ 只 Launch WSL 独立窗口；启动时自动关闭其他实例残留窗口（不再出现 Windows 独立窗口） |
| ZZ | **浏览器标签根因** | 启动 dsh 实例 | ✅ **dsh web 默认调用系统浏览器打开 URL**（日志 `opening the default browser; pass --no-open to disable`）→ 三处启动命令加 `--no-open` 后，`dsh-web.out.log` 该行 0 次，浏览器不再冒标签，只有独立 `--app` 窗口 |

## 勾选指示修复矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| AAA | 默认启动后端勾选不可见（根因） | 打开「默认启动后端」子菜单 | ❌ 两个选项无任何区分 → **根因**：.NET 4.8 `ShowCheckMargin` 默认 false + 子菜单 `ShowImageMargin=false` → `PaintCheck=false`，原生勾选永不绘制（WinForms 探针实证：`OnRenderItemCheck` 未被调用） |
| BBB | 勾选指示修复 | 打开「默认启动后端」子菜单 | ✅ `ShowCheckMargin=true`（仅含勾选项的子菜单）→ 当前默认项显示原生 16×16 勾选（探针实证 `OnRenderItemCheck` 调用 + 像素级确认）；无勾选项的子菜单（实例/更新）不开启，保持紧凑 |
| CCC | WSL 服务模式勾选 | 打开「WSL 服务模式」子菜单 | ✅ 当前模式（wrapper/systemd）同样显示勾选，与默认后端一致 |
| DDD | 布局回归检查 | 打开主菜单 + 两个带勾选子菜单 | ✅ 主菜单 256×423、状态项 46px 两行、实例/更新子菜单宽度不变（无空勾选列）；新 exe 反射验证：default/mode 子菜单 `ShowCheckMargin=True`、instances=False |
| EEE | 开机自启灰色标识 | 「开机自启」开 → 关 → 开 | ✅ 开启时该项背景灰色阴影（`#E6E6E6`），关闭恢复白底；主菜单无勾选列、宽度不变；悬停该项仍淡蓝高亮；以 config 为准同步（写注册表失败自动回退） |

## 管理器自更新测试矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| FFF | GitHub 无 Release | `checkmanagerupdate`（官方 API） | ✅ TLS 1.2 显式启用后连通；404 → 气泡「GitHub 上还没有发布版本」 |
| GGG | 本地测试源（fake release v3.0.2 + exe 资产） | `ManagerUpdateApi=http://127.0.0.1:8199/release.json` + `updatemanager` | ✅ 下载 → 校验文件版本 3.0.2.0 → 生成脱离脚本 → 退出（dsh 未停）→ 脚本等锁 → 替换 exe → 托盘自动重启 → 双后端重新附着；`manager-update.log` 记录 started/replaced/restarted |
| HHH | JavaScriptSerializer 数组类型（根因） | 解析 GitHub assets 数组 | ❌ `Deserialize<Dictionary<string,object>>` 把 JSON 数组反序列化为 **ArrayList** 而非 `object[]` → `DownloadUrl` 恒空、静默失败 → 修复：兼容两种形状（`ArrayList.ToArray()`）后通过 |
| III | 版本校验拒绝 | 下载版本 ≤ 当前 | ✅ 取消更新并气泡「下载的文件版本异常」 |

> 真实发布走 `gh release create vX.Y.Z dist/dsh-web-manager.exe`；自更新仅接受非 prerelease 的
> release，且要求资产名为 `dsh-web-manager.exe`。

## 插件包更新 + dsh-webui 测试矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| JJJ | `updateplugin` 控制动作 | `dsh-web-manager.exe updateplugin` | ✅ 自动探测 spec `file:/home/hgl/projects/dsh/dsh-web-manager` → `dsh plugin remove/add` → profile package.json 的 dependencies 与 bundles 完整保留，node_modules 刷新为新内容（含新增 scripts/dsh-webui） |
| KKK | `dsh-webui` 打开独立窗口 | WSL 内 `bash scripts/dsh-webui` | ✅ 转发 `open wsl` → 管理器 Launch Edge --app :3080 + AUMID 应用；未装管理器时给出安装提示 |
| LLL | 插件自动注册 dsh-webui | 运行插件 ensure-shortcut.sh | ✅ 幂等：复用共享管理器、快捷方式已正确则跳过、`install -m 755` 注册 `~/.local/bin/dsh-webui` |

## v3.1 修复矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| MMM | 窗口尺寸记忆（根因） | 多实例下调整 WSL 窗口大小后重开 | ❌ 旧版：`Launch` 读管理器级 `config.Window`（945x1020），`CaptureSize` 写实例级 `Window`（1665x1020）→ 尺寸永不生效 → 修复：`Launch/EnsureVisible` 改用实例级 WindowConfig；实测重开 `--window-size=1665x1020` ✅ |
| NNN | 退出托盘图标残留 | 菜单「退出」 | ✅ 根因：`Environment.Exit` 跳过 Dispose，图标不删 → `Exiting` 事件先 `NotifyIcon.Visible=false`（NIM_DELETE）再退出；覆盖菜单/控制管道/自更新三条退出路径 |
| OOO | 状态跟随顶部切换 | 点 Windows/WSL 按钮 | ✅ 切换即 `RefreshActiveStatus` 刷新状态项（旧版等下一次 StatusChanged 才更新）；状态只取 ActiveController，无匹配显示「未选择后端」 |
| PPP | 插件包更新明细 | `updateplugin` | ✅ 气泡/日志含具体包名@版本与来源：`updated dsh-web-manager@1.0.0 in web from file:/home/hgl/projects/dsh/dsh-web-manager` |

## 窗口尺寸记忆深度修复矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| QQQ | 参数正确但窗口仍错（根因） | 实例级修复后启动参数带 1665x1020 | ❌ 实际窗口仍 945x1020：**Edge 用 Preferences 保存的 `browser.window_placement` 覆盖 `--window-size`**（全新进程也一样，非转发问题）；且 2s 心跳 `CaptureSize` 先把记忆值覆盖成错误实际值（945），3.5s 后的校正拿到被覆盖值 → 空转 |
| RRR | 快照 + 保持期 + 轮询校正 | 修复后开 WSL 窗口 | ✅ 启动快照记忆值；CaptureSize 保持 6s 不覆盖；轮询到窗口即 `EnforceGeometry`（`SetWindowPos`）：实测 `applied 1665x1020 @130,7 (actual was 945x1020 @130,7)`，窗口 1665x1020；关窗重开再次校正成功；配置保持 1665 不被覆盖 |
| SSS | Chromium 持久化验证 | 校正后关窗再开 | ✅ 每次打开仍需校正（Chromium 只持久化用户主动调整，外部 SetWindowPos 不写入其 Preferences）→ 校正作为确定性兜底每次启动执行 |

## 窗口尺寸记忆根治测试矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| TTT | Edge 对尺寸参数的真实行为 | fresh profile + `--window-size=1500x800 --window-position=100,100` | ❌ 位置 100,100 生效，**尺寸仍 945×1020**（Edge 150 完全忽略 size 参数，app 窗口恒用默认尺寸） |
| UUU | Preferences 写入方案 | 启动前改写 `browser.window_placement` + `app_window_placement` | ❌ 窗口仍开 945；Edge 启动时未采用（该键不是 --app 窗口的尺寸来源） |
| VVV | SetWindowPlacement 直接调用 | 对 Chromium 窗口调用 | ❌ 全新结构体调用返回 error 87；**先 GetWindowPlacement 预填再 Set** 返回 true 但**尺寸改动被 Chromium 忽略**（只取消了最小化）；真正生效的是 SetWindowPos |
| WWW | 根治方案 | `--start-minimized` 启动 + 出现后「GetWindowPlacement 预填 → SW_HIDE → SetWindowPos 记忆尺寸 → SW_SHOW」 | ✅ 窗口全程不显示错误尺寸，直接以记忆的 1665×1020 出现；关窗重开循环稳定；配置不再被覆盖 |

## 弹出速度优化矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| XXX | 窗口弹出慢（根因） | 打开后日志时间差 | ❌ Launch → RestoreGeometry 2.55s：校正轮询用 `FindAppWindow`（**2s WMI 快照缓存** + 500ms 间隔），新进程要等缓存刷新才被发现 |
| YYY | 快速检测 | 改为直接轮询**启动进程**的 `MainWindowHandle`（150ms，无 WMI/无缓存）+ 每 4 次 WMI 兜底（防 re-exec） | ✅ Launch → RestoreGeometry **1.0~1.1s**（Edge 冷启动本身 ~0.8s，检测仅增 ~0.2s）；窗口弹出紧随任务栏按钮；尺寸稳定 1665×1020 |

## 弹出速度/观感优化（去最小化）矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| ZZZ | 最小化方案观感 | 打开 WSL 窗口 | ❌ 任务栏按钮 ~0.8s 出现但窗口 ~1.0s 才还原显示，用户感知"弹出慢" |
| AAAA | 去最小化直接调整 | 正常启动（无 `--start-minimized`）+ 150ms 轮询启动进程 MainWindowHandle + 窗口出现即 `SetWindowPos` | ✅ 窗口一出现即可见（~0.8s），~0.2s 内调整到记忆尺寸 1665×1020；Launch→调整 ~1.0s；关窗重开稳定；无闪烁无隐藏；配置不覆盖 |

## 弹出速度/观感优化（去最小化）矩阵 — 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| ZZZ | 最小化方案观感 | 打开 WSL 窗口 | ❌ 任务栏按钮 ~0.8s 出现但窗口 ~1.0s 才还原显示，用户感知"弹出慢" |
| AAAA | 去最小化直接调整 | 正常启动（无 `--start-minimized`）+ 150ms 轮询启动进程 MainWindowHandle + 窗口出现即 `SetWindowPos` | ✅ 窗口一出现即可见（~0.8s），~0.2s 内调整到记忆尺寸 1665×1020；Launch→调整 ~1.0s；关窗重开稳定；无闪烁无隐藏；配置不覆盖 |

## 弹出速度根治矩阵（关窗后 WSL 不关闭 + 移除 URL 门控）— 2026-08-22 实测

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| BBBB | 关窗后 WSL 后端/虚拟机是否关闭 | 关 WSL 窗口后检查 | ✅ **不关闭**：CloseStopsService=false + 附着实例不受影响；VM 持续运行（uptime 1.6h+）、dsh 进程存活；"慢"另有原因 |
| CCCC | 预热方案 | 关窗后 `--no-startup-window` 驻留 Edge | ❌ 结果不稳定（0.75s~1.9s），转发窗口 FindAppWindow 匹配困难 → **放弃** |
| DDDD | URL 门控（根因） | WSL localhost 转发抖动时打开 | ❌ `WindowUrl` 返回空 → **5 秒重试**（日志 `URL not ready, scheduling retries`）→ "大部分时候慢" → **移除门控**：窗口立即打开，页面等转发恢复后自动加载；10s 后台可达性检查替代诊断气泡 |
| EEEE | 移除门控后 | 打开/关窗重开 | ✅ 每次 ~1.0s（Edge 冷启动固有），尺寸 1665×1020 稳定，可达性检查不误报；FindAppWindow 放宽为按专属 profile 目录匹配（不再要求 --app=） |
