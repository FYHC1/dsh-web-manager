# 会话状态保留（dsh web manager 任务栏图标问题 —— 关键上下文）

> 本文件用途：会话压缩/恢复时读取，重建关键事实与未完成事项。
> 最后更新：2026-08-21 凌晨（Windows 侧修复进行中）

## 用户原始诉求（当前活跃问题）

任务栏上新拉起的 dsh web 窗口显示 Edge 图标而非 DeepSeek Harness 鲸鱼图标。
用户确认：**图标设置（WM_SETICON）应该是正确的**；用户之前在 Win10 开启了
「任务栏图标始终合并」导致看到 Edge 图标。

## 已确认的事实（全部技术验证，非猜测）

1. **系统 = Windows 10 Pro 22H2**（ProductName=Windows 10 Pro for Workstations,
   Build 19045）。`TaskbarGlomLevel` 注册表在此版本**有效**（不是 Win11 早期）。
2. 原 `TaskbarGlomLevel` = **1**（任务栏被占满时合并）。现已临时改为 **2（从不合并）**
   并重启 explorer（explorer 重启不影响用户窗口）。
3. **manager 拉起的窗口图标一直正确**：
   - 3081 窗口（manager 拉起）`WM_GETICON`(ICON_BIG) 像素 sha = `000000000000FFFFFFFFFFFF`
   - 与官方 `%USERPROFILE%\.dsh-webui\dsh-webui.ico` 32px 采样 sha **完全一致**
4. 3080 窗口（v1.4.2 旧脚本拉起，用户开发用）sha = `000000070707C6C6C6EFEFEF`
   = **Edge 图标特征**（该窗口由 v1.4.2 watchless launcher 拉起，当前无 watcher 在跑，
   无人设置图标 → 保留 Edge）。
5. **任务栏合并机制**（Windows 10）：
   - 合并（GlomLevel 0/1）时：同 EXE（msedge.exe）无 AUMID 窗口合并成一个按钮，
     按钮显示**进程图标（Edge）**，不显示窗口图标。
   - 从不合并（GlomLevel 2）：每个窗口独立按钮，显示**各自窗口图标**（WM_SETICON 生效）
   - **Chromium --app 窗口外部无法设置 AUMID**（SHGetPropertyStoreForWindow
     的 SetValue 被拒，PowerShell P/Invoke 抛 0x80070578；notepad 对照组成功证明是我们的
     P/Invoke 写法正确、是 Chromium 窗口拒绝；SetClassLongPtr 跨进程也无效）。
6. 用户观察：最初 manager 窗口（不带 user-data-dir）在任务栏是**独立按钮**；
   从「强制独立 user-data-dir」修复后反而合并。—— 说明用户环境里
   「不带 user-data-dir 的 --app 窗口」与 3080 窗口属于不同 Chromium 实例，
   分组不同；带 user-data-dir 后同属独立 msedge 进程，合并策略生效。详细机制未最终确定。

## 当前环境状态（Windows 侧）

- manager 在跑（PID 24476，从 `C:\D\CodeProjects\Github\dsh-web-manager\dist\dsh-web-manager.exe`
  启动）—— 这是**用户自己的克隆目录**，新编译 exe 已同步覆盖（sha 7d567d00…）
- 3081 服务监听（PID 20912）；3080 服务监听（PID 25080，users）
- 3080 窗口 hwnd=0x6294328；3081 窗口 hwnd=0x3672944
- 窗口标题：3080「…显示WebUI - DeepSeek Harness」；3081「深海女仆工坊 - DeepSeek Harness」
- TaskbarGlomLevel = 2（从不合并，已生效）
- 仓库 dist 已同步新 exe；源码含「强制独立 user-data-dir」+「AUMID 尝试」两处修改

## 识图结论（freebuff2api，mimo/mimo-v2.5 + minimax/minimax-m3 双模型）

从不合并生效后的任务栏截图（dwm-taskbar4.png / dwm-full4.png）：
- Edge 彩色 e 图标**单独显示、无合并叠层** ✅
- **DeepSeek 蓝底白鲸图标存在** ✅
- 图标独立显示（未合并）
- oc2api 提供商（192.168.2.14:18080，OC2API_API_KEY=sk-4vztNyXQ7VKGrFz4uVST）
  网关**不支持图像输入**（mimo-v2.5 返回"无法查看图片链接"，hy3/swe 明说 CANNOT_SEE_IMAGE，
  big-pickle 报 content 错误）→ 识图改用 **freebuff2api**（192.168.2.14:8877/v1，
  FREEBUFF2API_API_KEY=d59d8684…）成功。

## 用户最新指示（2026-08-21）

> 先试代码层解决（AUMID），不行的话就保留从不合并。在开始前，先写好 v2.1 和 v3.0 的
> 实现计划，并启动压缩会话，保留关键信息。

→ 已写 docs/PLANS-v2-v3.md（v2.1/v3.0 计划）。
→ 代码层解决方向：AUMID 外部写入已证明被拒，**下一步精确诊断**：
   a) 检查 manager 代码里 AUMID 应用的实际 HRESULT（SetValue 的真实返回值，之前只记了
      Commit 的 hr=0，且 PowerShell 对同一窗口抛 0x80070578 —— 需确认 C# 里 SetValue 是否也抛）
   b) 若确实失败 → 接受「从不合并」（GlomLevel=2 保留），并把该设置与说明写进 README；
   c) 可选：研究 Chromium `--app-id`（PWA 模式在页面上注册 AUMID）——用户担心无 Edge/
      仅 Chrome 环境不可用，暂不作为首选。

## 未完成事项

1. AUMID 精确诊断（C# 侧 SetValue HRESULT 是否成功；是否真是窗口只读）
2. 若 AUMID 失败 → 确认保留 TaskbarGlomLevel=2，写进 README（已知影响所有应用不合并）
3. v2.0 剩余：仓库 dist 同步确认；`docs/PLANS-v2-v3.md` 提交推送
4. v2.1：WslBackend + 互装协议（按计划）
5. 测试红线：3080 服务（PID 25080）/窗口/用户浏览器窗口（pid 11076, opencode 页面）
   全程不可动

## 仓库路径速查

- 新仓库 `/home/hgl/projects/dsh/dsh-web-manager`（GitHub FYHC1/dsh-web-manager，main，
  commit 5f14660 为最新「user-data-dir 修复 + AUMID 尝试」）
- 用户克隆 `C:\D\CodeProjects\Github\dsh-web-manager\`
- 编译临时目录 `C:\Users\hgl\AppData\Local\Temp\dwm-build\dist\`
- 共享配置 `C:\Users\hgl\.dsh-webui\config.json`（port=3080, DataDir=""）
- 日志 `C:\Users\hgl\AppData\Local\dsh-web-manager\logs\manager.log`
- 官方图标 `C:\Users\hgl\.dsh-webui\dsh-webui.ico`（sha a821…）

## 开发环境速查

- 编译：`powershell -File scripts\Build.ps1`（系统 csc.exe，目标 .NET 4.8）
- ps1 无 BOM 会乱码 → 每次编辑后加 BOM（`printf '\xef\xbb\xbf'` 前缀）；
  .cs 文件同样需要 BOM（含中文注释）
- 从 WSL 调 Windows：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File <win路径>`；
  避免 UNC 路径（`\\wsl.localhost\...` 不可用）→ 先复制到 C:\ 临时目录再执行
- WSL 发行版 = FedoraLinux44（`wsl.exe -l -q`）
- 用户 Windows dsh = `C:\nvm4w\nodejs\dsh.cmd`（nvm4w 链接到 winget 安装的
  C:\D\UnigetuiApps\winget\CoreyButler.NVMforWindows\v24.16.0）
- dsh 正确语法：`dsh --profile <name> --host 127.0.0.1 --port N`（不是 `dsh web --port`）
- web profile 插件 tree 曾因 better-sidebar 缺依赖（.pnpm store 空）启动失败；
  已用 `npm install --prefix ... --registry=https://registry.npmmirror.com --ignore-scripts`
  修复并重新启用（Windows 网络到 npmjs 不通，必须用 npmmirror）

## 测试资产

- 测试端口：3093/3095（禁止 3080）；适当时机清空
- 测试截图：`C:\Users\hgl\AppData\Local\Temp\dwm-*.png`（dwm-taskbar4.png = 当前任务栏）
- 识图工具链：python3 + freebuff2api OpenAI 兼容接口（base64 内联图片）