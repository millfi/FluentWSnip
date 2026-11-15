# WSnip

A screenshot tool with the feel of the Windows Snipping Tool that keeps HDR and wide color
gamut content. Screens are captured at 16-bit float precision. SDR content stays pixel-exact, and
HDR highlights are saved as gain-map images: SDR viewers show a tone-mapped base image, and HDR
viewers restore the original brightness.

WSnip is a Windows-only application built with WinUI 3 (Windows App SDK).

[![Get it from Microsoft](https://get.microsoft.com/images/en-us%20dark.svg)](https://apps.microsoft.com/detail/9PDM1H21NLB9)

## Features

- **Snip modes:** rectangle, window, full screen and freeform, with an optional 3, 5 or 10 second
  delay and an option to include the cursor. The screen freezes behind one borderless overlay per
  display. Change mode from the overlay's bar; press Esc or right-click to cancel.
- **Whole window mode:** the screen stays live and WSnip's own windows step aside. The pointer
  turns into a crosshair everywhere and the next click picks the window under it; the click does
  not reach that window. The window is read from the compositor whole, including parts covered by
  other windows and its own per-pixel transparency, so the snip has no desktop behind it. Esc or
  right-click cancels, and WSnip's windows come back either way.
- **Entry points:**
  - On Windows, a global hotkey with a user-assigned combination. It can be changed, set to Print Screen
    or turned off. Each snipping mode can have a hotkey of its own too. WSnip refuses a hotkey
    that another of its hotkeys, another app or Windows already uses.
  - On Windows, a notification-area icon.
  - `WSnip.exe --snip [rectangle|window|fullscreen|freeform|wholewindow]`.
  - On Windows, `--background` starts the app in the tray.
  - Later launches forward their command line to the running instance.
- **Capture:** On Windows, Windows.Graphics.Capture delivers `R16G16B16A16Float` scRGB frames at
  each display's SDR white level and peak brightness. Nothing is clipped to SDR or sRGB. WSnip's
  own windows stay open but are left out of the capture.
- **Editor:**
  - Pen, highlighter, eraser and crop tools, with undo/redo, zoom and pan.
  - A floating bottom dock with a blurred backdrop and HDR-aware icons. Fit keeps the image
    above the dock; zoomed images can extend behind it and pan fully into the unobstructed area.
  - On an HDR display it draws into a Win2D FP16 scRGB composition swap chain, so highlights show at their captured
    brightness, rolled off to the display's headroom. A toggle switches to the SDR rendition that
    SDR files and the clipboard get.
  - Custom pen colors on sRGB, Display P3 or BT.2020 primaries, with a brightness up to 16× SDR
    white, so strokes can be wide-gamut or HDR. The eyedropper keeps a color picked from the snip
    exactly, HDR and wide colors included. Custom colors are saved; right-click one to edit or
    remove it. Highlighters multiply, so their colors stay within SDR sRGB.
  - Pixel information next to the pointer while viewing or drawing: position, the color (hex for
    SDR sRGB; otherwise the narrowest of sRGB, Display P3 and BT.2020 with its brightness factor,
    plus linear scRGB), and luminance in multiples of SDR white and in nits at the capturing
    display's SDR white. It can be turned off in settings.
  - On Windows, closing the window returns WSnip to the tray and discards the snip, which was
    already copied or saved as the settings ask. Its pixels, undo history and window surfaces are
    released; reopening creates an empty editor in the previous position.
- **Delivery:**
  - Snips are copied to the clipboard and saved to `Pictures\Screenshots` automatically, using
    Snipping Tool file names ("Screenshot 2026-09-30 101530.png").
  - Save as offers every format.
- **Look:** Modern Windows styles and controls; light and dark themes; the system accent color;
  and an optional Mica backdrop on Windows 11.

## Localization

WSnip follows the system UI language at startup: en-US (the default and fallback), en-GB,
es-ES, fr-FR, de-DE, ja-JP, zh-CN and zh-TW. There is no in-app language setting.

UI text uses the existing .NET RESX resources in `src/WSnip.Core/Strings`, accessed from WinUI
XAML through compiled static `x:Bind` expressions and from C# through `AppStrings`. MSBuild generates the strongly typed
accessor and satellite resource assemblies; no custom localization generator is required.
British English overrides spelling differences and inherits unchanged strings from the neutral
en-US catalog. Other translations contain every resource key. Keep composite-format placeholders
such as `{0}` and `{1:0.00}` intact when editing translations.

## Output formats

The settings pick one format for SDR snips (PNG by default) and one for snips with HDR content (JPEG XL
by default). Quality defaults to 100 (lossless for JPEG XL), and JPEG XL uses the fastest encoding
effort (1), matching Light Player's video screenshots.

| Format | SDR snip | HDR snip |
| --- | --- | --- |
| PNG | 8-bit sRGB, with transparency | SDR rendition only |
| JPEG | Baseline sRGB | UltraHDR: SDR base + gain map (MPF, ISO 21496-1 and `hdrgm` XMP) |
| HEIC | HEVC through an available software or hardware encoder | SDR base + `tmap` gain map item |
| AVIF | AV1, 4:4:4 when supported by the encoder | SDR base + `tmap` gain map item |
| JPEG XL | 8-bit sRGB through libjxl | 16-bit BT.2100 PQ (no gain map) |
| PNG (HDR) | — | 16-bit BT.2100 PQ with `cICP` and `cLLI` |

HEIC, AVIF and JPEG XL use FFmpeg from `LightStudio.Ffmpeg` on Windows. HEIC and AVIF prefer software encoders and fall back to hardware encoders
that successfully open on the local device, using Light Player's shared still-image encoder probe.
A format without an applicable encoder is hidden, and automatic saves fall back to PNG or UltraHDR
JPEG. Large images are tiled when they exceed the encoder's picture-size limit.

## HDR pipeline

1. **Relative image.** A capture is converted to linear light relative to SDR white: 1.0 is the
   source display's SDR white and 2.0 is twice as bright. The image then gets robust statistics: a
   percentile peak that ignores isolated hot pixels, and whether it has HDR or wide-gamut content.
   HDR means brighter than SDR white on BT.2020 primaries, so wide-gamut SDR colors such as
   Display P3 red don't make a snip HDR, and get no gain map.
2. **SDR base.** A tone curve compresses the highlights into SDR white. The default is the
   ITU-R BT.2390 EETF, shaped in the PQ domain above a knee; Möbius, Reinhard, Hable, ACES and
   Khronos PBR Neutral are alternatives. Each curve has its own parameters, and all scale the
   color components together to keep hue, optionally fading compressed highlights toward white.
   The tone mapping setting chooses where the curve applies:
   - **Adaptive** (default): the compression fades out away from HDR regions, so UI and text next
     to an HDR video stay pixel-exact.
   - **Global:** one curve for the whole image.
   - **Clip:** no compression.

   The editor's tone mapping button tunes one snip with a live preview, and can make the result
   the default.
3. **Gain map:**
   - ISO 21496-1, with baseline headroom 0 (SDR) and alternate headroom log2(peak).
   - Offsets of 1/64, gamma 1, three channels, full resolution, so sharp HDR edges survive.
   - JPEG carries both the ISO metadata and Adobe's `hdrgm` XMP, following libultrahdr's segment
     layout.
   - HEIC and AVIF use a `tmap` derived item in an `altr` group, so readers that don't understand
     gain maps show the SDR base.
4. **PQ outputs** (JPEG XL, HDR PNG) map SDR white to 203 nits, per ITU-R BT.2408.
5. **Clipboard.** Windows has no standard HDR clipboard format, so the clipboard gets the SDR
   rendition as PNG (keeping transparency) and as a DIB. The data is written right away, so it
   stays pasteable after WSnip exits.

The gain-map output has been checked against libavif and FFmpeg decoders, and against Light
Player's decoders.

### Image memory

Uncompressed capture, rendition and encoder pixels use disposable, reference-counted native
buffers rather than large managed arrays. Display images share that storage with Skia; crop undo
states are views of the same pixels. A reference retained by an active render or export operation
keeps its pixels valid until that operation finishes, even if the editor closes meanwhile.

Snapshots are released before clipboard/export delivery. Confirming a new selection retires the
previous document before composing the replacement; cancelling keeps it. Successfully delivered
background snips are not retained in a hidden editor. Closing to the tray releases the real editor
window as well as its image; screenshot cleanup does not depend on a forced garbage collection.
GPU/driver caches and OS-owned clipboard data have separate lifetimes, so process working set
need not fall by exactly the released pixel byte count.

## Projects

- `WSnip.App`: the Windows executable, WinUI editor, frozen-screen overlays, settings, native tray,
  theme/backdrop integration, and FP16 image presentation.
- `WSnip.Windows`: Windows.Graphics.Capture, display color information, native window picking,
  global hotkeys, clipboard, single-instance forwarding, and shell integration.
- `WSnip.Core`: capture models, annotation history, tone mapping, HDR/WCG analysis, and encoders.

The image pipeline keeps linear scRGB data, including negative wide-gamut components and values
above SDR white. `HdrSurface` draws through a premultiplied FP16 Skia surface into a Win2D
`R16G16B16A16Float` swap chain. The captured SDR white is normalized by the document; presentation
scales it to the current monitor's SDR white in Windows' 80-nit scRGB units. Displays without
advanced color show the tone-mapped sRGB rendition. GPUs without FP16 swap-chain support use an
explicit SDR fallback. Moving between displays and changing HDR refreshes the presentation state.

## Build and run

Requirements: Windows 10 2004 or later (Mica requires Windows 11), .NET 10 SDK, WinApp CLI 0.7+
for packaged development runs, and LLVM/libclang for the shared FFmpeg binding generator.
Visual Studio 2026's Windows application packaging tools are needed for the legacy `.wapproj`.

The application continues to use Light Player's logging and FFmpeg shim. Check it out beside this
repository as `../LightPlayer`, or inside `.deps/LightPlayer` (ignored by Git), or pass an absolute
`-p:LightPlayerRoot=...` ending in a directory separator. NuGet restore fetches the FFmpeg release
package from Light Player's public release feed. There are no Avalonia runtime dependencies.

```powershell
git clone https://github.com/hjc4869/LightPlayer.git .deps/LightPlayer
dotnet build WSnip.slnx -p:Platform=x64
winapp run src/WSnip.App/WSnip.App.csproj --arch x64 -p Platform=x64
```

For ARM64, use `-p:Platform=ARM64 -r win-arm64`. `WSnip.App` is now the executable project;
`WSnip.Desktop` and the Linux projects have been removed.

Normal builds and publishes use self-contained .NET and Windows App SDK runtimes. WinUI and
Win2D native files ship alongside the executable; this is not a single-file application. The former
Avalonia static graphics packages and platform initialization have been removed.

```powershell
dotnet publish src/WSnip.App -c Release -r win-x64 -p:Platform=x64
```

## Packaging and releases

The application includes its own `Package.appxmanifest` for single-project WinUI packaging:

```powershell
winapp package src/WSnip.App/WSnip.App.csproj --arch x64 --arch arm64 --no-sign
```

The existing `packaging/WSnipMSIX/WSnipMSIX.wapproj` also targets the WinUI application, for the
signed sideload bundle built by CI. It retains the existing package identity, startup task,
`WSnip.exe` execution alias, screen-clipping protocol, and borderless capture capabilities.
`packaging/Set-Version.ps1 -Tag v1.2.3` stamps both manifests and the assembly version.

`packaging/WSnipSetup/build.ps1 [-Architecture x64,arm64]` builds the existing WiX installers from
an unpackaged self-contained WinUI publish, including the Windows App SDK runtime. The MSIX
configuration remains the default for development and distribution.

The release workflow builds Windows x64/ARM64 MSIX and setup artifacts only. Signing still uses
`WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`; the optional
`LIGHTPLAYER_REF` repository variable selects the shared dependency revision.

## Files

- Settings: `%APPDATA%\WSnip\settings.json`; the MSIX package uses its own application data at
  `%LOCALAPPDATA%\Packages\LightStudio.WSnip_<id>\LocalState\WSnip`.
- Logs: `logs\wsnip.log` beside the settings.
- Snips: the Windows Screenshots folder, or the folder selected in settings.
- Startup: the MSIX startup task or, for the setup installation, the user's Run key.

Existing settings, custom colors, export formats, and eight UI language catalogs are retained.

## Validation

```powershell
dotnet test tests/WSnip.Rendering.Tests -p:Platform=x64
winapp run src/WSnip.App/WSnip.App.csproj --arch x64 -p Platform=x64 -- --presentation-fixture
```

The six presentation regression tests cover HDR highlights, negative wide-gamut components,
SDR white scaling, matching multiply highlighters to export, and image lifetime. The Debug-only
fixture shows SDR/HDR ramps and P3 colors without capturing the desktop; it is excluded from
Release builds. Actual HDR appearance still requires a suitable display and Windows HDR enabled.

## Known limitations

- Whole-window snips retain a window's own transparency. Rounded corners and whole-window
  opacity applied later by DWM are not part of its captured surface.
- HEVC dimensions are padded to multiples of eight and cropped using a `clap` box.
- JPEG XL stores HDR as PQ rather than as a gain map.
- Actual peak brightness, multiple-monitor HDR/SDR-white matching, and display color depend on
  the monitor, GPU, driver, and Windows advanced-color configuration.

## License

Copyright (c) 2026 David Huang. **All rights reserved.**

This project is **source-available for reference and private, personal, non-redistributable use
only**; it is not FOSS licensed. See [LICENSE](LICENSE).
