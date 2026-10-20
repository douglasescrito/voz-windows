using System;
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
    public const double NormalSlotWidthDip = 375;

    private readonly TextBlock _slotTitleText;
    private readonly TextBlock _headerStatusText;
    private readonly Button _btnUpdateBanner;
    private readonly Button _btnToggleModeHeader;
    private readonly Button _btnRestorePrevious;
    private readonly Button _btnOriginal;
    private readonly Grid _cardsGrid;
    private readonly Border _nativeModePanel;
    private readonly TextBlock _nativeLivePreviewBox;
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
            TextTrimming = TextTrimming.CharacterEllipsis
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

        var btnReset = CreateHeaderButton("🔄 Limpar", "Zerar o texto acumulado para começar outra mensagem do zero");
        btnReset.Click += (_, _) =>
        {
            OnResetBlockRequested?.Invoke();
            _headerStatusText.Text = _realtimeRewriteEnabled
                ? "✨ BLOCO ZERADO • Fale a nova mensagem para gerar as 6 variações"
                : "⚡ BLOCO ZERADO • Modo Nativo Direto pronto para nova fala";
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

        // Panel 1: Native Direct Mode View (shown when RealtimeRewriteEnabled == false)
        _nativeModePanel = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = BrushNativePanelBg,
            BorderBrush = BrushNativePanelBorder,
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(14, 14, 14, 14),
            Margin = new Thickness(0, 2, 2, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        var nativeStack = new StackPanel();
        var nativeBadge = new TextBlock
        {
            Text = "⚡ MODO NATIVO DIRETO (ALTA PERFORMANCE)",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushSkyAccent,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var nativeDesc = new TextBlock
        {
            Text = "As palavras são digitadas diretamente no cursor assim que você fala, sem gastar processamento com correções ou geração das 6 opções de escrita.",
            FontSize = 11.5,
            Foreground = BrushMutedText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var previewCard = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = BrushCardBg,
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        _nativeLivePreviewBox = new TextBlock
        {
            Text = "Fale normalmente (F8) para digitar direto no cursor...",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = BrushBrightText,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 260,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        previewCard.Child = _nativeLivePreviewBox;

        var btnEnableSixOptions = CreateHeaderButton(
            "✨ Ativar 6 Opções de Escrita (IA)",
            "Habilitar novamente o painel com as 6 variações de reescrita quando precisar");
        btnEnableSixOptions.Background = CreateFrozen(28, 36, 52);
        btnEnableSixOptions.Foreground = BrushBrightText;
        btnEnableSixOptions.Padding = new Thickness(12, 7, 12, 7);
        btnEnableSixOptions.Margin = new Thickness(0);
        btnEnableSixOptions.HorizontalAlignment = HorizontalAlignment.Left;
        btnEnableSixOptions.Click += (_, _) =>
        {
            OnToggleRewriteModeRequested?.Invoke();
        };

        nativeStack.Children.Add(nativeBadge);
        nativeStack.Children.Add(nativeDesc);
        nativeStack.Children.Add(previewCard);
        nativeStack.Children.Add(btnEnableSixOptions);
        _nativeModePanel.Child = nativeStack;

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

            _slotTitleText.Text = $"⚡ VOZ NATIVA • v{AutoUpdateService.CurrentVersionString}";
            _btnToggleModeHeader.Content = "⚡ Só Nativo";
            _btnToggleModeHeader.Foreground = BrushSkyAccent;
            _headerStatusText.Text = "⚡ Modo Nativo Direto ativo • Sem reescrita e sem 6 opções (máxima velocidade)";
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
            _nativeLivePreviewBox.Text = "Fale normalmente (F8) para digitar direto no cursor...";
            _nativeLivePreviewBox.Foreground = BrushMutedText;
            return;
        }

        string tail = FormatRollingTailForCard(text, 280);
        _nativeLivePreviewBox.Text = (isFinal ? "✓ " : "🗣️ ") + tail;
        _nativeLivePreviewBox.Foreground = isFinal ? BrushBrightText : BrushSkyAccent;
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
