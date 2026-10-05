![Naut 标志](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut 是面向 Windows 的本地优先媒体收藏管理器。Profile 可将图片、视频和可交互的 3D Figure 汇集到便携式 Vault 中，同时让收藏始终由用户掌控。

私有 **naut-dv** 仓库是工程权威。本仓库 **naut** 经过筛选，是公开源码、社区贡献和官方发布的入口。Naut 自有源码依据 PolyForm Shield 1.0.0 以 **Source Available** 方式提供；第三方组件继续遵循各自的许可证。

## 构建

需要 Windows x64 和 .NET SDK **10.0.401**。

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

便携包通过 `naut.exe` 启动；请将 `runtime/` 和 `release-manifest.json` 保持在其旁边。

## 官方下载

Naut 官方二进制文件和已签名的更新元数据仅通过官方 **secondshift-dv/naut GitHub Releases** 页面分发。请勿运行重新上传到 Discord、文件分享服务或第三方镜像的二进制文件。

## 项目政策

请参阅 [LICENSE](LICENSE)、[许可范围](docs/licensing.md)、[品牌政策](BRAND-POLICY.md)、[贡献指南](CONTRIBUTING.md)、[安全](SECURITY.md)和[第三方声明](THIRD-PARTY-NOTICES.txt)。

官方应用语言：英语、德语、西班牙语、法语、印度尼西亚语、日语、韩语和简体中文。
