using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using AutomationCondition = System.Windows.Automation.Condition;

namespace ScreenAutomationApp
{
    public partial class MainWindow : System.Windows.Window
    {
        private sealed record StreamingSettings(string Name, int Fps, long JpegQuality, TimeSpan FullRefreshInterval)
        {
            public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(Math.Max(33, 1000 / Fps));

            public static StreamingSettings LowBandwidth { get; } = new("Low bandwidth", 5, 45, TimeSpan.FromSeconds(8));
            public static StreamingSettings Balanced { get; } = new("Balanced", 12, 65, TimeSpan.FromSeconds(10));
            public static StreamingSettings HighQuality { get; } = new("High quality", 8, 85, TimeSpan.FromSeconds(12));
            public static StreamingSettings Fast { get; } = new("Fast", 20, 55, TimeSpan.FromSeconds(6));
            public static StreamingSettings Localhost { get; } = new("Localhost", 24, 90, TimeSpan.FromSeconds(4));
        }

        // Remote mode components
        private NetworkClient? _networkClient;
        private ScreenChangeDetector? _changeDetector;
        private DispatcherTimer? _screenCaptureTimer;
        private Bitmap? _remoteScreenBitmap;
        private bool _isServerMode = false;
        private bool _isClientMode = false;
        private int _remoteScreenWidth = 0;
        private int _remoteScreenHeight = 0;
        private Screen? _selectedScreen;
        private NotifyIcon? _trayIcon;
        private readonly DispatcherTimer _metricsTimer;
        private readonly DispatcherTimer _pingTimer;
        private StreamingSettings _streamingSettings = StreamingSettings.Balanced;
        private bool _isSendingFrame;
        private DateTime _lastFullFrameSentUtc = DateTime.MinValue;
        private int _renderedFramesThisSecond;
        private int _receivedBytesThisSecond;
        private int _lastLatencyMs = -1;
        private bool _isInitializingUi = true;

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
        private const uint MOUSEEVENTF_LEFTUP = 0x04;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x08;
        private const uint MOUSEEVENTF_RIGHTUP = 0x10;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public MainWindow()
        {
            InitializeComponent();
            _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _metricsTimer.Tick += MetricsTimer_Tick;
            _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pingTimer.Tick += PingTimer_Tick;
            SetupRadioButtons();
            SetupTrayIcon();
            _isInitializingUi = false;
            UpdateStatus("Ready - Select a mode to begin");
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Application.ResourceAssembly.Location),
                Text = "Remote Desktop",
                Visible = false
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Show", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("Disconnect", null, (s, e) => Disconnect());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => ShowWindow();
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            _trayIcon.Visible = false;
        }

        private void MinimizeToTray()
        {
            Hide();
            _trayIcon.Visible = true;
        }

        private void Disconnect()
        {
            _networkClient?.Disconnect();
            _screenCaptureTimer?.Stop();
            _isServerMode = false;
            _isClientMode = false;
            
            // Reset UI
            ModeSelectionPanel.Visibility = Visibility.Visible;
            RemoteConnectionPanel.Visibility = Visibility.Collapsed;
            ServerModePanel.Visibility = Visibility.Collapsed;
            ScreenDisplayPanel.Visibility = Visibility.Collapsed;
            StopSharingButton.Visibility = Visibility.Collapsed;
            _metricsTimer.Stop();
            _pingTimer.Stop();
            
            ShowWindow();
            UpdateStatus("Disconnected");
        }

        private void ExitApplication()
        {
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && (_isServerMode || _isClientMode))
            {
                MinimizeToTray();
            }
        }

        private void SetupRadioButtons()
        {
            DirectIpRadioButton.Checked += DirectIpRadioButton_Checked;
            ConnectionCodeRadioButton.Checked += ConnectionCodeRadioButton_Checked;
            DirectServerRadioButton.Checked += DirectServerRadioButton_Checked;
            RouterServerRadioButton.Checked += RouterServerRadioButton_Checked;
        }

        // Mode Selection
        private void RemoteModeButton_Click(object sender, RoutedEventArgs e)
        {
            ModeSelectionPanel.Visibility = Visibility.Collapsed;
            RemoteConnectionPanel.Visibility = Visibility.Visible;
            UpdateStatus("Remote mode selected");
        }

        private void ServerModeButton_Click(object sender, RoutedEventArgs e)
        {
            ModeSelectionPanel.Visibility = Visibility.Collapsed;
            ServerModePanel.Visibility = Visibility.Visible;
            UpdateStatus("Server mode selected");
        }

        private void BackToModeButton_Click(object sender, RoutedEventArgs e)
        {
            ModeSelectionPanel.Visibility = Visibility.Visible;
            RemoteConnectionPanel.Visibility = Visibility.Collapsed;
            ServerModePanel.Visibility = Visibility.Collapsed;
            ScreenDisplayPanel.Visibility = Visibility.Collapsed;
            UpdateStatus("Back to mode selection");
        }

        // Remote Connection Method Radio Buttons
        private void DirectIpRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            DirectIpPanel.Visibility = Visibility.Visible;
            ConnectionCodePanel.Visibility = Visibility.Collapsed;
        }

        private void ConnectionCodeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            DirectIpPanel.Visibility = Visibility.Collapsed;
            ConnectionCodePanel.Visibility = Visibility.Visible;
        }

        // Server Type Radio Buttons
        private void DirectServerRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            DirectServerPanel.Visibility = Visibility.Visible;
            RouterServerPanel.Visibility = Visibility.Collapsed;
            ConnectionCodeDisplayPanel.Visibility = Visibility.Collapsed;
            StopSharingButton.Visibility = Visibility.Collapsed;
        }

        private void StopSharingButton_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void QualityPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingUi)
                return;

            int selectedIndex = sender == ViewerQualityPresetComboBox
                ? ViewerQualityPresetComboBox.SelectedIndex
                : ServerQualityPresetComboBox.SelectedIndex;

            ApplyStreamingPreset(selectedIndex, sendToPeer: sender == ViewerQualityPresetComboBox);
        }

        private async void ApplyStreamingPreset(int selectedIndex, bool sendToPeer)
        {
            _streamingSettings = GetStreamingSettings(selectedIndex);

            if (_screenCaptureTimer != null)
            {
                _screenCaptureTimer.Interval = _streamingSettings.FrameInterval;
            }

            if (StatusText != null)
            {
                UpdateStatus($"Streaming quality: {_streamingSettings.Name}");
            }

            if (ServerQualityPresetComboBox != null && ServerQualityPresetComboBox.SelectedIndex != selectedIndex)
            {
                ServerQualityPresetComboBox.SelectedIndex = selectedIndex;
            }

            if (ViewerQualityPresetComboBox != null && ViewerQualityPresetComboBox.SelectedIndex != selectedIndex)
            {
                ViewerQualityPresetComboBox.SelectedIndex = selectedIndex;
            }

            if (sendToPeer && _isClientMode && _networkClient != null && _networkClient.IsConnected)
            {
                await _networkClient.SendMessageAsync(new NetworkMessage
                {
                    Type = MessageType.QualityPreset,
                    Data = BitConverter.GetBytes(selectedIndex)
                });
            }
        }

        private static StreamingSettings GetStreamingSettings(int selectedIndex)
        {
            return selectedIndex switch
            {
                0 => StreamingSettings.LowBandwidth,
                2 => StreamingSettings.HighQuality,
                3 => StreamingSettings.Fast,
                4 => StreamingSettings.Localhost,
                _ => StreamingSettings.Balanced
            };
        }

        private static bool IsLocalhost(string host)
        {
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        }

        private void RouterServerRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            DirectServerPanel.Visibility = Visibility.Collapsed;
            RouterServerPanel.Visibility = Visibility.Visible;
            ConnectionCodeDisplayPanel.Visibility = Visibility.Collapsed;
        }

        // Direct Server Mode
        private async void StartDirectServerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int port = int.Parse(DirectServerPortTextBox.Text);
                UpdateStatus("Starting direct server mode...");
                
                _isServerMode = true;
                _selectedScreen = Screen.PrimaryScreen;
                _changeDetector = new ScreenChangeDetector();
                _networkClient = new NetworkClient();

                _networkClient.MessageReceived += NetworkClient_MessageReceived;
                _networkClient.BytesReceived += bytes => _receivedBytesThisSecond += bytes;
                _networkClient.Connected += () => 
                {
                    UpdateStatus("Client connected");
                    ServerModePanel.Visibility = Visibility.Visible;
                    ScreenDisplayPanel.Visibility = Visibility.Collapsed;
                    StopSharingButton.Visibility = Visibility.Visible;
                    ServerStatusText.Text = "Client connected. This computer is being shared.";
                    ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;
                };
                _networkClient.Disconnected += () => 
                {
                    UpdateStatus("Client disconnected");
                    ServerModePanel.Visibility = Visibility.Visible;
                    ScreenDisplayPanel.Visibility = Visibility.Collapsed;
                    StopSharingButton.Visibility = Visibility.Collapsed;
                    ShowWindow();
                };
                _networkClient.Error += (ex) => UpdateStatus($"Error: {ex.Message}");

                // Start listening for connections
                await _networkClient.StartServerAsync(port);

                // Start screen capture timer
                _screenCaptureTimer = new DispatcherTimer { Interval = _streamingSettings.FrameInterval };
                _screenCaptureTimer.Tick += ScreenCaptureTimer_Tick;
                _screenCaptureTimer.Start();

                ServerStatusText.Text = $"Direct server running on port {port}";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;

                UpdateStatus($"Direct server started on port {port}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error starting server: {ex.Message}");
            }
        }

        // Router Server Mode
        private async void RegisterWithRouterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string routerUrl = RouterServerTextBox2.Text;
                var parts = routerUrl.Split(':');
                string host = parts[0];
                int port = parts.Length > 1 ? int.Parse(parts[1]) : 9000;

                UpdateStatus("Registering with router server...");
                
                _isServerMode = true;
                _selectedScreen = Screen.PrimaryScreen;
                _changeDetector = new ScreenChangeDetector();
                _networkClient = new NetworkClient();

                _networkClient.MessageReceived += NetworkClient_MessageReceived;
                _networkClient.BytesReceived += bytes => _receivedBytesThisSecond += bytes;
                _networkClient.Connected += () => 
                {
                    UpdateStatus("Client connected");
                    ServerModePanel.Visibility = Visibility.Visible;
                    ScreenDisplayPanel.Visibility = Visibility.Collapsed;
                    StopSharingButton.Visibility = Visibility.Visible;
                    ServerStatusText.Text = "Client connected. This computer is being shared.";
                    ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;
                };
                _networkClient.Disconnected += () => 
                {
                    UpdateStatus("Client disconnected");
                    ServerModePanel.Visibility = Visibility.Visible;
                    ScreenDisplayPanel.Visibility = Visibility.Collapsed;
                    StopSharingButton.Visibility = Visibility.Collapsed;
                    ShowWindow();
                };
                _networkClient.Error += (ex) => UpdateStatus($"Error: {ex.Message}");

                // Request connection code from router server
                string connectionCode = await RequestConnectionCodeFromRouter(host, port);
                ConnectionCodeText.Text = connectionCode;
                ConnectionCodeDisplayPanel.Visibility = Visibility.Visible;

                // Start listening for connections
                await _networkClient.StartServerAsync(8888);

                // Start screen capture timer
                _screenCaptureTimer = new DispatcherTimer { Interval = _streamingSettings.FrameInterval };
                _screenCaptureTimer.Tick += ScreenCaptureTimer_Tick;
                _screenCaptureTimer.Start();

                ServerStatusText.Text = $"Server registered with router. Code: {connectionCode}";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;

                UpdateStatus($"Server registered. Share code: {connectionCode}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error registering with router: {ex.Message}");
            }
        }

        private async Task<string> RequestConnectionCodeFromRouter(string host, int port)
        {
            using (HttpClient client = new HttpClient())
            {
                string hostEndpoint = GetHostEndpointForRouter(host);
                var request = new
                {
                    hostId = Guid.NewGuid().ToString(),
                    hostName = Environment.MachineName,
                    hostEndpoint
                };

                var response = await client.PostAsync($"http://{host}:{port}/register",
                    new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(content);
                return document.RootElement.GetProperty("connectionCode").GetString() ?? throw new InvalidOperationException("Router did not return a connection code.");
            }
        }

        private string GetHostEndpointForRouter(string routerHost)
        {
            if (routerHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || routerHost == "127.0.0.1")
            {
                return "localhost:8888";
            }

            string localIp = Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?.ToString() ?? "127.0.0.1";

            return $"{localIp}:8888";
        }

        private async void ScreenCaptureTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isServerMode || _selectedScreen == null || _networkClient == null || !_networkClient.IsConnected)
                return;

            if (_isSendingFrame)
                return;

            try
            {
                _isSendingFrame = true;

                // Capture screen
                Bitmap bitmap = new Bitmap(_selectedScreen.Bounds.Width, _selectedScreen.Bounds.Height, PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(_selectedScreen.Bounds.X, _selectedScreen.Bounds.Y, 0, 0,
                        new System.Drawing.Size(_selectedScreen.Bounds.Width, _selectedScreen.Bounds.Height),
                        CopyPixelOperation.SourceCopy);
                }

                bool forceFullFrame = DateTime.UtcNow - _lastFullFrameSentUtc >= _streamingSettings.FullRefreshInterval;
                var deltas = _changeDetector!.DetectChanges(bitmap, forceFullFrame, _streamingSettings.JpegQuality);

                // Send deltas to client
                foreach (var delta in deltas)
                {
                    bool isFullFrame = delta.X == 0 &&
                                       delta.Y == 0 &&
                                       delta.Width == bitmap.Width &&
                                       delta.Height == bitmap.Height;

                    var message = new NetworkMessage
                    {
                        Type = isFullFrame ? MessageType.FullScreen : MessageType.ScreenDelta,
                        Data = isFullFrame ? delta.ImageData : delta.Serialize()
                    };
                    await _networkClient.SendMessageAsync(message);

                    if (isFullFrame)
                    {
                        _lastFullFrameSentUtc = DateTime.UtcNow;
                    }
                }

                bitmap.Dispose();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error capturing/sending screen: {ex.Message}");
            }
            finally
            {
                _isSendingFrame = false;
            }
        }

        // Direct IP Connection
        private async void DirectConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string hostIp = DirectConnectIpTextBox.Text.Trim();
                if (string.IsNullOrEmpty(hostIp))
                {
                    UpdateStatus("Please enter host IP");
                    return;
                }

                UpdateStatus("Connecting directly...");

                _isClientMode = true;
                _networkClient = new NetworkClient();

                _networkClient.MessageReceived += NetworkClient_MessageReceived;
                _networkClient.BytesReceived += bytes => _receivedBytesThisSecond += bytes;
                _networkClient.Connected += () =>
                {
                    UpdateStatus("Connected to host");
                    RemoteConnectionPanel.Visibility = Visibility.Collapsed;
                    ScreenDisplayPanel.Visibility = Visibility.Visible;
                    _metricsTimer.Start();
                    _pingTimer.Start();
                };
                _networkClient.Disconnected += () =>
                {
                    UpdateStatus("Disconnected from host");
                    RemoteConnectionPanel.Visibility = Visibility.Visible;
                    ScreenDisplayPanel.Visibility = Visibility.Collapsed;
                    _metricsTimer.Stop();
                    _pingTimer.Stop();
                    ShowWindow();
                };
                _networkClient.Error += (ex) => UpdateStatus($"Error: {ex.Message}");

                await _networkClient.ConnectAsync(hostIp, 8888);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error connecting: {ex.Message}");
            }
        }

        // Connection Code Connection
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string connectionCode = ConnectionCodeTextBox.Text.Trim();
                if (string.IsNullOrEmpty(connectionCode))
                {
                    UpdateStatus("Please enter a connection code");
                    return;
                }

                string routerUrl = RouterServerTextBox.Text;
                var parts = routerUrl.Split(':');
                string host = parts[0];
                int port = parts.Length > 1 ? int.Parse(parts[1]) : 9000;

                UpdateStatus("Connecting via router server...");

                _isClientMode = true;
                _networkClient = new NetworkClient();

                _networkClient.MessageReceived += NetworkClient_MessageReceived;
                _networkClient.BytesReceived += bytes => _receivedBytesThisSecond += bytes;
                _networkClient.Connected += () =>
                {
                    UpdateStatus("Connected to server");
                    RemoteConnectionPanel.Visibility = Visibility.Collapsed;
                    ScreenDisplayPanel.Visibility = Visibility.Visible;
                    _metricsTimer.Start();
                    _pingTimer.Start();
                };
                _networkClient.Disconnected += () =>
                {
                    UpdateStatus("Disconnected from server");
                    RemoteConnectionPanel.Visibility = Visibility.Visible;
                    ScreenDisplayPanel.Visibility = Visibility.Collapsed;
                    _metricsTimer.Stop();
                    _pingTimer.Stop();
                    ShowWindow();
                };
                _networkClient.Error += (ex) => UpdateStatus($"Error: {ex.Message}");

                // Get host endpoint from router server
                string hostEndpoint = await GetHostEndpointFromRouter(host, port, connectionCode);
                
                // Connect to the host
                var endpointParts = hostEndpoint.Split(':');
                await _networkClient.ConnectAsync(endpointParts[0], int.Parse(endpointParts[1]));
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error connecting: {ex.Message}");
            }
        }

        private async Task<string> GetHostEndpointFromRouter(string host, int port, string connectionCode)
        {
            using (HttpClient client = new HttpClient())
            {
                var request = new
                {
                    connectionCode,
                    clientId = Guid.NewGuid().ToString()
                };

                var response = await client.PostAsync($"http://{host}:{port}/connect",
                    new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(content);
                bool success = document.RootElement.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
                if (!success)
                {
                    string message = document.RootElement.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "Router connection failed."
                        : "Router connection failed.";
                    throw new InvalidOperationException(message);
                }

                return document.RootElement.GetProperty("hostEndpoint").GetString() ?? throw new InvalidOperationException("Router did not return a host endpoint.");
            }
        }

        private void NetworkClient_MessageReceived(NetworkMessage message)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    switch (message.Type)
                    {
                        case MessageType.ScreenDelta:
                            HandleScreenDelta(message.Data);
                            break;

                        case MessageType.FullScreen:
                            HandleFullScreen(message.Data);
                            break;

                        case MessageType.ScreenSize:
                            HandleScreenSize(message.Data);
                            break;

                        case MessageType.MouseMove:
                            HandleRemoteMouseMove(message.Data);
                            break;

                        case MessageType.MouseClick:
                            HandleRemoteMouseClick(message.Data);
                            break;

                        case MessageType.KeyPress:
                            HandleRemoteKeyPress(message.Data);
                            break;

                        case MessageType.Ping:
                            SendPong(message.Data);
                            break;

                        case MessageType.Pong:
                            HandlePong(message.Data);
                            break;

                        case MessageType.QualityPreset:
                            HandleQualityPreset(message.Data);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error handling message: {ex.Message}");
                }
            });
        }

        private void HandleScreenDelta(byte[] data)
        {
            try
            {
                var delta = ScreenDelta.Deserialize(data);
                
                using (MemoryStream ms = new MemoryStream(delta.ImageData))
                using (Bitmap deltaBitmap = new Bitmap(ms))
                {
                    if (_remoteScreenBitmap == null)
                    {
                        _remoteScreenBitmap = new Bitmap(delta.Width, delta.Height, PixelFormat.Format24bppRgb);
                        _remoteScreenWidth = delta.Width;
                        _remoteScreenHeight = delta.Height;
                    }

                    using (Graphics graphics = Graphics.FromImage(_remoteScreenBitmap))
                    {
                        graphics.DrawImage(deltaBitmap, delta.X, delta.Y, delta.Width, delta.Height);
                    }
                }

                ScreenImage.Source = ConvertBitmapToBitmapImage(_remoteScreenBitmap);
                _renderedFramesThisSecond++;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error applying screen delta: {ex.Message}");
            }
        }

        private void HandleFullScreen(byte[] data)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    _remoteScreenBitmap = new Bitmap(ms);
                    _remoteScreenWidth = _remoteScreenBitmap.Width;
                    _remoteScreenHeight = _remoteScreenBitmap.Height;
                    ScreenImage.Source = ConvertBitmapToBitmapImage(_remoteScreenBitmap);
                    _renderedFramesThisSecond++;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error handling full screen: {ex.Message}");
            }
        }

        private void HandleScreenSize(byte[] data)
        {
            UpdateStatus("Remote screen size received");
        }

        private void HandleRemoteMouseMove(byte[] data)
        {
            if (!_isServerMode) return;

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    int x = reader.ReadInt32();
                    int y = reader.ReadInt32();
                    System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error handling mouse move: {ex.Message}");
            }
        }

        private void HandleRemoteMouseClick(byte[] data)
        {
            if (!_isServerMode) return;

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    int x = reader.ReadInt32();
                    int y = reader.ReadInt32();
                    bool leftButton = reader.ReadBoolean();
                    bool rightButton = reader.ReadBoolean();

                    System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                    SendMouseClick(x, y, leftButton);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error handling mouse click: {ex.Message}");
            }
        }

        private void HandleRemoteKeyPress(byte[] data)
        {
            if (!_isServerMode) return;

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    int virtualKey = reader.ReadInt32();
                    bool isKeyDown = reader.ReadBoolean();

                    if (isKeyDown)
                    {
                        keybd_event((byte)virtualKey, 0, 0, 0);
                    }
                    else
                    {
                        keybd_event((byte)virtualKey, 0, KEYEVENTF_KEYUP, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error handling key press: {ex.Message}");
            }
        }

        private void ScreenImage_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isClientMode || _networkClient == null || !_networkClient.IsConnected) return;

            try
            {
                var position = e.GetPosition(ScreenImage);
                
                double scaleX = (double)_remoteScreenWidth / ScreenImage.ActualWidth;
                double scaleY = (double)_remoteScreenHeight / ScreenImage.ActualHeight;
                
                int remoteX = (int)(position.X * scaleX);
                int remoteY = (int)(position.Y * scaleY);

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write(remoteX);
                    writer.Write(remoteY);
                    writer.Write(true);
                    writer.Write(false);

                    var message = new NetworkMessage
                    {
                        Type = MessageType.MouseClick,
                        Data = ms.ToArray()
                    };
                    _networkClient.SendMessageAsync(message);
                }

                UpdateStatus($"Sent click to remote: {remoteX}, {remoteY}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error sending click: {ex.Message}");
            }
        }

        private void ScreenImage_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isClientMode || _networkClient == null || !_networkClient.IsConnected) return;

            try
            {
                var position = e.GetPosition(ScreenImage);
                
                double scaleX = (double)_remoteScreenWidth / ScreenImage.ActualWidth;
                double scaleY = (double)_remoteScreenHeight / ScreenImage.ActualHeight;
                
                int remoteX = (int)(position.X * scaleX);
                int remoteY = (int)(position.Y * scaleY);

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write(remoteX);
                    writer.Write(remoteY);
                    writer.Write(false);
                    writer.Write(true);

                    var message = new NetworkMessage
                    {
                        Type = MessageType.MouseClick,
                        Data = ms.ToArray()
                    };
                    _networkClient.SendMessageAsync(message);
                }

                UpdateStatus($"Sent right-click to remote: {remoteX}, {remoteY}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error sending right-click: {ex.Message}");
            }
        }

        private void SendMouseClick(int x, int y, bool isLeftClick)
        {
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);

            if (isLeftClick)
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            }
            else
            {
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
            }
        }

        private BitmapImage ConvertBitmapToBitmapImage(Bitmap bitmap)
        {
            using (MemoryStream memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Bmp);
                memory.Position = 0;

                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private void UpdateStatus(string message)
        {
            if (StatusText != null)
            {
                StatusText.Text = message;
            }
        }

        private async void PingTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isClientMode || _networkClient == null || !_networkClient.IsConnected)
                return;

            try
            {
                byte[] data = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                await _networkClient.SendMessageAsync(new NetworkMessage
                {
                    Type = MessageType.Ping,
                    Data = data
                });
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error sending ping: {ex.Message}");
            }
        }

        private async void SendPong(byte[] data)
        {
            if (!_isServerMode || _networkClient == null || !_networkClient.IsConnected)
                return;

            await _networkClient.SendMessageAsync(new NetworkMessage
            {
                Type = MessageType.Pong,
                Data = data
            });
        }

        private void HandlePong(byte[] data)
        {
            if (data.Length < sizeof(long))
                return;

            long pingTicks = BitConverter.ToInt64(data, 0);
            _lastLatencyMs = (int)Math.Max(0, (DateTime.UtcNow - new DateTime(pingTicks, DateTimeKind.Utc)).TotalMilliseconds);
        }

        private void HandleQualityPreset(byte[] data)
        {
            if (!_isServerMode || data.Length < sizeof(int))
                return;

            int selectedIndex = BitConverter.ToInt32(data, 0);
            ApplyStreamingPreset(selectedIndex, sendToPeer: false);
        }

        private void MetricsTimer_Tick(object? sender, EventArgs e)
        {
            int fps = _renderedFramesThisSecond;
            int bytes = _receivedBytesThisSecond;
            _renderedFramesThisSecond = 0;
            _receivedBytesThisSecond = 0;

            ViewerFpsText.Text = $"FPS: {fps}";
            ViewerLatencyText.Text = _lastLatencyMs >= 0 ? $"Latency: {_lastLatencyMs} ms" : "Latency: --";
            ViewerBandwidthText.Text = $"Down: {bytes / 1024.0:0} KB/s";
            SignalQualityText.Text = $"Signal: {GetSignalQuality(fps, _lastLatencyMs)}";
        }

        private static string GetSignalQuality(int fps, int latencyMs)
        {
            if (latencyMs < 0)
                return "measuring";

            if (latencyMs < 80 && fps >= 10)
                return "excellent";

            if (latencyMs < 150 && fps >= 6)
                return "good";

            if (latencyMs < 300)
                return "fair";

            return "poor";
        }

        protected override void OnClosed(EventArgs e)
        {
            _screenCaptureTimer?.Stop();
            _networkClient?.Disconnect();
            _changeDetector?.Dispose();
            _remoteScreenBitmap?.Dispose();
            base.OnClosed(e);
        }
    }
}
