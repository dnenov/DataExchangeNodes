using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autodesk.DataExchange;
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
        private DynamoAuthProvider authProvider;

        public DelegateCommand OpenDataExchangeCommand { get; set; }

        /// <summary>
        /// Opens the DataExchange SDK UI for element selection
        /// </summary>
        private async void OpenDataExchangeUI(object obj)
        {
            try
            {
                // Check authentication
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

                // Create DataExchange client
                var client = new Client(sdkOptions);
                var readModel = ReadExchangeModel.GetInstance(client);
                ReadExchangeModel.CurrentNode = nodeModel;

                // Initialize bridge if not already done
                if (_bridge == null)
                {
                    var bridgeOptions = InteropBridgeOptions.FromClient(client);
                    bridgeOptions.Exchange = readModel;
                    bridgeOptions.HostWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
                    bridgeOptions.Invoker = new MainThreadInvoker(
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);

                    _bridge = InteropBridgeFactory.Create(bridgeOptions);
                    await _bridge.InitializeAsync();
                }

                // Launch the DataExchange UI
                await _bridge.LaunchConnectorUiAsync();
                _bridge.SetWindowState(Autodesk.DataExchange.UI.Core.Enums.WindowState.Show);

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

