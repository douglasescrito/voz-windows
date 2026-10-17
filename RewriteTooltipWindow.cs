using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VozWindows;

/// <summary>
/// Embedded 6-Options Live Deck inside MainWindow._outerBorder.
/// Tracks conversion state (loading vs. converted ready) with a checkbox, visual highlight,
/// indeterminate loading bar + spinner, and locks usage while final conversion is in progress.
/// </summary>
public sealed class RewriteDeckView : Border
{
    private const int OptionCount = 6;
    private const int PredictionCount = 5;
    public const double NormalSlotWidthDip = 375;

    private readonly TextBlock _headerStatusText;
    private readonly ProgressBar _conversionProgressBar;
    private readonly Button _btnUpdateBanner;
    private readonly Button _btnRestorePrevious;
    private readonly Button _btnOriginal;
    private readonly Grid _cardsGrid;
    private readonly Border[] _cardBorders = new Border[OptionCount];
    private readonly Border[] _cardCheckBorders = new Border[OptionCount];
    private readonly TextBlock[] _cardCheckIcons = new TextBlock[OptionCount];
    private readonly TextBlock[] _cardTitleTexts = new TextBlock[OptionCount];
    private readonly TextBlock[] _cardBodyTexts = new TextBlock[OptionCount];
    private readonly TextBlock[] _cardBadgeTexts = new TextBlock[OptionCount];

    // Next-Word / Phrase Completion Prediction Bar
    private readonly Border _predictionBoxBorder;
    private readonly TextBlock _predictionBadgeText;
    private readonly Border[] _predictionChipBorders = new Border[PredictionCount];
    private readonly TextBlock[] _predictionChipTexts = new TextBlock[PredictionCount];
    private readonly string[] _currentPredictions = new string[PredictionCount];

    private readonly DispatcherTimer _spinnerTimer;
    private int _spinnerFrameIdx;
    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

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

    // True Dark Graphite/Obsidian Palette + Conversion Highlight & Loading Palette
    private static readonly SolidColorBrush BrushCardBg = CreateFrozen(20, 20, 24);              // #141418 (Idle)
    private static readonly SolidColorBrush BrushCardConvertingBg = CreateFrozen(24, 21, 16);    // #181510 (Converting subtle warm dark)
    private static readonly SolidColorBrush BrushCardConvertedBg = CreateFrozen(16, 29, 22);     // #101D16 (Converted Ready highlighted emerald dark)
    private static readonly SolidColorBrush BrushCardUsedBg = CreateFrozen(20, 38, 28);          // #14261C (Applied / Used deeper emerald highlight)
    private static readonly SolidColorBrush BrushCardHoverBg = CreateFrozen(26, 44, 33);         // #1A2C21 (Hover when Ready)

    private static readonly SolidColorBrush BrushCardBorder = CreateFrozen(40, 40, 46);          // #28282E (Idle border)
    private static readonly SolidColorBrush BrushCardConvertingBorder = CreateFrozen(146, 64, 14);// #92400E (Amber loading border)
    private static readonly SolidColorBrush BrushCardConvertedBorder = CreateFrozen(22, 163, 74);// #16A34A (Converted Ready green highlight border)
    private static readonly SolidColorBrush BrushCardUsedBorder = CreateFrozen(34, 197, 94);     // #22C55E (Bright green used border)
    private static readonly SolidColorBrush BrushCardHoverBorder = CreateFrozen(74, 222, 128);   // #4ADE80 (Hover green border)

    private static readonly SolidColorBrush BrushCheckIdleBg = CreateFrozen(28, 28, 34);         // #1C1C22
    private static readonly SolidColorBrush BrushCheckIdleBorder = CreateFrozen(82, 82, 91);     // #52525B
    private static readonly SolidColorBrush BrushCheckConvertingBg = CreateFrozen(69, 26, 3);    // #451A03
    private static readonly SolidColorBrush BrushCheckConvertedBg = CreateFrozen(22, 163, 74);   // #16A34A

    // Next-Word Prediction Bar Palette
    private static readonly SolidColorBrush BrushPredictBoxBg = CreateFrozen(16, 18, 24);        // #101218
    private static readonly SolidColorBrush BrushPredictBoxBorder = CreateFrozen(45, 55, 72);    // #2D3748
    private static readonly SolidColorBrush BrushChipBg = CreateFrozen(26, 30, 40);              // #1A1E28
    private static readonly SolidColorBrush BrushChipHoverBg = CreateFrozen(22, 101, 52);        // #166534
    private static readonly SolidColorBrush BrushChipBorder = CreateFrozen(63, 75, 98);          // #3F4B62
    private static readonly SolidColorBrush BrushChipAiBorder = CreateFrozen(56, 189, 248);      // #38BDF8
    private static readonly SolidColorBrush BrushSkyAccent = CreateFrozen(56, 189, 248);         // #38BDF8

    private static readonly SolidColorBrush BrushDivider = CreateFrozen(36, 36, 42);             // #24242A
    private static readonly SolidColorBrush BrushAccentSilver = CreateFrozen(212, 212, 216);     // #D4D4D8
    private static readonly SolidColorBrush BrushAccentGreen = CreateFrozen(34, 197, 94);        // #22C55E
    private static readonly SolidColorBrush BrushAccentBrightGreen = CreateFrozen(74, 222, 128); // #4ADE80
    private static readonly SolidColorBrush BrushAmberLoading = CreateFrozen(245, 158, 11);      // #F59E0B
    private static readonly SolidColorBrush BrushMutedText = CreateFrozen(161, 161, 170);        // #A1A1AA
    private static readonly SolidColorBrush BrushDimConvertingText = CreateFrozen(185, 185, 195);// #B9B9C3
    private static readonly SolidColorBrush BrushBrightText = CreateFrozen(244, 244, 245);       // #F4F4F5

    private static readonly string[] OptionLabels =
    {
        "Técnica", "Didática", "Operacional", "Formal", "Passo a Passo", "Direta"
    };

    public event Action<string, string>? OnOptionClicked;
    public event Action<string>? OnPredictionChipClicked;
    public event Action? OnRestorePreviousRequested;
    public event Action? OnResetBlockRequested;
    public event Action? OnUserDisabled;
    public event Action? OnUpdateRequested;

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
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header block (Title + Action Buttons + Auto-Update Banner + Loading Progress Bar)
        var headerStack = new StackPanel
        {
            Margin = new Thickness(2, 0, 2, 6)
        };

        var topHeaderRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        topHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slotTitle = new TextBlock
        {
            Text = $"✨ 6 OPÇÕES AO VIVO • v{AutoUpdateService.CurrentVersionString}",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushBrightText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(slotTitle, 0);
        topHeaderRow.Children.Add(slotTitle);

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
            Text = "Fale para converter • O checkbox ☑ libera o clique após concluir a correção",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushMutedText,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _conversionProgressBar = new ProgressBar
        {
            Height = 3.5,
            IsIndeterminate = true,
            Foreground = BrushAmberLoading,
            Background = BrushCardBg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 1),
            Visibility = Visibility.Collapsed
        };

        var headerBtns = new StackPanel { Orientation = Orientation.Horizontal };

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
            _headerStatusText.Text = "✨ BLOCO ZERADO • Fale a nova mensagem para gerar as 6 variações";
            _headerStatusText.Foreground = BrushAccentSilver;
        };

        var btnHide = CreateHeaderButton("—", "Minimizar para a bandeja do relógio (clique no ícone da bandeja para restaurar a barra lateral)");
        btnHide.Click += (_, _) =>
        {
            OnUserDisabled?.Invoke();
        };

        headerBtns.Children.Add(_btnRestorePrevious);
        headerBtns.Children.Add(_btnOriginal);
        headerBtns.Children.Add(btnReset);
        headerBtns.Children.Add(btnHide);
        Grid.SetColumn(headerBtns, 1);
        topHeaderRow.Children.Add(headerBtns);

        headerStack.Children.Add(topHeaderRow);
        headerStack.Children.Add(_btnUpdateBanner);
        headerStack.Children.Add(_headerStatusText);
        headerStack.Children.Add(_conversionProgressBar);
        Grid.SetRow(headerStack, 0);
        rootGrid.Children.Add(headerStack);

        // Fixed 6-Row Proportional Grid: all 6 boxes permanently stay in the exact same area!
        _cardsGrid = new Grid
        {
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 2, 0)
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

            // Visual Checkbox / Loading Spinner Box
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

                // Block usage while still converting to final version!
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

        Grid.SetRow(_cardsGrid, 1);
        rootGrid.Children.Add(_cardsGrid);

        // Row 2: Next-Word / Phrase Completion Prediction Bar (5 clickable chips)
        _predictionBoxBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = BrushPredictBoxBg,
            BorderBrush = BrushPredictBoxBorder,
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 2, 0),
            ClipToBounds = true
        };

        var predStack = new StackPanel();
        var predHeaderGrid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        predHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        predHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var predTitle = new TextBlock
        {
            Text = "🔮 PRÓXIMA PALAVRA • COMPLETAR FALA",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = BrushSkyAccent,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(predTitle, 0);
        predHeaderGrid.Children.Add(predTitle);

        _predictionBadgeText = new TextBlock
        {
            Text = "Clique para inserir no cursor",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushMutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_predictionBadgeText, 1);
        predHeaderGrid.Children.Add(_predictionBadgeText);

        var chipsWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };

        string[] initialPredictions = GeminiRewriteClient.PredictNextWordsLocally(string.Empty);
        for (int p = 0; p < PredictionCount; p++)
        {
            int pIdx = p;
            string initialWord = p < initialPredictions.Length ? initialPredictions[p] : "continuar";
            _currentPredictions[p] = initialWord;

            var chipBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = BrushChipBg,
                BorderBrush = BrushChipBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 5, 4),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = "Clique para acrescentar esta sugestão ao final do que você está falando no cursor"
            };

            var chipText = new TextBlock
            {
                Text = "+ " + initialWord,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushBrightText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 158
            };
            chipBorder.Child = chipText;

            chipBorder.MouseEnter += (_, _) =>
            {
                chipBorder.Background = BrushChipHoverBg;
                chipBorder.BorderBrush = BrushAccentBrightGreen;
            };
            chipBorder.MouseLeave += (_, _) =>
            {
                chipBorder.Background = BrushChipBg;
                chipBorder.BorderBrush = _isConvertedReady ? BrushChipAiBorder : BrushChipBorder;
            };
            chipBorder.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                string wordToAppend = _currentPredictions[pIdx];
                if (!string.IsNullOrWhiteSpace(wordToAppend))
                {
                    _predictionBadgeText.Text = $"✓ Inserido: \"{wordToAppend}\"";
                    _predictionBadgeText.Foreground = BrushAccentBrightGreen;
                    OnPredictionChipClicked?.Invoke(wordToAppend);
                }
            };

            _predictionChipBorders[p] = chipBorder;
            _predictionChipTexts[p] = chipText;
            chipsWrap.Children.Add(chipBorder);
        }

        predStack.Children.Add(predHeaderGrid);
        predStack.Children.Add(chipsWrap);
        _predictionBoxBorder.Child = predStack;

        Grid.SetRow(_predictionBoxBorder, 2);
        rootGrid.Children.Add(_predictionBoxBorder);

        _spinnerTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(85)
        };
        _spinnerTimer.Tick += (_, _) =>
        {
            if (!_hasContent || _isConvertedReady)
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

    /// <summary>
    /// Immediately engages the converting/loading indicator and locks card clicks as soon as speech starts,
    /// while also updating the 0ms next-word completion chips in real time.
    /// </summary>
    public void SetConvertingLiveState(int currentCharCount = 0, string? activeSpokenText = null)
    {
        _hasContent = true;
        _isConvertedReady = false;
        _conversionProgressBar.Visibility = Visibility.Visible;

        if (!_spinnerTimer.IsEnabled)
        {
            _spinnerTimer.Start();
        }

        if (!string.IsNullOrWhiteSpace(activeSpokenText))
        {
            UpdatePredictionsDisplay(GeminiRewriteClient.PredictNextWordsLocally(activeSpokenText), isAiContextual: false);
        }

        _headerStatusText.Text = currentCharCount > 0
            ? $"⏳ CONVERTENDO PARA VERSÃO FINAL ({currentCharCount} chars)... Aguarde concluir"
            : "⏳ CONVERTENDO PARA VERSÃO FINAL... Aguarde concluir para liberar o clique";
        _headerStatusText.Foreground = BrushAmberLoading;

        RefreshAllCardsVisualState();
    }

    private void UpdatePredictionsDisplay(string[]? suggestions, bool isAiContextual)
    {
        string[] effective = (suggestions != null && suggestions.Length > 0)
            ? suggestions
            : GeminiRewriteClient.PredictNextWordsLocally(_rawOriginal);

        for (int p = 0; p < PredictionCount; p++)
        {
            string word = p < effective.Length ? effective[p] : "...";
            _currentPredictions[p] = word;
            _predictionChipTexts[p].Text = "+ " + word;
            _predictionChipBorders[p].BorderBrush = isAiContextual ? BrushChipAiBorder : BrushChipBorder;
        }

        if (!_hasContent)
        {
            _predictionBadgeText.Text = "Clique para inserir no cursor";
            _predictionBadgeText.Foreground = BrushMutedText;
        }
        else if (isAiContextual)
        {
            _predictionBadgeText.Text = "✨ IA Contextual • Clique p/ completar";
            _predictionBadgeText.Foreground = BrushSkyAccent;
        }
        else
        {
            _predictionBadgeText.Text = "⚡ Ao vivo • Clique p/ completar";
            _predictionBadgeText.Foreground = BrushAmberLoading;
        }
    }

    public void UpdateVariations(RewriteVariations v)
    {
        // Store 100% of the complete text in memory so clicking any card pastes the ENTIRE speech
        _rawOriginal = v.RawOriginal;
        _optionTexts[0] = v.DiretaCoesa;
        _optionTexts[1] = v.FormalPolida;
        _optionTexts[2] = v.CurtaObjetiva;
        _optionTexts[3] = v.EmpaticaCordial;
        _optionTexts[4] = v.ExplicativaClara;
        _optionTexts[5] = v.PersuasivaConfiante;

        _hasContent = !string.IsNullOrWhiteSpace(v.RawOriginal);
        _isConvertedReady = _hasContent && v.IsAiRefined;

        for (int i = 0; i < OptionCount; i++)
        {
            _cardBodyTexts[i].Text = FormatRollingTailForCard(_optionTexts[i], maxVisibleChars: 175);
        }

        UpdatePredictionsDisplay(v.SugestoesCompletar, isAiContextual: _isConvertedReady);

        if (!_hasContent)
        {
            _spinnerTimer.Stop();
            _conversionProgressBar.Visibility = Visibility.Collapsed;
            _headerStatusText.Text = "Fale para converter • O checkbox ☑ libera o clique após concluir a correção";
            _headerStatusText.Foreground = BrushMutedText;
        }
        else if (!_isConvertedReady)
        {
            _conversionProgressBar.Visibility = Visibility.Visible;
            if (!_spinnerTimer.IsEnabled)
            {
                _spinnerTimer.Start();
            }
            _headerStatusText.Text = $"⏳ CONVERTENDO AO VIVO ({v.RawOriginal.Length} chars)... Bloqueado até concluir a correção";
            _headerStatusText.Foreground = BrushAmberLoading;
        }
        else
        {
            _spinnerTimer.Stop();
            _conversionProgressBar.Visibility = Visibility.Collapsed;
            _headerStatusText.Text = $"☑ 6 OPÇÕES CONVERTIDAS E LIBERADAS ({v.RawOriginal.Length} chars) • Clique para usar";
            _headerStatusText.Foreground = BrushAccentBrightGreen;
        }

        RefreshAllCardsVisualState();
    }

    private void RefreshAllCardsVisualState()
    {
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

        // Converted & Ready! Show Checkbox ☑ + Highlighted Visual State
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

    /// <summary>
    /// Displays "..." for the portion of the text that already passed while keeping the active live tail
    /// visible inside the fixed card box. The complete text remains 100% intact in _optionTexts[i].
    /// </summary>
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
        _btnRestorePrevious.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowUpdateAvailable(string tagName, string? releaseNotes = null)
    {
        _btnUpdateBanner.Content = $"⬆️ Nova atualização disponível ({tagName}) — Clique para revisar e atualizar";
        _btnUpdateBanner.ToolTip = string.IsNullOrWhiteSpace(releaseNotes)
            ? $"Clique para revisar e aceitar a atualização para a versão {tagName}"
            : $"Novidades da versão {tagName}:\n{releaseNotes.Trim()}\n\nClique para revisar e aceitar a atualização.";
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
            _headerStatusText.Text = hasBackup
                ? "✨ AUTO-LIMPO • Pronto p/ nova fala (ou clique em ⏪ Anterior)"
                : "✨ AUTO-LIMPO • Pronto para a próxima conversa";
        }
        _headerStatusText.Foreground = BrushAccentBrightGreen;
    }

    public void ResetDisplay()
    {
        _spinnerTimer.Stop();
        _conversionProgressBar.Visibility = Visibility.Collapsed;
        _rawOriginal = string.Empty;
        _hasContent = false;
        _isConvertedReady = false;
        _lastAppliedIndex = -1;

        for (int i = 0; i < OptionCount; i++)
        {
            _cardUsed[i] = false;
            _optionTexts[i] = string.Empty;
            _cardBodyTexts[i].Text = "Fale normalmente para ver esta opção atualizar ao vivo...";
        }

        UpdatePredictionsDisplay(GeminiRewriteClient.PredictNextWordsLocally(string.Empty), isAiContextual: false);
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
