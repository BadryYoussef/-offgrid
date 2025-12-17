using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using Microsoft.Win32;

namespace OffGrid
{
    public partial class MainWindow : Window
    {
        // Bluetooth Service UUID - All OffGrid instances must use the same UUID
        private static readonly Guid OffGridServiceId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        
        // Message type prefixes for protocol
        private const string MSG_PREFIX = "MSG:";
        private const string TYPING_PREFIX = "TYPING:";
        private const string NICK_PREFIX = "NICK:";
        private const string FILE_PREFIX = "FILE:";       // Legacy - kept for compatibility
        private const string FILE_DATA_PREFIX = "FILEDATA:"; // Legacy
        
        // Mesh relay prefixes
        private const string RELAY_PREFIX = "RELAY:";  // RELAY:msgId|fromAddr|fromNick|toAddr|hopCount|content
        private const string PEERS_PREFIX = "PEERS:";  // PEERS:nick1@addr1,nick2@addr2,...
        
        // Chunked file transfer prefixes
        private const string FSTART_PREFIX = "FSTART:";  // FSTART:filename|origSize|compSize|chunks|checksum
        private const string FCHUNK_PREFIX = "FCHUNK:";  // FCHUNK:index|base64data
        private const string FEND_PREFIX = "FEND:";      // FEND:success|checksum
        
        // Mesh settings
        private const int MAX_HOP_COUNT = 7;
        private const int PEER_ANNOUNCE_INTERVAL_MS = 30000; // 30 seconds
        
        // Chunked file transfer settings
        private const int CHUNK_SIZE = 16384; // 16KB chunks
        
        // Collection of paired devices
        private ObservableCollection<DeviceViewModel> _pairedDevices = new();
        
        // Active connections - keyed by normalized address (no IN_/OUT_ prefix)
        private readonly ConcurrentDictionary<string, PeerConnection> _activeConnections = new();
        
        // Track which addresses we've already connected to (to prevent duplicates)
        private readonly ConcurrentDictionary<string, bool> _connectedAddresses = new();
        
        // Remote nicknames received from peers
        private readonly ConcurrentDictionary<string, string> _remoteNicknames = new();
        
        // Mesh relay: track seen message IDs to prevent loops (msgId -> timestamp)
        private readonly ConcurrentDictionary<string, DateTime> _seenMessageIds = new();
        
        // Mesh relay: known peers (address -> MeshPeer info)
        private readonly ConcurrentDictionary<string, MeshPeer> _knownPeers = new();
        
        // Chunked file transfer: incoming transfers (peerAddress -> state)
        private readonly ConcurrentDictionary<string, FileTransferState> _incomingTransfers = new();
        
        // Bluetooth listener for incoming connections
        private BluetoothListener? _listener;
        private CancellationTokenSource? _listenerCts;
        
        // Peer announcement timer
        private Timer? _peerAnnounceTimer;
        
        // Local device info
        private string _localDeviceName = "UNKNOWN";
        private string _localNickname = "";
        private string _localAddress = "";
        
        // Typing indicator
        private CancellationTokenSource? _typingIndicatorCts;
        private DateTime _lastTypingSent = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            DeviceList.ItemsSource = _pairedDevices;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get local device name and address
                var localRadio = BluetoothRadio.Default;
                if (localRadio != null)
                {
                    _localDeviceName = localRadio.Name;
                    _localNickname = _localDeviceName;
                    _localAddress = NormalizeAddress(localRadio.LocalAddress?.ToString() ?? "LOCAL");
                    AppendLog($"[SYSTEM] Local Node: {_localDeviceName}");
                    AppendLog("[SYSTEM] Commands: /clear /nick <name> /sendfile /peers");
                    AppendLog("[SYSTEM] Mesh relay ENABLED - messages will be forwarded");
                }
                else
                {
                    AppendLog("[ERROR] No Bluetooth radio found!");
                    UpdateStatus("[STATUS] NO BLUETOOTH");
                    return;
                }

                // Start the listener for incoming connections
                await StartListenerAsync();

                // Load paired devices
                await LoadPairedDevicesAsync();
                
                // Start peer announcement timer
                _peerAnnounceTimer = new Timer(
                    _ => BroadcastPeerList(),
                    null,
                    5000, // Initial delay of 5 seconds
                    PEER_ANNOUNCE_INTERVAL_MS
                );
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] Initialization failed: {ex.Message}");
            }
        }

        private async Task LoadPairedDevicesAsync()
        {
            try
            {
                UpdateStatus("[STATUS] SCANNING PAIRED...");
                AppendLog("[SYSTEM] Scanning Windows Paired Devices...");

                await Task.Run(() =>
                {
                    var client = new BluetoothClient();
                    var pairedDevices = client.PairedDevices;

                    Dispatcher.Invoke(() =>
                    {
                        _pairedDevices.Clear();
                        foreach (var device in pairedDevices)
                        {
                            _pairedDevices.Add(new DeviceViewModel(device));
                        }
                    });
                });

                if (_pairedDevices.Count > 0)
                {
                    AppendLog($"[SYSTEM] Found {_pairedDevices.Count} paired device(s):");
                    foreach (var deviceVm in _pairedDevices)
                    {
                        AppendLog($"  ├─ {deviceVm.DisplayName} [{deviceVm.Address}]");
                    }
                    UpdateStatus($"[STATUS] {_pairedDevices.Count} DEVICES");
                }
                else
                {
                    AppendLog("[SYSTEM] No paired devices found.");
                    AppendLog("[HINT] Pair devices via Windows Bluetooth settings first.");
                    UpdateStatus("[STATUS] NO DEVICES");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] Failed to scan: {ex.Message}");
                UpdateStatus("[STATUS] SCAN FAILED");
            }
        }

        private async Task StartListenerAsync()
        {
            try
            {
                _listenerCts = new CancellationTokenSource();
                
                await Task.Run(() =>
                {
                    _listener = new BluetoothListener(OffGridServiceId);
                    _listener.Start();
                    
                    Dispatcher.Invoke(() =>
                    {
                        AppendLog("[SYSTEM] Listener ACTIVE - Accepting incoming connections...");
                    });

                    // Accept connections loop
                    _ = AcceptConnectionsAsync(_listenerCts.Token);
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] Listener failed: {ex.Message}");
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                try
                {
                    // Wait for an incoming connection
                    var client = await Task.Run(() => _listener.AcceptBluetoothClient(), cancellationToken);
                    
                    if (client != null)
                    {
                        // Get remote device info - use the socket's RemoteEndPoint
                        var remoteEndPoint = client.Client.RemoteEndPoint as BluetoothEndPoint;
                        var remoteAddress = remoteEndPoint?.Address?.ToString() ?? "UNKNOWN";
                        string remoteDevice;
                        
                        try
                        {
                            remoteDevice = client.RemoteMachineName ?? remoteAddress;
                        }
                        catch
                        {
                            remoteDevice = remoteAddress;
                        }
                        
                        // Use normalized address as key (prevents duplicate connections)
                        var normalizedAddress = NormalizeAddress(remoteAddress);
                        
                        // Check if we already have a connection to this address
                        if (_connectedAddresses.ContainsKey(normalizedAddress))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                AppendLog($"[LINK] Duplicate connection from {remoteDevice} - closing");
                            });
                            client.Close();
                            continue;
                        }
                        
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[LINK] Incoming connection from: {remoteDevice}");
                        });

                        var peerConnection = new PeerConnection
                        {
                            Client = client,
                            Stream = client.GetStream(),
                            DeviceName = remoteDevice,
                            DeviceAddress = normalizedAddress,
                            IsIncoming = true
                        };

                        if (_connectedAddresses.TryAdd(normalizedAddress, true) && 
                            _activeConnections.TryAdd(normalizedAddress, peerConnection))
                        {
                            UpdateConnectionCount();
                            
                            // Send our nickname to the new peer
                            _ = SendNicknameAsync(peerConnection);
                            
                            _ = ListenForMessagesAsync(peerConnection, normalizedAddress);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[ERROR] Accept error: {ex.Message}");
                        });
                    }
                }
            }
        }

        private string NormalizeAddress(string address)
        {
            // Remove colons and convert to uppercase for consistent comparison
            return address.Replace(":", "").ToUpperInvariant();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevices = _pairedDevices.Where(d => d.IsSelected).ToList();

            if (!selectedDevices.Any())
            {
                AppendLog("[WARN] No devices selected. Check the boxes first.");
                return;
            }

            ConnectButton.IsEnabled = false;
            UpdateStatus("[STATUS] CONNECTING...");
            AppendLog($"[SYSTEM] Initiating connection to {selectedDevices.Count} device(s)...");

            foreach (var deviceVm in selectedDevices)
            {
                await ConnectToDeviceAsync(deviceVm.Device);
            }

            ConnectButton.IsEnabled = true;
            UpdateConnectionCount();
        }

        private async Task ConnectToDeviceAsync(BluetoothDeviceInfo device)
        {
            var normalizedAddress = NormalizeAddress(device.DeviceAddress.ToString());
            
            // Skip if already connected to this address
            if (_connectedAddresses.ContainsKey(normalizedAddress))
            {
                AppendLog($"[INFO] Already connected to {device.DeviceName}");
                return;
            }

            try
            {
                AppendLog($"[LINK] Connecting to {device.DeviceName}...");

                await Task.Run(() =>
                {
                    var client = new BluetoothClient();
                    
                    // Set connection timeout
                    client.Client.SendTimeout = 5000;
                    client.Client.ReceiveTimeout = 5000;
                    
                    var endpoint = new BluetoothEndPoint(device.DeviceAddress, OffGridServiceId);
                    client.Connect(endpoint);

                    var peerConnection = new PeerConnection
                    {
                        Client = client,
                        Stream = client.GetStream(),
                        DeviceName = device.DeviceName,
                        DeviceAddress = normalizedAddress,
                        IsIncoming = false
                    };

                    if (_connectedAddresses.TryAdd(normalizedAddress, true) &&
                        _activeConnections.TryAdd(normalizedAddress, peerConnection))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[LINK] ✓ Connected to {device.DeviceName}");
                            UpdateConnectionCount();
                        });

                        // Send our nickname to the new peer
                        _ = SendNicknameAsync(peerConnection);

                        // Start listening for messages from this peer
                        _ = ListenForMessagesAsync(peerConnection, normalizedAddress);
                    }
                    else
                    {
                        // Connection was already established by incoming
                        client.Close();
                    }
                });
            }
            catch (SocketException sex)
            {
                AppendLog($"[ERROR] Connection to {device.DeviceName} failed: {sex.Message}");
                AppendLog($"[HINT] Ensure {device.DeviceName} is running OffGrid and in range.");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] {device.DeviceName}: {ex.Message}");
            }
        }

        private async Task SendNicknameAsync(PeerConnection peer)
        {
            try
            {
                var nickPacket = $"{NICK_PREFIX}{_localNickname}";
                var data = Encoding.UTF8.GetBytes(nickPacket);
                if (peer.Stream != null && peer.Client.Connected)
                {
                    await peer.Stream.WriteAsync(data, 0, data.Length);
                    await peer.Stream.FlushAsync();
                }
            }
            catch { }
        }

        private async Task ListenForMessagesAsync(PeerConnection peer, string connectionKey)
        {
            var buffer = new byte[65536]; // Larger buffer for file transfers
            var messageBuffer = new StringBuilder(); // Accumulate incomplete messages
            
            try
            {
                while (peer.Client.Connected && peer.Stream != null)
                {
                    var bytesRead = await peer.Stream.ReadAsync(buffer, 0, buffer.Length);
                    
                    if (bytesRead == 0)
                    {
                        // Connection closed
                        break;
                    }

                    var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuffer.Append(data);
                    
                    // Process only complete lines (ending with \n)
                    var fullBuffer = messageBuffer.ToString();
                    var lastNewline = fullBuffer.LastIndexOf('\n');
                    
                    if (lastNewline >= 0)
                    {
                        // Extract complete messages
                        var completeData = fullBuffer.Substring(0, lastNewline + 1);
                        
                        // Keep incomplete remainder for next read
                        messageBuffer.Clear();
                        if (lastNewline < fullBuffer.Length - 1)
                        {
                            messageBuffer.Append(fullBuffer.Substring(lastNewline + 1));
                        }
                        
                        // Handle different message types
                        await ProcessReceivedDataAsync(peer, completeData);
                    }
                }
            }
            catch (IOException)
            {
                // Connection lost
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"[ERROR] Read error from {GetDisplayName(peer)}: {ex.Message}");
                });
            }
            finally
            {
                // Remove from active connections
                _activeConnections.TryRemove(connectionKey, out _);
                _connectedAddresses.TryRemove(connectionKey, out _);
                _remoteNicknames.TryRemove(connectionKey, out _);
                peer.Dispose();
                
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"[LINK] Disconnected: {GetDisplayName(peer)}");
                    UpdateConnectionCount();
                    ClearTypingIndicator();
                });
            }
        }

        private async Task ProcessReceivedDataAsync(PeerConnection peer, string data)
        {
            // Split by newlines in case multiple messages come in one packet
            var messages = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var rawMessage in messages)
            {
                var message = rawMessage.Trim();
                if (string.IsNullOrEmpty(message)) continue;

                // === MESH RELAY MESSAGE ===
                if (message.StartsWith(RELAY_PREFIX))
                {
                    await HandleRelayMessageAsync(peer, message);
                }
                // === PEER ANNOUNCEMENT ===
                else if (message.StartsWith(PEERS_PREFIX))
                {
                    HandlePeerAnnouncement(peer, message);
                }
                // === REGULAR MESSAGE (upgrade to relay format) ===
                else if (message.StartsWith(MSG_PREFIX))
                {
                    // Regular message - display it and convert to relay for forwarding
                    var content = message.Substring(MSG_PREFIX.Length);
                    Dispatcher.Invoke(() =>
                    {
                        ClearTypingIndicator();
                        AppendLog($"[{GetDisplayName(peer)}]: {content}");
                    });
                    
                    // Forward via mesh relay (create relay message)
                    var msgId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    var relayMsg = $"{RELAY_PREFIX}{msgId}|{peer.DeviceAddress}|{GetDisplayName(peer)}|*|{MAX_HOP_COUNT - 1}|{content}\n";
                    await ForwardToOthersAsync(peer.DeviceAddress, relayMsg);
                }
                else if (message.StartsWith(TYPING_PREFIX))
                {
                    // Typing indicator
                    var isTyping = message.Substring(TYPING_PREFIX.Length) == "1";
                    Dispatcher.Invoke(() =>
                    {
                        if (isTyping)
                        {
                            ShowTypingIndicator(GetDisplayName(peer));
                        }
                        else
                        {
                            ClearTypingIndicator();
                        }
                    });
                }
                else if (message.StartsWith(NICK_PREFIX))
                {
                    // Nickname update
                    var nickname = message.Substring(NICK_PREFIX.Length);
                    _remoteNicknames[peer.DeviceAddress] = nickname;
                    Dispatcher.Invoke(() =>
                    {
                        AppendLog($"[SYSTEM] {peer.DeviceName} is now known as \"{nickname}\"");
                    });
                }
                else if (message.StartsWith(FILE_PREFIX))
                {
                    // File transfer header: FILE:filename:size
                    var parts = message.Substring(FILE_PREFIX.Length).Split(':');
                    if (parts.Length >= 2)
                    {
                        var filename = parts[0];
                        var size = parts.Length > 1 ? parts[1] : "?";
                        
                        // Store pending file info for this peer
                        peer.PendingFileName = filename;
                        peer.PendingFileSize = int.TryParse(size, out var s) ? s : 0;
                        
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[FILE] Receiving '{filename}' ({size} bytes) from {GetDisplayName(peer)}...");
                        });
                    }
                }
                else if (message.StartsWith(FILE_DATA_PREFIX))
                {
                    // File data received - decode and save
                    try
                    {
                        var base64 = message.Substring(FILE_DATA_PREFIX.Length).Trim();
                        var fileData = Convert.FromBase64String(base64);
                        
                        var filename = peer.PendingFileName ?? $"received_{DateTime.Now:yyyyMMdd_HHmmss}";
                        
                        // Save to Downloads folder
                        var downloadsPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Downloads",
                            "OffGrid_" + filename
                        );
                        
                        await File.WriteAllBytesAsync(downloadsPath, fileData);
                        
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[FILE] ✓ Saved: {downloadsPath}");
                        });
                        
                        // Clear pending file info
                        peer.PendingFileName = null;
                        peer.PendingFileSize = 0;
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[ERROR] File receive failed: {ex.Message}");
                        });
                    }
                }
                // === CHUNKED FILE TRANSFER START ===
                else if (message.StartsWith(FSTART_PREFIX))
                {
                    // Parse: FSTART:filename|origSize|compSize|chunks|checksum
                    var payload = message.Substring(FSTART_PREFIX.Length);
                    var parts = payload.Split('|');
                    if (parts.Length >= 5)
                    {
                        var state = new FileTransferState
                        {
                            FileName = parts[0],
                            OriginalSize = long.TryParse(parts[1], out var os) ? os : 0,
                            CompressedSize = long.TryParse(parts[2], out var cs) ? cs : 0,
                            TotalChunks = int.TryParse(parts[3], out var tc) ? tc : 0,
                            Checksum = parts[4],
                            StartTime = DateTime.Now
                        };
                        state.Chunks = new byte[state.TotalChunks][];
                        
                        _incomingTransfers[peer.DeviceAddress] = state;
                        
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[FILE] Receiving '{state.FileName}' ({state.OriginalSize:N0} bytes, {state.TotalChunks} chunks) from {GetDisplayName(peer)}...");
                        });
                    }
                }
                // === CHUNKED FILE TRANSFER CHUNK ===
                else if (message.StartsWith(FCHUNK_PREFIX))
                {
                    // Parse: FCHUNK:index|base64data
                    var payload = message.Substring(FCHUNK_PREFIX.Length);
                    var sepIndex = payload.IndexOf('|');
                    if (sepIndex > 0 && _incomingTransfers.TryGetValue(peer.DeviceAddress, out var state))
                    {
                        var indexStr = payload.Substring(0, sepIndex);
                        var base64 = payload.Substring(sepIndex + 1);
                        
                        if (int.TryParse(indexStr, out var chunkIndex) && chunkIndex < state.TotalChunks)
                        {
                            state.Chunks[chunkIndex] = Convert.FromBase64String(base64);
                            state.ChunksReceived++;
                            
                            // Progress update
                            if (state.ChunksReceived % 10 == 0 || state.ChunksReceived == state.TotalChunks)
                            {
                                var pct = (int)(((double)state.ChunksReceived / state.TotalChunks) * 100);
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateStatus($"[STATUS] RECEIVING {pct}%");
                                });
                            }
                        }
                    }
                }
                // === CHUNKED FILE TRANSFER END ===
                else if (message.StartsWith(FEND_PREFIX))
                {
                    // Reassemble and decompress
                    if (_incomingTransfers.TryRemove(peer.DeviceAddress, out var state))
                    {
                        try
                        {
                            // Combine chunks
                            using var compressedStream = new MemoryStream();
                            foreach (var chunk in state.Chunks)
                            {
                                if (chunk != null)
                                    compressedStream.Write(chunk, 0, chunk.Length);
                            }
                            compressedStream.Position = 0;
                            
                            // Decompress
                            using var decompressedStream = new MemoryStream();
                            using (var gzip = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Decompress))
                            {
                                gzip.CopyTo(decompressedStream);
                            }
                            var fileData = decompressedStream.ToArray();
                            
                            // Save to Downloads
                            var downloadsPath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                "Downloads",
                                "OffGrid_" + state.FileName
                            );
                            
                            await File.WriteAllBytesAsync(downloadsPath, fileData);
                            
                            var elapsed = DateTime.Now - state.StartTime;
                            
                            Dispatcher.Invoke(() =>
                            {
                                UpdateStatus($"[STATUS] {_activeConnections.Count} CONNECTED");
                                AppendLog($"[FILE] ✓ Saved: {downloadsPath}");
                                AppendLog($"[FILE] {state.OriginalSize:N0} bytes in {elapsed.TotalSeconds:F1}s");
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                AppendLog($"[ERROR] File assembly failed: {ex.Message}");
                            });
                        }
                    }
                }
                else
                {
                    // Legacy message without prefix (backwards compatibility)
                    // Skip if it looks like base64 data (very long string with no spaces)
                    if (message.Length > 500 && !message.Contains(' '))
                    {
                        // Likely corrupted or unhandled file data - ignore
                        continue;
                    }
                    
                    Dispatcher.Invoke(() =>
                    {
                        AppendLog($"[{GetDisplayName(peer)}]: {message}");
                    });
                }
            }
        }

        private string GetDisplayName(PeerConnection peer)
        {
            if (_remoteNicknames.TryGetValue(peer.DeviceAddress, out var nick) && !string.IsNullOrEmpty(nick))
            {
                return nick;
            }
            return peer.DeviceName;
        }

        private void ShowTypingIndicator(string name)
        {
            TypingIndicator.Text = $"{name} is typing...";
            TypingIndicator.Visibility = Visibility.Visible;
            
            // Auto-hide after 3 seconds
            _typingIndicatorCts?.Cancel();
            _typingIndicatorCts = new CancellationTokenSource();
            var token = _typingIndicatorCts.Token;
            
            Task.Delay(3000, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    Dispatcher.Invoke(() => ClearTypingIndicator());
                }
            });
        }

        private void ClearTypingIndicator()
        {
            TypingIndicator.Visibility = Visibility.Collapsed;
            TypingIndicator.Text = "";
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void MessageInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendMessage();
                e.Handled = true;
            }
        }

        private void MessageInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Send typing indicator (throttled to once per second)
            if ((DateTime.Now - _lastTypingSent).TotalSeconds >= 1 && !string.IsNullOrEmpty(MessageInput.Text))
            {
                _lastTypingSent = DateTime.Now;
                _ = BroadcastTypingAsync(true);
            }
        }

        private async Task BroadcastTypingAsync(bool isTyping)
        {
            var packet = $"{TYPING_PREFIX}{(isTyping ? "1" : "0")}\n";
            var data = Encoding.UTF8.GetBytes(packet);
            
            foreach (var kvp in _activeConnections)
            {
                try
                {
                    if (kvp.Value.Stream != null && kvp.Value.Client.Connected)
                    {
                        await kvp.Value.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch { }
            }
        }

        private async void SendMessage()
        {
            var message = MessageInput.Text.Trim();
            
            if (string.IsNullOrEmpty(message))
                return;

            MessageInput.Text = string.Empty;

            // Handle commands
            if (message.StartsWith("/"))
            {
                await HandleCommandAsync(message);
                return;
            }

            if (!_activeConnections.Any())
            {
                AppendLog("[WARN] No active connections. Connect to a device first.");
                return;
            }

            AppendLog($"[{_localNickname}]: {message}");

            // Send with MSG: prefix
            var packet = $"{MSG_PREFIX}{message}\n";
            var messageBytes = Encoding.UTF8.GetBytes(packet);
            var failedConnections = new System.Collections.Generic.List<string>();

            foreach (var kvp in _activeConnections)
            {
                try
                {
                    if (kvp.Value.Stream != null && kvp.Value.Client.Connected)
                    {
                        await kvp.Value.Stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                        await kvp.Value.Stream.FlushAsync();
                    }
                    else
                    {
                        failedConnections.Add(kvp.Key);
                    }
                }
                catch (Exception ex)
                {
                    failedConnections.Add(kvp.Key);
                    AppendLog($"[ERROR] Failed to send to {GetDisplayName(kvp.Value)}: {ex.Message}");
                }
            }

            // Clean up failed connections
            foreach (var key in failedConnections)
            {
                if (_activeConnections.TryRemove(key, out var peer))
                {
                    _connectedAddresses.TryRemove(key, out _);
                    peer.Dispose();
                }
            }

            if (failedConnections.Any())
            {
                UpdateConnectionCount();
            }

            // Clear typing indicator
            _ = BroadcastTypingAsync(false);
        }

        private async Task HandleCommandAsync(string command)
        {
            var parts = command.Split(' ', 2);
            var cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case "/clear":
                    ChatLog.Text = "[SYSTEM] Chat cleared.\n";
                    AppendLog("[SYSTEM] Commands: /clear /nick <name> /sendfile /peers");
                    break;

                case "/nick":
                    if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        var oldNick = _localNickname;
                        _localNickname = parts[1].Trim();
                        AppendLog($"[SYSTEM] Nickname changed: {oldNick} → {_localNickname}");
                        
                        // Broadcast nickname to all peers
                        var nickPacket = $"{NICK_PREFIX}{_localNickname}\n";
                        var data = Encoding.UTF8.GetBytes(nickPacket);
                        
                        foreach (var kvp in _activeConnections)
                        {
                            try
                            {
                                if (kvp.Value.Stream != null && kvp.Value.Client.Connected)
                                {
                                    await kvp.Value.Stream.WriteAsync(data, 0, data.Length);
                                }
                            }
                            catch { }
                        }
                        
                        // Also trigger peer announcement so others learn new name
                        BroadcastPeerList();
                    }
                    else
                    {
                        AppendLog("[SYSTEM] Usage: /nick <nickname>");
                    }
                    break;

                case "/sendfile":
                    await SendFileAsync();
                    break;
                    
                case "/peers":
                    ShowPeerList();
                    break;

                default:
                    AppendLog($"[SYSTEM] Unknown command: {cmd}");
                    AppendLog("[SYSTEM] Available: /clear /nick <name> /sendfile /peers");
                    break;
            }
        }
        
        private void ShowPeerList()
        {
            AppendLog("╔═══════════════════════════════════════╗");
            AppendLog("║         MESH NETWORK STATUS           ║");
            AppendLog("╠═══════════════════════════════════════╣");
            AppendLog($"║ You: {_localNickname} ({_localAddress})");
            AppendLog("╠═══════════════════════════════════════╣");
            
            // Direct connections
            AppendLog($"║ DIRECT CONNECTIONS ({_activeConnections.Count}):");
            if (_activeConnections.Any())
            {
                foreach (var kvp in _activeConnections)
                {
                    var nick = GetDisplayName(kvp.Value);
                    var direction = kvp.Value.IsIncoming ? "←IN" : "OUT→";
                    AppendLog($"║  ● {nick} [{direction}]");
                }
            }
            else
            {
                AppendLog("║  (none)");
            }
            
            // Mesh peers (indirect)
            AppendLog($"║ MESH PEERS ({_knownPeers.Count}):");
            if (_knownPeers.Any())
            {
                foreach (var kvp in _knownPeers)
                {
                    var peer = kvp.Value;
                    AppendLog($"║  ○ {peer.Nickname} (via {peer.ViaName})");
                }
            }
            else
            {
                AppendLog("║  (none discovered yet)");
            }
            
            AppendLog("╚═══════════════════════════════════════╝");
        }

        private async Task SendFileAsync()
        {
            if (!_activeConnections.Any())
            {
                AppendLog("[WARN] No active connections. Connect to a device first.");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Select file to send",
                Filter = "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                var filepath = dialog.FileName;
                var filename = Path.GetFileName(filepath);
                var fileInfo = new FileInfo(filepath);
                var originalSize = fileInfo.Length;

                AppendLog($"[FILE] Preparing '{filename}' ({originalSize:N0} bytes)...");

                try
                {
                    // Read and compress file
                    var fileData = await File.ReadAllBytesAsync(filepath);
                    byte[] compressedData;
                    
                    using (var outputStream = new MemoryStream())
                    {
                        using (var gzip = new System.IO.Compression.GZipStream(outputStream, System.IO.Compression.CompressionLevel.Optimal))
                        {
                            await gzip.WriteAsync(fileData, 0, fileData.Length);
                        }
                        compressedData = outputStream.ToArray();
                    }
                    
                    var compressionRatio = (1.0 - (double)compressedData.Length / originalSize) * 100;
                    AppendLog($"[FILE] Compressed: {compressedData.Length:N0} bytes ({compressionRatio:F1}% reduction)");
                    
                    // Calculate checksum (simple hash)
                    var checksum = Convert.ToBase64String(
                        System.Security.Cryptography.SHA256.HashData(fileData)
                    ).Substring(0, 8);
                    
                    // Split into chunks
                    var totalChunks = (int)Math.Ceiling((double)compressedData.Length / CHUNK_SIZE);
                    
                    // Send to all connections
                    foreach (var kvp in _activeConnections)
                    {
                        try
                        {
                            if (kvp.Value.Stream == null || !kvp.Value.Client.Connected)
                                continue;
                            
                            var stream = kvp.Value.Stream;
                            
                            // Send FSTART header
                            var header = $"{FSTART_PREFIX}{filename}|{originalSize}|{compressedData.Length}|{totalChunks}|{checksum}\n";
                            var headerBytes = Encoding.UTF8.GetBytes(header);
                            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                            await stream.FlushAsync();
                            
                            await Task.Delay(50);
                            
                            // Send chunks
                            for (int i = 0; i < totalChunks; i++)
                            {
                                var offset = i * CHUNK_SIZE;
                                var length = Math.Min(CHUNK_SIZE, compressedData.Length - offset);
                                var chunkData = new byte[length];
                                Array.Copy(compressedData, offset, chunkData, 0, length);
                                
                                var chunkBase64 = Convert.ToBase64String(chunkData);
                                var chunkPacket = $"{FCHUNK_PREFIX}{i}|{chunkBase64}\n";
                                var chunkBytes = Encoding.UTF8.GetBytes(chunkPacket);
                                
                                await stream.WriteAsync(chunkBytes, 0, chunkBytes.Length);
                                await stream.FlushAsync();
                                
                                // Progress update every 10 chunks or on last
                                if (i % 10 == 0 || i == totalChunks - 1)
                                {
                                    var pct = (int)(((double)(i + 1) / totalChunks) * 100);
                                    Dispatcher.Invoke(() =>
                                    {
                                        UpdateStatus($"[STATUS] SENDING {pct}%");
                                    });
                                }
                                
                                await Task.Delay(10); // Small delay between chunks
                            }
                            
                            // Send FEND
                            var endPacket = $"{FEND_PREFIX}success|{checksum}\n";
                            var endBytes = Encoding.UTF8.GetBytes(endPacket);
                            await stream.WriteAsync(endBytes, 0, endBytes.Length);
                            await stream.FlushAsync();
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[ERROR] Failed to send to {GetDisplayName(kvp.Value)}: {ex.Message}");
                        }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus($"[STATUS] {_activeConnections.Count} CONNECTED");
                    });
                    AppendLog($"[FILE] ✓ Sent '{filename}' ({totalChunks} chunks)");
                }
                catch (Exception ex)
                {
                    AppendLog($"[ERROR] File send failed: {ex.Message}");
                }
            }
        }

        private async Task ReceiveFileAsync(PeerConnection peer, string filename, int expectedSize)
        {
            try
            {
                // Read the file data packet
                var buffer = new byte[expectedSize * 2 + 1024]; // Base64 is ~1.37x larger
                var bytesRead = await peer.Stream!.ReadAsync(buffer, 0, buffer.Length);
                
                if (bytesRead > 0)
                {
                    var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    
                    if (data.StartsWith(FILE_DATA_PREFIX))
                    {
                        var base64 = data.Substring(FILE_DATA_PREFIX.Length).Trim();
                        var fileData = Convert.FromBase64String(base64);
                        
                        // Save to Downloads folder
                        var downloadsPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Downloads",
                            "OffGrid_" + filename
                        );
                        
                        await File.WriteAllBytesAsync(downloadsPath, fileData);
                        
                        Dispatcher.Invoke(() =>
                        {
                            AppendLog($"[FILE] ✓ Saved: {downloadsPath}");
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"[ERROR] File receive failed: {ex.Message}");
                });
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadPairedDevicesAsync();
        }

        private void AppendLog(string message)
        {
            ChatLog.Text += message + "\n";
            ChatScrollViewer.ScrollToEnd();
        }

        private void UpdateStatus(string status)
        {
            StatusText.Text = status;
        }

        private void UpdateConnectionCount()
        {
            Dispatcher.Invoke(() =>
            {
                var count = _activeConnections.Count;
                ConnectionCount.Text = $"[{count} ACTIVE LINK{(count != 1 ? "S" : "")}]";
                
                if (count > 0)
                {
                    UpdateStatus($"[STATUS] {count} CONNECTED");
                }
                else
                {
                    UpdateStatus("[STATUS] STANDBY");
                }
            });
        }

        #region Mesh Relay Methods

        /// <summary>
        /// Handle an incoming relay message - check for duplicates, display if for us, forward if not
        /// </summary>
        private async Task HandleRelayMessageAsync(PeerConnection fromPeer, string message)
        {
            try
            {
                // Parse: RELAY:msgId|fromAddr|fromNick|toAddr|hopCount|content
                var payload = message.Substring(RELAY_PREFIX.Length);
                var parts = payload.Split('|', 6);
                
                if (parts.Length < 6)
                {
                    return; // Malformed message
                }

                var msgId = parts[0];
                var fromAddress = parts[1];
                var fromNick = parts[2];
                var toAddress = parts[3]; // "*" for broadcast, or specific address
                var hopCountStr = parts[4];
                var content = parts[5];
                
                // Check if we've already seen this message
                if (_seenMessageIds.ContainsKey(msgId))
                {
                    return; // Duplicate, skip
                }
                
                // Mark as seen
                _seenMessageIds[msgId] = DateTime.Now;
                
                // Cleanup old message IDs periodically (keep last 5 minutes)
                CleanupSeenMessages();
                
                // Parse hop count
                if (!int.TryParse(hopCountStr, out var hopCount) || hopCount <= 0)
                {
                    return; // Expired or invalid
                }

                // Is this message for us or is it a broadcast?
                var isForUs = toAddress == "*" || toAddress == _localAddress;
                var isFromUs = fromAddress == _localAddress;
                
                if (isFromUs)
                {
                    return; // Don't process our own messages that came back
                }

                // Display the message if it's for us (or broadcast)
                if (isForUs)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ClearTypingIndicator();
                        // Show with mesh indicator if it didn't come directly from sender
                        var viaText = fromPeer.DeviceAddress != fromAddress ? " [via mesh]" : "";
                        AppendLog($"[{fromNick}]{viaText}: {content}");
                    });
                }

                // Forward to others (if hop count allows)
                var newHopCount = hopCount - 1;
                if (newHopCount > 0)
                {
                    var forwardMsg = $"{RELAY_PREFIX}{msgId}|{fromAddress}|{fromNick}|{toAddress}|{newHopCount}|{content}\n";
                    await ForwardToOthersAsync(fromPeer.DeviceAddress, forwardMsg);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"[ERROR] Relay parse error: {ex.Message}");
                });
            }
        }

        /// <summary>
        /// Handle peer announcement - learn about peers reachable via this connection
        /// </summary>
        private void HandlePeerAnnouncement(PeerConnection fromPeer, string message)
        {
            try
            {
                // Parse: PEERS:nick1@addr1,nick2@addr2,...
                var payload = message.Substring(PEERS_PREFIX.Length);
                
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return;
                }
                
                var peerEntries = payload.Split(',', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var entry in peerEntries)
                {
                    var parts = entry.Split('@', 2);
                    if (parts.Length != 2) continue;
                    
                    var nick = parts[0];
                    var addr = parts[1];
                    
                    // Skip if it's our own address
                    if (addr == _localAddress) continue;
                    
                    // Skip if we're directly connected
                    if (_connectedAddresses.ContainsKey(addr)) continue;
                    
                    // Add or update as indirect peer (reachable via fromPeer)
                    _knownPeers[addr] = new MeshPeer
                    {
                        Address = addr,
                        Nickname = nick,
                        ViaAddress = fromPeer.DeviceAddress,
                        ViaName = GetDisplayName(fromPeer),
                        LastSeen = DateTime.Now,
                        IsDirect = false
                    };
                }

                // Update UI to show mesh peers
                Dispatcher.Invoke(UpdateMeshPeerCount);
            }
            catch
            {
                // Ignore parse errors in peer announcements
            }
        }

        /// <summary>
        /// Forward a message to all connections except the source
        /// </summary>
        private async Task ForwardToOthersAsync(string excludeAddress, string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            
            foreach (var kvp in _activeConnections)
            {
                // Don't send back to the source
                if (kvp.Key == excludeAddress) continue;
                
                try
                {
                    if (kvp.Value.Stream != null && kvp.Value.Client.Connected)
                    {
                        await kvp.Value.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch
                {
                    // Ignore errors when forwarding
                }
            }
        }

        /// <summary>
        /// Broadcast our peer list to all connections
        /// </summary>
        private void BroadcastPeerList()
        {
            if (_activeConnections.IsEmpty) return;
            
            try
            {
                // Build peer list: ourselves + all our direct connections
                var peerList = new System.Collections.Generic.List<string>();
                
                // Add ourselves
                peerList.Add($"{_localNickname}@{_localAddress}");
                
                // Add all direct connections
                foreach (var kvp in _activeConnections)
                {
                    var nick = GetDisplayName(kvp.Value);
                    peerList.Add($"{nick}@{kvp.Key}");
                }
                
                // Also include known mesh peers (so they propagate further)
                foreach (var kvp in _knownPeers)
                {
                    peerList.Add($"{kvp.Value.Nickname}@{kvp.Key}");
                }
                
                var announcement = $"{PEERS_PREFIX}{string.Join(",", peerList)}\n";
                var data = Encoding.UTF8.GetBytes(announcement);
                
                // Send to all connections
                foreach (var kvp in _activeConnections)
                {
                    try
                    {
                        if (kvp.Value.Stream != null && kvp.Value.Client.Connected)
                        {
                            kvp.Value.Stream.Write(data, 0, data.Length);
                        }
                    }
                    catch
                    {
                        // Ignore errors in announcements
                    }
                }
            }
            catch
            {
                // Ignore errors in broadcast
            }
        }

        /// <summary>
        /// Clean up old seen message IDs (older than 5 minutes)
        /// </summary>
        private void CleanupSeenMessages()
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            var oldKeys = _seenMessageIds.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldKeys)
            {
                _seenMessageIds.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Update the UI to show both direct and mesh peer counts
        /// </summary>
        private void UpdateMeshPeerCount()
        {
            var directCount = _activeConnections.Count;
            var meshCount = _knownPeers.Count;
            
            if (meshCount > 0)
            {
                ConnectionCount.Text = $"[{directCount} DIRECT + {meshCount} MESH]";
            }
            else
            {
                ConnectionCount.Text = $"[{directCount} ACTIVE LINK{(directCount != 1 ? "S" : "")}]";
            }
        }

        #endregion

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Stop peer announcement timer
            _peerAnnounceTimer?.Dispose();
            
            // Stop listener
            _listenerCts?.Cancel();
            _listener?.Stop();

            // Close all connections
            foreach (var connection in _activeConnections.Values)
            {
                connection.Dispose();
            }
            _activeConnections.Clear();
            _connectedAddresses.Clear();
            _knownPeers.Clear();
            _seenMessageIds.Clear();
        }
    }

    /// <summary>
    /// Represents an active peer connection (incoming or outgoing)
    /// </summary>
    public class PeerConnection : IDisposable
    {
        public BluetoothClient Client { get; set; } = null!;
        public NetworkStream? Stream { get; set; }
        public string DeviceName { get; set; } = "UNKNOWN";
        public string DeviceAddress { get; set; } = "";
        public bool IsIncoming { get; set; }
        
        // Pending file transfer info
        public string? PendingFileName { get; set; }
        public int PendingFileSize { get; set; }

        public void Dispose()
        {
            try
            {
                Stream?.Close();
                Stream?.Dispose();
                Client?.Close();
                Client?.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// Represents a peer discovered via mesh (not directly connected)
    /// </summary>
    public class MeshPeer
    {
        public string Address { get; set; } = "";
        public string Nickname { get; set; } = "UNKNOWN";
        public string ViaAddress { get; set; } = "";  // Address of the relay node
        public string ViaName { get; set; } = "";     // Name of the relay node
        public DateTime LastSeen { get; set; }
        public bool IsDirect { get; set; }
    }

    /// <summary>
    /// Tracks state of an incoming chunked file transfer
    /// </summary>
    public class FileTransferState
    {
        public string FileName { get; set; } = "";
        public long OriginalSize { get; set; }
        public long CompressedSize { get; set; }
        public int TotalChunks { get; set; }
        public string Checksum { get; set; } = "";
        public byte[][] Chunks { get; set; } = Array.Empty<byte[]>();
        public int ChunksReceived { get; set; }
        public DateTime StartTime { get; set; }
    }

    /// <summary>
    /// Extension methods for BluetoothDeviceInfo to add selection state
    /// </summary>
    public static class BluetoothDeviceExtensions
    {
        private static readonly ConcurrentDictionary<BluetoothAddress, bool> _selectionStates = new();

        public static bool IsSelected(this BluetoothDeviceInfo device)
        {
            return _selectionStates.TryGetValue(device.DeviceAddress, out var selected) && selected;
        }

        public static void SetSelected(this BluetoothDeviceInfo device, bool selected)
        {
            _selectionStates[device.DeviceAddress] = selected;
        }
    }

    /// <summary>
    /// Wrapper class for device items in the UI
    /// </summary>
    public class DeviceViewModel : INotifyPropertyChanged
    {
        public BluetoothDeviceInfo Device { get; }
        
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    Device.SetSelected(value);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public string DisplayName => Device.DeviceName ?? Device.DeviceAddress.ToString();
        public string Address => Device.DeviceAddress.ToString();

        public DeviceViewModel(BluetoothDeviceInfo device)
        {
            Device = device;
            _isSelected = device.IsSelected();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
