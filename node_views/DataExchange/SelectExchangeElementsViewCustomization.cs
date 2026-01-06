using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private DynamoAuthProvider authProvider;
        private bool autoTriggerEnabled = true; // Flag to control auto-trigger

        /// <summary>
        /// Exposes the DataExchange Client instance for reflection-based inspection
        /// </summary>
        public static Client ClientInstance => _client;

        public DelegateCommand OpenDataExchangeCommand { get; set; }

        /// <summary>
        /// Event handler for auto-triggering element selection after exchange is loaded
        /// </summary>
        private async void OnExchangeLoadedAutoTrigger(object sender, AfterGetLatestExchangeDetailsEventArgs e)
        {
            if (!autoTriggerEnabled) return;

            try
            {
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Exchange loaded - {e.ExchangeItem?.Name}");
                dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Auto-triggering SelectElements...");

                // Auto-trigger the "Select elements" action
                var exchangeIds = new List<string> { e.ExchangeItem.ExchangeID };
                await _readModel.SelectElementsAsync(exchangeIds);
                
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Auto-triggered SelectElements for {e.ExchangeItem.ExchangeID}");
                
                // Update UI text
                if (contentText != null)
                {
                    contentText.Dispatcher.Invoke(() =>
                    {
                        contentText.Text = $"Selected: {e.ExchangeItem?.Name}";
                    });
                }
            }
            catch (Exception ex)
            {
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Error in auto-trigger: {ex.Message}");
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Opens the DataExchange SDK UI for element selection
        /// </summary>
        private async void OpenDataExchangeUI(object obj)
        {
            try
            {
                // Check authentication - the user might have logged out since the node was created
                if (!authProvider.IsAuthenticated())
                {
                    nodeModel.Warning("Please log in to Dynamo first");
                    contentText.Text = "Not logged in";
                    return;
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
                var hostingProvider = new ACC(logger, () => authProvider.GetToken());

                // Configure SDK options
                var sdkOptions = new SDKOptions
                {
                    AuthProvider = authProvider,
                    Storage = storage,
                    HostingProvider = hostingProvider,
                    Logger = logger,
                    HostApplicationName = "Dynamo",
                    HostApplicationVersion = "4.1",
                    ConnectorName = "DataExchange Nodes",
                    ConnectorVersion = "0.1.0"
                };

                // Create or reuse DataExchange client
                if (_client == null)
                {
                    dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Creating NEW DataExchange client...");
                    _client = new Client(sdkOptions);
                    _readModel = ReadExchangeModel.GetInstance(_client);
                    
                    // Subscribe to exchange loaded event for auto-triggering selection
                    _readModel.AfterGetLatestExchangeDetails += OnExchangeLoadedAutoTrigger;
                    
                    dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Client and ReadModel created with auto-trigger");
                }
                else
                {
                    dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Reusing existing client and ReadModel");
                }
                
                // Register auth provider and client for LoadGeometryFromExchange node
                DataExchangeNodes.DataExchange.LoadGeometryFromExchange.RegisterAuthProvider(() => authProvider.GetToken());
                //DataExchangeNodes.DataExchange.LoadGeometryFromExchange.RegisterClient(_client);
                dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Auth provider and Client registered for LoadGeometryFromExchange");
                
                // Always update the current node reference
                ReadExchangeModel.CurrentNode = nodeModel;
                ReadExchangeModel.Logger = dynamoViewModel?.Model?.Logger;
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: CurrentNode updated: {nodeModel?.GUID}");

                // Initialize bridge if not already done
                if (_bridge == null)
                {
                    dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Initializing InteropBridge...");
                    var bridgeOptions = InteropBridgeOptions.FromClient(_client);
                    bridgeOptions.Exchange = _readModel;
                    
                    // Try to get the actual Dynamo window handle
                    IntPtr windowHandle = IntPtr.Zero;
                    try
                    {
                        // Try Application.Current.MainWindow first (most reliable for WPF)
                        if (Application.Current?.MainWindow != null)
                        {
                            var helper = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow);
                            windowHandle = helper.Handle;
                            dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Got window handle from Application.Current.MainWindow: {windowHandle}");
                        }
                        else
                        {
                            // Fallback to process main window
                            windowHandle = Process.GetCurrentProcess().MainWindowHandle;
                            dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Using Process.MainWindowHandle: {windowHandle}");
                        }
                    }
                    catch (Exception ex)
                    {
                        dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Error getting window handle: {ex.Message}");
                        windowHandle = Process.GetCurrentProcess().MainWindowHandle;
                    }
                    
                    bridgeOptions.HostWindowHandle = windowHandle;
                    bridgeOptions.Invoker = new MainThreadInvoker(
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);

                    _bridge = InteropBridgeFactory.Create(bridgeOptions);
                    await _bridge.InitializeAsync();
                    dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: InteropBridge initialized successfully");
                }
                else
                {
                    dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Reusing existing InteropBridge (callback should still work)");
                }

                // Launch the DataExchange UI
                dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Launching DataExchange UI...");
                await _bridge.LaunchConnectorUiAsync();
                dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: LaunchConnectorUiAsync completed");
                
                _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Show);
                dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: SetWindowState(Show) called");
                
                // Try to bring the window to front and ensure it's visible
                try
                {
                    // Give the UI a moment to initialize
                    await Task.Delay(200);
                    
                    // Try to activate the Dynamo main window (parent)
                    if (Application.Current?.MainWindow != null)
                    {
                        Application.Current.MainWindow.Activate();
                        Application.Current.MainWindow.BringIntoView();
                        Application.Current.MainWindow.Focus();
                        dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Activated Dynamo main window");
                    }
                    
                    // Also try to set window state again after a delay to ensure it's shown
                    await Task.Delay(100);
                    _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Show);
                }
                catch (Exception ex)
                {
                    dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: Could not bring window to front: {ex.Message}");
                }
                
                dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: DataExchange UI launched and shown");

                contentText.Text = "UI opened";
            }
            catch (Exception ex)
            {
                nodeModel.Error($"Failed to open DataExchange: {ex.Message}");
                contentText.Text = "Error";
                dynamoViewModel?.Model?.Logger?.Log($"SelectExchangeElements: {ex.Message}\n{ex.StackTrace}");
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

            // Register auth provider for LoadGeometryFromExchange node (for saved graphs)
            DataExchangeNodes.DataExchange.LoadGeometryFromExchange.RegisterAuthProvider(() => authProvider.GetToken());
            dynamoViewModel?.Model?.Logger?.Log("SelectExchangeElements: Auth provider registered on view initialization");

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

            // Listen for node value changes
            model.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(model.Value))
                {
                    contentText.Text = string.IsNullOrEmpty(model.Value) ? "No selection" : "Selection stored";
                    contentText.Foreground = (System.Windows.Media.Brush)SharedDictionaryManager.DynamoModernDictionary["PrimaryCharcoal100Brush"];
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
            // Cleanup bridge if needed
            if (_bridge != null)
            {
                try
                {
                    _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Close);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}

