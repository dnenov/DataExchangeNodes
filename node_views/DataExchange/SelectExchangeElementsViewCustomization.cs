using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autodesk.DataExchange;
using Autodesk.DataExchange.Core.Events;
using Autodesk.DataExchange.Core.Interface;
using Autodesk.DataExchange.Extensions.HostingProvider;
using Autodesk.DataExchange.Extensions.Logging.File;
using Autodesk.DataExchange.Extensions.Storage.File;
using Autodesk.DataExchange.UI.Core;
using Autodesk.DataExchange.UI.Core.Interfaces;
using Dynamo.Configuration;
using Dynamo.Controls;
using Dynamo.Core;
using Dynamo.UI;
using Dynamo.UI.Commands;
using Dynamo.ViewModels;
using Dynamo.Wpf;
using CoreNodeModelsWpf.Nodes;
using DataExchangeNodes.NodeModels.DataExchange;
using Newtonsoft.Json;
using Autodesk.DataExchange.Models;
using Autodesk.DataExchange.Core.Enums;

namespace DataExchangeNodes.NodeViews.DataExchange
{
    /// <summary>
    /// View customization for SelectExchangeElements NodeModel
    /// Adds a "Select" button that opens the DataExchange SDK UI
    /// </summary>
    public class SelectExchangeElementsViewCustomization : INodeViewCustomization<SelectExchangeElements>
    {
        private SelectExchangeElements nodeModel;
        private Button buttonControl;
        private TextBlock contentText;
        private DynamoViewModel dynamoViewModel;
        private static IInteropBridge _bridge;
        private static Client _client;
        private static ReadExchangeModel _readModel;
        private static bool _eventHandlerRegistered = false; // Track handler registration
        private static bool _shutdownEventSubscribed = false; // Track shutdown event subscription (singleton)
        private static Dynamo.Models.DynamoModel _dynamoModel; // Hold reference for unsubscription
        private static bool _clientInitializedThisSession = false; // Track if we've already auto-initialized
        private static DynamoAuthProvider _staticAuthProvider; // Static auth provider that persists across workspace changes
        private DynamoAuthProvider authProvider;
        private bool autoTriggerEnabled = true; // Flag to control auto-trigger

        public DelegateCommand OpenDataExchangeCommand { get; set; }

        /// <summary>
        /// Helper to log messages to Dynamo console
        /// </summary>
        private void Log(string message)
        {
            dynamoViewModel?.Model?.Logger?.Log($"[SelectExchange] {message}");
        }

        /// <summary>
        /// Static cleanup method called when Dynamo shuts down
        /// This ensures adskdxui.exe is properly killed regardless of how many nodes exist
        /// </summary>
        private static void OnDynamoShutdown(Dynamo.Models.DynamoModel model)
        {
            model?.Logger?.Log("[SelectExchange] Dynamo shutdown detected - cleaning up DataExchange resources");

            // Unsubscribe from shutdown event
            if (_dynamoModel != null)
            {
                _dynamoModel.ShutdownStarted -= OnDynamoShutdown;
                _shutdownEventSubscribed = false;
                _dynamoModel = null;
            }

            // Unregister exchange event handler
            if (_readModel != null && _eventHandlerRegistered)
            {
                try
                {
                    // Can't unsubscribe instance method from static context, but we're shutting down anyway
                    _eventHandlerRegistered = false;
                }
                catch { }
            }

            // Cleanup bridge
            if (_bridge != null)
            {
                try
                {
                    _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Close);
                }
                catch { }

                try
                {
                    if (_bridge is IDisposable disposableBridge)
                    {
                        disposableBridge.Dispose();
                    }
                }
                catch { }

                _bridge = null;
            }

            // Force kill adskdxui.exe process if still running
            try
            {
                foreach (var process in Process.GetProcessesByName("adskdxui"))
                {
                    model?.Logger?.Log($"[SelectExchange] Killing adskdxui.exe process (PID: {process.Id})");
                    process.Kill();
                    process.Dispose();
                }
            }
            catch { }

            // Reset state flags for potential Dynamo restart
            _client = null;
            _readModel = null;
            _clientInitializedThisSession = false;

            model?.Logger?.Log("[SelectExchange] DataExchange cleanup complete");
        }

        /// <summary>
        /// Event handler for auto-triggering element selection after exchange is loaded
        /// This fires when user clicks "Load the latest" or "Select elements" in the UI
        /// </summary>
        private async void OnExchangeLoadedAutoTrigger(object sender, AfterGetLatestExchangeDetailsEventArgs e)
        {
            // Log the event details for debugging
            Log($"EVENT: AfterGetLatestExchangeDetails fired");
            Log($"  - ExchangeItem.Name: {e.ExchangeItem?.Name ?? "null"}");
            Log($"  - ExchangeItem.ExchangeID: {e.ExchangeItem?.ExchangeID ?? "null"}");
            Log($"  - ExchangeItem.ProjectName: {e.ExchangeItem?.ProjectName ?? "null"}");
            Log($"  - Sender type: {sender?.GetType().Name ?? "null"}");

            if (!autoTriggerEnabled)
            {
                Log("  - autoTriggerEnabled=false, skipping auto-selection");
                return;
            }

            try
            {
                // Auto-trigger the "Select elements" action
                Log("  - Calling SelectElementsAsync...");
                var exchangeIds = new List<string> { e.ExchangeItem.ExchangeID };
                var success = await _readModel.SelectElementsAsync(exchangeIds);
                Log($"  - SelectElementsAsync returned: {success}");

                // Update UI text to show selection result (fixes "UI opened" stuck state)
                // Use BeginInvoke to avoid blocking during workspace close
                if (contentText != null)
                {
                    contentText.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (success)
                        {
                            contentText.Text = $"Selected: {e.ExchangeItem?.Name}";
                            contentText.Foreground = (System.Windows.Media.Brush)SharedDictionaryManager.DynamoModernDictionary["PrimaryCharcoal100Brush"];
                        }
                        else
                        {
                            contentText.Text = "Selection failed";
                            contentText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                        }
                    }));
                }

                // Close the UI after successful selection
                if (success && _bridge != null)
                {
                    try
                    {
                        _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Hide);
                    }
                    catch
                    {
                        // Non-critical - UI may already be closed
                    }
                }
            }
            catch (Exception ex)
            {
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Error in auto-trigger: {ex.Message}");
                if (contentText != null)
                {
                    contentText.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        contentText.Text = "Selection error";
                        contentText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                    }));
                }
            }
        }

        /// <summary>
        /// Initializes the DataExchange client for saved graphs.
        /// Uses direct API calls to validate the exchange - NO UI/bridge is created.
        /// This enables downstream nodes to work without requiring user interaction.
        /// </summary>
        /// <param name="savedValue">The saved selection JSON from the node</param>
        /// <returns>True if initialization and validation succeeded</returns>
        private async Task<bool> InitializeClientForSavedGraphAsync(string savedValue)
        {
            // Only initialize if not already done
            if (DataExchangeNodes.DataExchange.DataExchangeClient.IsInitialized())
            {
                dynamoViewModel?.Model?.Logger?.Log("[SelectExchange] Client already initialized");
                // Still need to pre-load exchange if readModel exists
                if (_readModel != null && !string.IsNullOrEmpty(savedValue))
                {
                    _readModel.PreloadExchangeFromSelection(savedValue);
                }
                return true;
            }

            // Setup DataExchange paths
            string appBasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dynamo", "DataExchange");

            if (!Directory.Exists(appBasePath))
                Directory.CreateDirectory(appBasePath);

            string logPath = Path.Combine(appBasePath, "logs");
            ILogger logger = new Log(logPath);

            // Create storage and hosting provider
            var storage = new Storage(appBasePath);
            // Use static auth provider to avoid null reference when workspace changes
            var hostingProvider = new ACC(logger, () => _staticAuthProvider?.GetToken());

            // Configure SDK options
            var sdkOptions = new SDKOptions
            {
                AuthProvider = _staticAuthProvider,
                Storage = storage,
                HostingProvider = hostingProvider,
                Logger = logger,
                HostApplicationName = "Dynamo",
                HostApplicationVersion = "4.1",
                ConnectorName = "DataExchange Nodes",
                ConnectorVersion = "0.1.0",
                GeometryConfiguration = new GeometryConfiguration()
                {
                    STEPProtocol = STEPProtocol.ManagedModelBased3DEngineering,
                    STEPTolerance = 0.01,
                    WantAssetIdAsLabel = true
                }
            };

            // Initialize client (NO bridge/UI created here - just the SDK client)
            DataExchangeNodes.DataExchange.DataExchangeClient.InitializeClient(sdkOptions);
            _client = DataExchangeNodes.DataExchange.DataExchangeClient.GetClient();
            _readModel = ReadExchangeModel.GetInstance(_client);
            ReadExchangeModel.Logger = dynamoViewModel?.Model?.Logger;

            // Register event handler for future selections (when UI is opened later)
            if (!_eventHandlerRegistered)
            {
                _readModel.AfterGetLatestExchangeDetails += OnExchangeLoadedAutoTrigger;
                _eventHandlerRegistered = true;
            }

            // Register auth provider for LoadGeometryFromExchange node
            DataExchangeNodes.DataExchange.LoadGeometryFromExchange.RegisterAuthProvider(() => _staticAuthProvider?.GetToken());

            dynamoViewModel?.Model?.Logger?.Log("[SelectExchange] Client initialized for saved graph (no UI/bridge)");

            // Pre-load the saved exchange into ReadExchangeModel's localStorage
            // This ensures the UI will show it if opened later
            if (!string.IsNullOrEmpty(savedValue))
            {
                _readModel.PreloadExchangeFromSelection(savedValue);

                // Optionally validate the exchange still exists via direct API call
                try
                {
                    var selectionData = JsonConvert.DeserializeObject<Dictionary<string, string>>(savedValue);
                    if (selectionData != null &&
                        selectionData.TryGetValue("exchangeId", out var exchangeId) &&
                        selectionData.TryGetValue("collectionId", out var collectionId))
                    {
                        var exists = await _readModel.ValidateExchangeExistsAsync(exchangeId, collectionId);
                        if (!exists)
                        {
                            dynamoViewModel?.Model?.Logger?.Log($"[SelectExchange] WARNING: Saved exchange '{exchangeId}' may no longer exist");
                        }
                    }
                }
                catch (Exception ex)
                {
                    dynamoViewModel?.Model?.Logger?.Log($"[SelectExchange] Exchange validation skipped: {ex.Message}");
                }
            }

            return true;
        }

        /// <summary>
        /// Opens the DataExchange SDK UI for element selection
        /// </summary>
        private async void OpenDataExchangeUI(object obj)
        {
            Log("=== OpenDataExchangeUI called ===");

            try
            {
                // Check authentication - the user might have logged out since the node was created
                if (!authProvider.IsAuthenticated())
                {
                    Log("ERROR: Not authenticated");
                    nodeModel.Warning("Please log in to Dynamo first");
                    contentText.Text = "Not logged in";
                    return;
                }
                Log("Authentication OK");

                // Setup DataExchange paths
                string appBasePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Dynamo", "DataExchange");
                
                if (!Directory.Exists(appBasePath))
                    Directory.CreateDirectory(appBasePath);

                string logPath = Path.Combine(appBasePath, "logs");
                ILogger logger = new Log(logPath);

                // Create storage and hosting provider
                var storage = new Storage(appBasePath);
                // Use static auth provider to avoid null reference when workspace changes
                var hostingProvider = new ACC(logger, () => _staticAuthProvider?.GetToken());

                // Configure SDK options (matching grasshopper-connector pattern)
                var sdkOptions = new SDKOptions
                {
                    AuthProvider = _staticAuthProvider,
                    Storage = storage,
                    HostingProvider = hostingProvider,
                    Logger = logger,
                    HostApplicationName = "Dynamo",
                    HostApplicationVersion = "4.1",
                    ConnectorName = "DataExchange Nodes",
                    ConnectorVersion = "0.1.0",
                    GeometryConfiguration = new GeometryConfiguration()
                    {
                        STEPProtocol = STEPProtocol.ManagedModelBased3DEngineering,
                        STEPTolerance = 0.01,
                        WantAssetIdAsLabel = true
                    }
                };

                // Initialize or reuse centralized DataExchange client
                if (!DataExchangeNodes.DataExchange.DataExchangeClient.IsInitialized())
                {
                    Log("Initializing DataExchange client (first time)...");
                    DataExchangeNodes.DataExchange.DataExchangeClient.InitializeClient(sdkOptions);
                    _client = DataExchangeNodes.DataExchange.DataExchangeClient.GetClient();
                    _readModel = ReadExchangeModel.GetInstance(_client);

                    // Register event handler (only once)
                    if (!_eventHandlerRegistered)
                    {
                        Log("Registering AfterGetLatestExchangeDetails event handler");
                        _readModel.AfterGetLatestExchangeDetails += OnExchangeLoadedAutoTrigger;
                        _eventHandlerRegistered = true;
                    }
                }
                else
                {
                    Log("Reusing existing DataExchange client");
                    _client = DataExchangeNodes.DataExchange.DataExchangeClient.GetClient();
                    if (_readModel == null)
                    {
                        _readModel = ReadExchangeModel.GetInstance(_client);
                    }

                    // Register event handler (only once)
                    if (!_eventHandlerRegistered)
                    {
                        Log("Registering AfterGetLatestExchangeDetails event handler (late registration)");
                        _readModel.AfterGetLatestExchangeDetails += OnExchangeLoadedAutoTrigger;
                        _eventHandlerRegistered = true;
                    }
                }

                // Register auth provider for LoadGeometryFromExchange node
                DataExchangeNodes.DataExchange.LoadGeometryFromExchange.RegisterAuthProvider(() => _staticAuthProvider?.GetToken());

                // Update current node reference
                ReadExchangeModel.CurrentNode = nodeModel;
                ReadExchangeModel.Logger = dynamoViewModel?.Model?.Logger;
                Log($"Event handler registered: {_eventHandlerRegistered}");

                // Pre-load the saved exchange into localStorage before opening UI
                // This ensures the UI shows the previously selected exchange
                if (!string.IsNullOrEmpty(nodeModel.Value))
                {
                    Log("Pre-loading saved exchange into ReadExchangeModel...");
                    _readModel.PreloadExchangeFromSelection(nodeModel.Value);
                }

                // Initialize bridge if not already done, or recreate if invalid
                bool needNewBridge = _bridge == null;

                // If bridge exists, verify it's still usable
                if (!needNewBridge)
                {
                    try
                    {
                        // Test if bridge is still valid by checking if it responds
                        // If this throws, the bridge is in a bad state and needs recreation
                        Log("Verifying existing InteropBridge is valid...");
                        // Simple test - if bridge was disposed internally this will throw
                        var testState = _bridge.GetType().GetProperty("IsInitialized");
                        if (testState != null)
                        {
                            var isInit = testState.GetValue(_bridge);
                            Log($"Bridge IsInitialized: {isInit}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Existing bridge is invalid ({ex.Message}), will recreate");
                        needNewBridge = true;
                        // Dispose the old bridge reference
                        try
                        {
                            if (_bridge is IDisposable disposableBridge)
                            {
                                disposableBridge.Dispose();
                            }
                        }
                        catch { }
                        _bridge = null;
                    }
                }

                if (needNewBridge)
                {
                    Log("Creating new InteropBridge...");
                    var bridgeOptions = InteropBridgeOptions.FromClient(_client);
                    bridgeOptions.Exchange = _readModel;

                    // Get window handle
                    IntPtr windowHandle = IntPtr.Zero;
                    try
                    {
                        if (Application.Current?.MainWindow != null)
                        {
                            var helper = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow);
                            windowHandle = helper.Handle;
                        }
                        else
                        {
                            windowHandle = Process.GetCurrentProcess().MainWindowHandle;
                        }
                    }
                    catch
                    {
                        windowHandle = Process.GetCurrentProcess().MainWindowHandle;
                    }

                    bridgeOptions.HostWindowHandle = windowHandle;
                    bridgeOptions.Invoker = new MainThreadInvoker(
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);

                    _bridge = InteropBridgeFactory.Create(bridgeOptions);
                    await _bridge.InitializeAsync();
                    Log("InteropBridge initialized");
                }
                else
                {
                    Log("Reusing existing InteropBridge");
                }

                // Launch the DataExchange UI with recovery logic
                Log("Launching DataExchange UI...");
                try
                {
                    await _bridge.LaunchConnectorUiAsync();
                    _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Show);
                }
                catch (NullReferenceException ex)
                {
                    Log($"Bridge operation failed with NullReference: {ex.Message}");
                    Log("Attempting to recreate bridge...");

                    // Bridge is in bad state - recreate it
                    try
                    {
                        if (_bridge is IDisposable disposableBridge)
                        {
                            disposableBridge.Dispose();
                        }
                    }
                    catch { }
                    _bridge = null;

                    // Create new bridge
                    var bridgeOptions = InteropBridgeOptions.FromClient(_client);
                    bridgeOptions.Exchange = _readModel;

                    IntPtr windowHandle = IntPtr.Zero;
                    try
                    {
                        if (Application.Current?.MainWindow != null)
                        {
                            var helper = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow);
                            windowHandle = helper.Handle;
                        }
                        else
                        {
                            windowHandle = Process.GetCurrentProcess().MainWindowHandle;
                        }
                    }
                    catch
                    {
                        windowHandle = Process.GetCurrentProcess().MainWindowHandle;
                    }

                    bridgeOptions.HostWindowHandle = windowHandle;
                    bridgeOptions.Invoker = new MainThreadInvoker(
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);

                    _bridge = InteropBridgeFactory.Create(bridgeOptions);
                    await _bridge.InitializeAsync();
                    Log("InteropBridge recreated after failure");

                    // Retry the launch
                    await _bridge.LaunchConnectorUiAsync();
                    _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Show);
                }
                Log("UI launched - waiting for user to click an exchange");
                Log("NOTE: Simply click an exchange name to select it (auto-selection enabled)");

                // Bring window to front
                try
                {
                    // await Task.Delay(200); // Removed - testing without delays
                    if (Application.Current?.MainWindow != null)
                    {
                        Application.Current.MainWindow.Activate();
                        Application.Current.MainWindow.BringIntoView();
                        Application.Current.MainWindow.Focus();
                    }
                    // await Task.Delay(100); // Removed - testing without delays
                    _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Show);
                }
                catch
                {
                    // Window focus failed - non-critical
                }

                contentText.Text = "Click an exchange...";
                Log("UI displayed - click an exchange to select it");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
                nodeModel.Error($"Failed to open DataExchange: {ex.Message}");
                contentText.Text = "Error";
            }
        }

        private static bool CanOpenDataExchange(object obj)
        {
            return true;
        }

        /// <summary>
        /// Customizes the node view by adding a Select button
        /// </summary>
        public void CustomizeView(SelectExchangeElements model, NodeView nodeView)
        {
            OpenDataExchangeCommand = new DelegateCommand(OpenDataExchangeUI, CanOpenDataExchange);
            nodeModel = model;

            // Get DynamoViewModel for authentication
            dynamoViewModel = nodeView.ViewModel.DynamoViewModel;
            authProvider = new DynamoAuthProvider(dynamoViewModel);
            // Update static auth provider so SDK lambdas always have a valid reference
            _staticAuthProvider = authProvider;

            // Subscribe to Dynamo shutdown event (singleton fashion - only once)
            // NOTE: Bridge persists across workspaces - only cleaned up on Dynamo shutdown
            if (!_shutdownEventSubscribed && dynamoViewModel?.Model != null)
            {
                _dynamoModel = dynamoViewModel.Model;
                _dynamoModel.ShutdownStarted += OnDynamoShutdown;
                _shutdownEventSubscribed = true;
                dynamoViewModel.Model.Logger?.Log("[SelectExchange] Subscribed to Dynamo shutdown event for cleanup");
            }

            // Register auth provider for LoadGeometryFromExchange node (for saved graphs)
            DataExchangeNodes.DataExchange.LoadGeometryFromExchange.RegisterAuthProvider(() => _staticAuthProvider?.GetToken());

            // Create the Select button
            buttonControl = new Button()
            {
                Content = "Select",
                VerticalAlignment = VerticalAlignment.Center,
                Height = Dynamo.Configuration.Configurations.PortHeightInPixels,
                DataContext = this,
            };

            buttonControl.Click += (sender, args) => OpenDataExchangeUI(null);

            // Create status text
            contentText = new TextBlock
            {
                Text = string.IsNullOrEmpty(model.Value) ? "No selection" : "Selection stored",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)SharedDictionaryManager.DynamoModernDictionary["PrimaryCharcoal100Brush"],
                FontSize = 12,
                Margin = new Thickness(9, 8, 0, 6),
            };
            Grid.SetRow(contentText, 1);

            // Check authentication on initialization
            if (!authProvider.IsAuthenticated())
            {
                contentText.Text = "Please log in";
                contentText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                buttonControl.IsEnabled = false;
            }
            else if (!string.IsNullOrEmpty(model.Value) && !_clientInitializedThisSession)
            {
                // Auto-initialize client for saved graphs
                // Only do this once per session to avoid race conditions with multiple nodes
                // NOTE: This uses direct API calls - NO UI/bridge is created here
                _clientInitializedThisSession = true;
                var savedValue = model.Value; // Capture for async context

                Task.Run(async () =>
                {
                    try
                    {
                        await InitializeClientForSavedGraphAsync(savedValue);

                        // Force node to re-evaluate so selection propagates downstream
                        // Must be done on UI thread - use BeginInvoke to avoid deadlock
                        contentText.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            contentText.Text = "Selection restored";

                            // Trigger node re-evaluation to propagate stored selection to downstream nodes
                            dynamoViewModel?.Model?.Logger?.Log("[SelectExchange] Triggering node re-evaluation for saved selection");
                            nodeModel?.OnNodeModified(forceExecute: true);
                        }));
                    }
                    catch (Exception ex)
                    {
                        dynamoViewModel?.Model?.Logger?.Log($"[SelectExchange] Auto-init failed: {ex.Message}");
                        // Silent fail - user can manually click Select
                    }
                });
            }
            else if (!string.IsNullOrEmpty(model.Value))
            {
                // Client already initialized by another node - just update UI
                contentText.Text = "Selection stored";
            }

            // Listen for node value changes (use Dispatcher since this may be called from background thread)
            // Use BeginInvoke to avoid blocking during workspace close
            model.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(model.Value))
                {
                    contentText.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        contentText.Text = string.IsNullOrEmpty(model.Value) ? "No selection" : "Selection stored";
                        contentText.Foreground = (System.Windows.Media.Brush)SharedDictionaryManager.DynamoModernDictionary["PrimaryCharcoal100Brush"];

                        // Auto-close UI when selection is stored (from auto-selection on click)
                        if (!string.IsNullOrEmpty(model.Value) && _bridge != null)
                        {
                            try
                            {
                                _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Hide);
                                Log("UI closed after selection stored");
                            }
                            catch
                            {
                                // Non-critical - UI may already be closed
                            }
                        }
                    }));
                }
            };

            // Add button to the node
            var sp = new StackPanel();
            sp.Children.Add(buttonControl);
            nodeView.inputGrid.Children.Add(sp);

            // Add status text to the node
            nodeView.grid.Children.Add(contentText);
            Grid.SetColumn(contentText, 1);
            Grid.SetRow(contentText, 3);
            Canvas.SetZIndex(contentText, 5);
        }

        public void Dispose()
        {
            // Per-instance cleanup - clear references to avoid memory leaks
            // NOTE: Static resources (bridge, client, etc.) are cleaned up via OnDynamoShutdown
            nodeModel = null;
            buttonControl = null;
            contentText = null;
            authProvider = null;
        }
    }
}

