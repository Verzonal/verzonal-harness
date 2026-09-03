# DeepSeek Harness for Windows

[English](README.md) | 中文

harness 运行时的 C# 原生移植，配 WinUI 3 桌面前端。`packages/` 下的 TypeScript 源码树是参考实现，不受此处任何改动影响；`pnpm-workspace.yaml` 覆盖不到本目录，因此任何 TypeScript 门禁、打包器或 workspace 命令都看不到它。

设计理由、它胜过的备选方案，以及有意放弃的东西，见[本次移植的 Agent Note](../.agents/notes/implemented/architecture/2026-09-03-winui3-windows-desktop-port.zh.md)。

## 构建与运行

需要 .NET 8 SDK。在任意平台：

```sh
dotnet build Dsh.Portable.slnf     # the runtime, the capabilities, and the view-models
dotnet test Dsh.Portable.slnf      # every unit test
dotnet run --project src/Dsh.Cli -- --workspace . "list the markdown files"
```

`Dsh.Portable.slnf` 是除 WinUI 外壳外的每个工程。它之所以存在，是因为 Windows App SDK 只能在 Windows 上构建；没有这个过滤器，在 Linux 或 macOS 上构建 `Dsh.sln` 会因为一个与运行时毫无关系的工程而失败。

在 Windows 上另外：

```sh
dotnet build src/Dsh.App/Dsh.App.csproj -p:Platform=x64
dotnet run --project src/Dsh.App -p:Platform=x64
```

应用为未打包形式——一个普通 `.exe`，没有 MSIX，没有应用商店标识。`-p:Platform=x64` 是必需而非可选：WinUI 3 工程声明具体架构，且不含 `AnyCPU`。

`Dsh.Cli --fake` 用脚本化模型代替真实模型，这是在没有密钥时演练已组装 harness 的方式。`--dump-composition` 打印已挂载的插件行与已注册的工具。

### 凭据

`DEEPSEEK_API_KEY`，以及可选的 `DEEPSEEK_BASE_URL`。解析顺序为：进程环境变量，然后是 harness home 下的 `.credentials.yaml`，然后是工作区的 `.env`，最后是用户的 `.env`。桌面应用的设置页写入 harness home 中的那个文件。任何凭据都不会进入会话日志。

## 布局

| 工程 | 是什么 |
|---|---|
| `Dsh.Cordis` | 插件框架：上下文、服务、`inject` 门控、fiber、可逆副作用、四种派发模式 |
| `Dsh.Util` | home 路径、原子写入、行级 diff、ANSI |
| `Dsh.Llm` | 模型词汇表：消息、内容块、流式分片、适配器接缝 |
| `Dsh.Llm.DeepSeek` | DeepSeek provider：HTTP、严格遵循规范的 SSE、翻译、序列化 |
| `Dsh.Llm.Fake` | 脚本化 provider，用于测试以及无密钥运行 |
| `Dsh.Session` | 只追加事件日志、surface、`DeriveMessages`、存储 |
| `Dsh.Session.Persistence` | harness home 下的 JSONL |
| `Dsh.SystemPrompt` | 提示词分节与组装 |
| `Dsh.Tools` | 注册表、JSON schema 校验、带守卫的流水线、渲染意图 |
| `Dsh.Agent` | agent 接口、收件箱、`agent/*` 事件、注册表 |
| `Dsh.AgentLoop` | turn/step 驱动与工具调用调度器 |
| `Dsh.Fs`、`Dsh.Shell` | 能力接缝及其本地 provider |
| `Dsh.Tools.Fs`、`.Shell`、`.Todo` | 面向模型的工具 |
| `Dsh.Interaction` | 审批、权限预设、沙箱策略 |
| `Dsh.Settings`、`Dsh.Credentials` | `settings.yaml` 与凭据解析 |
| `Dsh.Bundle.Base` | 组装——即 TypeScript 树中 `cordis.yml` 的对应物 |
| `Dsh.App.Core` | 全部视图模型、对话投影、Markdown 解析器 |
| `Dsh.App` | WinUI 3 外壳：仅 XAML 视图 |
| `Dsh.Cli` | 同一套组装之上的控制台前端 |

## 为什么在 `Dsh.App` 处切分

除 `Dsh.App` 外，一切都以 `net8.0` 为目标。这不是打包上的偶然——正是它让移植可测试：所有行为，包括桌面应用自身的行为，都能在任意机器上通过 `dotnet test` 触达，只有视图标记是 Windows 专属的。

因此 `Dsh.App` 的规则是：它不承载任何决定。一次按键意味着什么、一份日志投影成哪些行、输入框何时启用、审批如何作答——统统在 `Dsh.App.Core` 中并受测试覆盖。其中两个界面用 C# presenter 而非标记语言实现，因为二者绘制的都是各分支需要不同元素的联合类型：Markdown 块，以及由工具自身声明的渲染意图选出的工具结果卡片。

## 应用展示什么

对话完全投影自会话日志，别无来源。实时流式输出与重放已存会话走同一条路径，这正是重新打开会话能精确而非近似地重现画面的原因。

审批是对输入框的接管而非对话框：它出现在人已经在看、且本来就要打字的位置。它在每一处边缘都失败即关闭——没有应答者、窗口已关闭、问题被撤回，都判为拒绝。`allowed-once` 是唯一的授予形式；不会有任何东西被记住为「已许可」。

设置页列出实时的插件行，这是「导出组装」的桌面等价物，也是让「一切皆插件」可见而非仅被断言的那个东西。

## 尚未移植

harness 方面：子 agent、workflow、压缩、作业类工具、skill 与 web search。权限与沙箱层已具备其预设与失败即关闭的审批，但未实现 Windows ACL 约束——策略拒绝工作区之外的写入，而不是约束进程本身，因此一个绕过策略的工具不会被操作系统拦住。

应用方面：斜杠命令面板、`@` 引用选择器、向用户提问、计划评审与目标栏。已实现的接管只有审批一种。工具卡片、清单与队列停靠区、权限标签与上下文占用表则都已具备。

磁盘*布局*与 Node harness 一致。与 TypeScript 写入方的逐字节事件一致性未经验证，且枚举成员按 camelCase 序列化，而参考实现用 snake_case。
