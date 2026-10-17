using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VozWindows;

public sealed class MainWindow : Window
{
    private const int HOTKEY_ID_F8 = 9001;
    private const int HOTKEY_ID_CTRL_SHIFT_SPACE = 9002;
    private const int HOTKEY_ID_SPEED_0 = 9100;
    private const int HOTKEY_ID_SPEED_1 = 9101;
    private const int HOTKEY_ID_SPEED_2 = 9102;
    private const int HOTKEY_ID_SPEED_3 = 9103;

    private readonly NativeMicrophoneCapture _mic = new();
    private readonly GeminiLiveClient _gemini = new();
    private readonly GeminiRewriteClient _rewriter = new();
    private readonly RewriteDeckView _rewriteDeck = new();

    // Integrated Module 2: CursorLoop (Touchpad Booster + Infinite Monitor Edges)
    private readonly CursorWrapEngine _cursorEngine = new();
    private readonly CursorLoopSettings _cursorSettings;
    private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 8 };

    // IPC Events for single-instance activation
    private readonly System.Threading.EventWaitHandle? _showUiEvent;
    private readonly System.Threading.EventWaitHandle? _cursorLoopEvent;
    private readonly System.Threading.EventWaitHandle? _legacyLoopEvent;
    private volatile bool _isClosingApp;

    private readonly Border _outerBorder;
    private readonly Grid _middleVisorGrid;
    private readonly StackPanel _wavePanel;
    private readonly StackPanel _textStack;
    private readonly Button _micButton;
    private readonly TextBlock _micButtonLabel;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _livePreviewText;
    private readonly Button _rewriteToggleButton;
    private readonly Button _cursorLoopButton;
    private readonly Button _minButton;
    private readonly Rectangle[] _bars = new Rectangle[10];
    private readonly Random _rng = new();

    private HwndSource? _hwndSource;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private volatile bool _isListening;
    private volatile bool _isCompact = false;
    private bool _rewriteTooltipEnabled = true; // Always docked in Native Sidebar when visible
    private bool _liveCursorMode = true;
    private double _expandedLeft;
    private double _expandedTop;
    private string _cachedApiKey = string.Empty;
    private string _activeCursorInterim = string.Empty;
    private string _lastAutoSealedText = string.Empty;
    private DateTime _lastAutoSealedUtc = DateTime.MinValue;
    private string _currentBlockCommittedText = string.Empty;
    private IntPtr _lastTargetWindowHwnd = IntPtr.Zero;
    private DateTime _lastInterimUiUtc = DateTime.UtcNow;
    private DateTime _lastInterimRewriteUtc = DateTime.MinValue;
    private DateTime _lastWaveformUiUtc = DateTime.MinValue;
    private DateTime _ignoreSpeechUntilUtc = DateTime.MinValue;
    private bool _needsLeadingSpace;
    private bool _isApplyingVariation;
    private bool _hasAppliedInCurrentCycle;
    private DateTime _lastAppliedUtc = DateTime.MinValue;
    private RewriteVariations? _latestVariationsSnapshot;
    private RewriteVariations? _previousArchivedVariations;
    private UpdateReleaseInfo? _pendingUpdateRelease;
    private volatile bool _isUpdatingApp;
    private string _sessionTranscript = string.Empty;
    private int _wordsTypedCount;

    private static readonly SolidColorBrush BrushRedLive = CreateFrozenBrush(239, 68, 68);
    private static readonly SolidColorBrush BrushSkyBlue = CreateFrozenBrush(212, 212, 216);    // #D4D4D8 Neutral Silver
    private static readonly SolidColorBrush BrushDarkBorder = CreateFrozenBrush(36, 36, 40);   // #242428 Dark Graphite Border
    private static readonly SolidColorBrush BrushGreenReady = CreateFrozenBrush(34, 197, 94);
    private static readonly SolidColorBrush BrushAmberWarn = CreateFrozenBrush(245, 158, 11);
    private static readonly SolidColorBrush BrushMutedGray = CreateFrozenBrush(161, 161, 170); // #A1A1AA
    private static readonly SolidColorBrush BrushWhiteText = CreateFrozenBrush(244, 244, 245); // #F4F4F5

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow(
        System.Threading.EventWaitHandle? showUiEvent = null,
        System.Threading.EventWaitHandle? cursorLoopEvent = null,
        System.Threading.EventWaitHandle? legacyLoopEvent = null,
        bool openCursorLoopOnStart = false)
    {
        _showUiEvent = showUiEvent;
        _cursorLoopEvent = cursorLoopEvent;
        _legacyLoopEvent = legacyLoopEvent;

        _cursorSettings = CursorLoopSettings.Load();
        _cursorEngine.SpeedMultiplier = _cursorSettings.GetEffectiveMultiplier();
        _cursorEngine.SmartPrecision = _cursorSettings.SmartPrecision;

        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            CursorLoopSettings.EnsureUnifiedStartup(Environment.ProcessPath);
        }

        Title = "VozWindows — Ditado por Voz + CursorLoop";
        SizeToContent = SizeToContent.Manual;
        WindowStyle = WindowStyle.None;
        // Hardware-accelerated native DWM surface (Zero WS_EX_LAYERED / Zero CPU DropShadow lag!)
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(11, 11, 13)); // #0B0B0D True Dark
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        TryLoadWindowIcon();

        Rect workArea = SystemParameters.WorkArea;
        _expandedLeft = workArea.Left + (workArea.Width - 920) / 2;
        _expandedTop = workArea.Bottom - 92 - 16;

        _outerBorder = new Border
        {
            CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Color.FromRgb(11, 11, 13)), // #0B0B0D True Dark
            BorderBrush = BrushDarkBorder,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0),
            ClipToBounds = true
        };

        // Unified 2-row Grid inside the single native sidebar window:
        // Row 0 (*): _rewriteDeck (6 Live Options — always visible in Sidebar mode)
        // Row 1 (Auto): mainGrid (Bottom Voice/Suite Control Bar)
        var rootMorphGrid = new Grid { ClipToBounds = true };
        rootMorphGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootMorphGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _rewriteDeck.Visibility = Visibility.Visible;
        Grid.SetRow(_rewriteDeck, 0);
        rootMorphGrid.Children.Add(_rewriteDeck);

        var mainGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            MinHeight = 46
        };
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col 0: Mic button
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Col 1: Status Visor
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col 2: CursorLoop + Minimize to Tray

        // 1. Mic Toggle Button (F8)
        _micButton = new Button
        {
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)), // #2563EB
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Ativar/Parar ditado por voz em qualquer janela (Atalho: F8 ou Ctrl+Shift+Espaço)"
        };
        _micButtonLabel = new TextBlock
        {
            Text = "🎙️ F8",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = Brushes.White
        };
        _micButton.Content = _micButtonLabel;
        ApplyRoundedButtonTemplate(_micButton, 10);
        _micButton.Click += async (_, _) => await ToggleDictationAsync();
        Grid.SetColumn(_micButton, 0);
        mainGrid.Children.Add(_micButton);

        // 2. Middle Visor Grid (Status + Live Preview inside Sidebar Footer)
        _middleVisorGrid = new Grid
        {
            ClipToBounds = true,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        _middleVisorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _middleVisorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _wavePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = 3, Height = 6 };
        }
        Grid.SetColumn(_wavePanel, 0);
        _middleVisorGrid.Children.Add(_wavePanel);

        _textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 1)
        };
        _statusDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = BrushGreenReady,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        _statusText = new TextBlock
        {
            Text = "INICIANDO VOZ (F8)...",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushMutedGray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        statusRow.Children.Add(_statusDot);
        statusRow.Children.Add(_statusText);

        _livePreviewText = new TextBlock
        {
            Text = "Ouvindo ao vivo...",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = BrushWhiteText,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _textStack.Children.Add(statusRow);
        _textStack.Children.Add(_livePreviewText);
        Grid.SetColumn(_textStack, 1);
        _middleVisorGrid.Children.Add(_textStack);

        Grid.SetColumn(_middleVisorGrid, 1);
        mainGrid.Children.Add(_middleVisorGrid);

        // 3. Right Controls: CursorLoop Mode + Minimize to Tray
        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _rewriteToggleButton = CreateSmallActionButton("✨ 6 ON", "Barra Lateral Nativa");
        _rewriteToggleButton.Visibility = Visibility.Collapsed;

        _cursorLoopButton = CreateSmallActionButton(
            _cursorSettings.GetModePillText(),
            "Alternar modo CursorLoop: 1.8x ➔ 2.8x ➔ 4.5x ➔ OFF (Borda infinita + Touchpad • Atalhos: Ctrl+Alt+1/2/3/0)");
        _cursorLoopButton.Foreground = _cursorSettings.Enabled ? BrushGreenReady : BrushMutedGray;
        _cursorLoopButton.Click += (_, _) => CycleCursorLoopModeOnBar();
        if (!CursorLoopSettings.IsCursorLoopSupported)
        {
            _cursorLoopButton.Visibility = Visibility.Collapsed;
        }

        _minButton = CreateSmallActionButton("—", "Minimizar para a bandeja");
        _minButton.Visibility = Visibility.Collapsed;

        var closeButton = CreateSmallActionButton("—", "Minimizar a barra lateral para o ícone da bandeja do relógio (clique no ícone da bandeja para restaurar)");
        closeButton.Click += (_, _) => MinimizeToTray();

        actionsPanel.Children.Add(_cursorLoopButton);
        actionsPanel.Children.Add(closeButton);

        Grid.SetColumn(actionsPanel, 2);
        mainGrid.Children.Add(actionsPanel);

        Grid.SetRow(mainGrid, 1);
        rootMorphGrid.Children.Add(mainGrid);

        _outerBorder.Child = rootMorphGrid;
        Content = _outerBorder;

        // 8ms (~120 FPS) Engine Timer: CursorLoop (only on owner machine) + Active Target Tracker
        _cursorTimer.Tick += (_, _) =>
        {
            if (CursorLoopSettings.IsCursorLoopSupported)
            {
                _cursorEngine.Tick(_cursorSettings.Enabled, _cursorSettings.HorizontalLoop, _cursorSettings.VerticalLoop);
            }
            TrackActiveExternalWindow();
        };
        _cursorTimer.Start();

        // Start background listener for IPC events (--cursorloop, Start Menu click)
        StartIpcSignalListener();

        Loaded += async (_, _) =>
        {
            AutoUpdateService.CleanupOldExecutableOnStartup();
            TryRestoreLastConversationState();
            MaximizeToSidebar(ensureListening: false);
            if (openCursorLoopOnStart)
                CycleCursorLoopModeOnBar();

            StartAutoUpdateChecker();

            // Automatically start F8 voice dictation right when the application opens!
            await StartDictationAsync();
        };

        // Wire embedded RewriteDeckView callbacks
        _rewriter.OnVariationsUpdated += variations =>
        {
            _latestVariationsSnapshot = variations;
            if (variations.IsAiRefined)
            {
                _ = Task.Run(() => SaveConversationState(variations));
            }
            Dispatcher.BeginInvoke(() =>
            {
                _rewriteDeck.UpdateVariations(variations);
                _rewriteDeck.SetPreviousBackupAvailable(_previousArchivedVariations != null);
            });
        };

        _rewriteDeck.OnUserDisabled += () =>
        {
            Dispatcher.BeginInvoke(MinimizeToTray);
        };

        _rewriteDeck.OnOptionClicked += (chosenText, label) =>
        {
            _ = ApplyChosenVariationToCursorAsync(chosenText, label);
        };

        _rewriteDeck.OnPredictionChipClicked += wordToAppend =>
        {
            _ = AppendPredictedCompletionToCursorAsync(wordToAppend);
        };

        _rewriteDeck.OnRestorePreviousRequested += () =>
        {
            RestorePreviousArchivedConversation();
        };

        _rewriteDeck.OnResetBlockRequested += () =>
        {
            ArchiveAndAutoClearForNewConversation(updateDeckImmediately: false);
            _rewriteDeck.ResetDisplay();
            _rewriteDeck.SetPreviousBackupAvailable(_previousArchivedVariations != null);
        };

        _rewriteDeck.OnUpdateRequested += () =>
        {
            _ = TriggerOneClickUpdateAsync();
        };

        // Wire audio & Gemini callbacks
        _mic.OnPcmFrame += (pcm, rms) =>
        {
            _gemini.EnqueueAudioPcm16k(pcm, rms);
        };

        _gemini.OnStatusChanged += status =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _statusText.Text = status.ToUpperInvariant();
                if (_isListening)
                {
                    _statusDot.Fill = _gemini.IsStandby ? BrushSkyBlue : BrushRedLive;
                }
            });
        };

        _gemini.OnInterimTranscript += interim =>
        {
            Dispatcher.BeginInvoke(() => HandleInterimTranscript(interim));
        };

        _gemini.OnFinalTranscript += finalText =>
        {
            Dispatcher.BeginInvoke(() => HandleFinalTranscript(finalText));
        };

        _gemini.OnError += err =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _statusDot.Fill = BrushAmberWarn;
                _statusText.Text = "RECUPERANDO AUTOMATICAMENTE: " + err;
            });
        };

        // Fast watchdog
        var watchdog = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        watchdog.Tick += (_, _) =>
        {
            if (!_isListening)
                return;

            DateTime now = DateTime.UtcNow;
            double silenceSecs = (now - _lastInterimUiUtc).TotalSeconds;

            // 1. Auto-seal any hanging interim phrase after 2.8s of silence and lock in the server turn prefix
            // so the server never re-emits those words when the user resumes speaking.
            if (!string.IsNullOrEmpty(_activeCursorInterim) && silenceSecs >= 2.8)
            {
                string sealedChunk = _activeCursorInterim.Trim();
                _lastAutoSealedText = _activeCursorInterim;
                _lastAutoSealedUtc = now;
                _needsLeadingSpace = !_activeCursorInterim.EndsWith(' ') && !_activeCursorInterim.EndsWith('\n');
                _activeCursorInterim = string.Empty;
                _gemini.ClearInterimState();

                if (!string.IsNullOrEmpty(sealedChunk))
                {
                    _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, sealedChunk);
                    _rewriter.RequestRealtimeRewrite(_cachedApiKey, _currentBlockCommittedText, isFinalCommit: true);
                }
            }

            // 2. Automatic 10-Second Silence Cut-Off ("Ponto de Corte de Silêncio"):
            // After ~9.5s of silence, automatically archive the current conversation into '⏪ Anterior'
            // and clear the 6 cards + memory so the next speech starts 100% clean without residue!
            // (If the user's mouse is hovering over the sidebar reading the options, wait until the mouse leaves.)
            if (!string.IsNullOrEmpty(_currentBlockCommittedText) &&
                silenceSecs >= 9.5 &&
                !_rewriteDeck.IsMouseOver &&
                !_isApplyingVariation)
            {
                ArchiveAndAutoClearForNewConversation(
                    updateDeckImmediately: true,
                    customReason: "✂️ PONTO DE CORTE (10s DE SILÊNCIO) • Limpo p/ nova fala (salvo em '⏪ Anterior')");
            }

            // 3. Revive Windows microphone if audio driver stopped delivering frames
            if ((now - _mic.LastFrameUtc).TotalSeconds > 3.2)
            {
                try
                {
                    _mic.Stop();
                    _mic.Start(chunkMs: 40);
                }
                catch { }
            }

            // 4. Ensure WebSocket reconnects if dropped
            if (!_gemini.IsStandby && !_gemini.IsConnected)
            {
                _ = _gemini.TriggerReconnectAsync("Watchdog de sessão");
            }
        };
        watchdog.Start();

        SetupSystemTray();
    }

    /// <summary>
    /// Mode 1: Maximized in the Native Windows Sidebar on the right edge of the screen.
    /// </summary>
    public void MaximizeToSidebar(bool ensureListening = false)
    {
        _rewriteTooltipEnabled = true;
        _isCompact = false;
        if (!IsVisible)
        {
            Show();
        }
        UpdateControlBarLabels();
        ApplyNativeLayout();

        string currentText = CombineBlockText(_currentBlockCommittedText, _activeCursorInterim);
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            string apiKey = !string.IsNullOrWhiteSpace(_cachedApiKey)
                ? _cachedApiKey
                : (NativeInterop.ReadApiKeyFromVault() ?? string.Empty);
            _rewriter.RequestRealtimeRewrite(apiKey, currentText, isFinalCommit: true);
        }

        if (ensureListening && !_isListening)
        {
            _ = StartDictationAsync();
        }
    }

    /// <summary>
    /// Mode 2: Minimized to the System Tray (releases the sidebar reservation and hides the window).
    /// </summary>
    public void MinimizeToTray()
    {
        _rewriteTooltipEnabled = false;
        NativeInterop.UndockNativeSidebarAppBar();
        Hide();
    }

    public void ToggleSidebarOrTray()
    {
        if (IsVisible && _rewriteTooltipEnabled)
        {
            MinimizeToTray();
        }
        else
        {
            MaximizeToSidebar(ensureListening: true);
        }
    }

    private void UpdateControlBarLabels()
    {
        _micButtonLabel.Text = _isListening ? "⏹️ F8" : "🎙️ F8";
        _outerBorder.BorderBrush = BrushDarkBorder;
    }

    /// <summary>
    /// Two-Mode Native Layout Engine:
    /// - When visible: Docked as Native Windows Sidebar AppBar on the right edge (dividing the screen with zero overlap).
    /// - When minimized: Undocked and hidden in the System Tray.
    /// </summary>
    private void ApplyNativeLayout()
    {
        _rewriteDeck.Visibility = Visibility.Visible;
        _middleVisorGrid.Visibility = Visibility.Visible;

        IntPtr myHwnd = new WindowInteropHelper(this).Handle;
        if (myHwnd == IntPtr.Zero)
            return;

        if (_rewriteTooltipEnabled)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            _outerBorder.CornerRadius = new CornerRadius(0);
            _outerBorder.BorderThickness = new Thickness(1, 0, 0, 0);
            _outerBorder.BorderBrush = BrushDarkBorder;

            int desiredWidthPx = (int)Math.Round(_rewriteDeck.PreferredSlotWidthDip * dpi.DpiScaleX);
            NativeInterop.DockNativeSidebarAppBar(myHwnd, desiredWidthPx);
        }
        else
        {
            NativeInterop.UndockNativeSidebarAppBar();
        }
    }

    private static Button CreateSmallActionButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Content = text,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 26)), // #16161A True Dark Card
            Foreground = new SolidColorBrush(Color.FromRgb(228, 228, 231)), // #E4E4E7
            BorderThickness = new Thickness(0),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyRoundedButtonTemplate(btn, 9);
        return btn;
    }

    private static void ApplyRoundedButtonTemplate(Button button, double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;
        button.Template = template;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        // Make window non-activating and toolwindow so it NEVER steals focus and NEVER appears in Taskbar/Alt+Tab!
        int exStyle = NativeInterop.GetWindowLong(hwnd, NativeInterop.GWL_EXSTYLE);
        NativeInterop.SetWindowLong(
            hwnd,
            NativeInterop.GWL_EXSTYLE,
            (exStyle | NativeInterop.WS_EX_TOOLWINDOW | NativeInterop.WS_EX_NOACTIVATE | NativeInterop.WS_EX_TOPMOST));

        // Configure pure hardware-accelerated DWM rendering (zero WS_EX_LAYERED lag)
        NativeInterop.ConfigureNativeHardwareWindow(hwnd, roundedCorners: true, borderColorRef: 0x00282424);

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        ApplyNativeLayout();

        // Register F8 and Ctrl+Shift+Space globally for Voice Dictation
        NativeInterop.RegisterHotKey(hwnd, HOTKEY_ID_F8, NativeInterop.MOD_NOREPEAT, NativeInterop.VK_F8);
        NativeInterop.RegisterHotKey(
            hwnd,
            HOTKEY_ID_CTRL_SHIFT_SPACE,
            NativeInterop.MOD_CONTROL | NativeInterop.MOD_SHIFT | NativeInterop.MOD_NOREPEAT,
            NativeInterop.VK_SPACE);

        if (CursorLoopSettings.IsCursorLoopSupported)
        {
            // Register Ctrl+Alt+0/1/2/3 globally for CursorLoop Touchpad Speed Levels
            uint ctrlAlt = NativeInterop.MOD_CONTROL | NativeInterop.MOD_ALT | NativeInterop.MOD_NOREPEAT;
            NativeInterop.RegisterHotKey(hwnd, HOTKEY_ID_SPEED_0, ctrlAlt, 0x30); // D0
            NativeInterop.RegisterHotKey(hwnd, HOTKEY_ID_SPEED_1, ctrlAlt, 0x31); // D1
            NativeInterop.RegisterHotKey(hwnd, HOTKEY_ID_SPEED_2, ctrlAlt, 0x32); // D2
            NativeInterop.RegisterHotKey(hwnd, HOTKEY_ID_SPEED_3, ctrlAlt, 0x33); // D3
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeInterop.WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeInterop.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeInterop.MA_NOACTIVATE);
        }

        if (msg == NativeInterop.APPBAR_CALLBACK_MSG && wParam.ToInt32() == NativeInterop.ABN_POSCHANGED)
        {
            if (_rewriteTooltipEnabled)
            {
                NativeInterop.HandleAppBarPosChanged(hwnd);
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeInterop.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_ID_F8 || id == HOTKEY_ID_CTRL_SHIFT_SPACE)
            {
                handled = true;
                _ = ToggleDictationAsync();
            }
            else if (id is >= HOTKEY_ID_SPEED_0 and <= HOTKEY_ID_SPEED_3)
            {
                handled = true;
                int level = id - HOTKEY_ID_SPEED_0;
                SetCursorLoopLevelOnBar(level);
            }
        }
        return IntPtr.Zero;
    }

    public async Task ToggleDictationAsync()
    {
        if (_isListening)
        {
            await StopDictationAsync();
        }
        else
        {
            await StartDictationAsync();
        }
    }

    public async Task StartDictationAsync()
    {
        if (_isListening)
            return;

        string? apiKey = NativeInterop.ReadApiKeyFromVault();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _statusDot.Fill = BrushRedLive;
            _statusText.Text = "CHAVE AUSENTE NO COFRE • Copie a chave (Ctrl+C) e importe pelo ícone da bandeja";
            return;
        }

        _cachedApiKey = apiKey;
        _isListening = true;
        _activeCursorInterim = string.Empty;
        _lastAutoSealedText = string.Empty;
        _needsLeadingSpace = false;

        if (!IsVisible)
            Show();

        // Update UI to active listening state (True Dark border without blue frame)
        _micButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
        _micButtonLabel.Text = (_isCompact || _rewriteTooltipEnabled) ? "⏹️ F8" : "⏹️ Parar (F8)";
        _outerBorder.BorderBrush = BrushDarkBorder;
        _statusDot.Fill = BrushRedLive;
        _statusText.Text = "OUVINDO AO VIVO • Qualidade máxima sem reescrita (gemini-3.5-transcribe-live)";
        _livePreviewText.Text = "Estou ouvindo... fale agora.";
        _livePreviewText.Foreground = BrushWhiteText;

        try
        {
            // Start microphone immediately with 40ms frames for clean acoustic context
            _mic.Start(chunkMs: 40);
            await _gemini.StartSessionAsync(apiKey, "pt-BR");
        }
        catch (Exception ex)
        {
            _isListening = false;
            _mic.Stop();
            ResetIdleVisuals($"Erro ao iniciar: {ex.Message}");
        }
    }

    public async Task StopDictationAsync()
    {
        if (!_isListening)
            return;

        _isListening = false;
        _statusText.Text = "FINALIZANDO ÚLTIMAS PALAVRAS...";

        // Capture a brief 180ms tail so final syllable is never clipped
        await Task.Delay(180);
        _mic.Stop();
        ResetWaveformBars();

        await _gemini.StopAndFlushAsync();

        ResetIdleVisuals($"PRONTO • {_wordsTypedCount} palavras digitadas • Aperte F8 para falar");
    }

    private static string GetConversationStatePath()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PROJETOS_RUNTIME",
            "voz-windows");
        Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, "last_conversation_state.json");
    }

    private void SaveConversationState(RewriteVariations v)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(v.RawOriginal))
                return;

            string json = System.Text.Json.JsonSerializer.Serialize(v);
            File.WriteAllText(GetConversationStatePath(), json, Encoding.UTF8);
        }
        catch { }
    }

    private void TryRestoreLastConversationState()
    {
        try
        {
            string path = GetConversationStatePath();
            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path, Encoding.UTF8);
            var restored = System.Text.Json.JsonSerializer.Deserialize<RewriteVariations>(json);
            if (restored != null && !string.IsNullOrWhiteSpace(restored.RawOriginal))
            {
                if (string.Equals(restored.DiretaCoesa, restored.FormalPolida, StringComparison.OrdinalIgnoreCase))
                {
                    restored = GeminiRewriteClient.BuildLocalSixVariations(
                        restored.RawOriginal,
                        restored.SugestoesCompletar,
                        isAiRefined: true);
                }

                // Store the last session's message inside '⏪ Anterior' so the active panel starts 100% clean without residue!
                _previousArchivedVariations = restored;
                GeminiRewriteClient.LearnFromUserText(restored.RawOriginal);
                _rewriteDeck.ResetDisplay();
                _rewriteDeck.SetPreviousBackupAvailable(true);
            }
        }
        catch { }
    }

    private void ArchiveAndAutoClearForNewConversation(bool updateDeckImmediately, string? customReason = null)
    {
        if (_latestVariationsSnapshot != null && !string.IsNullOrWhiteSpace(_latestVariationsSnapshot.RawOriginal))
        {
            _previousArchivedVariations = _latestVariationsSnapshot;
        }
        else if (!string.IsNullOrWhiteSpace(_currentBlockCommittedText))
        {
            _previousArchivedVariations = GeminiRewriteClient.BuildLocalSixVariations(
                _currentBlockCommittedText,
                isAiRefined: true);
        }

        _latestVariationsSnapshot = null;
        _hasAppliedInCurrentCycle = false;
        _lastAppliedUtc = DateTime.MinValue;
        _currentBlockCommittedText = string.Empty;
        _activeCursorInterim = string.Empty;
        _lastAutoSealedText = string.Empty;
        _needsLeadingSpace = false;

        // Cut off the server's audio turn prefix and cancel any in-flight AI rewrite so zero residue can ever return
        _gemini.CutOffServerTurn();
        _rewriter.ResetMemoryCache();
        DeleteSavedConversationState();

        bool hasBackup = _previousArchivedVariations != null;
        if (updateDeckImmediately)
        {
            _rewriteDeck.ShowAutoClearedReadyForNewSpeech(hasBackup, customReason);
            _livePreviewText.Text = customReason ?? "✨ Conversa limpa automaticamente • Fale a próxima mensagem";
            _livePreviewText.Foreground = BrushGreenReady;
            _statusText.Text = "✨ PAINEL LIMPO AUTOMATICAMENTE • PRONTO PARA NOVA CONVERSA";
        }
        else
        {
            _rewriteDeck.ResetDisplay();
            _rewriteDeck.SetPreviousBackupAvailable(hasBackup);
        }
    }

    private void RestorePreviousArchivedConversation()
    {
        if (_previousArchivedVariations == null || string.IsNullOrWhiteSpace(_previousArchivedVariations.RawOriginal))
            return;

        RewriteVariations restored = _previousArchivedVariations;
        _latestVariationsSnapshot = restored;
        _currentBlockCommittedText = restored.RawOriginal;
        _needsLeadingSpace = !restored.RawOriginal.EndsWith(' ') && !restored.RawOriginal.EndsWith('\n');
        _hasAppliedInCurrentCycle = false;
        _lastAppliedUtc = DateTime.MinValue;
        _lastInterimUiUtc = DateTime.UtcNow;

        SaveConversationState(restored);
        _rewriteDeck.UpdateVariations(restored);
        _rewriteDeck.SetPreviousBackupAvailable(true);
        _livePreviewText.Text = "⏪ Recuperada: " + RewriteDeckView.FormatRollingTailForCard(restored.RawOriginal, 130);
        _livePreviewText.Foreground = BrushGreenReady;
        _statusText.Text = "⏪ CONVERSA ANTERIOR RECUPERADA E PROTEGIDA";
    }

    private static void DeleteSavedConversationState()
    {
        try
        {
            string path = GetConversationStatePath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private bool _wasEnterDown;

    private void TrackActiveExternalWindow()
    {
        bool enterDown = NativeInterop.IsPlainEnterPressed();
        if (enterDown && !_wasEnterDown)
        {
            // Pressing plain Enter sends the message in chats/editors:
            // seal any open interim into the current block, archive it to '⏪ Anterior', and immediately clear for the next conversation!
            if (!string.IsNullOrEmpty(_activeCursorInterim))
            {
                string sealedChunk = _activeCursorInterim.Trim();
                _activeCursorInterim = string.Empty;
                if (!string.IsNullOrEmpty(sealedChunk))
                {
                    _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, sealedChunk);
                }
            }

            if (_hasAppliedInCurrentCycle || !string.IsNullOrWhiteSpace(_currentBlockCommittedText))
            {
                ArchiveAndAutoClearForNewConversation(
                    updateDeckImmediately: true,
                    customReason: "✨ ENVIADA (ENTER) E LIMPA • Pronta p/ próxima fala (salvo em '⏪ Anterior')");
            }
        }
        _wasEnterDown = enterDown;

        IntPtr fg = NativeInterop.GetForegroundWindowHandle();
        if (fg == IntPtr.Zero)
            return;

        IntPtr myHwnd = new WindowInteropHelper(this).Handle;
        if (fg == myHwnd)
            return;

        // Ignore desktop/taskbar/shell windows when minimizing or switching screens
        if (!NativeInterop.IsEligibleExternalWindowOnSameMonitor(fg, IntPtr.Zero))
            return;

        if (_lastTargetWindowHwnd != IntPtr.Zero && fg != _lastTargetWindowHwnd)
        {
            // User clicked another window: seal any open interim into _currentBlockCommittedText and lock server turn prefix
            if (!string.IsNullOrEmpty(_activeCursorInterim))
            {
                string sealedChunk = _activeCursorInterim.Trim();
                _lastAutoSealedText = _activeCursorInterim;
                _lastAutoSealedUtc = DateTime.UtcNow;
                _activeCursorInterim = string.Empty;
                _gemini.ClearInterimState();
                if (!string.IsNullOrEmpty(sealedChunk))
                {
                    _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, sealedChunk);
                }
            }
        }
        _lastTargetWindowHwnd = fg;
    }

    private static string CombineBlockText(string committed, string currentChunk)
    {
        string result;
        if (string.IsNullOrWhiteSpace(committed))
            result = currentChunk.Trim();
        else if (string.IsNullOrWhiteSpace(currentChunk))
            result = committed.Trim();
        else
            result = committed.Trim() + " " + currentChunk.Trim();

        // High-capacity memory ceiling (250,000 characters ~ 40,000+ words)
        if (result.Length > 250_000)
        {
            result = result[^220_000..].TrimStart();
        }
        return result;
    }

    private char GetPreviousCommittedLastChar()
    {
        if (string.IsNullOrWhiteSpace(_currentBlockCommittedText))
            return '\0';

        for (int i = _currentBlockCommittedText.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(_currentBlockCommittedText[i]))
                return _currentBlockCommittedText[i];
        }
        return '\0';
    }

    private void HandleInterimTranscript(string interim)
    {
        DateTime now = DateTime.UtcNow;
        if (_isApplyingVariation || now < _ignoreSpeechUntilUtc)
            return;

        TrackActiveExternalWindow();
        double silenceBeforeSpeechSecs = (now - _lastInterimUiUtc).TotalSeconds;
        _lastInterimUiUtc = now;

        // Smart Auto-Clear Cut-Off on New Speech:
        // If an option was applied OR the user stayed silent >= 8 seconds before resuming speech,
        // archive the previous conversation into '⏪ Anterior' and start this new speech 100% clean from zero!
        if (_hasAppliedInCurrentCycle ||
            (!string.IsNullOrWhiteSpace(_currentBlockCommittedText) && silenceBeforeSpeechSecs >= 8.0))
        {
            ArchiveAndAutoClearForNewConversation(updateDeckImmediately: false);
        }

        // If the server started a brand-new short interim turn while a long previous interim was still open,
        // seal the previous phrase in place and record it in _lastAutoSealedText so a late FinalTranscript won't duplicate it.
        if (_activeCursorInterim.Length >= 14 &&
            interim.Trim().Length <= _activeCursorInterim.Length / 2 &&
            !_activeCursorInterim.TrimStart().StartsWith(interim.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            string sealedPrev = _activeCursorInterim.Trim();
            _lastAutoSealedText = _activeCursorInterim;
            _lastAutoSealedUtc = now;
            _needsLeadingSpace = !_activeCursorInterim.EndsWith(' ') && !_activeCursorInterim.EndsWith('\n');
            _activeCursorInterim = string.Empty;
            if (!string.IsNullOrEmpty(sealedPrev))
            {
                _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, sealedPrev);
            }
        }

        string normalized = GeminiRewriteClient.NormalizeForLiveCursor(interim, GetPreviousCommittedLastChar());
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        _livePreviewText.Text = "🗣️ " + RewriteDeckView.FormatRollingTailForCard(normalized, 130);
        _livePreviewText.Foreground = BrushSkyBlue;

        if (_liveCursorMode)
        {
            string target = (_needsLeadingSpace ? " " : string.Empty) + normalized;
            _activeCursorInterim = SyncToCursor(_activeCursorInterim, target, isFinal: false);
        }

        if (_rewriteTooltipEnabled)
        {
            string fullBlock = CombineBlockText(_currentBlockCommittedText, normalized);
            _rewriteDeck.SetConvertingLiveState(fullBlock.Length, fullBlock);

            if ((now - _lastInterimRewriteUtc).TotalMilliseconds >= 320)
            {
                _lastInterimRewriteUtc = now;
                _rewriter.RequestRealtimeRewrite(_cachedApiKey, fullBlock, isFinalCommit: false);
            }
        }
    }

    private static bool WasAlreadyAutoSealed(string cleanedFinal, string lastAutoSealed, DateTime lastAutoSealedUtc)
    {
        if (string.IsNullOrWhiteSpace(lastAutoSealed) || (DateTime.UtcNow - lastAutoSealedUtc).TotalSeconds > 12.0)
            return false;

        string a = cleanedFinal.Trim().TrimEnd('.', '!', '?', ',', ';');
        string b = lastAutoSealed.Trim().TrimEnd('.', '!', '?', ',', ';');
        if (a.Length < 4 || b.Length < 4)
            return false;

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith(b, StringComparison.OrdinalIgnoreCase) && Math.Abs(a.Length - b.Length) <= 8;
    }

    private void HandleFinalTranscript(string finalText)
    {
        DateTime now = DateTime.UtcNow;
        if (_isApplyingVariation || now < _ignoreSpeechUntilUtc)
            return;

        TrackActiveExternalWindow();
        double silenceBeforeSpeechSecs = (now - _lastInterimUiUtc).TotalSeconds;
        _lastInterimUiUtc = now;

        // Smart Auto-Clear Cut-Off on New Speech:
        if (_hasAppliedInCurrentCycle ||
            (!string.IsNullOrWhiteSpace(_currentBlockCommittedText) && silenceBeforeSpeechSecs >= 8.0))
        {
            ArchiveAndAutoClearForNewConversation(updateDeckImmediately: false);
        }

        string cleaned = GeminiRewriteClient.NormalizeForLiveCursor(finalText, GetPreviousCommittedLastChar());
        if (string.IsNullOrEmpty(cleaned))
        {
            _activeCursorInterim = string.Empty;
            return;
        }

        // If this phrase was already auto-sealed when a new interim turn started (or after 2.8s watchdog),
        // do NOT wipe the new _activeCursorInterim and do NOT duplicate it in _currentBlockCommittedText!
        if (WasAlreadyAutoSealed(cleaned, _lastAutoSealedText, _lastAutoSealedUtc))
        {
            _lastAutoSealedText = string.Empty;
            return;
        }

        _livePreviewText.Text = "✓ " + RewriteDeckView.FormatRollingTailForCard(cleaned, 130);
        _livePreviewText.Foreground = BrushWhiteText;

        if (_liveCursorMode)
        {
            string onScreen = _activeCursorInterim;
            string target = (_needsLeadingSpace ? " " : string.Empty) + cleaned;
            string finalOnScreen = SyncToCursor(onScreen, target, isFinal: true);
            _activeCursorInterim = string.Empty;
            _lastAutoSealedText = string.Empty;
            _needsLeadingSpace = !finalOnScreen.EndsWith(' ') && !finalOnScreen.EndsWith('\n');
        }
        else
        {
            string toType = (_needsLeadingSpace ? " " : string.Empty) + cleaned;
            NativeInterop.TypeUnicodeText(toType);
            _needsLeadingSpace = !cleaned.EndsWith(' ') && !cleaned.EndsWith('\n');
        }

        _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, cleaned);
        if (_rewriteTooltipEnabled)
        {
            _rewriter.RequestRealtimeRewrite(_cachedApiKey, _currentBlockCommittedText, isFinalCommit: true);
        }

        _sessionTranscript = string.IsNullOrEmpty(_sessionTranscript)
            ? cleaned
            : _sessionTranscript + " " + cleaned;
        if (_sessionTranscript.Length > 250_000)
        {
            _sessionTranscript = _sessionTranscript[^200_000..];
        }

        int words = Regex.Matches(cleaned, @"\S+").Count;
        _wordsTypedCount += words;
        _statusText.Text = _rewriteTooltipEnabled
            ? $"DIGITADO NO CURSOR ({_wordsTypedCount} palavras) • Clique numa das 6 opções acima para substituir"
            : $"DIGITADO NO CURSOR ({_wordsTypedCount} palavras)";
    }

    private async Task ApplyChosenVariationToCursorAsync(string chosenText, string label)
    {
        if (string.IsNullOrWhiteSpace(chosenText) || _isApplyingVariation)
            return;

        _isApplyingVariation = true;
        try
        {
            // 1. Cut off any active interim speech so it doesn't interfere with the replacement
            _activeCursorInterim = string.Empty;
            _lastAutoSealedText = string.Empty;
            _ignoreSpeechUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
            _gemini.ClearInterimState();

            // 2. Put the chosen text into the Clipboard so it pastes all at once in a single shot
            try
            {
                Clipboard.SetDataObject(chosenText, true);
            }
            catch
            {
                try { Clipboard.SetText(chosenText); } catch { }
            }

            // 3. Ensure the target editor/chat window is in the foreground before sending Ctrl+A -> Ctrl+V
            IntPtr myHwnd = new WindowInteropHelper(this).Handle;
            IntPtr currentFg = NativeInterop.GetForegroundWindowHandle();

            if (_lastTargetWindowHwnd != IntPtr.Zero &&
                (currentFg == IntPtr.Zero || currentFg == myHwnd || currentFg != _lastTargetWindowHwnd))
            {
                NativeInterop.ForceForegroundWindow(_lastTargetWindowHwnd);
                await Task.Delay(35);
            }

            // 4. Select all text in the active input box (Ctrl+A) and paste the entire chosen text at once (Ctrl+V)
            NativeInterop.SendSelectAll();
            await Task.Delay(20);
            NativeInterop.SendPasteClipboard();

            // 5. IMMEDIATELY auto-clear the 6 cards after use (saving the used conversation into '⏪ Anterior')!
            ArchiveAndAutoClearForNewConversation(
                updateDeckImmediately: true,
                customReason: $"☑ APLICADA ({label.ToUpperInvariant()}) E LIMPA • Fale a próxima (ou clique em '⏪ Anterior')");

            _livePreviewText.Text = $"☑ [{label}] Aplicada e limpa • Fale a próxima mensagem";
            _livePreviewText.Foreground = BrushGreenReady;
            _statusText.Text = $"☑ APLICADA ({label.ToUpperInvariant()}) E LIMPA AUTOMATICAMENTE • PRONTO P/ NOVA FALA";
        }
        finally
        {
            _isApplyingVariation = false;
        }
    }

    private async Task AppendPredictedCompletionToCursorAsync(string wordToAppend)
    {
        string cleanedWord = (wordToAppend ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanedWord) || _isApplyingVariation)
            return;

        _isApplyingVariation = true;
        try
        {
            // Extending the sentence via a completion chip means the user is still composing this message
            _hasAppliedInCurrentCycle = false;

            // 1. If the user was mid-sentence in an open interim turn, seal the interim words in place first
            if (!string.IsNullOrEmpty(_activeCursorInterim))
            {
                string sealedPrev = _activeCursorInterim.Trim();
                _lastAutoSealedText = _activeCursorInterim;
                _lastAutoSealedUtc = DateTime.UtcNow;
                _needsLeadingSpace = !_activeCursorInterim.EndsWith(' ') && !_activeCursorInterim.EndsWith('\n');
                _activeCursorInterim = string.Empty;
                _gemini.ClearInterimState();
                if (!string.IsNullOrEmpty(sealedPrev))
                {
                    _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, sealedPrev);
                }
            }

            // 2. Focus the active target window if needed
            IntPtr myHwnd = new WindowInteropHelper(this).Handle;
            IntPtr currentFg = NativeInterop.GetForegroundWindowHandle();

            if (_lastTargetWindowHwnd != IntPtr.Zero &&
                (currentFg == IntPtr.Zero || currentFg == myHwnd || currentFg != _lastTargetWindowHwnd))
            {
                NativeInterop.ForceForegroundWindow(_lastTargetWindowHwnd);
                await Task.Delay(25);
            }

            // 3. Type the predicted completion directly at the active cursor position
            bool needsSpace = _needsLeadingSpace || !string.IsNullOrEmpty(_currentBlockCommittedText);
            string textToType = (needsSpace ? " " : string.Empty) + cleanedWord;
            NativeInterop.TypeUnicodeText(textToType);

            _needsLeadingSpace = !cleanedWord.EndsWith(' ') && !cleanedWord.EndsWith('\n');
            _currentBlockCommittedText = CombineBlockText(_currentBlockCommittedText, cleanedWord);

            int addedWords = Regex.Matches(cleanedWord, @"\S+").Count;
            _wordsTypedCount += addedWords;

            _livePreviewText.Text = "🔮 + " + RewriteDeckView.FormatRollingTailForCard(_currentBlockCommittedText, 130);
            _livePreviewText.Foreground = BrushGreenReady;
            _statusText.Text = $"🔮 COMPLETADO NO CURSOR: \"{cleanedWord}\"";

            // 4. Immediately advance next-word suggestions and refresh the 6 variations
            if (_rewriteTooltipEnabled)
            {
                _rewriter.RequestRealtimeRewrite(_cachedApiKey, _currentBlockCommittedText, isFinalCommit: true);
            }
        }
        finally
        {
            _isApplyingVariation = false;
        }
    }

    /// <summary>
    /// Never-Stuck Sliding Tail-Anchor Cursor Synchronizer.
    /// Ensures that even during very long sentences where Google ASR revises the last 1-3 words or inserts
    /// earlier punctuation, the active cursor ALWAYS receives the newly spoken words and NEVER freezes.
    /// </summary>
    private static string SyncToCursor(string currentTyped, string newTarget, bool isFinal)
    {
        if (string.IsNullOrEmpty(newTarget))
            return currentTyped;

        if (string.IsNullOrEmpty(currentTyped))
        {
            NativeInterop.TypeUnicodeText(newTarget);
            return newTarget;
        }

        if (string.Equals(currentTyped, newTarget, StringComparison.OrdinalIgnoreCase))
            return newTarget;

        // 1. Pure extension (case-insensitive prefix match): append only the new suffix (0 backspaces)
        if (newTarget.Length > currentTyped.Length &&
            newTarget.StartsWith(currentTyped, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = newTarget[currentTyped.Length..];
            NativeInterop.TypeUnicodeText(suffix);
            return newTarget;
        }

        // 2. Common prefix with local tail revision (up to 28 chars at the tip of the cursor):
        // Handles cases where ASR refines the last 1-3 words as the user speaks (e.g. "na tela" -> "no aplicativo e...").
        int common = 0;
        int minLen = Math.Min(currentTyped.Length, newTarget.Length);
        while (common < minLen &&
               char.ToLowerInvariant(currentTyped[common]) == char.ToLowerInvariant(newTarget[common]))
        {
            common++;
        }

        int backspacesFromCommon = currentTyped.Length - common;
        if (backspacesFromCommon <= 28)
        {
            if (backspacesFromCommon > 0)
            {
                NativeInterop.SendBackspaces(backspacesFromCommon);
            }
            string suffix = newTarget[common..];
            if (!string.IsNullOrEmpty(suffix))
            {
                NativeInterop.TypeUnicodeText(suffix);
            }
            return newTarget;
        }

        // 3. Sliding Tail-Anchor Alignment:
        // When a sentence is long (> 30 chars) and ASR inserted a comma or changed capitalization near the start
        // (so common prefix is small), slide back up to 28 chars from the end of currentTyped to find where
        // the end of currentTyped aligns inside newTarget.
        string cleanCurrent = currentTyped.TrimEnd('.', ',', ';', ':', '!', '?', ' ');
        int trailingPunctCount = currentTyped.Length - cleanCurrent.Length;

        if (cleanCurrent.Length >= 4)
        {
            int maxTailCut = Math.Min(28, cleanCurrent.Length - 4);
            for (int tailCut = 0; tailCut <= maxTailCut; tailCut++)
            {
                int searchEnd = cleanCurrent.Length - tailCut;
                int maxAnchor = Math.Min(22, searchEnd);
                for (int len = maxAnchor; len >= 5; len--)
                {
                    string anchor = cleanCurrent.Substring(searchEnd - len, len);
                    int idx = newTarget.LastIndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int matchEnd = idx + len;
                        if (matchEnd >= Math.Max(searchEnd - 15, newTarget.Length / 3))
                        {
                            int backspacesToSend = trailingPunctCount + tailCut;
                            if (backspacesToSend > 0)
                            {
                                NativeInterop.SendBackspaces(backspacesToSend);
                            }

                            string tail = newTarget[matchEnd..];
                            if (!string.IsNullOrEmpty(tail))
                            {
                                NativeInterop.TypeUnicodeText(tail);
                            }
                            // Always return newTarget so our state is 100% synced and never gets stuck on old words!
                            return newTarget;
                        }
                    }
                }
            }
        }

        // 4. Guaranteed Forward-Progress Fallback:
        // If newTarget grew longer than currentTyped and no anchor matched, type the newly added tail words
        // and synchronize state to newTarget so the main text box NEVER stops registering speech.
        if (newTarget.Length > currentTyped.Length)
        {
            int startIdx = currentTyped.Length;
            while (startIdx > 0 && startIdx < newTarget.Length &&
                   !char.IsWhiteSpace(newTarget[startIdx - 1]) && !char.IsWhiteSpace(newTarget[startIdx]))
            {
                startIdx--;
            }

            string newWords = newTarget[startIdx..];
            if (!currentTyped.EndsWith(' ') && !newWords.StartsWith(' '))
            {
                newWords = " " + newWords;
            }
            NativeInterop.TypeUnicodeText(newWords);
            return newTarget;
        }

        if (isFinal && newTarget.Length > 0)
        {
            char lastChar = newTarget[^1];
            if ((lastChar == '.' || lastChar == '?' || lastChar == '!') &&
                !currentTyped.EndsWith('.') && !currentTyped.EndsWith('?') && !currentTyped.EndsWith('!'))
            {
                NativeInterop.TypeUnicodeText(lastChar.ToString());
            }
        }

        return newTarget;
    }

    private void ImportKeyFromClipboard()
    {
        try
        {
            string clip = (Clipboard.GetText() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clip) || (!clip.StartsWith("AQ.") && !clip.StartsWith("AIza") && clip.Length < 20))
            {
                _statusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                _statusText.Text = "COPIE UMA CHAVE VÁLIDA (AQ. ou AIza) NO CTRL+C ANTES DE CLICAR EM 🔑 CHAVE";
                return;
            }

            if (NativeInterop.WriteApiKeyToVault(clip))
            {
                _statusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                _statusText.Text = $"CHAVE ATUALIZADA NO COFRE DO WINDOWS ({clip[..3]}*** • {clip.Length} chars)";
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = "ERRO AO SALVAR CHAVE: " + ex.Message;
        }
    }

    private void UpdateWaveformBars(double rms)
    {
        double boosted = Math.Min(1.0, rms * 9.5);
        for (int i = 0; i < _bars.Length; i++)
        {
            double wave = 6 + boosted * 26 * (0.45 + 0.55 * _rng.NextDouble());
            _bars[i].Height = Math.Max(5, Math.Min(32, wave));
        }
    }

    private void ResetWaveformBars()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height = 6;
        }
    }

    private void ResetIdleVisuals(string statusMsg)
    {
        _micButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
        _micButtonLabel.Text = (_isCompact || _rewriteTooltipEnabled) ? "🎙️ F8" : "🎙️ Narrar (F8)";
        _outerBorder.BorderBrush = BrushDarkBorder;
        _statusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        _statusText.Text = statusMsg;
    }

    public void CycleCursorLoopModeOnBar()
    {
        _cursorSettings.CycleModeOnBar();
        ApplyCursorLoopToBar();
    }

    public void SetCursorLoopLevelOnBar(int level)
    {
        _cursorSettings.SetLevelDirect(level);
        ApplyCursorLoopToBar();
    }

    private void ApplyCursorLoopToBar()
    {
        _cursorEngine.SpeedMultiplier = _cursorSettings.GetEffectiveMultiplier();
        _cursorEngine.SmartPrecision = _cursorSettings.SmartPrecision;
        _cursorLoopButton.Content = _cursorSettings.GetModePillText();
        _cursorLoopButton.Foreground = _cursorSettings.Enabled
            ? BrushGreenReady
            : new SolidColorBrush(Color.FromRgb(148, 163, 184));

        if (!_isListening)
        {
            _statusDot.Fill = _cursorSettings.Enabled ? BrushGreenReady : BrushAmberWarn;
            _statusText.Text = _cursorSettings.Enabled
                ? $"MODO CURSORLOOP HABILITADO ({_cursorSettings.GetEffectiveMultiplier():0.0}x + Borda Infinita) • Clique no botão para alternar"
                : "MODO CURSORLOOP DESATIVADO (1.0x Padrão) • Clique em ⚪ Loop OFF para reativar";
        }
        UpdateTrayTooltip();
    }

    private void StartIpcSignalListener()
    {
        var handles = new List<System.Threading.WaitHandle>();
        if (_showUiEvent != null) handles.Add(_showUiEvent);
        if (_cursorLoopEvent != null) handles.Add(_cursorLoopEvent);
        if (_legacyLoopEvent != null) handles.Add(_legacyLoopEvent);

        if (handles.Count == 0)
            return;

        var waitArray = handles.ToArray();
        var ipcThread = new System.Threading.Thread(() =>
        {
            while (!_isClosingApp)
            {
                try
                {
                    int idx = System.Threading.WaitHandle.WaitAny(waitArray, 500);
                    if (_isClosingApp || idx == System.Threading.WaitHandle.WaitTimeout)
                        continue;

                    var triggered = waitArray[idx];
                    if (ReferenceEquals(triggered, _showUiEvent))
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            MaximizeToSidebar(ensureListening: true);
                        });
                    }
                    else if (ReferenceEquals(triggered, _cursorLoopEvent) || ReferenceEquals(triggered, _legacyLoopEvent))
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            MaximizeToSidebar(ensureListening: false);
                            CycleCursorLoopModeOnBar();
                        });
                    }
                }
                catch
                {
                    if (_isClosingApp) break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "VozWindowsSuiteIpcListener"
        };
        ipcThread.Start();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon == null) return;
        _trayIcon.Text = $"VozWindows • Clique para Maximizar/Minimizar Barra Lateral • {_cursorSettings.GetModePillText()}";
    }

    private void SetupSystemTray()
    {
        System.Drawing.Icon trayIco = System.Drawing.SystemIcons.Application;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                trayIco = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;
            }
        }
        catch { }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = CursorLoopSettings.IsCursorLoopSupported
                ? $"VozWindows • Clique para Maximizar/Minimizar Barra Lateral • {_cursorSettings.GetModePillText()}"
                : "VozWindows • Clique para Maximizar/Minimizar Barra Lateral (F8 para Ditado)",
            Icon = trayIco,
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("📌 Maximizar na Barra Lateral / Minimizar para a Bandeja", null, (_, _) => Dispatcher.BeginInvoke(ToggleSidebarOrTray));
        menu.Items.Add("🎙️ Iniciar/Parar Ditado por Voz (F8)", null, (_, _) => Dispatcher.BeginInvoke(async () => await ToggleDictationAsync()));
        if (CursorLoopSettings.IsCursorLoopSupported)
        {
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("🟢 Alternar Modo CursorLoop na Barra", null, (_, _) => Dispatcher.BeginInvoke(CycleCursorLoopModeOnBar));
            menu.Items.Add("   • Touchpad Nível 1 (1.8x) [Ctrl+Alt+1]", null, (_, _) => Dispatcher.BeginInvoke(() => SetCursorLoopLevelOnBar(1)));
            menu.Items.Add("   • Touchpad Nível 2 (2.8x) [Ctrl+Alt+2]", null, (_, _) => Dispatcher.BeginInvoke(() => SetCursorLoopLevelOnBar(2)));
            menu.Items.Add("   • Touchpad Nível 3 (4.5x) [Ctrl+Alt+3]", null, (_, _) => Dispatcher.BeginInvoke(() => SetCursorLoopLevelOnBar(3)));
            menu.Items.Add("   • Desativar CursorLoop (OFF) [Ctrl+Alt+0]", null, (_, _) => Dispatcher.BeginInvoke(() => SetCursorLoopLevelOnBar(0)));
        }
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add($"🔄 Verificar Atualização (Atual: v{AutoUpdateService.CurrentVersionString})", null, (_, _) => Dispatcher.BeginInvoke(async () => await CheckForUpdateBackgroundAsync(showUpToDateFeedback: true)));
        menu.Items.Add("👁️ Pré-visualizar Aviso de Atualização (Simulação)", null, (_, _) => Dispatcher.BeginInvoke(SimulateUpdateNotificationPreview));
        menu.Items.Add("🔑 Atualizar Chave da Área de Transferência", null, (_, _) => Dispatcher.BeginInvoke(ImportKeyFromClipboard));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("✕ Sair de Tudo", null, (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            await StopDictationAsync();
            Close();
        }));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(ToggleSidebarOrTray);
            }
        };
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsVisible)
                    ToggleSidebarOrTray();
            });
        };

        PromoteTrayIconNextToClock();
    }

    private static void PromoteTrayIconNextToClock()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root == null) return;
            foreach (string subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName, writable: true);
                string? path = sub?.GetValue("ExecutablePath") as string;
                if (!string.IsNullOrEmpty(path) && path.Contains("VozWindows", StringComparison.OrdinalIgnoreCase))
                {
                    sub!.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
        }
        catch { }
    }

    public async Task RunAutomatedLiveProofAsync(string wavPath, string screenshotPath)
    {
        // Create a target test editor window above the floating bar to prove live cursor typing
        var editorWin = new Window
        {
            Title = "Bloco de Teste — Digitação por Voz em Qualquer Janela (Cursor Ativo)",
            Width = Width,
            Height = 250,
            Left = Left,
            Top = Top - 265,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = Brushes.White,
            Topmost = true
        };

        var editorStack = new DockPanel { Margin = new Thickness(16) };
        var header = new TextBlock
        {
            Text = "📝 Campo de Texto com Cursor Ativo (Recebendo digitação automática via SendInput do VozWindows):",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);

        var textBox = new TextBox
        {
            FontSize = 18,
            FontWeight = FontWeights.Medium,
            Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true
        };

        editorStack.Children.Add(header);
        editorStack.Children.Add(textBox);
        editorWin.Content = editorStack;
        editorWin.Show();
        IntPtr editorHwnd = new WindowInteropHelper(editorWin).Handle;
        NativeInterop.ForceForegroundWindow(editorHwnd);
        editorWin.Activate();
        textBox.Focus();
        Keyboard.Focus(textBox);

        await Task.Delay(400);

        string? apiKey = NativeInterop.ReadApiKeyFromVault();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Chave não encontrada no Cofre do Windows.");

        _isListening = true;
        _activeCursorInterim = string.Empty;
        _needsLeadingSpace = false;
        _micButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
        _micButtonLabel.Text = "⏹️ Parar (F8)";
        _outerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        _statusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));

        await _gemini.StartSessionAsync(apiKey, "pt-BR");

        // Stream WAV file as 16kHz PCM into Gemini 3.5 Transcribe Live
        byte[] rawWav = await File.ReadAllBytesAsync(wavPath);
        int pcmOffset = 44; // Standard WAV header
        for (int i = pcmOffset; i < rawWav.Length; i += 1280)
        {
            int len = Math.Min(1280, rawWav.Length - i);
            byte[] slice = new byte[len];
            Buffer.BlockCopy(rawWav, i, slice, 0, len);
            _gemini.EnqueueAudioPcm16k(slice);
            UpdateWaveformBars(0.08);
            await Task.Delay(38);
        }

        await _gemini.StopAndFlushAsync();
        await Task.Delay(500);

        _isListening = false;
        ResetIdleVisuals($"PRONTO • {_wordsTypedCount} palavras digitadas no cursor • F8 para narrar");
        UpdateLayout();
        editorWin.UpdateLayout();
        await Task.Delay(250);

        // Render both live WPF windows at native DPI into a single proof image
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int wPx = (int)Math.Round(Width * dpi.DpiScaleX);
        int edHPx = (int)Math.Round(editorWin.Height * dpi.DpiScaleY);
        int pillHPx = (int)Math.Round(Height * dpi.DpiScaleY);
        int gapPx = (int)Math.Round(14 * dpi.DpiScaleY);
        int padPx = (int)Math.Round(20 * dpi.DpiScaleX);

        var rtbEditor = new System.Windows.Media.Imaging.RenderTargetBitmap(
            wPx, edHPx, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtbEditor.Render(editorWin);

        var rtbPill = new System.Windows.Media.Imaging.RenderTargetBitmap(
            wPx, pillHPx, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtbPill.Render(this);

        int totalW = wPx + padPx * 2;
        int totalH = edHPx + gapPx + pillHPx + padPx * 2;

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 13, 22)), null, new Rect(0, 0, totalW / dpi.DpiScaleX, totalH / dpi.DpiScaleY));
            dc.DrawImage(rtbEditor, new Rect(padPx / dpi.DpiScaleX, padPx / dpi.DpiScaleY, Width, editorWin.Height));
            dc.DrawImage(rtbPill, new Rect(padPx / dpi.DpiScaleX, (padPx + edHPx + gapPx) / dpi.DpiScaleY, Width, Height));
        }

        var finalRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            totalW, totalH, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        finalRtb.Render(dv);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(screenshotPath)!);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(finalRtb));
        await using (var fs = new FileStream(screenshotPath, FileMode.Create, FileAccess.Write))
        {
            encoder.Save(fs);
        }

        await Task.Delay(200);
        editorWin.Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosingApp = true;
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeInterop.UnregisterHotKey(hwnd, HOTKEY_ID_F8);
                NativeInterop.UnregisterHotKey(hwnd, HOTKEY_ID_CTRL_SHIFT_SPACE);
                NativeInterop.UnregisterHotKey(hwnd, HOTKEY_ID_SPEED_0);
                NativeInterop.UnregisterHotKey(hwnd, HOTKEY_ID_SPEED_1);
                NativeInterop.UnregisterHotKey(hwnd, HOTKEY_ID_SPEED_2);
                NativeInterop.UnregisterHotKey(hwnd, HOTKEY_ID_SPEED_3);
            }
        }
        catch { }

        try { _cursorTimer.Stop(); _cursorTimer.Dispose(); } catch { }
        try { _mic.Dispose(); } catch { }
        try { _rewriter.Dispose(); } catch { }
        try { NativeInterop.UndockNativeSidebarAppBar(); } catch { }
        try
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
        catch { }

        base.OnClosed(e);
    }

    private void StartAutoUpdateChecker()
    {
        string[] cmdArgs = Environment.GetCommandLineArgs();
        if (Array.Exists(cmdArgs, a => a.Equals("--preview-update", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(SimulateUpdateNotificationPreview);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(3500);
            while (!_isClosingApp)
            {
                await CheckForUpdateBackgroundAsync();
                await Task.Delay(TimeSpan.FromMinutes(10));
            }
        });
    }

    public void SimulateUpdateNotificationPreview()
    {
        var simulated = new UpdateReleaseInfo(
            "v1.2.0 (Simulação)",
            new Version(1, 2, 0),
            "SIMULATED_PREVIEW",
            0,
            "- Melhorias nas 6 opções de linguagem técnica e operacional\n- Correções de estabilidade e desempenho");
        _pendingUpdateRelease = simulated;
        _rewriteDeck.ShowUpdateAvailable(simulated.TagName, simulated.Notes);
        _statusText.Text = "⬆️ NOVA ATUALIZAÇÃO DISPONÍVEL • CLIQUE NO BANNER VERDE ACIMA PARA ACEITAR";
        try
        {
            _trayIcon?.ShowBalloonTip(
                4000,
                $"VozWindows — Nova versão disponível ({simulated.TagName})",
                "Clique no botão verde no topo da barra lateral para revisar e aceitar a atualização.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    private async Task CheckForUpdateBackgroundAsync(bool showUpToDateFeedback = false)
    {
        if (_isUpdatingApp || _isClosingApp)
            return;

        var release = await AutoUpdateService.CheckForUpdateAsync();
        if (release != null)
        {
            bool isNewNotice = _pendingUpdateRelease?.TagName != release.TagName;
            _pendingUpdateRelease = release;
            _ = Dispatcher.BeginInvoke(() =>
            {
                _rewriteDeck.ShowUpdateAvailable(release.TagName, release.Notes);
                _statusText.Text = $"⬆️ ATUALIZAÇÃO {release.TagName} DISPONÍVEL • CLIQUE NO BOTÃO VERDE PARA ACEITAR";
                if (isNewNotice)
                {
                    try
                    {
                        _trayIcon?.ShowBalloonTip(
                            5000,
                            $"VozWindows — Nova atualização ({release.TagName})",
                            "Uma nova versão está disponível. Clique no botão verde na barra lateral para aceitar e atualizar.",
                            System.Windows.Forms.ToolTipIcon.Info);
                    }
                    catch { }
                }
            });
        }
        else if (showUpToDateFeedback)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                _statusText.Text = $"✓ VOCÊ JÁ ESTÁ NA VERSÃO MAIS RECENTE (v{AutoUpdateService.CurrentVersionString})";
            });
        }
    }

    private async Task TriggerOneClickUpdateAsync()
    {
        var release = _pendingUpdateRelease;
        if (release == null || _isUpdatingApp)
            return;

        string notesBlock = string.IsNullOrWhiteSpace(release.Notes)
            ? "• Melhorias gerais de estabilidade e desempenho."
            : release.Notes.Trim();

        var answer = MessageBox.Show(
            this,
            $"Uma nova versão do VozWindows está disponível!\n\n" +
            $"• Sua versão atual: v{AutoUpdateService.CurrentVersionString}\n" +
            $"• Nova versão: {release.TagName}\n\n" +
            $"Novidades:\n{notesBlock}\n\n" +
            $"Deseja baixar e instalar esta atualização agora?",
            "Confirmar Atualização do VozWindows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            _statusText.Text = "ATUALIZAÇÃO ADIADA PELO USUÁRIO • O BOTÃO CONTINUA DISPONÍVEL QUANDO QUISER";
            return;
        }

        if (release.DownloadUrl == "SIMULATED_PREVIEW")
        {
            MessageBox.Show(
                this,
                "Simulação concluída com sucesso!\n\n" +
                "Quando você publicar uma versão real (ex: v1.2.0) no GitHub, clicar em 'Sim' fará o download automático do novo VozWindows.exe e reiniciará o aplicativo atualizado.",
                "Pré-visualização do Auto-Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _pendingUpdateRelease = null;
            _rewriteDeck.HideUpdateBanner();
            _statusText.Text = $"PRONTO • SIMULAÇÃO DE ATUALIZAÇÃO CONCLUÍDA (v{AutoUpdateService.CurrentVersionString})";
            return;
        }

        _isUpdatingApp = true;
        try
        {
            _rewriteDeck.SetUpdateProgress($"⏳ Preparando atualização {release.TagName}...", isBusy: true);
            await StopDictationAsync();

            var (ok, msg) = await AutoUpdateService.DownloadAndApplyUpdateAsync(
                release,
                progressText =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _rewriteDeck.SetUpdateProgress(progressText, isBusy: true);
                        _statusText.Text = progressText.ToUpperInvariant();
                    });
                });

            if (ok)
            {
                _statusText.Text = $"✨ ATUALIZADO PARA {release.TagName} • REINICIANDO...";
                await Task.Delay(250);
                Close();
                Environment.Exit(0);
            }
            else
            {
                _rewriteDeck.SetUpdateProgress($"⚠️ {msg} — Clique para tentar novamente", isBusy: false);
                _statusText.Text = msg;
            }
        }
        finally
        {
            _isUpdatingApp = false;
        }
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
        }
        catch { }
    }
}
