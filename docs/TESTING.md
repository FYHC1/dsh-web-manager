# 验证矩阵（v2.0 真机实测记录）

测试机：Windows 11 + WSL2 FedoraLinux44；dsh 命令 `C:\nvm4w\nodejs\dsh.cmd`
测试端口：3093 / 3095（绝不用 3080 做破坏性实验）；用户 3080 服务（PID 25080）全程不动。

| # | 场景 | 操作 | 结果 |
|---|------|------|------|
| A | 启动 | `dsh-web-manager.exe open` → 拉起 dsh web → 弹 Edge 窗口 | ✅ 服务监听；窗口图标 sha 与官方 ico 一致；窗口尺寸 945x1020 已保存 |
| B | 关窗常驻 | 关闭 app 窗口 | ✅ 服务存活（默认 closeStopsService=false） |
| C | 托盘唤起 | 再次 `exe open`（单实例转发） | ✅ 新窗口重现（hwnd 变化）；管理器进程唯一 |
| D | 单实例 | 多次启动 | ✅ 恒为 1 个 dsh-web-manager 进程（Mutex + 命名管道转发） |
| E | 崩溃守护 | 杀 managed node 进程 | ✅ `crash #1` 检测 → 自动重启（1/3 退避）→ 服务恢复 |
| F | 外部附着 | 先起外部 3095 dsh web，再启动 manager | ✅ attached 识别（不抢占）；关窗与退出管理器均不杀外部服务 |
| G | 退出停服 | `exe exit`（managed 模式） | ✅ 停服务、taskkill 树、端口释放 |
| H | 关窗停服开关 | `closeStopsService=true` + 关窗 | ✅ 服务随窗口关闭而停止（可配置回默认常驻） |
| I | 3080 红线 | 全程监听 3080 | ✅ 用户服务与窗口全程不受影响 |

## 关键发现与修复（开发过程记录）

- `dsh web` 是错误用法；正确语法为 `dsh --profile <profile> [--host 127.0.0.1] --port <port>`。
- Windows 侧访问 npmjs.org 不通：dsh 构建 profile 依赖时须走 npmmirror 镜像，或直接用既有 profile 目录。
- 用户 web profile 的 `dsh-better-sidebar` 插件曾缺依赖：已用
  `npm install --prefix ...\dsh-better-sidebar --registry=https://registry.npmmirror.com --ignore-scripts`
  补齐全部依赖（schemastery 等 207 包），并恢复 patch 中该插件为启用状态；
  dsh web 完整启动验证通过（HTTP 200）。
- 启动子进程用 `cmd /d /s /c ""dsh.cmd" args"` + 异步流捕获（重定向到文件在 UNC 工作目录下会失败）。
- 二次实例经命名管道转发动作；`exit` 用 `Environment.Exit` 保证托盘进程必然终止。