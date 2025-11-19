using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.DataExchange.UI.Core.Interfaces;

namespace DataExchangeNodes.NodeViews.DataExchange
{
    /// <summary>
    /// Main thread invoker for DataExchange SDK to marshal callbacks to UI thread
    /// </summary>
    public class MainThreadInvoker : IMainThreadInvoker
    {
        private readonly Dispatcher _dispatcher;

        public MainThreadInvoker(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public async Task<T> InvokeAsync<T>(Func<Task<T>> func)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            // If already on UI thread, just execute
            if (_dispatcher.CheckAccess())
                return await func();

            // Marshal to UI thread
            var operation = await _dispatcher.InvokeAsync(func);
            return await operation;
        }
    }
}

