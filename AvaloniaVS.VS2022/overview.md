# AxamlTPreviewer for Visual Studio 2022

AxamlTPreviewer 是面向 **Visual Studio 2022** 的 **Avalonia / AXAML** 开发增强扩展，在开源 AvaloniaVS 生态基础上继续维护与增强。

## 开源说明与致谢

本扩展基于 **MIT** 许可证开源（见仓库根目录 [license.md](../license.md)），为社区独立维护的衍生版本，**并非** Avalonia 官方产品，亦与 [Avalonia Accelerate](https://avaloniaui.net/accelerate) 无官方关联。

代码谱系（便于追溯与合规）：

| 层级 | 说明 |
|------|------|
| 官方原版（已归档） | [AvaloniaUI/AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) |
| 直接上游 fork | [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS) |
| 本仓库 | [xiu-xiang/AvaloniaVS](https://github.com/xiu-xiang/AvaloniaVS) |

感谢 Avalonia 原作者与贡献者，以及 [kongdetuo](https://github.com/kongdetuo) 在上游分支中的定制与开源贡献。本项目在保留原有版权与许可声明的前提下进行修改与再分发。

> 本包安装目标为 Visual Studio 2022（产品版本 17.x）。若使用 Visual Studio 2026，请安装对应的 VS2026 扩展包。

## 特色功能

- **样式类补全（Style Class Completion）**
  - 在 `Classes=""` 属性内直接补全样式类名（支持空格分隔的多类名、精确/基类/接口/全局候选）
  - 支持 `Classes.class="{Binding}"` 绑定语法，在 `Classes.` 点号后自动补全样式类并生成绑定
  - 自动过滤伪类（`:pseudo`），只提示真实存在的样式类
- **AXAML 实时预览（Live Previewer）**
  - 编辑即预览，无需手动刷新
- **已废弃成员提示（Obsolete）**
  - 补全列表标识已标记 `[Obsolete]` 的属性/事件（如 `TextBox.Watermark`）
  - 文档中已写属性显示警告波浪线与悬停说明（替代 API 提示）
- **转到定义**
  - 支持对 AXAML 中类型、属性、附加属性、事件及事件处理方法使用 F12 / Ctrl+点击导航

## 功能列表

- AXAML 实时预览（Previewer / Designer）
- AXAML 智能代码补全（IntelliSense）：元素、属性、绑定与标记扩展等
- **样式类补全**（特色功能）
- 已废弃成员补全提示与编辑器警告标记
- 转到定义（类型 / 成员 / 事件处理程序）
- 绑定表达式补全（`{Binding ...}`）
- AXAML 文件图标
- 代码片段（Snippets）
- 面向 **Avalonia 12.1.0** 的项目模板支持

## 捆绑模板

- 插件内置 **Avalonia.Templates 12.1.0** 项目模板（创建项目默认引用 Avalonia **12.1.0**）
- 安装插件后即可在 VS「新建项目」中直接创建 Avalonia 应用（App / Mvvm App / UserControl / Window 等）
