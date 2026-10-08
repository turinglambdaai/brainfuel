# BrainFuel

一个用于监控 **GLM Coding Plan** 5 小时滚动额度和每周额度的桌面小组件，看一眼即走。同时通过本机 CLI 登录读取 OpenAI Codex 与 Claude（Pro/Max）用量。

![Racket](https://img.shields.io/badge/Racket-9.3-blue) ![Swift](https://img.shields.io/badge/macOS-SwiftUI-orange) [![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE) [![CI](https://github.com/turinglambdaai/brainfuel/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/turinglambdaai/brainfuel/actions/workflows/ci.yml)

[English](README.md) · **中文**

基于 [Rivet](https://github.com/turinglambdaai/rivet) 构建：一份 Racket 领域核心（426 项测试）通过类型化 RPC 驱动各平台第一方原生宿主。Racket 后端拥有全部业务——provider 客户端、燃速、历史、设置、凭据；宿主只做渲染与交互转发。

## 宿主状态

| 平台 | 技术栈 | 状态 |
|---|---|---|
| macOS | SwiftUI | 可用——悬浮卡、托盘、通知、设置窗 |
| Linux | GTK4 | 可用——卡片、详情、设置（无托盘：rivet #118） |
| Windows | C++/WinRT | 脚手架 |

## 产品设计

- **额度数据**只属于主卡片。主卡片上的可见动作明确叫 **“刷新额度”**。
- **三层信息架构** — L0：常驻卡片，看一眼即走（零交互）。L1：单击卡片打开详情面板（燃速、预计耗尽、重置时间、套餐档位）。L2：⋯ 菜单与设置负责配置。没有主窗口，没有任务栏噪音。
- **应用生命周期在托盘** — 关闭卡片只是“隐藏”：轮询与通知继续，只有显式**退出**才结束进程。
- **桌面行为由用户决定** — “置顶显示”是可选项；新安装不抢焦点。

## 功能特性

- GLM Coding Plan 用 API Key；OpenAI Codex 与 Claude 复用本机 CLI 登录（`~/.codex`、`~/.claude`，每次刷新现读，不存储任何 token）
- 周额度 / 5 小时双环、75%/90% 紧迫色、迷你模式
- 燃速智能：各窗口消耗速度、预计耗尽时间
- 用量历史图（近 24 小时 / 7 天），支持上一周期叠加对比
- 多账号，各自节奏轮询、即时切换
- API Key 存于系统凭据库：Windows DPAPI、macOS Keychain、Linux Secret Service；旧明文 Key 自动迁移
- 中英双语；浅色 / 深色 / 跟随系统
- 数据格式与 v0.9.0 逐字节兼容：同一 `settings.json`、同一历史文件、同一数据目录

## 从源码构建

需要 Racket CS 9.x 与已 link 的 [Rivet](https://github.com/turinglambdaai/rivet)：

```bash
cd ../rivet && raco pkg install --auto --no-docs --name rivet --link file://$PWD
cd ../brainfuel
raco rivet doctor --json   # 工具链自检
raco rivet build           # 后端 bundle + 宿主构建
raco test racket/          # 领域核心测试
```

## 项目结构

```text
brainfuel/
├── rivet.rktd          # Rivet 应用清单
├── app/backend.rkt     # Rivet 后端入口（装配领域层）
├── racket/             # Racket 领域核心 + tests
├── shared/i18n/        # zh.json / en.json 单源
├── macos-host/         # SwiftUI 宿主（SwiftPM）
├── windows/            # WinUI3 宿主（脚手架）
├── linux/              # GTK4 宿主（脚手架）
└── docs/               # 官网 + 签名文档
```

## 许可

[MIT](LICENSE)。
