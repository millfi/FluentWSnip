# FluentWSnip の開発案内

## 最初に読むべきこと

- [docs/architecture.md](docs/architecture.md): 機能とファイルの対応、キャプチャ処理、描画・画像メモリの設計。
- [docs/development.md](docs/development.md): 環境構築、ビルド、実行、検証、配布、問題の切り分け。
- [README.md](README.md): 利用者向けの機能、対応形式、HDR パイプラインの説明。

ドキュメントと既存のコードが食い違うときは既存のコードの実装を優先し、見つけた食い違いを最後の返答ですべて挙げること。

## このアプリについて

- Windows 専用の C# / WinUI 3 アプリ。hjc4869/WSnipからのフォーク。実行ファイル名は `FluentWSnip.exe`へ改名したが、内部のプロジェクト名と名前空間は `WSnip.*` のままである。
- `WSnip.App` が UI とアプリ全体の進行、`WSnip.Windows` が Windows API、`WSnip.Core` が画像処理・エンコードを担当する。
- 「ウィンドウ」は凍結画像上の選択、「ウィンドウ全体」はライブ画面上の選択とウィンドウ単独キャプチャ。経路を混同しない。
- HDR の負の RGB 成分と SDR 白を超える値、物理ピクセルと DIP の区別、参照カウント付き画像の所有権を維持する。
- 凍結オーバーレイの背景キャッシュ、初回描画後の表示、テーマ連動アクリルは意図した動作。[設計上の要点](docs/architecture.md#設計上の要点)を参照。
- 共有依存は `external/LightPlayer` の Git submodule。初回取得・親側の更新後は `git submodule update --init --recursive` を実行する。参照先とバージョン設定は `Directory.Build.props` / `Directory.Packages.props` を確認する。
