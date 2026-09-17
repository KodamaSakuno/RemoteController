# RemoteController — Agent 工作总纲

本文件是所有 AI Agent 在本仓库工作时的必读约定。规则变更需同步更新本文件。

## 1. 项目概述

- **RemoteController**：局域网远程控制工具——PC 控制端显示高通 ARM64 Windows 平板被控端的指定屏幕区域画面，并回传鼠标操作。架构见 `docs/adr/0001-architecture.md`。
- **两端进程**：`src/RemoteController.Server`（被控端：采集+注入，WebSocket 服务端）、`src/RemoteController.Client`（控制端：Avalonia UI，WebSocket 客户端）、`src/RemoteController.Protocol`（两端共享消息协议）。
- **技术栈**：.NET / C#，SDK-style 项目，统一由 `RemoteController.slnx` 管理。
- **目标框架**：`net10.0`。

## 2. 目录结构约定

```
RemoteController/
├── RemoteController.slnx
├── AGENTS.md                  # 本文件（根总纲）
├── src/                       # 所有可执行程序与库代码
│   └── <ProjectName>/         # 一项目一目录，PascalCase
├── tests/                     # 所有测试项目，命名 <被测项目>.Tests
│   └── <ProjectName>.Tests/
└── docs/                      # 架构说明、决策记录（ADR）
```

- 新代码默认进 `src/`；测试必须放 `tests/`。
- **测试栈**：xUnit v3 + Microsoft.Testing.Platform v2。新建测试项目一律用模板生成（`dotnet new xunit3`），不手写 `.csproj`；测试项目命名 `<被测项目>.Tests`。
- 某子目录有额外规则时，在该目录放自己的 `AGENTS.md`，局部规则优先于本文件。

## 3. 完成定义（验证命令）

每个任务结束前必须执行并通过：

```bash
dotnet build RemoteController.slnx   # 0 错误
dotnet test RemoteController.slnx    # 全部通过
```

- 代码风格遵循 .NET 运行时编码约定，可用 `dotnet format` 检查。
- **构建有错误或测试不绿（存在失败）时，任务未完成**，不得声称完成；如实报告阻塞原因。

## 4. 代码与注释约定

- 注释只写「为什么」，不写「是什么」。代码自身能表达的内容（做了什么、怎么做的）不注释。
- 仅以下四处允许写注释：
  1. **公开 API** — XML 文档注释说明契约与用途；
  2. **复杂算法** — 说明思路与不可直改的推导；
  3. **非显然边界条件** — 说明约束来源（如 Win32 API 的特定行为）；
  4. **临时兼容逻辑** — 必须注明触发条件与可移除时机。
- 代码、注释、提交信息中不得引用 `AGENTS.md` 等指令文档（如「按 AGENTS.md 要求」）——指令文档属于工作流约定，不是代码语境的一部分。

## 5. Agent 协作规则

### 任务流程
- 大任务先出实施方案（改哪些文件、如何验证），再动手实现。
- 动手前先探索现有代码，遵循既有命名与结构习惯，不引入个人偏好风格。

### 子代理分工
- 探索类工作（读代码、找文件、回答结构问题）→ 派 explore 子代理。
- 相互独立的模块实现或批量改动 → 可派多个 coder 子代理并行。
- 子代理交付后，回本体验证第 3 节的构建与测试命令。

### 变更纪律
- 小步提交：每完成一个逻辑单元（功能点、修复、文档）立即提交一次；提交前确保构建与测试通过，不积压大批量未提交改动。
- 不擅自添加 NuGet 依赖；确有需要时先向用户说明理由。
- 公共 API 或架构决策变更时，在 `docs/` 留下说明（推荐 ADR 格式）。
- 不提交密钥、连接串、用户私有配置；此类内容只进 `.env` 类被 `.gitignore` 覆盖的文件。
- 改动使本文件任何描述失效时，同步更新本文件。
