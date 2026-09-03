# Agent Note: WinUI 3 Windows 桌面移植

Status: implemented

[English](2026-09-03-winui3-windows-desktop-port.md) | 中文

## Problem

本 harness 以 CLI 和由本地 Node 服务托管的浏览器 Web UI 形式发布。两者都不是 Windows 桌面应用；而把现有服务包进一个窗口里，交付的只是功能而非架构：套在 sidecar 外面的外壳继承不到任何让这套代码成其为自身的性质——一切皆插件、注册即可逆副作用、模型可见 ⟺ 已记录、能力接缝完整。转换必须让这些性质仍可表达，也就意味着要移植运行时，而不是嵌入它。

## Decision

`windows/` 存放 harness 运行时的 C# 原生移植，拥有自己的 .NET 解决方案，并配一个 WinUI 3 前端。TypeScript 源码树保持不变，仍是参考实现；`pnpm-workspace.yaml` 的通配符不覆盖 `windows/`，因此现有的任何门禁、打包器或 workspace 命令都看不到这棵新树。

工程布局按角色逐个工程镜像 `docs/architecture.md`——`Dsh.Cordis`、`Dsh.Llm`、`Dsh.Session`、`Dsh.Tools`、`Dsh.SystemPrompt`、`Dsh.Agent`、`Dsh.AgentLoop`、各能力工程、承载组装的 `Dsh.Bundle.Base`，以及其上的两个前端：`Dsh.Cli` 与 `Dsh.App`。

**除 `Dsh.App` 外，每个工程都以 `net8.0` 为目标。** 正是这一条让移植可验证：运行时、各能力以及全部应用视图模型（`Dsh.App.Core`）可在任意平台构建并运行测试，只有 XAML 视图带 `net8.0-windows10.0.19041.0` 标识。`Dsh.Portable.slnf` 选出可移植集合；`.github/workflows/windows-app.yml` 在 `ubuntu-latest` 上跑这些测试，并在 `windows-latest` 上编译完整解决方案。

一套组装之上两个前端，正是 TypeScript 树已有的形状（`dsh-web-app` 与 `dsh-headless` 之于 `dsh-base`）。在这里它还承担验证职责：在没有 Windows 机器的地方，`Dsh.Cli` 端到端驱动已组装的 harness。

### 视图层被允许成为什么

`Dsh.App` 只存放视图，别无其他。所有决定——一次按键意味着什么、一份日志投影成哪些行、卡片如何选取——都在 `Dsh.App.Core` 里，由单元测试覆盖。其中两个界面用 C# presenter 而非标记语言实现，因为二者绘制的都是各分支需要不同元素的联合类型：Markdown 块，以及由工具自身声明的渲染意图选出的工具结果卡片。应用从未见过的工具照样能正确绘制；抛异常的 presenter 只损失自己那张卡片，而不是整场对话。

对话完全投影自会话日志，别无来源。实时流式输出与重放已存会话走同一条 `Apply` 路径，这正是恢复会话能精确重现画面的原因；有测试同时驱动两者并比对行。

### 有意的偏离

`net8.0` 上没有 `System.Threading.Lock` 与 Zstandard，因此移植改用 `object` 监视器，写出纯 `.jsonl`，同时经由 `ZstdSharp` 读取两种封帧。只有 `Dsh.App` 关闭了「警告即错误」：XAML 编译器生成的分部类不归本仓库所有，让构建在 SDK 升级时受制于它们的警告没有任何收益。

## Alternatives considered

**在运行中的 Node 服务之上套 WebView2 外壳。** 出窗口最快的路，也正是不满足诉求的那条：结果只是把现有 Web UI 装进框里，harness 仍以 JavaScript 跑在 sidecar 进程中。桌面应用本身不会表达任何架构不变量，所谓「转换」不过是一次打包方式的变化。

**Electron 或 Tauri。** 同样的反对意见，还要多带一个运行时。Tauri 至少能让宿主原生化，但宿主依然是围着同一个服务的浏览器外壳。

**用 .NET MAUI 取代 WinUI 3。** MAUI 买来的是诉求并未要求的跨平台覆盖，代价是在唯一被要求的平台上损失保真度：WinUI 3 是 Windows 原生呈现层，Mica、自定义标题栏与主题资源集在那里都是一等公民。

**在 Node 服务的 JSON-RPC 之上做一个轻量 C# 客户端。** 诱人之处在于 `packages/sdk` 已经定义了协议。它本可以很快产出一个原生窗口，但 harness 依然不存在于 C# 中，而每一处能力接缝都会坍缩成单一 RPC 边界——恰好是所要保留性质的反面。

**只移植视图模型，循环仍调用 TypeScript。** 上一条的折中版本，且缺陷落在最要紧的接缝上：agent 循环正是那些不变量所描述其行为的东西。

## Consequences

这次移植让运行时有了第二份实现。这是代价，且是实打实的：改动 turn/step 驱动或会话词汇表如今有两个落点，而没有任何机制会自动检查两者是否一致。

它换来的是：Windows 应用**就是** harness，而不是它的客户端。约束是 C# 一侧强于原实现的一处——`System.Security.AccessControl` 与 `System.Security.Principal` 直接触达 Win32 ACL 与令牌 API，而 Node 实现要经由辅助程序。

## Testing

`dotnet test Dsh.Portable.slnf` 覆盖 Cordis 生命周期与全部四种派发模式、会话日志的 `seq == index` 规则、surface 放置、替换遮蔽、崩溃修复与未知事件拒绝、工具流水线的单调拒绝与失败即关闭的审批、turn/step 驱动及其取消路径、DeepSeek SSE 读取器与序列化器，以及应用层的投影、输入框规则与审批接管。

`windows-latest` 任务编译 `Dsh.App`。本仓库没有任何环节会启动它：构建通过证明视图可编译可链接，而非窗口绘制正确。

## Deferred

磁盘*布局*与 Node harness 一致——home 目录、路径形状、头行、格式版本、设置与凭据解析顺序——且 C# 一侧能往返自己写的日志。与 TypeScript 写入方的逐字节事件一致性未经验证，且枚举成员按 camelCase 序列化，而参考实现用 snake_case。子 agent、workflow、压缩与作业类工具尚未移植。
