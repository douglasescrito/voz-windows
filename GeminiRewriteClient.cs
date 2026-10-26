using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VozWindows;

public sealed record RewriteVariations(
    string RawOriginal,
    string DiretaCoesa,
    string FormalPolida,
    string CurtaObjetiva,
    string EmpaticaCordial,
    string ExplicativaClara,
    string PersuasivaConfiante,
    bool IsAiRefined);

public sealed record TimelineLogEntry(
    long Id,
    string Timestamp,
    bool IsPcAudio,
    string TopicTitle,
    string[] Bullets,
    string RawText,
    bool IsAiRefined);

public sealed class GeminiRewriteClient : IDisposable
{
    private const int MaxActiveRewriteTailChars = 520;

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(8),
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(2.5)
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    // Circuit breaker cooldown map so rate-limited (429) or overloaded (503/timeout) models are skipped in 0ms
    private static readonly ConcurrentDictionary<string, DateTime> ModelCooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _pendingCts;
    private long _versionCounter;

    // High-capacity memory cache for long speeches so prefix paragraphs stay AI-refined
    private readonly object _prefixLock = new();
    private string _cachedRawPrefix = string.Empty;
    private RewriteVariations? _cachedPrefixVariations;

    // Cache of the last AI-refined active tail so duplicate requests return in 0ms without hitting the network
    private string _lastRefinedTailRaw = string.Empty;
    private RewriteVariations? _lastRefinedTailResult;

    // Realtime Thematic Timeline Summarizer state (works for both 🎧 Ouvir PC and 🎙️ Mic F8)
    private readonly object _timelineLock = new();
    private long _nextTimelineId;
    private int _timelineConsumedLen;
    private string _activeTurnBuffer = string.Empty;
    private string _lastEmittedSegmentNorm = string.Empty;
    private DateTime _lastEmittedUtc = DateTime.MinValue;
    private string _lastKnownThemeTitle = string.Empty;

    public event Action<RewriteVariations>? OnVariationsUpdated;
    public event Action<TimelineLogEntry, bool>? OnTimelineEntryUpdated;
    public event Action<string, string>? OnCurrentLiveTopicPreview;

    public void ResetMemoryCache()
    {
        Interlocked.Increment(ref _versionCounter);
        var prevCts = Interlocked.Exchange(ref _pendingCts, null);
        try { prevCts?.Cancel(); } catch { }
        try { prevCts?.Dispose(); } catch { }

        lock (_prefixLock)
        {
            _cachedRawPrefix = string.Empty;
            _cachedPrefixVariations = null;
            _lastRefinedTailRaw = string.Empty;
            _lastRefinedTailResult = null;
        }
    }

    /// <summary>
    /// Emits 6 distinct local variations and schedules a fast Gemini rewrite (~800ms via gemini-2.5-flash)
    /// when a sentence/turn commits.
    /// </summary>
    public void RequestRealtimeRewrite(string apiKey, string rawSpokenText, bool isFinalCommit = false)
    {
        string trimmed = (rawSpokenText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        long myVersion = Interlocked.Increment(ref _versionCounter);

        var prevCts = Interlocked.Exchange(ref _pendingCts, new CancellationTokenSource());
        try { prevCts?.Cancel(); } catch { }
        try { prevCts?.Dispose(); } catch { }

        CancellationToken token = _pendingCts!.Token;

        SplitHistoryPrefixAndActiveTail(trimmed, out string rawPrefix, out string rawTail);

        RewriteVariations? prefixVarsSnapshot = null;
        RewriteVariations? cachedTailSnapshot = null;

        lock (_prefixLock)
        {
            if (!string.IsNullOrEmpty(rawPrefix))
            {
                if (_cachedRawPrefix != rawPrefix || _cachedPrefixVariations == null)
                {
                    _cachedRawPrefix = rawPrefix;
                    _cachedPrefixVariations = BuildLocalSixVariations(rawPrefix, isAiRefined: true);
                }
                prefixVarsSnapshot = _cachedPrefixVariations;
            }

            if (!string.IsNullOrEmpty(rawTail) && _lastRefinedTailRaw == rawTail && _lastRefinedTailResult != null)
            {
                cachedTailSnapshot = _lastRefinedTailResult;
            }
        }

        if (cachedTailSnapshot != null)
        {
            RewriteVariations cachedFull = CombinePrefixAndTailVariations(
                trimmed,
                prefixVarsSnapshot,
                cachedTailSnapshot,
                isAiRefined: true);
            OnVariationsUpdated?.Invoke(cachedFull);
            return;
        }

        RewriteVariations localTailSix = BuildLocalSixVariations(rawTail, isAiRefined: false);
        RewriteVariations draftResult = CombinePrefixAndTailVariations(
            trimmed,
            prefixVarsSnapshot,
            localTailSix,
            isAiRefined: false);

        OnVariationsUpdated?.Invoke(draftResult);

        // Only call cloud AI when sentence commits (35ms) or after a genuine speech pause (750ms)
        int debounceMs = isFinalCommit ? 35 : 750;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceMs, token);
                if (token.IsCancellationRequested || Interlocked.Read(ref _versionCounter) != myVersion)
                    return;

                if (CountWords(rawTail) < 2 || string.IsNullOrWhiteSpace(apiKey))
                {
                    OnVariationsUpdated?.Invoke(draftResult with { IsAiRefined = true });
                    return;
                }

                string keyTrimmed = apiKey.Trim();
                var aiTailResult = await FetchGeminiVariationsAsync(keyTrimmed, rawTail, token);

                if (!token.IsCancellationRequested && Interlocked.Read(ref _versionCounter) == myVersion)
                {
                    RewriteVariations effectiveTail = aiTailResult != null
                        ? EnsureAllSixAreDistinct(aiTailResult, localTailSix)
                        : localTailSix with { IsAiRefined = true };

                    RewriteVariations fullResult = CombinePrefixAndTailVariations(
                        trimmed,
                        prefixVarsSnapshot,
                        effectiveTail,
                        isAiRefined: true);

                    lock (_prefixLock)
                    {
                        _lastRefinedTailRaw = rawTail;
                        _lastRefinedTailResult = effectiveTail;

                        if (string.IsNullOrEmpty(rawPrefix))
                        {
                            _cachedRawPrefix = trimmed;
                            _cachedPrefixVariations = fullResult;
                        }
                    }

                    OnVariationsUpdated?.Invoke(fullResult);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (!token.IsCancellationRequested && Interlocked.Read(ref _versionCounter) == myVersion)
                {
                    OnVariationsUpdated?.Invoke(draftResult with { IsAiRefined = true });
                }
            }
        }, token);
    }

    private static RewriteVariations CombinePrefixAndTailVariations(
        string fullRawOriginal,
        RewriteVariations? prefixVars,
        RewriteVariations tailVars,
        bool isAiRefined)
    {
        if (prefixVars == null)
        {
            return tailVars with
            {
                RawOriginal = fullRawOriginal,
                IsAiRefined = isAiRefined
            };
        }

        return new RewriteVariations(
            RawOriginal: fullRawOriginal,
            DiretaCoesa: CombinePrefixAndTail(prefixVars.DiretaCoesa, tailVars.DiretaCoesa),
            FormalPolida: CombinePrefixAndTail(prefixVars.FormalPolida, tailVars.FormalPolida),
            CurtaObjetiva: CombinePrefixAndTail(prefixVars.CurtaObjetiva, tailVars.CurtaObjetiva),
            EmpaticaCordial: CombinePrefixAndTail(prefixVars.EmpaticaCordial, tailVars.EmpaticaCordial),
            ExplicativaClara: CombinePrefixAndTail(prefixVars.ExplicativaClara, tailVars.ExplicativaClara),
            PersuasivaConfiante: CombinePrefixAndTail(prefixVars.PersuasivaConfiante, tailVars.PersuasivaConfiante),
            IsAiRefined: isAiRefined);
    }

    private static void SplitHistoryPrefixAndActiveTail(string fullText, out string historyPrefix, out string activeTail)
    {
        if (string.IsNullOrWhiteSpace(fullText) || fullText.Length <= MaxActiveRewriteTailChars)
        {
            historyPrefix = string.Empty;
            activeTail = fullText ?? string.Empty;
            return;
        }

        int splitSearchStart = fullText.Length - MaxActiveRewriteTailChars;
        int splitIdx = -1;

        for (int i = splitSearchStart; i < Math.Min(fullText.Length - 80, splitSearchStart + 180); i++)
        {
            char c = fullText[i];
            if ((c == '.' || c == '!' || c == '?' || c == '\n') && i + 1 < fullText.Length && char.IsWhiteSpace(fullText[i + 1]))
            {
                splitIdx = i + 1;
                break;
            }
        }

        if (splitIdx < 0)
        {
            int spaceIdx = fullText.IndexOf(' ', splitSearchStart);
            splitIdx = spaceIdx > 0 ? spaceIdx : splitSearchStart;
        }

        historyPrefix = fullText[..splitIdx].Trim();
        activeTail = fullText[splitIdx..].Trim();
    }

    private static string CombinePrefixAndTail(string prefix, string tail)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return (tail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tail))
            return prefix.Trim();
        return prefix.Trim() + " " + tail.Trim();
    }

    private static async Task<RewriteVariations?> FetchGeminiVariationsAsync(
        string apiKey,
        string rawSpokenText,
        CancellationToken token)
    {
        string prompt =
            "Reescreva a fala ditada (PT-BR) em 6 versões REALMENTE DIFERENTES entre si (nunca repita o mesmo texto em dois estilos), mantendo 100% dos fatos:\n" +
            "d = 1. Técnica e Detalhada (especificação técnica clara e bem estruturada)\n" +
            "f = 2. Didática e Simples (explicativa, acessível e em linguagem simples)\n" +
            "c = 3. Operacional / Entrega (retorno objetivo em voz ativa, abrindo com 'O que foi feito:')\n" +
            "e = 4. Formal e Institucional (norma culta executiva, polida e corporativa)\n" +
            "x = 5. Passo a Passo / Checklist (sequência lógica numerada '1)', '2)', '3)')\n" +
            "p = 6. Direta e Limpa (voz natural de quem falou, apenas corrigindo pontuação e gramática)\n\n" +
            "Fala:\n\"\"\"\n" + rawSpokenText + "\n\"\"\"\n\n" +
            "JSON estrito: {\"d\":\"...\",\"f\":\"...\",\"c\":\"...\",\"e\":\"...\",\"x\":\"...\",\"p\":\"...\"}";

        // Fast verified models in priority order:
        // 1. gemini-2.5-flash (thinkingBudget=0 -> ~800ms)
        // 2. gemini-3-flash-preview (MINIMAL thinking -> ~1050ms)
        // 3. gemini-3.5-flash (MINIMAL thinking -> ~1800ms)
        (string Model, bool UseBudgetZero, int TimeoutMs)[] candidates =
        {
            ("gemini-2.5-flash", true, 2800),
            ("gemini-3-flash-preview", false, 3200),
            ("gemini-3.5-flash", false, 3500)
        };

        foreach (var candidate in candidates)
        {
            if (token.IsCancellationRequested)
                return null;

            if (ModelCooldownUntilUtc.TryGetValue(candidate.Model, out DateTime cooldownUntil) &&
                DateTime.UtcNow < cooldownUntil)
            {
                continue;
            }

            var result = await TryModelAsync(
                apiKey,
                candidate.Model,
                prompt,
                candidate.UseBudgetZero,
                candidate.TimeoutMs,
                rawSpokenText,
                token);

            if (result != null)
                return result;
        }

        return null;
    }

    private static async Task<RewriteVariations?> TryModelAsync(
        string apiKey,
        string model,
        string prompt,
        bool useThinkingBudgetZero,
        int modelTimeoutMs,
        string rawOriginal,
        CancellationToken outerToken)
    {
        using var modelTimeoutCts = new CancellationTokenSource(modelTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken, modelTimeoutCts.Token);
        CancellationToken token = linkedCts.Token;

        try
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            object thinkingCfg = useThinkingBudgetZero
                ? new { thinkingBudget = 0 }
                : new { thinkingLevel = "MINIMAL" };

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = prompt } }
                    }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    temperature = 0.35,
                    maxOutputTokens = 650,
                    thinkingConfig = thinkingCfg
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-goog-api-key", apiKey);

            using var response = await SharedHttp.SendAsync(req, token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.NotFound)
                {
                    ModelCooldownUntilUtc[model] = DateTime.UtcNow.AddSeconds(75);
                }
                else if ((int)response.StatusCode >= 500)
                {
                    ModelCooldownUntilUtc[model] = DateTime.UtcNow.AddSeconds(25);
                }
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(token);
            using var outerDoc = JsonDocument.Parse(body);
            if (!outerDoc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
                candidates.GetArrayLength() == 0)
                return null;

            string? innerJson = candidates[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(innerJson))
                return null;

            using var innerDoc = JsonDocument.Parse(innerJson);
            JsonElement root = innerDoc.RootElement;

            string direta = ReadJsonString(root, "d", "direta");
            string formal = ReadJsonString(root, "f", "formal");
            string concisa = ReadJsonString(root, "c", "concisa");
            string empatica = ReadJsonString(root, "e", "empatica");
            string explicativa = ReadJsonString(root, "x", "explicativa");
            string persuasiva = ReadJsonString(root, "p", "persuasiva");

            if (string.IsNullOrWhiteSpace(direta) && string.IsNullOrWhiteSpace(formal) && string.IsNullOrWhiteSpace(concisa))
                return null;

            return new RewriteVariations(
                RawOriginal: rawOriginal,
                DiretaCoesa: direta,
                FormalPolida: formal,
                CurtaObjetiva: concisa,
                EmpaticaCordial: empatica,
                ExplicativaClara: explicativa,
                PersuasivaConfiante: persuasiva,
                IsAiRefined: true);
        }
        catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ModelCooldownUntilUtc[model] = DateTime.UtcNow.AddSeconds(45);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadJsonString(JsonElement root, string shortKey, string longKey)
    {
        if (root.TryGetProperty(shortKey, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            return (el.GetString() ?? string.Empty).Trim();
        if (root.TryGetProperty(longKey, out el) && el.ValueKind == JsonValueKind.String)
            return (el.GetString() ?? string.Empty).Trim();
        return string.Empty;
    }

    private static RewriteVariations EnsureAllSixAreDistinct(
        RewriteVariations ai,
        RewriteVariations localSix)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string PickDistinct(string aiCandidate, string localCandidate, string styleTagFallback)
        {
            string c = (aiCandidate ?? string.Empty).Trim();
            string normC = NormalizeForComparison(c);
            if (!string.IsNullOrWhiteSpace(c) && !used.Contains(normC))
            {
                used.Add(normC);
                return c;
            }

            string l = (localCandidate ?? string.Empty).Trim();
            string normL = NormalizeForComparison(l);
            if (!string.IsNullOrWhiteSpace(l) && !used.Contains(normL))
            {
                used.Add(normL);
                return l;
            }

            string fallback = string.IsNullOrWhiteSpace(l) ? c : l;
            string styled = ApplyGuaranteedStyleDistinction(fallback, styleTagFallback);
            used.Add(NormalizeForComparison(styled));
            return styled;
        }

        string d = PickDistinct(ai.DiretaCoesa, localSix.DiretaCoesa, "tecnica");
        string f = PickDistinct(ai.FormalPolida, localSix.FormalPolida, "didatica");
        string c = PickDistinct(ai.CurtaObjetiva, localSix.CurtaObjetiva, "operacional");
        string e = PickDistinct(ai.EmpaticaCordial, localSix.EmpaticaCordial, "formal");
        string x = PickDistinct(ai.ExplicativaClara, localSix.ExplicativaClara, "passos");
        string p = PickDistinct(ai.PersuasivaConfiante, localSix.PersuasivaConfiante, "direta");

        return new RewriteVariations(
            RawOriginal: ai.RawOriginal,
            DiretaCoesa: d,
            FormalPolida: f,
            CurtaObjetiva: c,
            EmpaticaCordial: e,
            ExplicativaClara: x,
            PersuasivaConfiante: p,
            IsAiRefined: true);
    }

    private static string NormalizeForComparison(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return Regex.Replace(text.Trim().ToLowerInvariant(), @"[\s,.;:!?]+", " ").Trim();
    }

    public static RewriteVariations BuildLocalSixVariations(
        string rawInput,
        bool isAiRefined = true)
    {
        string raw = (rawInput ?? string.Empty).Trim();
        string diretaLimpa = CleanSpokenPortugueseLocally(raw);
        if (string.IsNullOrWhiteSpace(diretaLimpa))
        {
            return new RewriteVariations(raw, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAiRefined);
        }

        string tecnicaDetalhada = BuildTechnicalDetailedStyleLocally(diretaLimpa);
        string didaticaSimples = BuildDidacticSimpleStyleLocally(diretaLimpa);
        string operacionalEntrega = BuildOperationalDeliveryStyleLocally(diretaLimpa);
        string formalInstitucional = BuildFormalStyleLocally(diretaLimpa);
        string passoAPasso = BuildStepByStepChecklistStyleLocally(diretaLimpa);

        var rawSet = new RewriteVariations(
            RawOriginal: raw,
            DiretaCoesa: tecnicaDetalhada,
            FormalPolida: didaticaSimples,
            CurtaObjetiva: operacionalEntrega,
            EmpaticaCordial: formalInstitucional,
            ExplicativaClara: passoAPasso,
            PersuasivaConfiante: diretaLimpa,
            IsAiRefined: isAiRefined);

        return EnsureAllSixAreDistinct(rawSet, rawSet) with { IsAiRefined = isAiRefined };
    }

    private static string BuildTechnicalDetailedStyleLocally(string cleanText)
    {
        string s = cleanText;
        s = Regex.Replace(s, @"(?i)^(olá[,.!\s]*)?(está me vendo\?\s*)?(olá[,.!\s]*)?(está me ouvindo\?\s*)", string.Empty);
        s = Regex.Replace(s, @"(?i)\bnão está funcionando\b", "apresenta falha de execução no fluxo esperado");
        s = Regex.Replace(s, @"(?i)\bàs vezes demora muito\b", "apresenta latência elevada de processamento em determinados cenários");
        s = Regex.Replace(s, @"(?i)\bdemora muito\b", "apresenta tempo de resposta acima do esperado");
        s = Regex.Replace(s, @"(?i)\bficando lento\b", "apresentando degradação progressiva de desempenho");

        s = EnsureSentenceCapitalizationAndPeriod(s);

        if (!s.StartsWith("Especificação", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("Detalhamento", StringComparison.OrdinalIgnoreCase))
        {
            s = $"Detalhamento técnico: {LowercaseFirstIfNotProper(s)}";
        }

        return EnsureSentenceCapitalizationAndPeriod(s);
    }

    private static string BuildDidacticSimpleStyleLocally(string cleanText)
    {
        string s = cleanText;
        s = Regex.Replace(s, @"(?i)^(olá[,.!\s]*)?(está me vendo\?\s*)?(olá[,.!\s]*)?(está me ouvindo\?\s*)", string.Empty);
        s = EnsureSentenceCapitalizationAndPeriod(s);

        if (!s.StartsWith("Explicando", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("De forma simples", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("Funciona assim", StringComparison.OrdinalIgnoreCase))
        {
            s = $"Explicando de forma simples e didática: {LowercaseFirstIfNotProper(s)}";
        }

        return EnsureSentenceCapitalizationAndPeriod(s);
    }

    private static string BuildOperationalDeliveryStyleLocally(string cleanText)
    {
        string s = cleanText;
        s = Regex.Replace(s, @"(?i)^(olá[,.!\s]+)?(bom dia|boa tarde|boa noite)[,.!\s]*(tudo bem\??[,.!\s]*)?", string.Empty);
        s = Regex.Replace(s, @"(?i)^(olá[,.!\s]*)?(está me vendo\?\s*)?(olá[,.!\s]*)?(está me ouvindo\?\s*)", string.Empty);

        s = EnsureSentenceCapitalizationAndPeriod(s);

        if (!s.StartsWith("O que foi feito:", StringComparison.OrdinalIgnoreCase))
        {
            s = $"O que foi feito: {LowercaseFirstIfNotProper(s)}";
        }

        return EnsureSentenceCapitalizationAndPeriod(s);
    }

    private static string BuildFormalStyleLocally(string cleanText)
    {
        string s = cleanText;
        s = Regex.Replace(s, @"(?i)\ba gente\b", "nós");
        s = Regex.Replace(s, @"(?i)\bnão está funcionando\b", "apresenta inconsistência operacional");
        s = EnsureSentenceCapitalizationAndPeriod(s);

        if (string.Equals(NormalizeForComparison(s), NormalizeForComparison(cleanText), StringComparison.OrdinalIgnoreCase))
        {
            string body = LowercaseFirstIfNotProper(s);
            s = $"Prezados, informo formalmente que {body}";
        }

        return EnsureSentenceCapitalizationAndPeriod(s);
    }

    private static string BuildStepByStepChecklistStyleLocally(string cleanText)
    {
        string s = cleanText;
        string[] parts = Regex.Split(s, @"(?<=[.!?])\s+|[,;]\s+(?=(?:e\s+|mas\s+|então\s+|para\s+|porque\s+))")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (parts.Length >= 2)
        {
            var sb = new StringBuilder("Passo a passo: ");
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append($"{i + 1}) {EnsureSentenceCapitalizationAndPeriod(parts[i])}");
            }
            return sb.ToString();
        }

        return EnsureSentenceCapitalizationAndPeriod($"Passo a passo: 1) {s}");
    }

    private static string ApplyGuaranteedStyleDistinction(string baseText, string styleTag)
    {
        string clean = EnsureSentenceCapitalizationAndPeriod(baseText);
        string lower = LowercaseFirstIfNotProper(clean);
        return styleTag switch
        {
            "tecnica" => $"Detalhamento técnico: {lower}",
            "didatica" => $"Explicando de forma simples e didática: {lower}",
            "operacional" => $"O que foi feito: {lower}",
            "formal" => $"Prezados, comunico formalmente que {lower}",
            "passos" => $"Passo a passo: 1) {clean}",
            _ => clean
        };
    }

    private static string LowercaseFirstIfNotProper(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;
        string trimmed = s.Trim();
        Match firstWordMatch = Regex.Match(trimmed, @"^[A-Za-zÀ-ÿ]+");
        string firstWord = firstWordMatch.Success ? firstWordMatch.Value : string.Empty;
        bool isAcronym = firstWord.Length > 1 && firstWord == firstWord.ToUpperInvariant();
        if (!isAcronym && !ProperNouns.Contains(firstWord) && char.IsUpper(trimmed[0]))
        {
            return char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
        }
        return trimmed;
    }

    private static string EnsureSentenceCapitalizationAndPeriod(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        if (char.IsLower(s[0]))
        {
            s = char.ToUpper(s[0]) + s[1..];
        }
        if (!s.EndsWith('.') && !s.EndsWith('!') && !s.EndsWith('?') && !s.EndsWith(':'))
        {
            s += ".";
        }
        return s;
    }

    private static readonly HashSet<string> ProperNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google", "Windows", "Gemini", "Brasil", "WhatsApp", "Chrome", "Edge",
        "ETEP", "Focus", "Futurus", "JACAD", "Moodle", "LXP", "SGR", "UniBTA",
        "Excel", "Word", "PowerPoint", "Office", "Antigravity", "Avalia", "SafeA"
    };

    /// <summary>
    /// Zero-regex native passthrough for Modo Nativo Direto (RealtimeRewriteEnabled = false).
    /// Delivers the native Gemini Live transcription directly to the cursor with 0.00ms overhead.
    /// </summary>
    public static string NormalizeNativeDirect(string rawChunk)
    {
        return string.IsNullOrWhiteSpace(rawChunk) ? string.Empty : rawChunk.Trim();
    }

    /// <summary>
    /// Prefix-stable normalization for the active Windows cursor when 6 Options / Correction mode is enabled.
    /// </summary>
    public static string NormalizeForLiveCursor(string rawChunk, char previousLastChar)
    {
        if (string.IsNullOrWhiteSpace(rawChunk))
            return string.Empty;

        string s = rawChunk.Trim();

        s = Regex.Replace(s, @"(?i)(^|[\s,;]+)(eh+|éh+|hum+|hã+|ahn+|uhm+)(?=([\s,;.!?]+|$))", " ");
        s = Regex.Replace(
            s,
            @"(?i)(?<!\b(como|palavra|palavras|termo|escrever|literal|dizer|falar)\s+),?\s*\bné\b\s*([?.!,;]*)",
            "$2");
        s = Regex.Replace(s, @"(?i)\b([a-zà-ÿ]+)\s+\1\b", "$1");
        s = Regex.Replace(s, @"([,;:!?])(?=[A-Za-zÀ-ÿ])", "$1 ");
        s = Regex.Replace(s, @"\s+([,;.!?])", "$1");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        if (string.IsNullOrEmpty(s))
            return string.Empty;

        bool isSentenceStart =
            previousLastChar == '\0' ||
            previousLastChar == '.' ||
            previousLastChar == '!' ||
            previousLastChar == '?' ||
            previousLastChar == '\n';

        if (isSentenceStart && char.IsLower(s[0]))
        {
            s = char.ToUpper(s[0]) + s[1..];
        }

        return s;
    }

    public static string CleanSpokenPortugueseLocally(string input)
    {
        string s = NormalizeForLiveCursor(input, '\0');
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        s = Regex.Replace(s, @"([.!?]\s+)([a-zà-ÿ])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        if (s.Length > 0 && !s.EndsWith('.') && !s.EndsWith('!') && !s.EndsWith('?') && !s.EndsWith(':'))
        {
            s += ".";
        }
        return s;
    }

    private static int CountWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return 0;
        int count = 0;
        bool inWord = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    // ========================================================================
    // RESUMIDOR TEMÁTICO EM TEMPO REAL — LINHA DO TEMPO CONTÍNUA (MIC & PC)
    // ========================================================================

    public void ResetTimelineTurnState()
    {
        lock (_timelineLock)
        {
            _timelineConsumedLen = 0;
            _activeTurnBuffer = string.Empty;
        }
    }

    /// <summary>
    /// Alimenta o resumidor de Linha do Tempo em tempo real (tanto no modo 🎧 Ouvir PC quanto no 🎙️ Microfone).
    /// - Atualiza instantaneamente o indicador de "Assunto Atual em Andamento".
    /// - Quando a fala contínua atinge ~135 caracteres (ex.: transmissão de TV, vídeo ou reunião sem pausas)
    ///   OU quando uma frase/pausa conclui (isFinalTurn = true), fatia o trecho, cria imediatamente o card
    ///   na Linha do Tempo e refina o Título do Assunto + Tópicos via Gemini em segundo plano (~600ms).
    /// </summary>
    public void FeedLiveSpeechForTimeline(string apiKey, string currentTurnText, bool isFinalTurn, bool isPcAudio)
    {
        string raw = (currentTurnText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return;

        List<string> segmentsToEmit = new();
        string liveRemainder = string.Empty;

        lock (_timelineLock)
        {
            // Se o texto recebido é menor que o já consumido (ou mudou de turno), recomeça o cursor do turno atual
            if (raw.Length < _timelineConsumedLen ||
                (_timelineConsumedLen > 0 && !raw.StartsWith(_activeTurnBuffer[..Math.Min(_activeTurnBuffer.Length, _timelineConsumedLen)], StringComparison.OrdinalIgnoreCase)))
            {
                _timelineConsumedLen = 0;
            }

            _activeTurnBuffer = raw;
            string unconsumed = _timelineConsumedLen < raw.Length
                ? raw[_timelineConsumedLen..].TrimStart()
                : string.Empty;

            if (!isFinalTurn)
            {
                // Enquanto fala continuamente (ex: 🎧 Ouvir PC em vídeos/jornais/reuniões), fatia a cada ~140 chars num limite natural
                while (unconsumed.Length >= 145)
                {
                    int splitPos = FindNaturalTimelineSplit(unconsumed, minChars: 105, maxChars: 165);
                    if (splitPos <= 0)
                        break;

                    string chunk = unconsumed[..splitPos].Trim();
                    int consumedAdvance = raw.Length - unconsumed.Length + splitPos;
                    _timelineConsumedLen = Math.Clamp(consumedAdvance, 0, raw.Length);
                    unconsumed = _timelineConsumedLen < raw.Length
                        ? raw[_timelineConsumedLen..].TrimStart()
                        : string.Empty;

                    if (CountWords(chunk) >= 4)
                    {
                        segmentsToEmit.Add(chunk);
                    }
                }
                liveRemainder = unconsumed;
            }
            else
            {
                // Fechamento de frase ou pausa (isFinalTurn): emite o trecho restante se tiver conteúdo relevante
                if (CountWords(unconsumed) >= 3 && unconsumed.Length >= 14)
                {
                    segmentsToEmit.Add(unconsumed.Trim());
                }
                _timelineConsumedLen = 0;
                _activeTurnBuffer = string.Empty;
                liveRemainder = string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(liveRemainder))
        {
            string quickTheme = ExtractQuickLocalTheme(liveRemainder, _lastKnownThemeTitle);
            OnCurrentLiveTopicPreview?.Invoke(quickTheme, liveRemainder);
        }

        foreach (string seg in segmentsToEmit)
        {
            EmitAndRefineTimelineSegment(apiKey, seg, isPcAudio);
        }
    }

    public void FlushPendingTimelineSegment(string apiKey, bool isPcAudio)
    {
        string pending = string.Empty;
        lock (_timelineLock)
        {
            if (!string.IsNullOrWhiteSpace(_activeTurnBuffer) && _timelineConsumedLen < _activeTurnBuffer.Length)
            {
                pending = _activeTurnBuffer[_timelineConsumedLen..].Trim();
            }
            _timelineConsumedLen = 0;
            _activeTurnBuffer = string.Empty;
        }

        if (CountWords(pending) >= 3 && pending.Length >= 14)
        {
            EmitAndRefineTimelineSegment(apiKey, pending, isPcAudio);
        }
    }

    private static int FindNaturalTimelineSplit(string text, int minChars, int maxChars)
    {
        int limit = Math.Min(text.Length, maxChars);
        // 1. Prioriza fim de frase (. ! ? ;)
        for (int i = limit - 1; i >= minChars; i--)
        {
            char c = text[i];
            if (c is '.' or '!' or '?' or ';')
                return i + 1;
        }
        // 2. Vírgula ou conjunção
        for (int i = limit - 1; i >= minChars; i--)
        {
            if (text[i] == ',')
                return i + 1;
        }
        // 3. Espaço entre palavras
        for (int i = limit - 1; i >= minChars; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i + 1;
        }
        return limit;
    }

    private void EmitAndRefineTimelineSegment(string apiKey, string rawSegment, bool isPcAudio)
    {
        string cleanSeg = CleanSpokenPortugueseLocally(rawSegment);
        if (string.IsNullOrWhiteSpace(cleanSeg))
            return;

        string norm = NormalizeForComparison(cleanSeg);
        TimelineLogEntry initialEntry;

        lock (_timelineLock)
        {
            if (!string.IsNullOrEmpty(_lastEmittedSegmentNorm) &&
                (DateTime.UtcNow - _lastEmittedUtc).TotalSeconds < 8.0 &&
                (string.Equals(norm, _lastEmittedSegmentNorm, StringComparison.OrdinalIgnoreCase) ||
                 _lastEmittedSegmentNorm.EndsWith(norm, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _lastEmittedSegmentNorm = norm;
            _lastEmittedUtc = DateTime.UtcNow;

            long id = ++_nextTimelineId;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string localTheme = ExtractQuickLocalTheme(cleanSeg, _lastKnownThemeTitle);
            string[] localBullets = BuildQuickLocalBullets(cleanSeg);
            _lastKnownThemeTitle = localTheme;

            initialEntry = new TimelineLogEntry(
                Id: id,
                Timestamp: timestamp,
                IsPcAudio: isPcAudio,
                TopicTitle: localTheme,
                Bullets: localBullets,
                RawText: cleanSeg,
                IsAiRefined: false);
        }

        OnTimelineEntryUpdated?.Invoke(initialEntry, true);
        OnCurrentLiveTopicPreview?.Invoke(initialEntry.TopicTitle, cleanSeg);

        if (string.IsNullOrWhiteSpace(apiKey) || CountWords(cleanSeg) < 4)
        {
            var markedReady = initialEntry with { IsAiRefined = true };
            OnTimelineEntryUpdated?.Invoke(markedReady, false);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var aiSummary = await FetchGeminiTimelineSummaryAsync(apiKey.Trim(), cleanSeg, _lastKnownThemeTitle);
                if (aiSummary != null)
                {
                    lock (_timelineLock)
                    {
                        _lastKnownThemeTitle = aiSummary.Value.Theme;
                    }
                    var refined = initialEntry with
                    {
                        TopicTitle = aiSummary.Value.Theme,
                        Bullets = aiSummary.Value.Bullets,
                        IsAiRefined = true
                    };
                    OnTimelineEntryUpdated?.Invoke(refined, false);
                    OnCurrentLiveTopicPreview?.Invoke(refined.TopicTitle, cleanSeg);
                }
                else
                {
                    OnTimelineEntryUpdated?.Invoke(initialEntry with { IsAiRefined = true }, false);
                }
            }
            catch
            {
                OnTimelineEntryUpdated?.Invoke(initialEntry with { IsAiRefined = true }, false);
            }
        });
    }

    private static async Task<(string Theme, string[] Bullets)?> FetchGeminiTimelineSummaryAsync(
        string apiKey,
        string cleanSegment,
        string previousTheme)
    {
        string contextLine = string.IsNullOrWhiteSpace(previousTheme)
            ? string.Empty
            : $"Assunto imediatamente anterior (para continuidade): \"{previousTheme}\"\n";

        string prompt =
            "Você é um resumidor temático em tempo real para uma Linha do Tempo de Assuntos.\n" +
            contextLine +
            "Analise o trecho de fala/áudio abaixo e sintetize:\n" +
            "1) \"tema\": Título curto, claro e específico do assunto atual (3 a 6 palavras, sem ponto final).\n" +
            "2) \"topicos\": Lista com 1 ou 2 frases curtas e diretas resumindo a informação principal (sem repetir o título).\n\n" +
            "Trecho:\n\"\"\"\n" + cleanSegment + "\n\"\"\"\n\n" +
            "Retorne APENAS JSON: {\"tema\":\"...\",\"topicos\":[\"...\"]}";

        (string Model, bool UseBudgetZero, int TimeoutMs)[] candidates =
        {
            ("gemini-2.5-flash", true, 2600),
            ("gemini-3-flash-preview", false, 3000)
        };

        foreach (var c in candidates)
        {
            if (ModelCooldownUntilUtc.TryGetValue(c.Model, out DateTime until) && DateTime.UtcNow < until)
                continue;

            try
            {
                using var cts = new CancellationTokenSource(c.TimeoutMs);
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{c.Model}:generateContent";
                object thinkingCfg = c.UseBudgetZero
                    ? new { thinkingBudget = 0 }
                    : new { thinkingLevel = "MINIMAL" };

                var payload = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new
                    {
                        responseMimeType = "application/json",
                        temperature = 0.25,
                        maxOutputTokens = 240,
                        thinkingConfig = thinkingCfg
                    }
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-goog-api-key", apiKey);

                using var resp = await SharedHttp.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    continue;

                string body = await resp.Content.ReadAsStringAsync(cts.Token);
                using var outerDoc = JsonDocument.Parse(body);
                if (!outerDoc.RootElement.TryGetProperty("candidates", out JsonElement cands) || cands.GetArrayLength() == 0)
                    continue;

                string? innerJson = cands[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(innerJson))
                    continue;

                using var innerDoc = JsonDocument.Parse(innerJson);
                JsonElement root = innerDoc.RootElement;
                string tema = ReadJsonString(root, "tema", "assunto").Trim().TrimEnd('.', ':', ';');
                var bullets = new List<string>();

                if (root.TryGetProperty("topicos", out JsonElement topicosEl) && topicosEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in topicosEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string b = (item.GetString() ?? string.Empty).Trim().TrimStart('•', '-', '*').Trim();
                            if (!string.IsNullOrWhiteSpace(b))
                                bullets.Add(EnsureSentenceCapitalizationAndPeriod(b));
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(tema) && bullets.Count > 0)
                {
                    return (tema, bullets.Take(3).ToArray());
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static readonly HashSet<string> PortugueseStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "o", "a", "os", "as", "um", "uma", "de", "do", "da", "dos", "das", "em", "no", "na", "nos", "nas",
        "por", "para", "com", "sem", "que", "se", "como", "mais", "mas", "ou", "e", "é", "são", "foi", "está",
        "estão", "ser", "ter", "vai", "vamos", "muito", "bem", "já", "aqui", "ali", "isso", "isto", "esse",
        "essa", "este", "esta", "ele", "ela", "eles", "elas", "nós", "eu", "você", "então", "assim", "sobre",
        "também", "quando", "onde", "porque", "pelo", "pela", "ao", "aos", "às", "dos", "bom", "boa", "tarde",
        "dia", "noite", "olá", "gente", "cara", "caro", "todos", "todas"
    };

    public static string ExtractQuickLocalTheme(string text, string? fallbackPreviousTheme = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return !string.IsNullOrWhiteSpace(fallbackPreviousTheme) ? fallbackPreviousTheme : "Captando assunto ao vivo...";

        var words = Regex.Matches(text, @"[A-Za-zÀ-ÿ0-9]{3,}")
            .Select(m => m.Value)
            .Where(w => !PortugueseStopWords.Contains(w))
            .Take(5)
            .ToArray();

        if (words.Length == 0)
            return !string.IsNullOrWhiteSpace(fallbackPreviousTheme) ? fallbackPreviousTheme : "Fala em andamento";

        string rawTitle = string.Join(" ", words.Select((w, idx) =>
            idx == 0 || w.Length > 3
                ? char.ToUpper(w[0]) + w[1..]
                : w.ToLowerInvariant()));

        return rawTitle.Length > 48 ? rawTitle[..48].Trim() + "..." : rawTitle;
    }

    private static string[] BuildQuickLocalBullets(string cleanText)
    {
        string[] sentences = Regex.Split(cleanText, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(2)
            .Select(EnsureSentenceCapitalizationAndPeriod)
            .ToArray();

        return sentences.Length > 0 ? sentences : new[] { EnsureSentenceCapitalizationAndPeriod(cleanText) };
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _pendingCts, null);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}
