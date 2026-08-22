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

托盘右键菜单：打开窗口 / 重启服务 / 后端（Windows 本机 ⇄ WSL）/ 退出（停服务）/ 开机自启 / 状态。

## WSL 后端（v2.1）

菜单「后端 → WSL」或 `dsh-web-manager.exe "backend wsl"` 即可把 dsh web 托管进 WSL：

- **wsl-start.sh**：WSL 侧自愈启动器（物化到 `~/.dsh-webui/`），自带崩溃循环 + pidfile + TERM 陷阱
- **发行版自动探测**：`wsl --list` 过滤辅助发行版（Docker/Rancher/Podman），
  优先级：配置 `wslDistro` > 唯一候选 > 运行中 > 默认 > 名称打分
- **所有权模型**：管理器拉起的 = managed（完整生命周期/守护/退避重启）；
  外部已在跑的 WSL dsh = attached（只监控，不抢不杀）
- **端口策略**：每后端独立端口记忆（`Port` / `WslPort`），非 dsh 占用自动顺延并写回
- **健康探测**：Windows 端口探测 + WSL 侧 `ss` 解析双通道 —— 即使 localhostForwarding
  关闭，守护/状态也不误判（dsh 出于安全拒绝 `--host 0.0.0.0`，故服务只绑 127.0.0.1，
  forwarding 关闭时 Windows 无法访问，打开窗口会给出明确提示）
- **systemd 托管（v3.0）**：托盘「后端 → WSL 服务模式 → systemd」或 `wslmode systemd`。
  管理器生成 `~/.config/systemd/user/dsh-web-<port>.service`（Restart=on-failure 自愈、
  journald 日志、随登录拉起），前台运行 dsh；`systemd` 不可用（未开
  `/etc/wsl.conf [boot] systemd=true`）时自动回退 wrapper 模式。
- **双向互装**：`wsl-bootstrap.sh`（WSL→Windows）检测 manager 未运行则静默拉起，
  未安装则经共享目录 `~/.dsh-webui/wsl-bootstrap/Install.ps1` 静默安装；
  安装器会把 WSL 伴侣脚本物化进默认发行版（bootstrap.lock 先到先得防竞态）

测试沙箱：设置环境变量 `DSH_WEB_MANAGER_HOME=<目录>` 可把 config/日志/mutex/管道整体隔离，
用于并行验证而不影响真实安装。

## 多实例（v3.0）

`config.json` 的 `Instances` 数组可同时托管多个独立实例（Windows 与 WSL 混用、不同端口）。
数组留空则回退到传统单实例字段（`Port` / `WslPort` / `BackendType` 等）。

```json
{
  "Instances": [
    { "Id": "windows", "BackendType": "windows", "Port": 3081, "Profile": "web", "Enabled": true },
    { "Id": "wsl", "BackendType": "wsl", "WslPort": 3080, "WslDistro": "FedoraLinux",
      "WslServiceMode": "systemd", "Profile": "web", "Enabled": true }
  ]
}
```

每个实例独立管理：端口、窗口尺寸/位置、图标、崩溃守护、Runtime Bridge 状态。
托盘「实例」菜单列出全部实例（打开窗口 / 重启服务 / 状态），并支持**添加实例**与**删除实例**
（添加对话框可下拉选择 WSL 发行版，自动探测真实发行版，无需手填）。

## Runtime Bridge（v3.0 → 可安装的 dsh 插件包）

本仓库本身就是一个可安装的 dsh 插件包 **`dsh-web-manager`**：它把运行时的桥插件
（`lib/index.js` + `cordis.patch.yml`）和 Windows 托盘 exe、WSL 伴生脚本打包在一起。
管理器借桥拿到 dsh 的**权威状态**（版本、node、运行时长、pid、端口）并做**优雅停止**
（先 SIGTERM 再 kill），而不是只看端口猜测。

协议（line-delimited JSON，监听 `127.0.0.1:<webPort+100>`）：
`ping` / `getStatus` / `getRuntimeInfo` / `shutdown`，请求形如
`{"v":1,"method":"getRuntimeInfo","token":"<BridgeToken>"}`。

**安装**（WSL 与 Windows 两侧 profile 各自执行一次）：

```bash
# 从本地仓库安装
dsh plugin --profile web add file:/path/to/dsh-web-manager
# 或从 GitHub 安装
dsh plugin --profile web add github:FYHC1/dsh-web-manager
```

该命令会：

1. 用 pnpm 把 `dsh-web-manager` 装入 profile 的 `node_modules/`，并因
   `package.json` 的 `dsh.bundle.patch` **自动追加到 `dsh.profile.bundles`**，
   桥插件随之自动参与组合（不再需要手动拷贝目录 + 改 `cordis.patch.yml`）。
2. 托盘 exe 为**显式一步**（避开 pnpm 对构建脚本的默认拦截）：Windows 侧在 profile
   目录跑一次
   `node node_modules\dsh-web-manager\scripts\install-tray.mjs`（等价于
   `powershell -ExecutionPolicy Bypass -File <profile>\node_modules\dsh-web-manager\dist\Install.ps1`：
   安装托盘 exe 到 `%LOCALAPPDATA%\dsh-web-manager\app`、初始化配置、拉起托盘）。

**从旧的手动注入迁移**：若 profile 之前手动拷过 `node_modules/dsh-runtime-bridge`
并在 `cordis.patch.yml` 里手动 `insert` 了 `dsh-runtime-bridge`，安装后请删除那段
insert 与手拷目录，避免出现两套桥（双桥会抢同一个 `DSH_BRIDGE_PORT`）。

管理器启动 dsh 时会自动注入 `DSH_BRIDGE_PORT`（=port+100）、`DSH_BRIDGE_TOKEN`
（config 的 `BridgeToken`，首次自动生成）、`DSH_PROFILE`、`DSH_WEB_PORT`。
托盘状态随之显示 `运行中 (…) · dsh <版本> · node <版本> · 运行 <时长>`。

## 桌面快捷方式与托盘共享（自动）

插件 `apply()` 时（dsh web 每次启动）会**幂等**地做两件事：

1. **确保共享托盘**：若 `%LOCALAPPDATA%\dsh-web-manager\app\dsh-web-manager.exe`
   已存在则直接复用（共用同一个托盘，不再装第二份）；否则把随包的
   `dist/dsh-web-manager.exe` + 图标拷贝过去。用户配置/状态在
   `%USERPROFILE%\.dsh-webui\`，不会被覆盖。
2. **创建/修正桌面快捷方式**（按安装平台区分）：
   - Windows 端 dsh 安装 → `DeepSeek Harness WebUI (win).lnk` → 管理器 `open windows`
   - WSL 端 dsh 安装 → Windows 桌面 `DeepSeek Harness WebUI (wsl).lnk` → 管理器 `open wsl`

   快捷方式目标都是同一个共享托盘 exe；若发现旧版遗留的 `wscript.exe`/`.vbs`
   快捷方式（dsh-webui-installer 时代产物）会自动替换为指向托盘的新快捷方式。

管理器新增控制动作 `open windows` / `open wsl`（`dsh-web-manager.exe open windows`
或 `open wsl`）：打开指定后端的窗口，与快捷方式一致。双击快捷方式时若托盘未运行会
先冷启动托盘（并附着/启动各实例），再打开对应后端窗口；若托盘已在运行则直接转发。

## 更新机制（v3.1）

托盘「更新」菜单同时管理 **dsh** 与 **dsh web manager** 两个软件，互不影响：

- **检查 dsh 更新**：24 小时节流，经 npmmirror 查询 `@deepseek-ai/dsh` 最新版，与运行中 dsh 版本
  （优先取 Runtime Bridge 的 `dshVersion`）比对，有新版才弹通知。
- **更新 dsh**：一键 `npm install -g @deepseek-ai/dsh@latest`（走 npmmirror），完成后提示新版本号。
- **检查管理器更新**：查询 GitHub Releases（`FYHC1/dsh-web-manager/releases/latest`），与当前
  管理器版本比对，弹通知告知结果（无发布 / 已最新 / 发现新版）。
- **更新 dsh web manager**（自更新）：下载最新 release 中的 `dsh-web-manager.exe` →
  校验文件版本 → 生成脱离式更新脚本 → 退出托盘（**不停止任何 dsh 服务**）→ 脚本等 exe
  解锁后替换并自动以托盘模式重启 → 管理器重新附着各实例。全程 dsh 保持运行。
  - 更新包下载到 `%LOCALAPPDATA%\dsh-web-manager\update\`，更新过程记录在
    `%LOCALAPPDATA%\dsh-web-manager\logs\manager-update.log`。
  - 启动时也会做一次 24h 节流的管理器版本检查，发现新版才弹通知。
- **更新 dsh 插件包**：一键刷新 dsh profile 里的 `dsh-web-manager` 插件（bridge + 快捷方式
  脚本）。自动读取 profile `package.json` 中记录的安装来源（如 `file:/home/.../dsh-web-manager`），
  执行 `dsh plugin --profile <p> remove` + `add <spec>`，重启 dsh 后生效。来源可通过配置
  `PluginUpdateSpec` 覆盖。

### 发布新版本（给维护者）

自更新依赖 GitHub Release 附带 `dsh-web-manager.exe` 资产（tag 形如 `v3.0.1`，跳过 prerelease）：

```bash
# 在仓库根目录（WSL 或 Windows 均可，需要 gh 已登录）
gh release create v3.0.1 dist/dsh-web-manager.exe --title "dsh web manager v3.0.1" --notes "更新说明"
```

发布后，用户点托盘「更新 dsh web manager」即可自动升级。

## 状态与配置

`config.json` 关键字段（`%USERPROFILE%\.dsh-webui\config.json`）：

| 字段 | 说明 | 默认 |
| --- | --- | --- |
| `Port` | Windows 后端首选端口 | `3080` |
| `AutoFallback` | 非 dsh 占用时自动顺延空闲端口 | `true` |
| `DataDir` | Edge 独立浏览器数据目录（留空用默认） | `""` |
| `CloseStopsService` | 关闭窗口时同时停止服务（旧行为开关） | `false` |
| `ExitKeepService` | 退出托盘时保留服务 | `false` |
| `AutoStart` | 开机自启（托盘不弹窗） | `false` |
| `BackendType` | `windows` / `wsl` | `windows` |
| `WslPort` | WSL 后端首选端口 | `3080` |
| `WslDistro` | 指定 WSL 发行版（留空自动） | `""` |
| `WslServiceMode` | WSL 服务模式：`wrapper`（自愈脚本）/ `systemd`（unit） | `wrapper` |
| `Profile` | dsh profile 名 | `web` |
| `Window.Size` / `Window.Position` | 记忆的窗口尺寸与位置 | 空（Edge 默认） |
| `Instances` | v3.0 多实例列表（Id/Profile/BackendType/Port/WslPort/WslDistro/WslServiceMode/Enabled）；空 = 单实例回退 | `null` |
| `BridgeToken` | Runtime Bridge 共享密钥（首次自动生成） | `""` |
| `LastWslDistro` | 记忆上次成功使用的 WSL 发行版 | `""` |
| `LastVersionCheckUtc` / `LastKnownLatest` | dsh 更新检查节流时间戳 / 已知最新版本 | `""` |
| `LastManagerCheckUtc` / `LastKnownManagerLatest` | 管理器更新检查节流时间戳 / 已知最新版本 | `""` |
| `ManagerUpdateApi` | 管理器 Release API 覆盖地址（留空用官方 GitHub；测试/镜像用） | `""` |
| `PluginUpdateSpec` | 插件包安装来源覆盖（留空自动从 profile 的 package.json 探测） | `""` |

## WSL 命令：dsh-webui

Linux 端注册了 `dsh-webui` 命令（插件在 WSL 启动时自动安装到 `~/.local/bin/`），用于从
WSL 里手动打开**独立窗口**——它把请求转发给共享的 Windows 托盘管理器，由管理器拉起
Edge `--app` 独立窗口（无需记忆端口/URL）：

```bash
dsh-webui            # 打开 WSL 后端独立窗口（默认）
dsh-webui wsl        # 同上
dsh-webui windows    # 打开 Windows 后端独立窗口
```

未安装插件时手动注册：`install -D -m 755 scripts/dsh-webui ~/.local/bin/dsh-webui`。

## 开发

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Build.ps1   # 系统 csc.exe 编译 + 测试程序
```

要求：任意 Windows 10/11（自带 .NET Framework 4.8 与 C# 编译器 `csc.exe`），无需安装 Visual Studio。

## 里程碑

- **v2.0**：Windows 后端全量——托盘、窗口、图标、尺寸、守护、配置、日志、迁移、A–I 验证
- **v2.1**：WSL 后端（wsl-start.sh 自愈托管 + distro 自动探测 + attached/managed 所有权 +
  双向互装 bootstrap + 每后端端口记忆）✅ 已交付，J–Q 真机矩阵通过
- **v2.2**：后端感知健康探测（forwarding 关闭时守护不误判）+ 窗口 URL 策略（不可达时提示
  而非打开打不开的窗口）+ Error/Starting 残留清理 + 可中断 sleep + 墙钟超时 ✅ 已交付，
  R–V 真机矩阵通过
- **v3.0**：systemd 托管（W–Z 矩阵）+ Runtime Bridge 插件（权威状态/优雅停止，ping/
  getStatus/getRuntimeInfo/shutdown 协议）+ 多实例（Instances 数组，Windows+WSL 同开）+
  更新机制（24h 节流版本检查 + 托盘一键更新）✅ 已交付
- **v3.0 P1–P2 增强**：Runtime Bridge 状态接入托盘（dsh/node 版本、运行时长显示；检查更新联动）+
  Windows 侧 Runtime Bridge + 多实例「添加/删除实例」托盘 UI + FindAppWindow WMI 卡死修复 ✅ 已交付
- **v3.0 P2 后续（UI 优化）**：托盘顶部横向 Windows/WSL 激活按钮（记忆当前后端）+
  状态项两行显示（运行中 / 未启动·未知版本）+ 端口跨后端全局独占 +
  实例菜单「关闭窗口」+ 每实例独立浏览器 profile（多实例窗口不再合并）+
  菜单底部锚定（切换后端只伸缩顶部）+ 面板美化（浅色主题）✅ 已交付
- **浏览器标签页根因修复**：dsh web 启动时默认调用系统浏览器打开 URL（日志提示
  `pass --no-open to disable`）→ 三处启动命令统一加 `--no-open`（Windows / wsl-start.sh /
  wsl-systemd-start.sh），浏览器不再冒 dsh 标签，只保留 manager 拉起的独立 `--app` 窗口 ✅ 已修复
- **默认启动后端**：托盘「默认启动后端」子菜单（Windows 本机 / WSL）决定 manager 以
  `open` 启动时拉起的后端窗口；启动时自动关闭其他实例的残留窗口 ✅ 已交付
- **默认启动后端勾选指示修复**：根因是 .NET Framework 4.8 的 `ShowCheckMargin` 默认关闭，
  且子菜单又设了 `ShowImageMargin=false` → 原生勾选符号永远不会绘制，「默认启动后端」与
  「WSL 服务模式」的当前选中项看起来与未选中完全一样。修复：仅对包含勾选项的子菜单开启
  `ShowCheckMargin`（原生勾选列），无勾选项的子菜单（实例/更新）保持紧凑；
  主菜单 256×423 布局、状态项两行高度均不变 ✅ 已修复
- **开机自启灰色标识**：主菜单不开勾选列（会加宽所有项）——「开机自启」开启时该项背景
  改为灰色阴影（`#E6E6E6`），关闭时恢复白底；以 config 为准同步，`ToggleAutoStart` 失败
  也能自愈；悬停仍显示淡蓝高亮 ✅ 已交付
- **悬停高亮卡顿修复**：点击顶部 Windows/WSL 切换按钮后，焦点被托管按钮夺走导致菜单
  不再跟踪悬停 → 切换后把焦点还给菜单（`RefocusMenu`），高亮恢复即时 ✅ 已修复
- **v3.1 管理器自更新**：GitHub Releases 查询/比对（跳过 prerelease）+ 下载校验 +
  脱离式更新脚本（等 exe 解锁 → 替换 → 托盘重启，不停止 dsh）+ 托盘「检查管理器更新 /
  更新 dsh web manager」+ 启动时 24h 节流检查 + `updatemanager` 控制动作 ✅ 已交付
- **v3.1 插件包更新 + dsh-webui**：托盘「更新 dsh 插件包」（自动探测 profile 安装来源，
  `dsh plugin remove/add` 一键刷新，提示具体包名@版本与来源）+ Linux 端 `dsh-webui`
  命令（转发给共享管理器打开独立窗口，插件自动注册到 `~/.local/bin`）✅ 已交付
- **v3.1 修复四连**：①退出时立即隐藏托盘图标（`Exiting` 事件 → `NIM_DELETE`，不再出现
  幽灵图标/需点两次退出）；②独立窗口尺寸记忆修复（根因：`Launch` 读管理器级 `Window`，
  而 `CaptureSize` 写入实例级 `Window`，多实例下尺寸永不生效 → 改用实例级窗口配置）；
  ③状态栏严格跟随顶部 Windows/WSL 切换按钮（切换即刷新，只显示所选端，无匹配显示
  「未选择后端」）；④插件包更新提示具体更新的包与来源 ✅ 已交付
- **v3.1 窗口尺寸记忆深度修复**：实例级配置修复后仍不生效的**真正根因**——Edge 150
  完全忽略 `--window-size`（fresh profile 实证：`--window-size=1500x800` 仍开 945×1020
  默认尺寸），始终按自己保存的边界打开 `--app` 窗口。最终方案：**正常启动（不最小化，
  窗口一出现即可见）→ 直接轮询启动进程 `MainWindowHandle`（150ms，无 WMI 缓存）→
  窗口出现后 ~0.2s 内 `SetWindowPos` 应用记忆尺寸**——窗口弹出快（Launch→调整 ~1.0s，
  其中 ~0.8s 是 Edge 冷启动本身）、无最小化延迟、无隐藏闪烁；若窗口意外最小化则走
  隐藏→调整→显示兜底。另保留：启动时快照记忆几何、CaptureSize 启动后 6 秒保持期
  （阻断覆盖循环）、启动前清除残留后台进程。实测：打开/关窗重开均以记忆的
  1665×1020 出现，配置稳定 ✅ 已交付

## 许可

MIT
## 任务栏图标说明（Windows）

窗口图标由管理器通过 `WM_SETICON` 持续设置（32/16px，官方 `DeepSeek Harness.ico`），
验证方式：`WM_GETICON` 像素采样与官方图标一致。

**注意**：若任务栏启用了「合并任务栏按钮」（TaskbarGlomLevel 0/1，Win10 生效），
多个同进程窗口（如 Edge 的多个 `--app` 窗口）会被合并成单个按钮并显示**进程图标
（Edge）**，即使每个窗口自身的图标都是 DeepSeek 鲸鱼。

要使 DSH 窗口在任务栏显示鲸鱼图标，请将任务栏设置为「从不合并」：
- 设置 → 个性化 → 任务栏 → 「合并任务栏按钮」→「从不合并」
- 或注册表：`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` 下
  `TaskbarGlomLevel = 2`，然后重启资源管理器（explorer）生效。

> 为什么不通过 AUMID 在代码层解决？Chromium 的 `--app` 窗口在 Windows 上
> 不接受外部进程写入窗口 AppUserModelID（`SHGetPropertyStoreForWindow` 的
> `SetValue` 对 Chromium 窗口抛 `0x80070002`，普通窗口正常）。AUMID 由页面
> manifest 决定，外部无法覆盖。
