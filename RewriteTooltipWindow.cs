using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VozWindows;

/// <summary>
/// Embedded Live Deck inside MainWindow._outerBorder.
/// Supports two instant modes controlled by a user flag:
/// 1) Modo Nativo Puro (RealtimeRewriteEnabled = false): Zero rewrite CPU/API overhead, direct native dictation.
/// 2) Modo 6 Opções (RealtimeRewriteEnabled = true): 6 rewrite styles on sentence completion.
/// </summary>
public sealed class RewriteDeckView : Border
{
    private const int OptionCount = 6;
    public const double NormalSlotWidthDip = 385;

    private readonly TextBlock _slotTitleText;
    private readonly TextBlock _headerStatusText;
    private readonly Button _btnUpdateBanner;
    private readonly Button _btnToggleModeHeader;
    private readonly Button _btnRestorePrevious;
    private readonly Button _btnOriginal;
    private readonly Grid _cardsGrid;
    private readonly Border _nativeModePanel;
    private readonly TextBlock _nativeLivePreviewBox;
    private readonly TextBlock _currentTopicBadgeText;
    private readonly TextBlock _timelineCountText;
    private readonly Button _btnCopyTimeline;
    private readonly ScrollViewer _timelineScrollViewer;
    private readonly StackPanel _timelineItemsStack;
    private readonly Border _timelineEmptyPlaceholder;

    private sealed record TimelineCardRefs(
        Border CardBorder,
        TextBlock MetaText,
        TextBlock BadgeText,
        TextBlock ThemeTitleText,
        TextBlock BulletsBodyText);

    private readonly Dictionary<long, TimelineCardRefs> _timelineCards = new();
    private readonly List<TimelineLogEntry> _timelineHistory = new();
    private readonly Border[] _cardBorders = new Border[OptionCount];
    private readonly Border[] _cardCheckBorders = new Border[OptionCount];
    private readonly TextBlock[] _cardCheckIcons = new TextBlock[OptionCount];
    private readonly TextBlock[] _cardTitleTexts = new TextBlock[OptionCount];
    private readonly TextBlock[] _cardBodyTexts = new TextBlock[OptionCount];
    private readonly TextBlock[] _cardBadgeTexts = new TextBlock[OptionCount];

    private readonly DispatcherTimer _spinnerTimer;
    private int _spinnerFrameIdx;
    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    private bool _realtimeRewriteEnabled;
    private bool _hasContent;
    private bool _isConvertedReady;
    private readonly bool[] _cardUsed = new bool[OptionCount];
    private int _lastAppliedIndex = -1;

    private string _rawOriginal = string.Empty;
    private readonly string[] _optionTexts = new string[OptionCount]
    {
        string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty
    };

    public double PreferredSlotWidthDip => NormalSlotWidthDip;

    // True Dark Graphite/Obsidian Palette + Conversion Highlight Palette
    private static readonly SolidColorBrush BrushCardBg = CreateFrozen(20, 20, 24);               // #141418 (Idle)
    private static readonly SolidColorBrush BrushCardConvertingBg = CreateFrozen(24, 21, 16);     // #181510 (Converting subtle warm dark)
    private static readonly SolidColorBrush BrushCardConvertedBg = CreateFrozen(16, 29, 22);      // #101D16 (Converted Ready highlighted emerald dark)
    private static readonly SolidColorBrush BrushCardUsedBg = CreateFrozen(20, 38, 28);           // #14261C (Applied / Used deeper emerald highlight)
    private static readonly SolidColorBrush BrushCardHoverBg = CreateFrozen(26, 44, 33);          // #1A2C21 (Hover when Ready)

    private static readonly SolidColorBrush BrushCardBorder = CreateFrozen(40, 40, 46);           // #28282E (Idle border)
    private static readonly SolidColorBrush BrushCardConvertingBorder = CreateFrozen(146, 64, 14);// #92400E (Amber loading border)
    private static readonly SolidColorBrush BrushCardConvertedBorder = CreateFrozen(22, 163, 74); // #16A34A (Converted Ready green highlight border)
    private static readonly SolidColorBrush BrushCardUsedBorder = CreateFrozen(34, 197, 94);      // #22C55E (Bright green used border)
    private static readonly SolidColorBrush BrushCardHoverBorder = CreateFrozen(74, 222, 128);    // #4ADE80 (Hover green border)

    private static readonly SolidColorBrush BrushCheckIdleBg = CreateFrozen(28, 28, 34);          // #1C1C22
    private static readonly SolidColorBrush BrushCheckIdleBorder = CreateFrozen(82, 82, 91);      // #52525B
    private static readonly SolidColorBrush BrushCheckConvertingBg = CreateFrozen(69, 26, 3);     // #451A03
    private static readonly SolidColorBrush BrushCheckConvertedBg = CreateFrozen(22, 163, 74);    // #16A34A

    private static readonly SolidColorBrush BrushNativePanelBg = CreateFrozen(15, 20, 28);        // #0F141C
    private static readonly SolidColorBrush BrushNativePanelBorder = CreateFrozen(37, 99, 235);   // #2563EB
    private static readonly SolidColorBrush BrushSkyAccent = CreateFrozen(56, 189, 248);          // #38BDF8

    private static readonly SolidColorBrush BrushDivider = CreateFrozen(36, 36, 42);              // #24242A
    private static readonly SolidColorBrush BrushAccentSilver = CreateFrozen(212, 212, 216);      // #D4D4D8
    private static readonly SolidColorBrush BrushAccentGreen = CreateFrozen(34, 197, 94);         // #22C55E
    private static readonly SolidColorBrush BrushAccentBrightGreen = CreateFrozen(74, 222, 128);  // #4ADE80
    private static readonly SolidColorBrush BrushAmberLoading = CreateFrozen(245, 158, 11);       // #F59E0B
    private static readonly SolidColorBrush BrushMutedText = CreateFrozen(161, 161, 170);         // #A1A1AA
    private static readonly SolidColorBrush BrushDimConvertingText = CreateFrozen(185, 185, 195); // #B9B9C3
    private static readonly SolidColorBrush BrushBrightText = CreateFrozen(244, 244, 245);        // #F4F4F5

    private static readonly string[] OptionLabels =
    {
        "Técnica", "Didática", "Operacional", "Formal", "Passo a Passo", "Direta"
    };

    public event Action<string, string>? OnOptionClicked;
    public event Action? OnRestorePreviousRequested;
    public event Action? OnResetBlockRequested;
    public event Action? OnUserDisabled;
    public event Action? OnUpdateRequested;
    public event Action? OnToggleRewriteModeRequested;

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public RewriteDeckView()
    {
        ClipToBounds = true;
        BorderBrush = BrushDivider;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(2, 4, 2, 6);
        Margin = new Thickness(0, 0, 0, 6);

        var rootGrid = new Grid { ClipToBounds = true };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header block (Title + Action Buttons + Auto-Update Banner)
        var headerStack = new StackPanel
        {
            Margin = new Thickness(2, 0, 2, 6)
        };

        var topHeaderRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        topHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _slotTitleText = new TextBlock
        {
            Text = $"⚡ VOZ NATIVA • v{AutoUpdateService.CurrentVersionString}",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushBrightText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_slotTitleText, 0);
        topHeaderRow.Children.Add(_slotTitleText);

        _btnUpdateBanner = CreateHeaderButton(
            "⬆️ Nova atualização disponível — Clique para atualizar",
            "Baixar e aplicar automaticamente a nova versão publicada no GitHub");
        _btnUpdateBanner.Background = CreateFrozen(22, 101, 52); // #166534 Emerald Banner
        _btnUpdateBanner.Foreground = BrushBrightText;
        _btnUpdateBanner.FontWeight = FontWeights.Bold;
        _btnUpdateBanner.Padding = new Thickness(10, 5, 10, 5);
        _btnUpdateBanner.Margin = new Thickness(0, 0, 0, 4);
        _btnUpdateBanner.HorizontalAlignment = HorizontalAlignment.Stretch;
        _btnUpdateBanner.Visibility = Visibility.Collapsed;
        _btnUpdateBanner.Click += (_, _) =>
        {
            OnUpdateRequested?.Invoke();
        };

        _headerStatusText = new TextBlock
        {
            Text = "⚡ Modo Nativo Direto ativo • Sem processamento de reescrita",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushSkyAccent,
            TextWrapping = TextWrapping.Wrap
        };

        var headerBtns = new StackPanel { Orientation = Orientation.Horizontal };

        _btnToggleModeHeader = CreateHeaderButton(
            "⚡ Só Nativo",
            "Alternar entre Modo Nativo Direto (sem correção/sem 6 opções, máxima velocidade) e Modo 6 Opções de Escrita");
        _btnToggleModeHeader.Foreground = BrushSkyAccent;
        _btnToggleModeHeader.Click += (_, _) =>
        {
            OnToggleRewriteModeRequested?.Invoke();
        };

        _btnRestorePrevious = CreateHeaderButton("⏪ Anterior", "Recuperar a conversa anterior que foi limpa automaticamente após o uso");
        _btnRestorePrevious.Foreground = BrushSkyAccent;
        _btnRestorePrevious.Visibility = Visibility.Collapsed;
        _btnRestorePrevious.Click += (_, _) =>
        {
            OnRestorePreviousRequested?.Invoke();
        };

        _btnOriginal = CreateHeaderButton("↩️ Original", "Voltar para o texto completo exato que você falou");
        _btnOriginal.Click += (_, _) =>
        {
            if (!_hasContent || string.IsNullOrWhiteSpace(_rawOriginal))
                return;

            if (!_isConvertedReady)
            {
                _headerStatusText.Text = "⏳ AGUARDANDO CONVERSÃO FINAL • Aguarde o indicador concluir para usar";
                _headerStatusText.Foreground = BrushAmberLoading;
                return;
            }

            _lastAppliedIndex = -1;
            RefreshAllCardsVisualState();
            _headerStatusText.Text = "☑ ORIGINAL APLICADO • Próxima fala ou Enter iniciará conversa limpa";
            _headerStatusText.Foreground = BrushAccentBrightGreen;
            OnOptionClicked?.Invoke(_rawOriginal, "Original");
        };

        var btnReset = CreateHeaderButton("🔄 Limpar", "Zerar o texto acumulado e limpar a Linha do Tempo para começar do zero");
        btnReset.Click += (_, _) =>
        {
            ClearTimelineLog();
            OnResetBlockRequested?.Invoke();
            _headerStatusText.Text = _realtimeRewriteEnabled
                ? "✨ BLOCO ZERADO • Fale a nova mensagem para gerar as 6 variações"
                : "📋 LINHA DO TEMPO LIMPA • Pronta para captar novos assuntos em tempo real";
            _headerStatusText.Foreground = BrushAccentSilver;
        };

        var btnHide = CreateHeaderButton("—", "Minimizar para a bandeja do relógio (clique no ícone da bandeja para restaurar a barra lateral)");
        btnHide.Click += (_, _) =>
        {
            OnUserDisabled?.Invoke();
        };

        headerBtns.Children.Add(_btnToggleModeHeader);
        headerBtns.Children.Add(_btnRestorePrevious);
        headerBtns.Children.Add(_btnOriginal);
        headerBtns.Children.Add(btnReset);
        headerBtns.Children.Add(btnHide);
        Grid.SetColumn(headerBtns, 1);
        topHeaderRow.Children.Add(headerBtns);

        headerStack.Children.Add(topHeaderRow);
        headerStack.Children.Add(_btnUpdateBanner);
        headerStack.Children.Add(_headerStatusText);
        Grid.SetRow(headerStack, 0);
        rootGrid.Children.Add(headerStack);

        // Panel 1: Realtime Thematic Timeline + Native Live Mode (shown when RealtimeRewriteEnabled == false)
        _nativeModePanel = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = BrushNativePanelBg,
            BorderBrush = BrushNativePanelBorder,
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(10, 10, 10, 10),
            Margin = new Thickness(0, 2, 2, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var nativeGrid = new Grid { ClipToBounds = true };
        nativeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: Assunto Atual + Captura ao Vivo
        nativeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: Cabeçalho da Linha do Tempo + Botões
        nativeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 2: ScrollViewer da Linha do Tempo Contínua

        var topLiveStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var currentTopicBanner = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = CreateFrozen(16, 32, 44),
            BorderBrush = CreateFrozen(14, 116, 144),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        _currentTopicBadgeText = new TextBlock
        {
            Text = "🎯 ASSUNTO ATUAL: Aguardando fala (F8) ou áudio do PC (🎧)...",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushSkyAccent,
            TextWrapping = TextWrapping.Wrap
        };
        currentTopicBanner.Child = _currentTopicBadgeText;

        var previewCard = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = BrushCardBg,
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 6, 9, 6)
        };
        _nativeLivePreviewBox = new TextBlock
        {
            Text = "🗣️ Captura em tempo real: fale no microfone (F8) ou ative '🎧 Ouvir PC' para resumir continuamente em tópicos...",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = BrushMutedText,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 68,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        previewCard.Child = _nativeLivePreviewBox;

        topLiveStack.Children.Add(currentTopicBanner);
        topLiveStack.Children.Add(previewCard);
        Grid.SetRow(topLiveStack, 0);
        nativeGrid.Children.Add(topLiveStack);

        // Row 1: Timeline Toolbar
        var timelineToolbar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        timelineToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timelineToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _timelineCountText = new TextBlock
        {
            Text = "📋 LINHA DO TEMPO DE ASSUNTOS (0)",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = BrushAccentBrightGreen,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_timelineCountText, 0);
        timelineToolbar.Children.Add(_timelineCountText);

        var timelineActionsRow = new StackPanel { Orientation = Orientation.Horizontal };
        _btnCopyTimeline = CreateHeaderButton(
            "📋 Copiar Log",
            "Copiar toda a Linha do Tempo de Assuntos e Tópicos para a Área de Transferência");
        _btnCopyTimeline.Background = CreateFrozen(20, 42, 32);
        _btnCopyTimeline.Foreground = BrushAccentBrightGreen;
        _btnCopyTimeline.Padding = new Thickness(8, 4, 8, 4);
        _btnCopyTimeline.Margin = new Thickness(0, 0, 4, 0);
        _btnCopyTimeline.Click += (_, _) => CopyEntireTimelineToClipboard();

        var btnEnableSixOptions = CreateHeaderButton(
            "✨ 6 Estilos",
            "Alternar para o gerador das 6 opções de escrita no mesmo espaço");
        btnEnableSixOptions.Background = CreateFrozen(28, 36, 52);
        btnEnableSixOptions.Foreground = BrushBrightText;
        btnEnableSixOptions.Padding = new Thickness(8, 4, 8, 4);
        btnEnableSixOptions.Margin = new Thickness(0);
        btnEnableSixOptions.Click += (_, _) =>
        {
            OnToggleRewriteModeRequested?.Invoke();
        };

        timelineActionsRow.Children.Add(_btnCopyTimeline);
        timelineActionsRow.Children.Add(btnEnableSixOptions);
        Grid.SetColumn(timelineActionsRow, 1);
        timelineToolbar.Children.Add(timelineActionsRow);

        Grid.SetRow(timelineToolbar, 1);
        nativeGrid.Children.Add(timelineToolbar);

        // Row 2: Continuous Scrolling Timeline Log
        _timelineScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 2, 0)
        };

        _timelineItemsStack = new StackPanel();

        _timelineEmptyPlaceholder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = BrushCardBg,
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(0, 2, 0, 4)
        };
        var emptyText = new TextBlock
        {
            Text = "🕒 Log contínuo em tempo real:\n• Conforme você fala (🎙️ F8) ou escuta o computador (🎧 Ouvir PC), cada assunto é resumido em tópicos com horário.\n• Os novos blocos entram abaixo e a linha do tempo vai subindo automaticamente.\n• Clique em qualquer card para copiar o resumo daquele assunto.",
            FontSize = 11.2,
            Foreground = BrushMutedText,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        };
        _timelineEmptyPlaceholder.Child = emptyText;
        _timelineItemsStack.Children.Add(_timelineEmptyPlaceholder);

        _timelineScrollViewer.Content = _timelineItemsStack;
        Grid.SetRow(_timelineScrollViewer, 2);
        nativeGrid.Children.Add(_timelineScrollViewer);

        _nativeModePanel.Child = nativeGrid;

        // Panel 2: Fixed 6-Row Proportional Grid (shown when RealtimeRewriteEnabled == true)
        _cardsGrid = new Grid
        {
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 2, 0),
            Visibility = Visibility.Collapsed
        };
        for (int r = 0; r < OptionCount; r++)
        {
            _cardsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        string[] titles =
        {
            "1. Técnica e Detalhada (Especificação clara e completa)",
            "2. Didática e Simples (Explicativa em linguagem simples)",
            "3. Operacional / Entrega (\"O que foi feito\" e status)",
            "4. Formal e Institucional (Profissional e polida)",
            "5. Passo a Passo / Checklist (Sequência prática 1, 2, 3)",
            "6. Direta e Limpa (Só corrige e pontua a fala)"
        };

        for (int i = 0; i < OptionCount; i++)
        {
            int idx = i;
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = BrushCardBg,
                BorderBrush = BrushCardBorder,
                BorderThickness = new Thickness(1.2),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 0, i == OptionCount - 1 ? 0 : 5),
                ClipToBounds = true,
                Cursor = Cursors.Arrow,
                Focusable = false,
                ToolTip = "Aguardando fala e conversão final..."
            };

            var cardGrid = new Grid { ClipToBounds = true };
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkBorder = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = BrushCheckIdleBg,
                BorderBrush = BrushCheckIdleBorder,
                BorderThickness = new Thickness(1.2),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var checkIcon = new TextBlock
            {
                Text = "",
                FontSize = 10.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = BrushBrightText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            checkBorder.Child = checkIcon;
            Grid.SetColumn(checkBorder, 0);
            topRow.Children.Add(checkBorder);

            var titleBlock = new TextBlock
            {
                Text = titles[i],
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushMutedText,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titleBlock, 1);
            topRow.Children.Add(titleBlock);

            var badgeBlock = new TextBlock
            {
                Text = "☐ Aguardando",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = BrushMutedText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(badgeBlock, 2);
            topRow.Children.Add(badgeBlock);

            var bodyBlock = new TextBlock
            {
                Text = "Fale normalmente para ver esta opção atualizar ao vivo...",
                FontSize = 12.2,
                FontWeight = FontWeights.Medium,
                Foreground = BrushBrightText,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            };

            Grid.SetRow(topRow, 0);
            Grid.SetRow(bodyBlock, 1);
            cardGrid.Children.Add(topRow);
            cardGrid.Children.Add(bodyBlock);
            card.Child = cardGrid;

            card.MouseEnter += (_, _) =>
            {
                if (_hasContent && _isConvertedReady)
                {
                    card.Background = BrushCardHoverBg;
                    card.BorderBrush = BrushCardHoverBorder;
                }
            };
            card.MouseLeave += (_, _) =>
            {
                RefreshSingleCardVisualState(idx);
            };
            card.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!_hasContent || string.IsNullOrWhiteSpace(_optionTexts[idx]))
                    return;

                if (!_isConvertedReady)
                {
                    _headerStatusText.Text = "⏳ CONVERSÃO EM ANDAMENTO • Aguarde o checkbox ☑ ficar verde para usar";
                    _headerStatusText.Foreground = BrushAmberLoading;
                    return;
                }

                string chosen = _optionTexts[idx];
                _cardUsed[idx] = true;
                _lastAppliedIndex = idx;
                RefreshAllCardsVisualState();

                string label = OptionLabels[idx];
                _headerStatusText.Text = $"☑ APLICADA ({label.ToUpperInvariant()}) • Próxima fala ou Enter iniciará conversa limpa";
                _headerStatusText.Foreground = BrushAccentBrightGreen;
                OnOptionClicked?.Invoke(chosen, label);
            };

            _cardBorders[i] = card;
            _cardCheckBorders[i] = checkBorder;
            _cardCheckIcons[i] = checkIcon;
            _cardTitleTexts[i] = titleBlock;
            _cardBodyTexts[i] = bodyBlock;
            _cardBadgeTexts[i] = badgeBlock;

            Grid.SetRow(card, i);
            _cardsGrid.Children.Add(card);
        }

        Grid.SetRow(_nativeModePanel, 1);
        Grid.SetRow(_cardsGrid, 1);
        rootGrid.Children.Add(_nativeModePanel);
        rootGrid.Children.Add(_cardsGrid);

        // Low-overhead background spinner (250ms at Background priority, only when 6 options mode is active)
        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _spinnerTimer.Tick += (_, _) =>
        {
            if (!_realtimeRewriteEnabled || !_hasContent || _isConvertedReady)
            {
                _spinnerTimer.Stop();
                return;
            }

            _spinnerFrameIdx = (_spinnerFrameIdx + 1) % SpinnerFrames.Length;
            string frame = SpinnerFrames[_spinnerFrameIdx];
            for (int i = 0; i < OptionCount; i++)
            {
                _cardCheckIcons[i].Text = frame;
                _cardBadgeTexts[i].Text = $"{frame} Convertendo...";
            }
        };

        Child = rootGrid;
    }

    public void SetRealtimeRewriteMode(bool enabled)
    {
        _realtimeRewriteEnabled = enabled;
        if (!enabled)
        {
            _spinnerTimer.Stop();
            _cardsGrid.Visibility = Visibility.Collapsed;
            _nativeModePanel.Visibility = Visibility.Visible;
            _btnOriginal.Visibility = Visibility.Collapsed;
            _btnRestorePrevious.Visibility = Visibility.Collapsed;

            _slotTitleText.Text = $"📋 RESUMO TIMELINE • v{AutoUpdateService.CurrentVersionString}";
            _btnToggleModeHeader.Content = "📋 Resumo Live";
            _btnToggleModeHeader.Foreground = BrushSkyAccent;
            _headerStatusText.Text = "📋 Linha do Tempo em Tópicos ativa • Resumo contínuo em tempo real (Mic & Ouvir PC)";
            _headerStatusText.Foreground = BrushSkyAccent;
        }
        else
        {
            _nativeModePanel.Visibility = Visibility.Collapsed;
            _cardsGrid.Visibility = Visibility.Visible;
            _btnOriginal.Visibility = Visibility.Visible;

            _slotTitleText.Text = $"✨ 6 OPÇÕES AO VIVO • v{AutoUpdateService.CurrentVersionString}";
            _btnToggleModeHeader.Content = "✨ 6 Opções: ON";
            _btnToggleModeHeader.Foreground = BrushAccentBrightGreen;
            _headerStatusText.Text = _hasContent
                ? "☑ 6 opções ativas • Clique em qualquer opção para aplicar no cursor"
                : "Fale para converter • O checkbox ☑ libera o clique após concluir a correção";
            _headerStatusText.Foreground = _hasContent ? BrushAccentBrightGreen : BrushMutedText;
            RefreshAllCardsVisualState();
        }
    }

    public void UpdateNativeLivePreview(string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _nativeLivePreviewBox.Text = "🗣️ Captura em tempo real: fale no microfone (F8) ou ative '🎧 Ouvir PC' para resumir continuamente em tópicos...";
            _nativeLivePreviewBox.Foreground = BrushMutedText;
            return;
        }

        string tail = FormatRollingTailForCard(text, 220);
        _nativeLivePreviewBox.Text = (isFinal ? "✓ " : "🗣️ ") + tail;
        _nativeLivePreviewBox.Foreground = isFinal ? BrushBrightText : BrushSkyAccent;
    }

    public void UpdateLiveTopicPreview(string themeTitle, string liveSnippet)
    {
        if (!string.IsNullOrWhiteSpace(themeTitle))
        {
            _currentTopicBadgeText.Text = $"🎯 ASSUNTO ATUAL: {themeTitle}";
            _currentTopicBadgeText.Foreground = BrushAccentBrightGreen;
        }
        if (!string.IsNullOrWhiteSpace(liveSnippet))
        {
            UpdateNativeLivePreview(liveSnippet, isFinal: false);
        }
    }

    public void AddOrUpdateTimelineEntry(TimelineLogEntry entry, bool isNewEntry)
    {
        _timelineEmptyPlaceholder.Visibility = Visibility.Collapsed;
        _currentTopicBadgeText.Text = $"🎯 ASSUNTO ATUAL: {entry.TopicTitle}";
        _currentTopicBadgeText.Foreground = entry.IsAiRefined ? BrushAccentBrightGreen : BrushSkyAccent;

        int existingHistIdx = _timelineHistory.FindIndex(x => x.Id == entry.Id);
        if (existingHistIdx >= 0)
        {
            _timelineHistory[existingHistIdx] = entry;
        }
        else
        {
            _timelineHistory.Add(entry);
        }

        _timelineCountText.Text = $"📋 LINHA DO TEMPO DE ASSUNTOS ({_timelineHistory.Count})";

        string sourceLabel = entry.IsPcAudio ? "🎧 PC" : "🎙️ MIC";
        string metaLabel = $"🕒 {entry.Timestamp}  •  {sourceLabel}";
        string badgeLabel = entry.IsAiRefined ? "✨ Resumido • Copiar" : "⚡ Resumindo...";
        string themeLabel = $"📌 {entry.TopicTitle}";
        string bulletsText = FormatTimelineBullets(entry.Bullets);

        if (_timelineCards.TryGetValue(entry.Id, out var existingCard))
        {
            existingCard.MetaText.Text = metaLabel;
            existingCard.BadgeText.Text = badgeLabel;
            existingCard.BadgeText.Foreground = entry.IsAiRefined ? BrushAccentBrightGreen : BrushAmberLoading;
            existingCard.ThemeTitleText.Text = themeLabel;
            existingCard.ThemeTitleText.Foreground = entry.IsAiRefined ? BrushAccentBrightGreen : BrushSkyAccent;
            existingCard.BulletsBodyText.Text = bulletsText;
            existingCard.CardBorder.Background = entry.IsAiRefined ? BrushCardConvertedBg : BrushCardBg;
            existingCard.CardBorder.BorderBrush = entry.IsAiRefined ? BrushCardConvertedBorder : BrushCardBorder;
            existingCard.CardBorder.Tag = entry;
            _timelineScrollViewer.ScrollToEnd();
            return;
        }

        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = entry.IsAiRefined ? BrushCardConvertedBg : BrushCardBg,
            BorderBrush = entry.IsAiRefined ? BrushCardConvertedBorder : BrushCardBorder,
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = "Clique para copiar este resumo temático para a Área de Transferência",
            Tag = entry
        };

        var cardStack = new StackPanel();

        var topMetaRow = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        topMetaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topMetaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var metaText = new TextBlock
        {
            Text = metaLabel,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushMutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(metaText, 0);
        topMetaRow.Children.Add(metaText);

        var badgeText = new TextBlock
        {
            Text = badgeLabel,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = entry.IsAiRefined ? BrushAccentBrightGreen : BrushAmberLoading,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badgeText, 1);
        topMetaRow.Children.Add(badgeText);

        var themeTitleText = new TextBlock
        {
            Text = themeLabel,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = entry.IsAiRefined ? BrushAccentBrightGreen : BrushSkyAccent,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3)
        };

        var bulletsBodyText = new TextBlock
        {
            Text = bulletsText,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = BrushBrightText,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16
        };

        cardStack.Children.Add(topMetaRow);
        cardStack.Children.Add(themeTitleText);
        cardStack.Children.Add(bulletsBodyText);
        cardBorder.Child = cardStack;

        cardBorder.MouseEnter += (_, _) =>
        {
            cardBorder.Background = BrushCardHoverBg;
            cardBorder.BorderBrush = BrushCardHoverBorder;
        };
        cardBorder.MouseLeave += (_, _) =>
        {
            if (cardBorder.Tag is TimelineLogEntry cur)
            {
                cardBorder.Background = cur.IsAiRefined ? BrushCardConvertedBg : BrushCardBg;
                cardBorder.BorderBrush = cur.IsAiRefined ? BrushCardConvertedBorder : BrushCardBorder;
            }
        };
        cardBorder.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (cardBorder.Tag is TimelineLogEntry cur)
            {
                try
                {
                    string formatted = $"[{cur.Timestamp}] 📌 {cur.TopicTitle}\n{FormatTimelineBullets(cur.Bullets)}";
                    Clipboard.SetText(formatted);
                    badgeText.Text = "☑ Copiado!";
                    badgeText.Foreground = BrushAccentBrightGreen;
                    _headerStatusText.Text = $"📋 Tópico \"{cur.TopicTitle}\" copiado para a Área de Transferência!";
                    _headerStatusText.Foreground = BrushAccentBrightGreen;
                }
                catch { }
            }
        };

        _timelineCards[entry.Id] = new TimelineCardRefs(cardBorder, metaText, badgeText, themeTitleText, bulletsBodyText);
        _timelineItemsStack.Children.Add(cardBorder);

        // Mantém os últimos 120 cards em memória visual para fluidez infinita
        if (_timelineItemsStack.Children.Count > 125)
        {
            for (int idx = 0; idx < _timelineItemsStack.Children.Count; idx++)
            {
                if (_timelineItemsStack.Children[idx] is Border oldBorder &&
                    !ReferenceEquals(oldBorder, _timelineEmptyPlaceholder))
                {
                    if (oldBorder.Tag is TimelineLogEntry oldEntry)
                    {
                        _timelineCards.Remove(oldEntry.Id);
                    }
                    _timelineItemsStack.Children.RemoveAt(idx);
                    break;
                }
            }
        }

        _timelineScrollViewer.UpdateLayout();
        _timelineScrollViewer.ScrollToEnd();
    }

    public void ClearTimelineLog()
    {
        _timelineCards.Clear();
        _timelineHistory.Clear();
        _timelineItemsStack.Children.Clear();
        _timelineEmptyPlaceholder.Visibility = Visibility.Visible;
        _timelineItemsStack.Children.Add(_timelineEmptyPlaceholder);
        _timelineCountText.Text = "📋 LINHA DO TEMPO DE ASSUNTOS (0)";
        _currentTopicBadgeText.Text = "🎯 ASSUNTO ATUAL: Aguardando fala (F8) ou áudio do PC (🎧)...";
        _currentTopicBadgeText.Foreground = BrushSkyAccent;
    }

    private void CopyEntireTimelineToClipboard()
    {
        if (_timelineHistory.Count == 0)
        {
            _headerStatusText.Text = "⚠️ A Linha do Tempo ainda está vazia — fale (F8) ou ative '🎧 Ouvir PC'";
            _headerStatusText.Foreground = BrushAmberLoading;
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== LINHA DO TEMPO DE ASSUNTOS ({DateTime.Now:dd/MM/yyyy HH:mm}) ===");
            sb.AppendLine();
            foreach (var item in _timelineHistory)
            {
                string src = item.IsPcAudio ? "PC" : "MIC";
                sb.AppendLine($"[{item.Timestamp} • {src}] 📌 {item.TopicTitle}");
                foreach (string b in item.Bullets)
                {
                    sb.AppendLine($"  • {b}");
                }
                sb.AppendLine();
            }

            Clipboard.SetText(sb.ToString().TrimEnd());
            _btnCopyTimeline.Content = "☑ Log Copiado!";
            _headerStatusText.Text = $"📋 Linha do Tempo completa ({_timelineHistory.Count} assuntos) copiada!";
            _headerStatusText.Foreground = BrushAccentBrightGreen;
        }
        catch { }
    }

    private static string FormatTimelineBullets(string[]? bullets)
    {
        if (bullets == null || bullets.Length == 0)
            return "• Processando resumo do tópico...";
        return string.Join("\n", bullets.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => "• " + b.Trim()));
    }

    public void SetConvertingLiveState(int currentCharCount = 0, string? activeSpokenText = null)
    {
        if (!_realtimeRewriteEnabled)
        {
            if (!string.IsNullOrWhiteSpace(activeSpokenText))
            {
                UpdateNativeLivePreview(activeSpokenText, isFinal: false);
            }
            return;
        }

        _hasContent = true;
        _isConvertedReady = false;

        if (!_spinnerTimer.IsEnabled)
        {
            _spinnerTimer.Start();
        }

        _headerStatusText.Text = currentCharCount > 0
            ? $"⏳ FALE À VONTADE ({currentCharCount} chars)... As 6 opções serão convertidas ao concluir"
            : "⏳ FALE À VONTADE... As 6 opções serão convertidas ao concluir";
        _headerStatusText.Foreground = BrushAmberLoading;
    }

    public void UpdateVariations(RewriteVariations v)
    {
        _rawOriginal = v.RawOriginal;
        _optionTexts[0] = v.DiretaCoesa;
        _optionTexts[1] = v.FormalPolida;
        _optionTexts[2] = v.CurtaObjetiva;
        _optionTexts[3] = v.EmpaticaCordial;
        _optionTexts[4] = v.ExplicativaClara;
        _optionTexts[5] = v.PersuasivaConfiante;

        _hasContent = !string.IsNullOrWhiteSpace(v.RawOriginal);
        _isConvertedReady = _hasContent && v.IsAiRefined;

        if (!_realtimeRewriteEnabled)
        {
            UpdateNativeLivePreview(_rawOriginal, isFinal: true);
            return;
        }

        for (int i = 0; i < OptionCount; i++)
        {
            _cardBodyTexts[i].Text = FormatRollingTailForCard(_optionTexts[i], maxVisibleChars: 175);
        }

        if (!_hasContent)
        {
            _spinnerTimer.Stop();
            _headerStatusText.Text = "Fale para converter • O checkbox ☑ libera o clique após concluir a correção";
            _headerStatusText.Foreground = BrushMutedText;
        }
        else if (!_isConvertedReady)
        {
            if (!_spinnerTimer.IsEnabled)
            {
                _spinnerTimer.Start();
            }
            _headerStatusText.Text = $"⏳ CONVERTENDO ({v.RawOriginal.Length} chars)... Aguarde concluir";
            _headerStatusText.Foreground = BrushAmberLoading;
        }
        else
        {
            _spinnerTimer.Stop();
            _headerStatusText.Text = $"☑ 6 OPÇÕES CONVERTIDAS E LIBERADAS ({v.RawOriginal.Length} chars) • Clique para usar";
            _headerStatusText.Foreground = BrushAccentBrightGreen;
        }

        RefreshAllCardsVisualState();
    }

    private void RefreshAllCardsVisualState()
    {
        if (!_realtimeRewriteEnabled)
            return;

        for (int i = 0; i < OptionCount; i++)
        {
            RefreshSingleCardVisualState(i);
        }
    }

    private void RefreshSingleCardVisualState(int i)
    {
        if (!_hasContent)
        {
            _cardBorders[i].Background = BrushCardBg;
            _cardBorders[i].BorderBrush = BrushCardBorder;
            _cardBorders[i].BorderThickness = new Thickness(1.2);
            _cardBorders[i].Cursor = Cursors.Arrow;
            _cardBorders[i].ToolTip = "Fale normalmente para gerar e converter as 6 opções";

            _cardCheckBorders[i].Background = BrushCheckIdleBg;
            _cardCheckBorders[i].BorderBrush = BrushCheckIdleBorder;
            _cardCheckIcons[i].Text = "";
            _cardCheckIcons[i].Foreground = BrushMutedText;

            _cardTitleTexts[i].Foreground = BrushMutedText;
            _cardBodyTexts[i].Foreground = BrushBrightText;
            _cardBadgeTexts[i].Text = "☐ Aguardando";
            _cardBadgeTexts[i].Foreground = BrushMutedText;
            return;
        }

        if (!_isConvertedReady)
        {
            string frame = SpinnerFrames[_spinnerFrameIdx % SpinnerFrames.Length];
            _cardBorders[i].Background = BrushCardConvertingBg;
            _cardBorders[i].BorderBrush = BrushCardConvertingBorder;
            _cardBorders[i].BorderThickness = new Thickness(1.2);
            _cardBorders[i].Cursor = Cursors.No;
            _cardBorders[i].ToolTip = "⏳ Conversão em andamento — aguarde finalizar a correção antes de usar";

            _cardCheckBorders[i].Background = BrushCheckConvertingBg;
            _cardCheckBorders[i].BorderBrush = BrushAmberLoading;
            _cardCheckIcons[i].Text = frame;
            _cardCheckIcons[i].Foreground = BrushAmberLoading;

            _cardTitleTexts[i].Foreground = BrushAmberLoading;
            _cardBodyTexts[i].Foreground = BrushDimConvertingText;
            _cardBadgeTexts[i].Text = $"{frame} Convertendo...";
            _cardBadgeTexts[i].Foreground = BrushAmberLoading;
            return;
        }

        bool isUsed = _cardUsed[i];
        bool isLastApplied = (_lastAppliedIndex == i);

        _cardBorders[i].Background = isUsed ? BrushCardUsedBg : BrushCardConvertedBg;
        _cardBorders[i].BorderBrush = isLastApplied ? BrushCardUsedBorder : BrushCardConvertedBorder;
        _cardBorders[i].BorderThickness = new Thickness(isLastApplied ? 1.8 : 1.3);
        _cardBorders[i].Cursor = Cursors.Hand;
        _cardBorders[i].ToolTip = "☑ Versão final convertida e pronta! Clique para aplicar o texto completo no cursor";

        _cardCheckBorders[i].Background = BrushCheckConvertedBg;
        _cardCheckBorders[i].BorderBrush = BrushAccentBrightGreen;
        _cardCheckIcons[i].Text = "✓";
        _cardCheckIcons[i].Foreground = BrushBrightText;

        _cardTitleTexts[i].Foreground = BrushAccentBrightGreen;
        _cardBodyTexts[i].Foreground = BrushBrightText;

        if (isLastApplied)
        {
            _cardBadgeTexts[i].Text = "☑ Aplicada ✓";
            _cardBadgeTexts[i].Foreground = BrushAccentBrightGreen;
        }
        else if (isUsed)
        {
            _cardBadgeTexts[i].Text = "☑ Já usada • Colar ↵";
            _cardBadgeTexts[i].Foreground = BrushAccentGreen;
        }
        else
        {
            _cardBadgeTexts[i].Text = _optionTexts[i].Length > 175 ? "☑ Convertida • Colar Tudo ↵" : "☑ Convertida • Colar ↵";
            _cardBadgeTexts[i].Foreground = BrushAccentBrightGreen;
        }
    }

    public static string FormatRollingTailForCard(string fullText, int maxVisibleChars = 175)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return string.Empty;

        string trimmed = fullText.Trim();
        if (trimmed.Length <= maxVisibleChars)
            return trimmed;

        int startIdx = trimmed.Length - maxVisibleChars;
        int spaceIdx = trimmed.IndexOf(' ', startIdx);
        if (spaceIdx > startIdx && spaceIdx - startIdx <= 28 && spaceIdx + 1 < trimmed.Length)
        {
            startIdx = spaceIdx + 1;
        }

        return "... " + trimmed[startIdx..].TrimStart();
    }

    public void SetPreviousBackupAvailable(bool available)
    {
        _btnRestorePrevious.Visibility = (_realtimeRewriteEnabled && available) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowUpdateAvailable(string tagName, string? releaseNotes = null, int pendingCount = 1)
    {
        int count = Math.Max(1, pendingCount);
        string badgeLabel = count > 9 ? "9+" : count.ToString();
        _btnUpdateBanner.Content = count == 1
            ? $"🔔 [{badgeLabel}] Nova atualização disponível ({tagName}) — Clique para revisar"
            : $"🔔 [{badgeLabel}] {count} atualizações acumuladas (até {tagName}) — Clique para atualizar";

        string countHeader = count == 1
            ? $"1 nova atualização disponível ({tagName}):"
            : $"{count} atualizações acumuladas (instalando direto a {tagName}):";

        _btnUpdateBanner.ToolTip = string.IsNullOrWhiteSpace(releaseNotes)
            ? $"{countHeader}\nClique para revisar e aceitar a atualização."
            : $"{countHeader}\n{releaseNotes.Trim()}\n\nClique para revisar e aceitar a atualização.";
        _btnUpdateBanner.IsEnabled = true;
        _btnUpdateBanner.Visibility = Visibility.Visible;
    }

    public void HideUpdateBanner()
    {
        _btnUpdateBanner.Visibility = Visibility.Collapsed;
    }

    public void SetUpdateProgress(string statusText, bool isBusy)
    {
        _btnUpdateBanner.Content = statusText;
        _btnUpdateBanner.IsEnabled = !isBusy;
        _btnUpdateBanner.Visibility = Visibility.Visible;
    }

    public void ShowAutoClearedReadyForNewSpeech(bool hasBackup, string? customReason = null)
    {
        ResetDisplay();
        SetPreviousBackupAvailable(hasBackup);
        if (!string.IsNullOrWhiteSpace(customReason))
        {
            _headerStatusText.Text = customReason;
        }
        else
        {
            _headerStatusText.Text = _realtimeRewriteEnabled
                ? (hasBackup ? "✨ AUTO-LIMPO • Pronto p/ nova fala (ou clique em ⏪ Anterior)" : "✨ AUTO-LIMPO • Pronto para a próxima conversa")
                : "⚡ MODO NATIVO DIRETO • Pronto para a próxima fala";
        }
        _headerStatusText.Foreground = _realtimeRewriteEnabled ? BrushAccentBrightGreen : BrushSkyAccent;
    }

    public void ResetDisplay()
    {
        _spinnerTimer.Stop();
        _rawOriginal = string.Empty;
        _hasContent = false;
        _isConvertedReady = false;
        _lastAppliedIndex = -1;
        _nativeLivePreviewBox.Text = "Fale normalmente (F8) para digitar direto no cursor...";
        _nativeLivePreviewBox.Foreground = BrushMutedText;

        for (int i = 0; i < OptionCount; i++)
        {
            _cardUsed[i] = false;
            _optionTexts[i] = string.Empty;
            _cardBodyTexts[i].Text = "Fale normalmente para ver esta opção atualizar ao vivo...";
        }

        RefreshAllCardsVisualState();
    }

    private static Button CreateHeaderButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Content = text,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = BrushCardBg,
            Foreground = BrushBrightText,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0)
        };
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;
        btn.Template = template;
        return btn;
    }
}
