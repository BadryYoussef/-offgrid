# OffGrid 📡

**A peer-to-peer Bluetooth mesh chat application for Windows**

> Communicate offline. No internet. No servers. Just Bluetooth.

![.NET 8](https://img.shields.io/badge/.NET-8.0-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

---

## 🎯 What is OffGrid?

OffGrid is a **serverless chat application** that uses **Classic Bluetooth RFCOMM** to create direct connections between Windows devices. It features:

- **Peer-to-Peer Messaging** - No central server, every device is equal
- **Mesh Relay** - Messages hop through intermediate devices to reach peers not directly connected
- **File Transfer** - Send files with GZip compression and chunked transfer
- **Terminal UI** - Hacker-style green-on-black interface

---

## ✨ Features

| Feature | Description |
|---------|-------------|
| 💬 **Chat** | Real-time text messaging over Bluetooth |
| 🔗 **Mesh Network** | Messages relay through up to 7 hops |
| 📁 **File Transfer** | Compressed, chunked file sending |
| ⌨️ **Typing Indicators** | See when peers are typing |
| 🏷️ **Nicknames** | Custom display names with `/nick` |
| 🖥️ **Terminal Theme** | Cyberpunk hacker aesthetic |

---

## 🚀 Quick Start

### Prerequisites
- Windows 10/11
- .NET 8 SDK
- Bluetooth adapter
- Devices paired via Windows Bluetooth Settings

### Run
```powershell
git clone https://github.com/BadryYoussef/-offgrid.git
cd -offgrid
dotnet run
```

### Build Executable
```powershell
dotnet publish -c Release -r win-x64 --self-contained
```

---

## 💻 Commands

| Command | Description |
|---------|-------------|
| `/clear` | Clear chat window |
| `/nick <name>` | Set your display name |
| `/sendfile` | Send a file to connected peers |
| `/peers` | Show network topology |

---

## 🏗️ Architecture

```
┌─────────────────────────────────────┐
│         OffGrid Application         │
├─────────────────────────────────────┤
│  UI (WPF)      → Terminal Theme     │
│  Logic (C#)    → Message Handling   │
│  Network       → Bluetooth RFCOMM   │
│  Library       → InTheHand.Net      │
└─────────────────────────────────────┘
```

### Mesh Relay
```
[Device A] ←→ [Device B] ←→ [Device C]
     └── Messages hop automatically! ──┘
```

---

## 📁 Project Structure

```
├── App.xaml / App.xaml.cs      # Application entry
├── MainWindow.xaml             # UI layout (terminal theme)
├── MainWindow.xaml.cs          # All logic (~1500 lines)
├── OffGrid.csproj              # Project configuration
├── TECHNICAL_GUIDE.md          # Detailed documentation
└── logo.ico / logo.png         # App icons
```

---

## 🔧 Technology Stack

- **Language**: C# (.NET 8)
- **UI Framework**: WPF (Windows Presentation Foundation)
- **Bluetooth**: [InTheHand.Net.Bluetooth](https://www.nuget.org/packages/InTheHand.Net.Bluetooth)
- **Protocol**: Classic Bluetooth RFCOMM

---

## 📖 Documentation

See [TECHNICAL_GUIDE.md](TECHNICAL_GUIDE.md) for detailed technical documentation including:
- Protocol specification
- Code walkthrough
- Line-by-line function locations
- Design decisions

---

## ⚠️ Limitations

- Windows only (uses Windows Bluetooth stack)
- Devices must be paired in Windows Settings first
- Messages are not encrypted (plaintext)

---

## 📄 License

MIT License - feel free to use and modify!

---

*Built with ❤️ and Bluetooth*
