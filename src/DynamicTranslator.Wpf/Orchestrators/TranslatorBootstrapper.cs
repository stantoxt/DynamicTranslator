﻿using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;

using Abp.Dependency;
using Abp.Runtime.Caching;

using DynamicTranslator.Configuration;
using DynamicTranslator.Constants;
using DynamicTranslator.Domain.Events;
using DynamicTranslator.Domain.Model;
using DynamicTranslator.Extensions;
using DynamicTranslator.Wpf.Orchestrators.Observers;
using DynamicTranslator.Wpf.Utility;
using DynamicTranslator.Wpf.ViewModel;

using Gma.System.MouseKeyHook;

using Clipboard = System.Windows.Clipboard;
using Point = System.Drawing.Point;

namespace DynamicTranslator.Wpf.Orchestrators
{
    public class TranslatorBootstrapper : ITranslatorBootstrapper, ISingletonDependency
    {
        private readonly ITypedCache<string, TranslateResult[]> cache;
        private readonly ICacheManager cacheManager;
        private readonly GrowlNotifiactions growlNotifications;
        private readonly MainWindow mainWindow;
        private readonly IDynamicTranslatorStartupConfiguration startupConfiguration;
        private CancellationTokenSource cancellationTokenSource;
        private IDisposable finderObservable;
        private IKeyboardMouseEvents globalMouseHook;
        private IntPtr hWndNextViewer;
        private HwndSource hWndSource;
        private bool isMouseDown;
        private Point mouseFirstPoint;
        private Point mouseSecondPoint;
        private IDisposable syncObserver;

        public TranslatorBootstrapper(MainWindow mainWindow,
            GrowlNotifiactions growlNotifications,
            IDynamicTranslatorStartupConfiguration startupConfiguration,
            ICacheManager cacheManager)
        {
            if (mainWindow == null)
                throw new ArgumentNullException(nameof(mainWindow));

            if (growlNotifications == null)
                throw new ArgumentNullException(nameof(growlNotifications));

            if (startupConfiguration == null)
                throw new ArgumentNullException(nameof(startupConfiguration));

            if (cacheManager == null)
                throw new ArgumentNullException(nameof(cacheManager));

            this.mainWindow = mainWindow;
            this.growlNotifications = growlNotifications;
            this.startupConfiguration = startupConfiguration;
            this.cacheManager = cacheManager;
            cache = this.cacheManager.GetCache<string, TranslateResult[]>(CacheNames.MeanCache);
        }

        public event EventHandler<WhenClipboardContainsTextEventArgs> WhenClipboardContainsTextEventHandler;

        public void Dispose()
        {
            DecomposeRoot();
        }

        public void Initialize()
        {
            CompositionRoot();
        }

        public async Task InitializeAsync()
        {
            await CompositionRootAsync();
        }

        public void SubscribeShutdownEvents()
        {
            mainWindow.Dispatcher.ShutdownStarted +=
                (sender, args) => { cancellationTokenSource?.Cancel(false); };

            mainWindow.Dispatcher.ShutdownFinished += (sender, args) =>
            {
                Dispose();
                GC.SuppressFinalize(this);
            };
        }

        public bool IsInitialized { get; private set; }

        private void CompositionRoot()
        {
            cancellationTokenSource = new CancellationTokenSource();
            StartHooks();
            ConfigureNotificationMeasurements();
            SubscribeLocalevents();
            Task.Run(FlushCopyCommandAsync);
            StartObservers();
            IsInitialized = true;
        }

        private async Task CompositionRootAsync()
        {
            await mainWindow.Dispatcher.InvokeAsync(async () =>
            {
                cancellationTokenSource = new CancellationTokenSource();
                StartHooks();
                ConfigureNotificationMeasurements();
                SubscribeLocalevents();
                await FlushCopyCommandAsync();
                await StartObserversAsync();
                IsInitialized = true;
            });
        }

        private void ConfigureNotificationMeasurements()
        {
            growlNotifications.Top = SystemParameters.WorkArea.Top + startupConfiguration.TopOffset;
            growlNotifications.Left = SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width - startupConfiguration.LeftOffset;
        }

        private void DecomposeRoot()
        {
            if (IsInitialized)
            {
                if (cancellationTokenSource.Token.CanBeCanceled)
                {
                    cancellationTokenSource.Cancel(false);
                }

                DisposeHooks();
                Task.Run(FlushCopyCommandAsync);
                UnsubscribeLocalEvents();
                growlNotifications.Dispose();
                finderObservable.Dispose();
                syncObserver.Dispose();
                cache.Clear();
                IsInitialized = false;
            }
        }

        private void DisposeHooks()
        {
            Win32.ChangeClipboardChain(hWndSource.Handle, hWndNextViewer);
            hWndNextViewer = IntPtr.Zero;
            hWndSource.RemoveHook(WinProc);
            globalMouseHook.Dispose();
        }

        private void FlushCopyCommand()
        {
            SendKeys.Flush();
        }

        private Task FlushCopyCommandAsync()
        {
            SendKeys.Flush();
            return Task.FromResult(0);
        }

        private async void MouseDoubleClicked(object sender, MouseEventArgs e)
        {
            await Task.Run(async () =>
            {
                isMouseDown = false;
                if (cancellationTokenSource.Token.IsCancellationRequested)
                    return;

                await SendCopyCommandAsync();
            });
        }

        private async void MouseDown(object sender, MouseEventArgs e)
        {
            await Task.Run(() =>
            {
                if (cancellationTokenSource.Token.IsCancellationRequested)
                    return;

                mouseFirstPoint = e.Location;
                isMouseDown = true;
            });
        }

        private async void MouseUp(object sender, MouseEventArgs e)
        {
            await Task.Run(async () =>
            {
                if (isMouseDown && !mouseSecondPoint.Equals(mouseFirstPoint))
                {
                    mouseSecondPoint = e.Location;
                    if (cancellationTokenSource.Token.IsCancellationRequested)
                        return;

                    await SendCopyCommandAsync();
                    isMouseDown = false;
                }
            });
        }

        private Task SendCopyCommandAsync()
        {
            SendKeys.SendWait("^c");
            SendKeys.Flush();
            return Task.FromResult(0);
        }

        private void StartHooks()
        {
            var wih = new WindowInteropHelper(mainWindow);
            hWndSource = HwndSource.FromHwnd(wih.Handle);
            globalMouseHook = Hook.GlobalEvents();
            var source = hWndSource;
            if (source != null)
            {
                source.AddHook(WinProc); // start processing window messages
                hWndNextViewer = Win32.SetClipboardViewer(source.Handle); // set this window as a viewer
            }
        }

        private void StartObservers()
        {
            finderObservable = Observable
                .FromEventPattern<WhenClipboardContainsTextEventArgs>(
                    h => WhenClipboardContainsTextEventHandler += h,
                    h => WhenClipboardContainsTextEventHandler -= h).
                 Subscribe(IocManager.Instance.Resolve<Finder>());

            syncObserver = Observable
                .Interval(TimeSpan.FromSeconds(7.0), TaskPoolScheduler.Default)
                .StartWith(-1L)
                .Subscribe(IocManager.Instance.Resolve<Feeder>());
        }

        private Task StartObserversAsync()
        {
            StartObservers();
            return Task.FromResult(0);
        }

        private void SubscribeLocalevents()
        {
            globalMouseHook.MouseDoubleClick += MouseDoubleClicked;
            globalMouseHook.MouseDown += MouseDown;
            globalMouseHook.MouseUp += MouseUp;
        }

        private void UnsubscribeLocalEvents()
        {
            globalMouseHook.MouseDoubleClick -= MouseDoubleClicked;
            globalMouseHook.MouseDownExt -= MouseDown;
            globalMouseHook.MouseUp -= MouseUp;
        }

        private IntPtr WinProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Win32.WmChangecbchain:
                    if (wParam == hWndNextViewer)
                        hWndNextViewer = lParam; //clipboard viewer chain changed, need to fix it.
                    else if (hWndNextViewer != IntPtr.Zero)
                        Win32.SendMessage(hWndNextViewer, msg, wParam, lParam); //pass the message to the next viewer.

                    break;
                case Win32.WmDrawclipboard:
                    Task.Run(async () =>
                    {
                        await mainWindow.Dispatcher.InvokeAsync(async () =>
                        {
                            Win32.SendMessage(hWndNextViewer, msg, wParam, lParam); //pass the message to the next viewer //clipboard content changed
                            if (Clipboard.ContainsText() && !string.IsNullOrEmpty(Clipboard.GetText().Trim()))
                            {
                                var currentText = Clipboard.GetText().RemoveSpecialCharacters().ToLowerInvariant();

                                if (!string.IsNullOrEmpty(currentText))
                                {
                                    await Task.Run(async () =>
                                    {
                                        if (cancellationTokenSource.Token.IsCancellationRequested)
                                            return;

                                        await WhenClipboardContainsTextEventHandler.InvokeSafelyAsync(this,
                                            new WhenClipboardContainsTextEventArgs {CurrentString = currentText}
                                            );

                                        await FlushCopyCommandAsync();
                                    });
                                }
                            }
                        },
                            DispatcherPriority.Background);
                    });

                    break;
            }

            return IntPtr.Zero;
        }
    }
}