<p align="center">
  <img src="assets/labelscope-logo-256.png" alt="LabelScope logo" width="160">
</p>

<h1 align="center">LabelScope</h1>

<p align="center">
  A virtual Zebra® label printer for Windows.<br>
  Send ZPL to it from any program and see the label on screen, with no physical printer and no internet connection.
</p>

<p align="center">
  <em>By <a href="https://github.com/P4Software">P4 Software</a> · Status: in development (nothing to download yet)</em>
</p>

---

## Contents

- [What is LabelScope?](#what-is-labelscope)
- [Who is it for?](#who-is-it-for)
- [How it works](#how-it-works)
- [Features](#features)
- [Using it (planned first release)](#using-it-planned-first-release)
- [Settings](#settings)
- [ZPL command support](#zpl-command-support)
- [Privacy and security](#privacy-and-security)
- [Building from source](#building-from-source)
- [Project layout](#project-layout)
- [Roadmap](#roadmap)
- [Troubleshooting](#troubleshooting)
- [Trademarks and licence](#trademarks-and-licence)

## What is LabelScope?

Warehouse, shipping and retail systems print labels by sending **ZPL** (Zebra Programming Language) text to a label printer. Testing that output normally needs a real printer, a roll of labels and a lot of wasted paper.

LabelScope pretends to be that printer. It installs a normal Windows printer, receives the ZPL, draws the label on screen and shows you the raw ZPL next to it, so you can check a label in seconds.

## Who is it for?

- **Developers and integrators** building or changing label output in an ERP, WMS or shipping system.
- **Support and implementation teams** who need to see exactly what a system sent to the printer.
- **Anyone** who wants to preview a ZPL file without owning a Zebra printer.

## How it works

```
 Any Windows program ──print──▶  "P4 LabelScope Printer"  ──┐
                                 (a real Windows printer)    │  TCP, port 9100
 ERP / WMS / script ──────────── send ZPL over the network ──┤
                                                             ▼
                                                      LabelScope
                                          ┌────────────────┴────────────────┐
                                          │  receives ZPL, splits it into   │
                                          │  labels at ^XZ, draws each one  │
                                          └────────────────┬────────────────┘
                                                           ▼
                                      ┌───────────────────────────────────────┐
                                      │  label picture   │   raw ZPL text      │
                                      │  (zoom, save)    │   (line numbers,    │
                                      │                  │    warnings)        │
                                      └───────────────────────────────────────┘
```

- The Windows printer uses the **Generic / Text Only** driver that ships with Windows, so the ZPL reaches LabelScope untouched.
- Programs that can talk TCP directly can skip the printer and send to port **9100**, the standard port for network label printers.
- Rendering is done **inside LabelScope**. Nothing is uploaded anywhere.

## Features

| | |
|---|---|
| **Windows printer** | One button installs (and removes) a printer you can pick in any program. |
| **Network input** | Listens on a TCP port (default 9100) for ZPL from other programs or computers. |
| **Offline rendering** | Draws labels itself. No online service, no usage limits, no label data leaving the computer. |
| **Label and ZPL side by side** | The picture and the raw ZPL are visible at the same time, with line numbers. |
| **Plain-language warnings** | Commands that cannot be drawn are listed with their line. Click one to jump to it. |
| **History** | Every received label is kept in a list, newest first. |
| **Export** | Save a label as a PNG or copy it to the clipboard. |
| **Simple settings** | One `settings.json` file next to the program, created for you with explanations. |
| **No installation tricks** | Double-click to run. No command-line options or environment variables. |

## Using it (planned first release)

1. Unzip LabelScope anywhere and double-click **LabelScope.exe**.
2. Press **Install printer** and answer **Yes** when Windows asks for permission. This is the only time administrator rights are needed.
3. Print from any program to **P4 LabelScope Printer**, or send ZPL to `127.0.0.1:9100`.
4. The label appears in the window. Select older labels from the list on the left.

To remove the printer again, press **Remove printer**. LabelScope only ever removes a printer it created itself.

Quick test without any other program (PowerShell):

```powershell
$zpl = "^XA^FO50,50^A0N,60,60^FDHello LabelScope^FS^FO50,150^GB400,4,4^FS^XZ"
$client = New-Object Net.Sockets.TcpClient("127.0.0.1", 9100)
$bytes = [Text.Encoding]::UTF8.GetBytes($zpl)
$client.GetStream().Write($bytes, 0, $bytes.Length)
$client.Close()
```

## Settings

On first start LabelScope creates `settings.json` next to the program, with an explanation for every option. If a value is wrong it falls back to a safe default and tells you which setting to fix.

| Setting | Default | Meaning |
|---|---|---|
| `ListenAddress` | `127.0.0.1` | `127.0.0.1` accepts labels from this computer only; `0.0.0.0` also accepts them from the network. |
| `ListenPort` | `9100` | Port for incoming ZPL. Change it if another program already uses 9100. |
| `DefaultDpi` | `203` | Print resolution: 152, 203, 300 or 600. |
| `DefaultLabelWidthMm` | `101.6` | Label width when the ZPL does not say (4 inch). |
| `DefaultLabelHeightMm` | `152.4` | Label height when the ZPL does not say (6 inch). |
| `HistoryLimit` | `100` | How many labels to keep in the list. |
| `FontsFolder` | empty | Folder with your own TrueType fonts (a later release). |
| `LogFolder` | `logs` | Where log files are written. |
| `PrinterName` | `P4 LabelScope Printer` | Name of the Windows printer. |

## ZPL command support

LabelScope draws what it understands and **tells you about everything else** instead of failing silently. Support grows in stages:

| Stage | Commands | Status |
|---|---|---|
| 1. Core | `^XA ^XZ ^PW ^LL ^LH ^FO ^FT ^FD ^FS ^A ^CF ^FB ^FR ^GB ^GC ^PQ` | In development |
| 2. Barcodes and rotation | Code 128, Code 39, EAN-13, UPC-A, Interleaved 2 of 5, QR, Data Matrix, PDF417; `^GD ^FW ^PO` | Planned |
| 3. Graphics and fonts | `^GF ~DG ^XG ^IM ^IL`, your own TrueType fonts | Planned |
| 4. Advanced | `^SN ^FV ^CI ^FH`, more | Planned |

Text is drawn with an approximation of the Zebra fonts until the font stage is finished, so letter shapes and widths can differ slightly from a real printer. LabelScope is a preview tool, not a pixel-exact replacement for a printer.

## Privacy and security

- Labels are rendered locally; LabelScope makes no internet connections.
- By default it listens on `127.0.0.1` only, so other computers cannot reach it. Set `ListenAddress` to `0.0.0.0` only on networks you trust: anyone who can reach the port can send it labels.
- The program itself never runs as administrator. Only the printer install/remove step asks Windows for permission.
- LabelScope never changes or removes a printer it did not create.

## Building from source

Requirements: Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/P4Software/LabelScope.git
cd LabelScope
dotnet build
dotnet test
dotnet run --project src/LabelScope.App
```

Self-contained build (nothing to install on the target computer):

```powershell
dotnet publish src/LabelScope.App -c Release -r win-x64 --self-contained -o publish/win-x64
```

## Project layout

```
src/LabelScope.Core/    Settings, TCP listener, ZPL parser and renderer, printer installer (no UI)
src/LabelScope.App/     The Windows (WPF) application
tests/                  Automated tests for the Core library
assets/                 Logo and icon
```

## Roadmap

- [x] Name, logo and design
- [ ] First release: Windows printer, listener, core ZPL commands, label and raw ZPL side by side
- [ ] Barcodes, rotated text, light-grid and stacked-layout view options
- [ ] Graphics, images and custom fonts
- [ ] Advanced commands, installer package, Spanish user interface
- [ ] Optional: save every received label automatically to PDF or image files

## Troubleshooting

| Problem | What to do |
|---|---|
| "Port 9100 is already in use" | Another program (or a second LabelScope) uses the port. Close it, or set another `ListenPort` in `settings.json` and start LabelScope again. If you change the port, remove and reinstall the printer. |
| Windows asks for permission and nothing happens | The prompt was declined. Press **Install printer** again and choose **Yes**. |
| A printer with my name already exists | LabelScope will not touch printers it did not create. Choose a different `PrinterName` in `settings.json`. |
| A label looks different from the real printer | Fonts are approximated until the font stage is done. Check the warning list for commands that are not supported yet. |
| A label appears marked "incomplete" | The sender disconnected before sending `^XZ`. The label is shown as far as it arrived. |
| Something else | Use **Open log folder** and look at the newest file. |

## Trademarks and licence

Zebra® and ZPL® are trademarks of Zebra Technologies Corporation. LabelScope is an independent product by P4 Software and is not affiliated with or endorsed by Zebra Technologies.

The licence has not been chosen yet. Until it is, all rights are reserved.
