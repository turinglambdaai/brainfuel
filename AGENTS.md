# AGENTS.md

指引给 AI agent（及开发者）：如何理解、构建、运行、改动 BrainFuel。

> main 即 Rivet 实现。旧的 Avalonia/.NET 10 栈已删除（行为与文案参照可在
> git 历史 / v0.9.0 及更早的 tag 里找）。行为参照 v0.9.0，数据与其逐字节兼容。

## 这是什么

BrainFuel 是 GLM Coding Plan / Codex / Claude 配额悬浮卡。实现基于
Rivet（github.com/turinglambdaai/rivet）：一份 Racket 领域核心，通过 Rivet
类型化 RPC（RVT1 协议）驱动各平台第一方原生 UI 薄壳。

| 平台宿主 | 技术栈 | 目录 | 状态 |
|---|---|---|---|
| macOS | SwiftUI 宿主 + 生成客户端 | `macos-host/` | 可跑已实机验收（悬浮卡+托盘+通知+设置窗） |
| Linux | GTK4 宿主 + 生成客户端 | `linux/` | 可用（卡片/详情/设置全实现；托盘缺上游 #118，关窗即退出） |
| Windows | C++/WinRT 宿主 + 生成客户端 | `windows/` | 脚手架（待实现卡片/设置/托盘） |
| agent CLI | — | — | 不适用 |

## 快速命令

```bash
# 前置：Racket CS 9.x，rivet 以 link 方式安装
# cd ../rivet && raco pkg install --auto --no-docs --name rivet --link file://$PWD

raco rivet doctor --json   # 工具链自检
raco rivet build           # 生成各端客户端 + 编译后端 bundle + 宿主构建
raco rivet dev             # 开发循环
raco test racket/          # 领域核心测试（426 项）
```

## 契约（不要破坏）

- **数据路径与格式与 v0.9.0 完全一致**（drop-in 迁移）：settings.json / usage-history-{accountId}.json / 凭据（Windows DPAPI·macOS 钥匙串·Linux secret-tool），目录 macOS ~/Library/Application Support/BrainFuel · Linux ~/.config/BrainFuel · Windows %APPDATA%/BrainFuel
- **i18n 单源**：`shared/i18n/{zh,en}.json`，平台副本必须逐字节一致；zh 为默认
- **RPC 面**：`app/backend.rkt` 的 define-rpc 是宿主唯一数据通道（initialize / refresh-now / switch-account / get-details / save-account / remove-account / get-settings / save-settings / get-diagnostics）；改签名 = 各端宿主 + 生成客户端同步改
- **宿主只做渲染与交互**：业务一律走 RPC；定时器/调度/HTTP/存储都在 Racket 后端
- **Rivet 改动走上游**：缺能力先提 issue/PR 到 turinglambdaai/rivet

## 项目结构

```
├── rivet.rktd          Rivet 应用清单
├── rivet-schema.json   RPC schema 基线（codegen 输出入库）
├── app/backend.rkt     Rivet 后端入口（装配领域层）
├── racket/             Racket 领域核心 + tests/（426 项，三平台同套件）
├── shared/i18n/        zh.json / en.json 单源
├── macos-host/         SwiftUI 宿主（SwiftPM，经 raco rivet build 驱动）
├── windows/            WinUI3 宿主（脚手架）
├── linux/              GTK4 宿主（脚手架）
├── scripts/            gen-swift-strings.mjs（生成 GeneratedStrings.swift）
└── docs/               官网（index.html）+ 签名/winget 文档
```
