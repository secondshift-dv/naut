<p align="center">
  <img src="src/Neuterradise.App/Assets/Brand/wordmark-lockup.png" alt="Naut" width="460">
</p>

<p align="center"><strong>A Navigator for Your Things Worth Keeping</strong></p>

<p align="center">
  <a href="https://github.com/secondshift-dv/naut/releases/tag/v0.0.1"><strong>下载 Naut v0.0.1</strong></a>
  ·
  <a href="https://secondshift-dv.github.io/naut/"><strong>查看在线演示</strong></a>
  ·
  <a href="docs/README.md"><strong>文档</strong></a>
</p>

<p align="center">
  <sub><strong>README:</strong>
  <a href="README.md">English</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.zh-Hans.md">简体中文</a>
  </sub>
</p>

<p align="center">
  <img src="docs/assets/readme/naut-showcase.gif" alt="Naut 交互展示" width="682">
</p>

## 让你的收藏更值得探索。

Naut 是一款面向 Windows 的 **local-first 媒体收藏管理器**。它将图片、视频和受支持的 3D 模型组织成以 Profile 为中心的收藏，无需把所有权交给云服务即可识别、展示和探索。

## 核心功能

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/face-intelligence.md"><img src="docs/assets/readme/feature-face-intelligence.svg" alt="Face Intelligence" width="100%"></a><br>
<strong>Face Intelligence</strong><br>
YuNet 检测适用的人脸，SFace 生成 embedding；在已有已确认 identity sample 时，Naut 可以在本地给出 Profile 候选。候选仍需审阅，不会被当作静默的身份决定。
</td>
<td width="50%" valign="top">
<a href="docs/figures.md"><img src="docs/assets/readme/feature-3d-figures.svg" alt="交互式 3D Figure" width="100%"></a><br>
<strong>交互式 3D Figure</strong><br>
兼容模型可成为支持旋转、平移、缩放、Naut 管理 framing 和受控 runtime texture 上限的交互式 Figure。
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/import-media.md"><img src="docs/assets/readme/feature-smart-import.svg" alt="Smart Media Import" width="100%"></a><br>
<strong>Smart Media Import</strong><br>
同一条 import path 只执行各类媒体真正需要的准备：metadata、thumbnail、受控 video presentation media、适用时的 face analysis，以及受支持模型的 Figure derivative。
</td>
<td width="50%" valign="top">
<a href="docs/customization.md"><img src="docs/assets/readme/feature-customization.svg" alt="不破坏原文件的展示" width="100%"></a><br>
<strong>不破坏原文件的展示</strong><br>
可调整 Home、Gallery、Card、Profile、Cover、Banner、Frame、Backdrop、layout、effect 和 theme，而不修改原始媒体。
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/languages.md"><img src="docs/assets/readme/feature-multilingual.svg" alt="8 种界面语言" width="100%"></a><br>
<strong>8 种界面语言</strong><br>
Naut 提供 English、Bahasa Indonesia、日本語、한국어、简体中文、Deutsch、Français 和 Español。切换界面语言不会重写 Vault 或媒体 metadata。
</td>
<td width="50%" valign="top">
<a href="docs/vault.md"><img src="docs/assets/readme/feature-local-vault.svg" alt="可携带的本地 Vault" width="100%"></a><br>
<strong>可携带的本地 Vault</strong><br>
便携应用包和 Vault 是独立边界。Import 会把文件复制进 Vault，源文件仍保留在原位置。
</td>
</tr>
</table>

## 为什么选择 Naut

- **以 Profile 为中心，而不是先按文件夹。** Profile 可以代表人物、角色、项目、物件、主题或其他收藏身份。
- **本地控制。** authoritative collection 保存在你选择的 Vault 中。
- **为浏览而准备。** Naut 生成受控 presentation derivative，而不是修改原始媒体。
- **3D 是可选能力。** 普通使用不要求 discrete GPU 或 Figure。
- **应用可替换，收藏保持持久。** 更新应用包不会替换 Vault。

## 实际界面

<table>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-home.png" alt="Home — 推荐 Profiles、activity 和 collection context。" width="100%"><br><sub>Home — 推荐 Profiles、activity 和 collection context。</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-profile.png" alt="Profile — identity、media、presentation 和可选 interactive Figure。" width="100%"><br><sub>Profile — identity、media、presentation 和可选 interactive Figure。</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-gallery.png" alt="Gallery — collection browsing、search、filter 和 Card presentation。" width="100%"><br><sub>Gallery — collection browsing、search、filter 和 Card presentation。</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-settings.png" alt="Settings — Theme、语言、Vault controls 和 system options。" width="100%"><br><sub>Settings — Theme、语言、Vault controls 和 system options。</sub></td>
</tr>
</table>

## 快速开始

1. 从官方 [GitHub Release](https://github.com/secondshift-dv/naut/releases/tag/v0.0.1) 下载 **Naut v0.0.1**。
2. 将完整 ZIP 解压到普通文件夹。
3. 运行 `naut.exe`。
4. 选择 Vault 的保存位置。
5. Import 媒体，或创建 Profile 后使用 **Add media**。

`runtime/` 和 `release-manifest.json` 必须与 `naut.exe` 保持在同一位置。若只想在浏览器中预览而不接触本地文件或 Vault，请打开 [Interactive Showcase](https://secondshift-dv.github.io/naut/)。

## 系统要求

Naut 的普通收藏体验刻意保持轻量，可选 Figure workload 需要更多图形余量。

| | Minimum | Recommended |
| --- | --- | --- |
| **OS** | Windows 10/11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64, 2 cores | Modern x86-64, 4+ cores |
| **RAM** | 8 GB | 16 GB |
| **GPU** | 普通使用可用 integrated graphics | 更流畅的 Figure 建议 Direct3D 11 integrated/discrete GPU |
| **Display** | 1280 × 720 | 1920 × 1080 或更高 |
| **App/update 可用空间** | 1.5 GB | SSD 上 1.5 GB+ |

Vault 存储空间独立计算，取决于收藏规模。详见 [Naut v0.0.1 System Requirements](docs/system-requirements.md)。

## 文档

从 [Naut Documentation](docs/README.md) 开始：Import Media、Face Intelligence、Profiles、Vault、Customization、Languages、3D Figures、System Requirements 和 Troubleshooting。

Contributor-facing build, licensing, and Presentation Pack material lives under [docs/development](docs/development/).

## Source 与 releases

私有 **naut-dv** repository 是 engineering authority。此整理后的 **naut** repository 是 public source、contribution、demo 和 official release surface。

Naut 自有 source 采用 PolyForm Shield 1.0.0，以 **Source Available** 方式提供；third-party component 保留各自 license。参见 [LICENSE](LICENSE)、[licensing scope](docs/development/licensing.md)、[brand policy](BRAND-POLICY.md)、[contributing](CONTRIBUTING.md)、[security](SECURITY.md) 和 [third-party notices](THIRD-PARTY-NOTICES.txt)。

官方 binary 和已签名 update metadata 仅通过 `secondshift-dv/naut` 的官方 **GitHub Releases** 发布。
