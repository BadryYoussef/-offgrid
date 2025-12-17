# OffGrid - Complete Technical Documentation

## 🎯 Project Overview

**OffGrid** is a **peer-to-peer (P2P) Bluetooth mesh chat application** for Windows that enables completely offline, serverless communication between devices.

### Key Innovation
- **No Internet Required** - Pure Bluetooth communication
- **No Server** - Every device is equal (peer-to-peer)
- **Mesh Networking** - Messages hop through intermediate devices
- **File Transfer** - Send/receive files with compression

---

## 📋 Technology Stack

| Component | Technology | Purpose |
|-----------|------------|---------|
| **Language** | C# (.NET 8) | Modern, strongly-typed |
| **UI Framework** | WPF (Windows Presentation Foundation) | Native Windows desktop |
| **Bluetooth** | `InTheHand.Net.Bluetooth` NuGet | Wraps Windows Bluetooth APIs |
| **Protocol** | Classic Bluetooth RFCOMM | Reliable TCP-like streams |
| **Compression** | GZip | File transfer optimization |

---

## 🏗️ Architecture

```
┌──────────────────────────────────────────────────┐
│              OFFGRID APPLICATION                 │
├──────────────────────────────────────────────────┤
│  UI Layer         │  MainWindow.xaml             │
│                   │  - "Hacker terminal" theme   │
│                   │  - Green on black (#00FF41)  │
│                   │  - Consolas monospace font   │
├───────────────────┼──────────────────────────────┤
│  Logic Layer      │  MainWindow.xaml.cs          │
│                   │  - Connection management     │
│                   │  - Message processing        │
│                   │  - Mesh relay logic          │
│                   │  - File transfer handling    │
├───────────────────┼──────────────────────────────┤
│  Network Layer    │  BluetoothListener/Client    │
│                   │  - RFCOMM connections        │
│                   │  - Stream-based I/O          │
├───────────────────┼──────────────────────────────┤
│  External Library │  InTheHand.Net.Bluetooth     │
│                   │  - Windows Bluetooth wrapper │
└──────────────────────────────────────────────────┘
```

---

## 🔑 Core Features

### 1. Dual-Role Connectivity
Every OffGrid instance runs as **BOTH**:
- **Server** - Listens for incoming connections (`BluetoothListener`)
- **Client** - Initiates outgoing connections (`BluetoothClient`)

```csharp
// Server: Accept incoming connections
_listener = new BluetoothListener(OffGridServiceId);
_listener.Start();
var client = await _listener.AcceptBluetoothClientAsync();

// Client: Connect to a peer
var client = new BluetoothClient();
await client.ConnectAsync(device.DeviceAddress, OffGridServiceId);
```

### 2. Service UUID
```csharp
Guid OffGridServiceId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
```
- **Unique identifier** for the OffGrid service
- All instances use the **same UUID** to find each other

### 3. Mesh Relay (Multi-Hop Messages)

**Problem**: Device A can only talk to devices it's paired with.

**Solution**: Use intermediate devices as relays!

```
[Device A] ←paired→ [Device B] ←paired→ [Device C]

Message: A → B → C (B forwards automatically!)
```

#### Loop Prevention:
1. **Message ID** - Each message has unique ID (8 hex chars)
2. **Seen tracking** - Skip already-processed messages
3. **Hop count** - Max 7 hops, decrements each relay

### 4. Chunked File Transfer

Files are sent in chunks with compression:

```
1. File → GZip Compress → Split into 16KB chunks
2. Send: FSTART (header) → FCHUNK × N → FEND
3. Receiver: Reassemble → Decompress → Save
```

**Features**:
- GZip compression (reduces size)
- Progress tracking (percentage updates)
- SHA256 checksum verification
- Saves to Downloads folder as `OffGrid_filename`

---

## 📨 Message Protocol

All messages use prefix-based parsing:

| Prefix | Format | Purpose |
|--------|--------|---------|
| `MSG:` | `MSG:Hello world` | Direct chat message |
| `RELAY:` | `RELAY:id|fromAddr|nick|toAddr|hops|msg` | Mesh relay message |
| `TYPING:` | `TYPING:1` or `TYPING:0` | Typing indicator |
| `NICK:` | `NICK:NewName` | Nickname change |
| `PEERS:` | `PEERS:nick1@addr1,nick2@addr2` | Peer announcement |
| `FSTART:` | `FSTART:file|origSize|compSize|chunks|checksum` | File transfer start |
| `FCHUNK:` | `FCHUNK:index|base64data` | File chunk data |
| `FEND:` | `FEND:success|checksum` | File transfer end |

### Relay Message Format (Detailed)
```
RELAY:abc123|AA:BB:CC:DD|Alice|*|5|Hello world
       │        │         │   │  │     │
       │        │         │   │  │     └── Message content
       │        │         │   │  └── Hop count (decrements)
       │        │         │   └── Destination (* = broadcast)
       │        │         └── Sender's nickname
       │        └── Sender's Bluetooth address
       └── Unique message ID (prevents loops)
```

---

## 💻 Available Commands

| Command | Description |
|---------|-------------|
| `/clear` | Clear the chat window |
| `/nick <name>` | Change your display name |
| `/sendfile` | Open file picker & send file |
| `/peers` | Show mesh network topology |

---

## 📁 Project Structure

| File | Purpose |
|------|---------|
| `OffGrid.csproj` | Project config, NuGet references |
| `App.xaml` | Application entry point |
| `App.xaml.cs` | App code-behind |
| `MainWindow.xaml` | UI layout (XAML) - Terminal theme |
| `MainWindow.xaml.cs` | **All logic** (~1500 lines) |
| `logo.ico` / `logo.png` | App icons |

---

## 🎨 UI Design

**Theme**: "Hollywood Hacker Terminal" / Cyberpunk

- **Color**: `#00FF41` (Matrix green) on `#000000` (black)
- **Font**: Consolas (monospace)
- **Border**: Single-pixel green borders
- **Hover effect**: Inverts to black on green

### Layout
```
┌───────────────┬─────────────────────────────────┐
│ PAIRED        │  ▀█▀ OFFGRID    [0 ACTIVE LINKS]│
│ DEVICES       ├─────────────────────────────────┤
│               │                                 │
│ ☐ Device 1    │  [SYSTEM] Messages appear here  │
│ ☐ Device 2    │  [Alice]: Hello!                │
│               │  [You]: Hi back!                │
│ [STATUS]      │                                 │
│ [CONNECT]     ├─────────────────────────────────┤
│ [REFRESH]     │  ► [Message input box]   [SEND] │
└───────────────┴─────────────────────────────────┘
```

---

## ⚙️ Key Code Components

### Thread-Safe Collections
```csharp
ConcurrentDictionary<string, PeerConnection> _activeConnections;  // Active links
ConcurrentDictionary<string, bool> _connectedAddresses;           // Prevent duplicates
ConcurrentDictionary<string, string> _remoteNicknames;            // Peer nicknames
ConcurrentDictionary<string, DateTime> _seenMessageIds;           // Loop prevention
ConcurrentDictionary<string, MeshPeer> _knownPeers;               // Mesh topology
ConcurrentDictionary<string, FileTransferState> _incomingTransfers; // File transfers
```

### Message Flow
```csharp
async Task ProcessReceivedDataAsync(PeerConnection peer, string data)
{
    if (message.StartsWith(RELAY_PREFIX))  → HandleRelayMessageAsync()
    if (message.StartsWith(MSG_PREFIX))    → Display + Forward
    if (message.StartsWith(FSTART_PREFIX)) → Start file receive
    if (message.StartsWith(FCHUNK_PREFIX)) → Store chunk
    if (message.StartsWith(FEND_PREFIX))   → Reassemble + Save
    // ... etc
}
```

### Forwarding Logic
```csharp
async Task ForwardToOthersAsync(string excludeAddress, string message)
{
    foreach (var connection in _activeConnections)
    {
        if (connection.Key != excludeAddress)  // Don't echo back
        {
            await connection.Value.Stream.WriteAsync(data);
        }
    }
}
```

---

## ⚡ How It Works (Step by Step)

### Connecting
1. User pairs devices in **Windows Bluetooth Settings**
2. Opens OffGrid on both devices
3. Selects peer → Clicks **CONNECT**
4. RFCOMM connection established
5. Nicknames exchanged automatically

### Sending a Message
1. User types message + Enter
2. `MSG:message` sent to all connections
3. Peers receive, display, **and forward** via mesh
4. Forwarded as `RELAY:...` with hop counting

### Sending a File
1. User types `/sendfile`
2. File picker opens
3. File compressed with GZip
4. Split into 16KB chunks
5. `FSTART` → `FCHUNK` × N → `FEND` sent
6. Receiver reassembles + saves

---

## 🔒 Design Decisions

| Decision | Rationale |
|----------|-----------|
| Classic Bluetooth (not BLE) | More reliable for streams/files |
| RFCOMM protocol | TCP-like, bidirectional streams |
| ConcurrentDictionary | Thread-safe async operations |
| Prefix-based protocol | Easy to parse, extensible |
| 7-hop limit | Prevents infinite loops |
| GZip compression | Reduces transfer time |
| Chunked transfers | Handles large files reliably |

---

## ⚠️ Known Limitations

1. **Windows only** - Uses Windows Bluetooth stack
2. **Requires pairing** - Cannot discover unpaired devices
3. **No encryption** - Messages are plaintext (could add later)
4. **Range limited** - Bluetooth range ~10-100 meters

---

## 🎤 Quick Presentation Summary

> "OffGrid is a peer-to-peer Bluetooth chat application I built using C# and WPF (.NET 8). It uses Classic Bluetooth RFCOMM to create direct, encrypted-channel connections between paired Windows devices. 
>
> Each instance acts as both a server and a client simultaneously. I implemented a custom text-based protocol with prefixes for different message types.
>
> The most interesting feature is **mesh relay** - messages automatically hop through intermediate devices to reach peers that aren't directly paired. I use message IDs and hop counters to prevent infinite loops.
>
> I also added **chunked file transfer** with GZip compression - files are split into 16KB chunks, sent with checksums, and reassembled on the receiving end.
>
> The UI follows a 'hacker terminal' aesthetic with green-on-black colors and monospace fonts."

---

## 📊 Technical Statistics

- **Total Lines of Code**: ~1,500 (MainWindow.xaml.cs)
- **UI Lines**: ~260 (MainWindow.xaml)
- **Max Hop Count**: 7
- **Chunk Size**: 16KB
- **Peer Announce Interval**: 30 seconds
- **Typing Indicator Timeout**: 3 seconds

---

## 🔧 Dependencies (NuGet)

```xml
<PackageReference Include="InTheHand.Net.Bluetooth" Version="4.*" />
```

---

## 🚀 Running the Application

```powershell
# From project directory
dotnet run

# Or build and run
dotnet build
.\bin\Debug\net8.0-windows\OffGrid.exe
```

**Prerequisites**:
1. Windows 10/11 with Bluetooth
2. .NET 8 SDK
3. Bluetooth adapter enabled
4. Devices paired via Windows Settings

---

*Generated: December 2024 | OffGrid v1.0*
