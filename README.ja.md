![Naut ワードマーク](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut は Windows 向けのローカルファーストなメディアコレクション管理アプリです。プロフィールごとに画像、動画、インタラクティブな 3D Figure をポータブルな Vault にまとめ、コレクションをユーザー自身の管理下に保ちます。

非公開の **naut-dv** リポジトリがエンジニアリング上の権威です。このキュレーション済み **naut** リポジトリは、公開ソース、コントリビューション、公式リリースの窓口です。Naut 独自のソースは PolyForm Shield 1.0.0 に基づく **Source Available** で、サードパーティ製コンポーネントには各ライセンスが引き続き適用されます。

## ビルド

Windows x64 と .NET SDK **10.0.401** が必要です。

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

ポータブルパッケージは `naut.exe` から起動します。`runtime/` と `release-manifest.json` は同じ場所に保持してください。

## 公式ダウンロード

Naut の公式バイナリと署名済み更新メタデータは、公式の **secondshift-dv/naut GitHub Releases** ページからのみ配布されます。Discord、ファイル共有サービス、第三者ミラーに再アップロードされたバイナリは実行しないでください。

## プロジェクトポリシー

[LICENSE](LICENSE)、[ライセンス範囲](docs/licensing.md)、[ブランドポリシー](BRAND-POLICY.md)、[コントリビューション](CONTRIBUTING.md)、[セキュリティ](SECURITY.md)、[サードパーティ通知](THIRD-PARTY-NOTICES.txt)を参照してください。

公式アプリ言語: 英語、ドイツ語、スペイン語、フランス語、インドネシア語、日本語、韓国語、簡体字中国語。
