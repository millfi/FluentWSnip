# 開発・検証・配布

機能の実装箇所は [architecture.md](architecture.md)、利用者向けの仕様は [README](../README.md) を参照する。
以下のコマンドはリポジトリのルートの PowerShell で実行する。

## 環境と共有依存

- Windows 10 2004 以降、.NET 10 SDK。ターゲットの Windows SDK / パッケージバージョンは [Directory.Build.props](../Directory.Build.props) と [Directory.Packages.props](../Directory.Packages.props) が基準。
- パッケージ化した開発実行には WinApp CLI 0.7 以降。旧 WAP プロジェクトのビルドには Visual Studio の Windows application packaging tools が必要。
- 共有の FFmpeg バインディング生成には LLVM / libclang を使う。
- Mica は Windows 11 の機能。HDR の見え方は実機の画面、Windows HDR、SDR コンテンツの明るさにも依存する。

Light Player は external/LightPlayer/ の Git submodule。親リポジトリの記録済みコミットをローカル・CI 共通で使用する。
初回取得や親側の更新後は次を実行する。

```powershell
git submodule update --init --recursive
```

並行開発時だけ LightPlayerRoot プロパティで別の配置を明示指定できる。末尾の区切りも付ける。
依存更新は submodule 内で対象コミットを checkout し、ビルド・テスト後に git add external/LightPlayer で参照を更新する。
通常の取得では --remote を使わない。

ルートの [Directory.Build.targets](../Directory.Build.targets) が共有の NuGet 取得・ネイティブバインディング生成ターゲットを読み込む。
ログ、FFmpeg shim、静止画エンコーダー探索などの共有実装は Light Player 側にもある。両リポジトリの責務を確認して変更先を決める。

## ビルドと実行

```powershell
dotnet build WSnip.slnx -p:Platform=x64
winapp run src/WSnip.App/WSnip.App.csproj --arch x64 -p Platform=x64 --devtools off
```

Release の UI を確認する場合:

```powershell
winapp run src/WSnip.App/WSnip.App.csproj --arch x64 -c Release -p Platform=x64 --devtools off
```

`--devtools off` は検証用の追加ツールバーを表示させないための指定。XAML のライブ検査が必要なら外すか `--devtools headless` を使う。
すでに同じ構成をビルド済みなら `--no-build --no-restore` を追加できる。ソースを変更した後は古いバイナリを起動しないようにする。
パッケージの再登録や DevTools の起動に伴い既存プロセスが終了することがあるため、開いている画像・未保存の編集を確認してから実行する。

ARM64 のビルド例:

```powershell
dotnet build WSnip.slnx -p:Platform=ARM64 -r win-arm64
```

対応プラットフォームは x64 / ARM64。AnyCPU は使用しない。実行とテストは、そのアーキテクチャを実行できる環境で行う。
[WSnip.slnx](../WSnip.slnx) の明示的なビルド依存と `BuildInParallel=false` は、共有のネイティブバインディング生成が競合するのを避けるためのもの。
並列化を変更するときは、共有プロジェクトの生成ファイルへの同時書き込みも確認する。

通常のビルドは .NET / Windows App SDK とも framework-dependent。実行にはマシン共通の .NET Desktop Runtime と Windows App Runtime framework package (`Microsoft.WindowsAppRuntime.2`) が必要で、どちらも成果物には同梱しない。Native AOT は既定ではなく、WinUI / Win2D / FFmpeg の DLL が隣接する複数ファイルの構成。
成果物は通常 `artifacts/bin/FluentWSnip/<構成>_win-<arch>/`、開発登録されたレイアウトはその下の `AppX/` に置かれる。
正確な出力先は MSBuild / WinApp の実行結果を確認する。`artifacts/` と `.deps/` は Git 管理外。

## 検証

### 自動テスト

```powershell
dotnet test tests/WSnip.Rendering.Tests -p:Platform=x64
```

ソリューションの同じ構成をビルドした直後なら、再ビルドせず実行できる。

```powershell
dotnet test tests/WSnip.Rendering.Tests -p:Platform=x64 --no-build --no-restore
```

| ファイル | 検証するもの |
| --- | --- |
| [ScRgbPresentationTests.cs](../tests/WSnip.Rendering.Tests/ScRgbPresentationTests.cs) | 現在 6 ケース。HDR ハイライト、負の広色域成分、SDR 白の倍率、蛍光ペンの表示と書き出しの一致、取得画像を解放した後の表示画像の寿命。 |
| [InstanceIsolationTests.cs](../tests/WSnip.Rendering.Tests/InstanceIsolationTests.cs) | 現在 2 ケース。旧名を模したインスタンスとの mutex 分離と、FluentWSnip だけへの named pipe 引数転送。実際の Store 版を起動するテストではない。 |

このテストは実ディスプレイの HDR 表示や WinUI / DWM 全体の動作を保証するものではない。UI・素材・キャプチャ経路を変えた場合は、対応する画面でも確認する。

### キャプチャの E2E テスト

ロックされていない Windows デスクトップで、PowerShell 7.2 以降と WinApp CLI 0.8 以降を使う。実行前に FluentWSnip のキャプチャや編集を終了し、アプリを閉じる。

```powershell
./tests/WSnip.Capture.E2E/Test-Capture.ps1
```

既定は x64 の Release。該当構成のビルドが済んでいれば `-NoBuild` を追加できる。ARM64 で実行する場合は `-Architecture ARM64` を指定する。

スクリプトは通常版と別の開発パッケージを一時登録し、専用の設定で保存とコピーを有効にする。Windows がテスト版に撮影枠の非表示を許可するか尋ねた場合は、枠を表示する選択肢を使う。ツールバーの前面化を待つメッセージが出た場合は、ツールバーの余白をクリックする。

既知の画像を表示する2つのウィンドウを少し重ね、四角形・ウィンドウ・全画面・自由形・ウィンドウ全体を順に撮影する。2回目以降はエディターの新規キャプチャボタンから始める。各モードの選択前にホバーして、オーバーレイとツールバーが維持されることを確認する。保存とクリップボードから取得した PNG は寸法と画素を検査し、重なり方や自由形の透過も確認する。最後に新しいキャプチャを Esc で終了し、出力が増えないこととクリップボードが変わらないことを確認する。

出力は `artifacts/capture-e2e/run-*/` の `results.json`、`saved/`、`*-clipboard.png`。全画面の PNG には実行中のデスクトップが写り、実際のクリップボードには最後にコピーした画像が残る。

### 画面確認

Debug 専用の [PresentationFixture.cs](../tests/Fixtures/PresentationFixture.cs) は SDR / HDR ランプと P3 色を表示する。デスクトップの撮影は行わない。
起動時だけ処理する引数なので、既存インスタンスを終了してから実行する。

```powershell
winapp run src/WSnip.App/WSnip.App.csproj --arch x64 -c Debug -p Platform=x64 --devtools off -- --presentation-fixture
```

変更した機能に応じて次を確認する。

| 変更対象 | 画面で確認すること |
| --- | --- |
| 凍結オーバーレイ | 開始前の白いフラッシュがない、矩形ドラッグが滑らか、表示倍率・複数画面をまたぐ選択、確定時の座標、Esc / 右クリック。 |
| モード切り替え・終了 | エディターやトレイのない起動から「ウィンドウ全体」と往復し、同じツールバーが消えず位置・サイズも変わらない。凍結背景を前面にしてもツールバーを覆わない。ツールバーが撮影に写らない。キャンセル後は通常の終了条件に従って終了する。 |
| ライブの「ウィンドウ全体」 | ホバー先だけ明るい、枠が追従する、ツールバーを操作できる、クリックが選択先アプリに届かない、キャンセル後に暗幕とカーソルが残らない。 |
| テーマ・素材 | アプリの Light / Dark / System、通常選択と「ウィンドウ全体」の両ツールバー、アイコンの可読性、アクリルの表示とフォールバック。 |
| エディター | ペン・蛍光ペン・消しゴム、クロップと undo / redo、ズーム・パン、HDR / SDR 切替、表示先モニターの変更。 |
| 保存・寿命 | 保存とコピー、実行中に閉じた場合、別のキャプチャで置換した場合、透明画像と HDR / SDR の各出力。 |

UI 自動操作には `winapp ui inspect` / `invoke` / `screenshot` などを使用できる。プロセスと HWND を確認して対象を絞る。
前面入力ガードで操作が拒否された場合、その操作を確認済みと報告したり、別の入力 API でガードを迂回したりしない。

以前の `OverlayDrawingFixture.cs` / `OverlayPerformanceFixture.cs` とそのベンチマーク引数は削除済み。
`artifacts/` に過去の診断スクリプトや画像があっても、現在の正式なテストや新しいチェックアウトで使えるファイルとは扱わない。

## 実行データとアプリの識別

| 項目 | 現在の値・実装 |
| --- | --- |
| 製品・実行ファイル | `FluentWSnip` / `FluentWSnip.exe`。内部のプロジェクト名は `WSnip.*`。 |
| MSIX identity | `LightStudio.FluentWSnip`。publisher と合わせて package family が決まる。 |
| インスタンス制御 | `WindowsPlatform.AppId = LightStudio.FluentWSnip`。MSIX 版と非パッケージの開発実行が同じ FluentWSnip 用のインスタンス制御を使う。 |
| startup task / Run 値 | `FluentWSnipStartup` / `FluentWSnip`。 |
| MSIX の設定 | `%LOCALAPPDATA%\Packages\LightStudio.FluentWSnip_<publisher-id>\LocalState\FluentWSnip\settings.json`。 |
| 非パッケージ版の設定 | `%APPDATA%\FluentWSnip\settings.json`。MSIX 側とは別の保存先。 |
| ログ | 設定と同じデータディレクトリの `logs\fluentwsnip.log`。起動・取得・保存・描画失敗・終了を確認する。 |
| 自動保存 | Windows の既知の Screenshots フォルダー内の `FluentWSnip`、または設定で指定したフォルダー。OneDrive などへのリダイレクトにも従う。 |

旧 WSnip との比較対象はユーザーが別にインストールした Store 版。旧アプリのデータや登録を使い回さない。
ホットキーは別の組み合わせにし、`ms-screenclip` の既定ハンドラーは Windows の設定で選択する。

## パッケージとリリースの実装箇所

| ファイル | 役割 |
| --- | --- |
| [src/WSnip.App/Package.appxmanifest](../src/WSnip.App/Package.appxmanifest) | 単一プロジェクトの WinUI パッケージ。実行ファイルはパッケージ直下の `FluentWSnip.exe`。 |
| [packaging/WSnipMSIX/Package.appxmanifest](../packaging/WSnipMSIX/Package.appxmanifest) | WAP 用の manifest。実行ファイルは `WSnip.App\FluentWSnip.exe`。上記と同じ FluentWSnip identity を使うが、配置が異なる。 |
| [WSnipMSIX.wapproj](../packaging/WSnipMSIX/WSnipMSIX.wapproj)、[Resources.resw](../packaging/WSnipMSIX/Resources.resw)、[Images/](../packaging/WSnipMSIX/Images) | WAP の構成、パッケージ表示文字列、ロゴ。 |
| [app.manifest](../src/WSnip.App/app.manifest)、[wsnip.ico](../src/WSnip.App/Assets/wsnip.ico) | Win32 の manifest と実行ファイルのアイコン。 |
| [Set-Version.ps1](../packaging/Set-Version.ps1) | `Directory.Build.props` と二つの MSIX manifest のバージョン更新。 |
| [release.yml](../.github/workflows/release.yml) | x64 / ARM64 の MSIX の作成、署名、アーカイブ、GitHub Release。記録済み submodule も取得する。 |
| [LICENSE](../LICENSE)、[THIRDPARTY.txt](../THIRDPARTY.txt) | 利用条件と同梱依存の表記。 |

単一プロジェクトの未署名 MSIX:

```powershell
winapp package src/WSnip.App/WSnip.App.csproj --arch x64 --arch arm64 --no-sign
```

インストールまで含めて動作確認する場合は開発証明書で署名する。

```powershell
winapp cert generate --manifest src/WSnip.App/Package.appxmanifest --output devcert.pfx
winapp cert install devcert.pfx   # 最初の一度だけ管理者で実行
winapp package src/WSnip.App/WSnip.App.csproj --arch x64 --cert devcert.pfx
Add-AppxPackage .\FluentWSnip-0.1.0.0-x64.msix
```

WAP のビルド・署名は CI のコマンドを基準にする。複数 RID は `RuntimeIdentifiers` 環境変数に `win-x64;win-arm64` を設定している。
framework-dependent のため、生成される manifest の `Dependencies` には Windows App SDK のビルド機構が `Microsoft.WindowsAppRuntime.2` の framework 依存 (`PackageDependency`) を付与する。実行マシン側には .NET Desktop Runtime が必要。
署名鍵は Git に入れず、CI は `WINDOWS_SIGNING_CERTIFICATE_BASE64` / `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` を使う。
MSIX の identity、alias、startup task、バージョンを変える場合は二つの manifest を確認する。
`packaging/flatpak/` に残るアイコンは現行 Windows ビルドには使われない。

## 問題の切り分け

| 症状 | まず確認する箇所 |
| --- | --- |
| 共有ターゲットが見つからない | `LightPlayerRoot`、共有チェックアウト、ルートの props / targets。 |
| FFmpeg の生成・取得で失敗 | libclang、NuGet キャッシュと GitHub release asset の取得、共有 `NativeBindings` のログ。同時に別のビルドを走らせていないか。 |
| 起動したはずの変更が見えない | 同じ名前の古いプロセスへ引数転送されていないか、Debug / Release、パッケージの登録先と `AppX/`。 |
| モード切り替えで選択が終わる | `App.DispatcherShutdownMode` が `OnExplicitShutdown` か。最後の選択ウィンドウの Close と、`AppController` のキャプチャ継続・終了判定。 |
| 設定・ログが見つからない | `WindowsPackage.IsPackaged`。開発登録もパッケージ identity を持つため、単に Debug だから `%APPDATA%` とは限らない。 |
| キャプチャ開始前に白くなる | `OverlayWindow.PrepareAsync`、`HdrSurface.InitialFrameReady`、compositor commit より先に表示していないか。 |
| 矩形選択が低 FPS | 背景の再描画・転送回数、composition にまとめた更新、モニターごとの不要な更新。 |
| ツールバーが消える・レイアウトが変わる | `CaptureToolbar` の寿命、内容の測定と DPI、`SetOverlayCutout`。取得経路の切り替えでツールバーを作り直していないか。 |
| ツールバーの色や素材が違う | `App.xaml` の `SnipModeBarStyle`、固定の `RequestedTheme`、`ThemeService.Attach` の背景管理フラグ、`DesktopAcrylicBackdrop`。 |
| ライブ選択を終えても暗幕・十字カーソルが残る | picker の `finally`、native window / timer / hook の解放、`RestoreCursors`。 |
| 色・明るさ・透明度が変わる | 取得時と相対 scRGB の区別、SDR 白、表示側の白倍率、straight / premultiplied alpha、行ストライド。 |

`Compiled XAML coordinates are unavailable` はこの環境の DevTools で観測された警告。ビルド失敗と区別し、通常の UI 確認では `--devtools off` でも再現するか調べる。
ビルド成功、数値テスト成功、実際の画面確認はそれぞれ別の結果として記録する。
