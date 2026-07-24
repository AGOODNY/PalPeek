# PalPeek

PalPeek 是一个面向小型 Tailscale 好友网络的 Steam 游戏观战工具。它会在玩家启动 Steam
游戏后自动识别游戏窗口，并允许同一 Tailnet 中的好友通过内置 Moonlight 观看。

PalPeek 只提供观看，不提供远程控制。

## 当前已经实现

- 自动读取 Steam 主库和附加库中的游戏
- 自动识别正在运行的 Steam 游戏及其主要窗口
- 每次检测到新的 Steam 游戏后自动开放分享
- 提供“停止分享”按钮；停止只针对当前游戏，下次启动游戏会重新自动分享
- 只捕获指定游戏窗口，不捕获整个桌面，也不会回退到桌面捕获
- 只采集游戏进程树的音频，不采集全系统声音，不影响玩家本地听到的声音
- 通过 Tailscale 发现在线好友及其正在分享的游戏
- 显示好友昵称、游戏、在线状态、画质和当前观看人数
- 点击好友后自动申请名额、完成 Moonlight 配对并启动播放器
- 播放期间自动续租，退出播放器后自动释放名额
- 每局最多三名观众；第四人会看到“观看人数已满”
- 自动管理 Sunshine Host 的启动、停止、状态检查和崩溃恢复
- 强制禁用远程键盘、鼠标、触摸和手柄输入

## 当前限制

- 仅支持 Windows 11 x64
- 仅自动识别通过 Steam 安装并启动的游戏
- 主机和观看端都需要安装 PalPeek 与 Tailscale，并加入同一个 Tailnet
- 当前提供 H.264：默认 720p60 / 4 Mbps，可配置为 1080p60 / 8 Mbps
- 这是早期测试版，安装程序尚未进行代码签名，Windows 可能显示 SmartScreen 提示
- 请只在本人拥有或获得明确授权的电脑和好友网络中使用

## 下载安装

### 推荐：安装程序

1. 打开项目的 [GitHub Releases](https://github.com/AGOODNY/PalPeek/releases)。
2. 下载最新的 `PalPeek-Setup-*-x64.exe`。
   同时下载同名的 `.sha256` 文件；传给其他人前可运行
   `Get-FileHash .\PalPeek-Setup-*-x64.exe -Algorithm SHA256`，确认结果与校验文件一致。
3. 双击安装程序并按提示完成安装。安装程序需要管理员权限来添加仅限 Tailscale
   地址范围的 Windows 防火墙规则。
4. 在两台电脑上安装并登录
   [Tailscale Windows 客户端](https://tailscale.com/download/windows)，确保两台电脑出现在同一个
   Tailnet 中。访问https://console.tailscale.com/admin/users 邀请好友加入你的网络
5. 启动 PalPeek。安装包已经包含 PalPeek 专用 Sunshine Host 和 Moonlight，无需另外安装它们。

如果 Releases 页面还没有安装包，可以由项目维护者在 `E:\PalPeek` 构建本地测试包：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Configuration Release
```

发布目录位于 `artifacts\publish`。必须复制整个目录，不能只复制 `PalPeek.exe`。

## 和朋友进行第一次测试

两台电脑分别称为“分享端”和“观看端”。

### 1. 两边准备

1. 两边都使用 Windows 11 x64。
2. 两边都安装 PalPeek。
3. 两边都安装并登录 Tailscale，确认处于同一个 Tailnet。
4. 两边都启动 PalPeek，并确认右上角显示“Tailscale 已连接”。

### 2. 分享端开始游戏

1. 正常通过 Steam 启动一个游戏。
2. 等待约 5–10 秒，让 PalPeek 识别并确认游戏窗口。
3. PalPeek 右上角会显示“正在分享 游戏名”。
4. 如果这次不想分享，点击“停止分享”。需要重新开放时点击“恢复分享”。

启动游戏即表示本次主动分享；PalPeek 不会分享未识别的普通应用窗口。

### 3. 观看端进入

1. 在 PalPeek 好友列表中等待分享端出现。
2. 确认卡片上的游戏名称和观看人数。
3. 点击“偷看一眼”。
4. 首次连接会自动完成 Moonlight 配对，随后打开播放器。
5. 关闭 Moonlight 窗口即可退出观战并释放名额。

### 4. 建议的第一轮验收

- 确认画面只有游戏窗口，切换到浏览器或聊天软件时不会显示桌面内容
- 确认只能听到游戏进程的声音
- 确认观看端键盘、鼠标和手柄不能控制分享端
- 确认分享端点击“停止分享”后观看会话结束
- 如果有四台观看设备，确认第四台显示“观看人数已满”

## 常见问题

### 看不到好友

- 确认双方 Tailscale 都在线并在同一个 Tailnet
- 确认双方 PalPeek 都在运行
- 确认分享端已经启动 Steam 游戏，并显示“正在分享”
- 如果使用便携目录而不是安装程序，需要手动配置 Windows 防火墙规则

### 检测到游戏但不能观看

- 等待游戏主窗口完全出现
- 不要最小化或关闭游戏窗口
- 查看 PalPeek 顶部提示，确认是窗口、音频还是编码器错误
- 尝试停止分享后再点“恢复分享”

### 能否远程操作游戏

不能。PalPeek 的 Sunshine 分支在代码和配置层都禁用了键盘、鼠标、触摸和手柄输入。

## 开发与构建

项目结构：

- `src/PalPeek.Core`：Steam/Tailscale 发现、名额租约和协议模型
- `src/PalPeek.App`：WPF 界面、托盘、好友 API 和观看流程
- `third_party/Sunshine`：PalPeek 专用 Sunshine 分支
- `packaging/sunshine`：锁定安全边界的 Host 配置
- `installer`：Windows 安装程序脚本
- `tests/PalPeek.Core.Tests`：单元测试

使用 .NET SDK 8.0.423：

```powershell
dotnet restore PalPeek.sln
dotnet build PalPeek.sln -c Release
dotnet test PalPeek.sln -c Release
```

Sunshine 基于 `v2026.516.143833`，Moonlight PC 固定为 `v6.1.0`。第三方许可和源码分发要求见
`THIRD_PARTY.md`。
