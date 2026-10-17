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
    bool IsAiRefined,
    string[]? SugestoesCompletar = null);

public sealed class GeminiRewriteClient : IDisposable
{
    private const int MaxActiveRewriteTailChars = 520;

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(8),
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(3.5)
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    // Circuit breaker cooldown map so rate-limited (429) or overloaded (503/timeout) models are skipped in 0ms
    private static readonly ConcurrentDictionary<string, DateTime> ModelCooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _pendingCts;
    private long _versionCounter;

    // High-capacity memory cache for long speeches so prefix paragraphs stay AI-refined and never re-run regexes on every syllable
    private readonly object _prefixLock = new();
    private string _cachedRawPrefix = string.Empty;
    private RewriteVariations? _cachedPrefixVariations;

    // Cache of the last AI-refined active tail so duplicate requests return in 0ms without hitting the network
    private string _lastRefinedTailRaw = string.Empty;
    private RewriteVariations? _lastRefinedTailResult;

    // User vocabulary Bigram/Trigram memory for 0ms personalized next-word predictions
    private static readonly object BigramLock = new();
    private static readonly Dictionary<string, List<string>> LearnedNextWords =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<RewriteVariations>? OnVariationsUpdated;

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
    /// Immediately emits 6 distinct 0ms local variations + 0ms next-word predictions (preserving 100% of the long text in memory)
    /// and schedules a fast Gemini rewrite + contextual next-word prediction on the active tail window.
    /// </summary>
    public void RequestRealtimeRewrite(string apiKey, string rawSpokenText, bool isFinalCommit = false)
    {
        string trimmed = (rawSpokenText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        if (isFinalCommit)
        {
            LearnFromUserText(trimmed);
        }

        long myVersion = Interlocked.Increment(ref _versionCounter);

        var prevCts = Interlocked.Exchange(ref _pendingCts, new CancellationTokenSource());
        try { prevCts?.Cancel(); } catch { }
        try { prevCts?.Dispose(); } catch { }

        CancellationToken token = _pendingCts!.Token;

        SplitHistoryPrefixAndActiveTail(trimmed, out string rawPrefix, out string rawTail);
        string[] instantPredictions = PredictNextWordsLocally(trimmed);

        RewriteVariations? prefixVarsSnapshot = null;
        RewriteVariations? cachedTailSnapshot = null;

        lock (_prefixLock)
        {
            if (!string.IsNullOrEmpty(rawPrefix))
            {
                if (_cachedRawPrefix != rawPrefix || _cachedPrefixVariations == null)
                {
                    _cachedRawPrefix = rawPrefix;
                    _cachedPrefixVariations = BuildLocalSixVariations(rawPrefix, instantPredictions, isAiRefined: true);
                }
                prefixVarsSnapshot = _cachedPrefixVariations;
            }

            if (!string.IsNullOrEmpty(rawTail) && _lastRefinedTailRaw == rawTail && _lastRefinedTailResult != null)
            {
                cachedTailSnapshot = _lastRefinedTailResult;
            }
        }

        // If the exact active tail was already refined, emit the complete refined result immediately in 0ms!
        if (cachedTailSnapshot != null)
        {
            RewriteVariations cachedFull = CombinePrefixAndTailVariations(
                trimmed,
                prefixVarsSnapshot,
                cachedTailSnapshot,
                instantPredictions,
                isAiRefined: true);
            OnVariationsUpdated?.Invoke(cachedFull);
            return;
        }

        // Build 6 genuinely distinct local variations in 0ms for the active tail
        RewriteVariations localTailSix = BuildLocalSixVariations(rawTail, instantPredictions, isAiRefined: false);
        RewriteVariations draftResult = CombinePrefixAndTailVariations(
            trimmed,
            prefixVarsSnapshot,
            localTailSix,
            instantPredictions,
            isAiRefined: false);

        // 1. Emit instant 0ms local variations (while keeping IsAiRefined = false so spinner shows during conversion)
        OnVariationsUpdated?.Invoke(draftResult);

        // Fast debounce on final sentence commit (55ms); pause-based debounce during interim speech (580ms) so we never spam the API mid-word
        int debounceMs = isFinalCommit ? 55 : 580;

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
                var aiTailResult = await FetchGeminiVariationsAsync(keyTrimmed, rawTail, instantPredictions, token);

                if (!token.IsCancellationRequested && Interlocked.Read(ref _versionCounter) == myVersion)
                {
                    RewriteVariations effectiveTail = aiTailResult != null
                        ? EnsureAllSixAreDistinct(aiTailResult, localTailSix, instantPredictions)
                        : localTailSix with { IsAiRefined = true };

                    RewriteVariations fullResult = CombinePrefixAndTailVariations(
                        trimmed,
                        prefixVarsSnapshot,
                        effectiveTail,
                        effectiveTail.SugestoesCompletar ?? instantPredictions,
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
        string[] suggestions,
        bool isAiRefined)
    {
        if (prefixVars == null)
        {
            return tailVars with
            {
                RawOriginal = fullRawOriginal,
                IsAiRefined = isAiRefined,
                SugestoesCompletar = suggestions
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
            IsAiRefined: isAiRefined,
            SugestoesCompletar: suggestions);
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

        // Prefer splitting at a sentence boundary ('. ', '! ', '? ', '\n') near the window boundary
        for (int i = splitSearchStart; i < Math.Min(fullText.Length - 80, splitSearchStart + 180); i++)
        {
            char c = fullText[i];
            if ((c == '.' || c == '!' || c == '?' || c == '\n') && i + 1 < fullText.Length && char.IsWhiteSpace(fullText[i + 1]))
            {
                splitIdx = i + 1;
                break;
            }
        }

        // Fallback to word boundary space
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
        string[] fallbackPredictions,
        CancellationToken token)
    {
        string prompt =
            "Reescreva a fala ditada (PT-BR) em 6 versões REALMENTE DIFERENTES entre si (nunca repita o mesmo texto em dois estilos), mantendo 100% dos fatos, e sugira 5 continuações curtas (1 a 4 palavras):\n" +
            "d = 1. Técnica e Detalhada (transforme a fala em especificação técnica completa, detalhada e bem estruturada de computação/sistema, detalhando contexto, causa ou requisito com clareza)\n" +
            "f = 2. Didática e Simples (explique o assunto de forma muito didática, acessível e em linguagem simples para leigos, esclarecendo o porquê e como funciona sem jargão difícil)\n" +
            "c = 3. Operacional / Entrega (formato objetivo de retorno operacional em voz ativa, abrindo com 'O que foi feito:' ou 'Alinhamento operacional:', direto e focado em ação/status)\n" +
            "e = 4. Formal e Institucional (norma culta executiva, polida, corporativa e respeitosa para comunicados oficiais, fornecedores ou diretoria)\n" +
            "x = 5. Passo a Passo / Checklist (estruture em sequência lógica numerada '1)', '2)', '3)' com passos claros e acionáveis)\n" +
            "p = 6. Direta e Limpa (mantenha a voz natural de quem falou, apenas removendo cacoetes/repetições da fala e corrigindo pontuação e gramática)\n" +
            "s = array com 5 sugestões curtas de próximas palavras para completar a última frase.\n\n" +
            "Fala:\n\"\"\"\n" + rawSpokenText + "\n\"\"\"\n\n" +
            "JSON estrito: {\"d\":\"...\",\"f\":\"...\",\"c\":\"...\",\"e\":\"...\",\"x\":\"...\",\"p\":\"...\",\"s\":[\"...\",\"...\",\"...\",\"...\",\"...\"]}";

        // Priority order of models with automatic circuit-breaker cooldown:
        // 1. gemini-3.8-flash (thinkingBudget=0 -> primary model requested by owner, full quota availability)
        // 2. gemini-3.5-flash (MINIMAL thinking -> fast backup)
        // 3. gemini-3-flash-preview (MINIMAL thinking -> secondary backup)
        (string Model, bool UseBudgetZero, int TimeoutMs)[] candidates =
        {
            ("gemini-3.8-flash", true, 9500),
            ("gemini-3.5-flash", false, 4500),
            ("gemini-3-flash-preview", false, 4200)
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
                fallbackPredictions,
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
        string[] fallbackPredictions,
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
                    maxOutputTokens = 750,
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
                    // Cooldown 429/404 model for 75 seconds so subsequent turns skip it in 0ms
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

            string[] parsedSuggestions = fallbackPredictions;
            if ((root.TryGetProperty("s", out JsonElement sugElem) || root.TryGetProperty("sugestoes", out sugElem)) &&
                sugElem.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(5);
                foreach (JsonElement item in sugElem.EnumerateArray())
                {
                    string? val = item.GetString()?.Trim().TrimStart('+', '•', '-', ' ');
                    if (!string.IsNullOrWhiteSpace(val) && val.Length <= 42 && !list.Contains(val, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(val);
                        if (list.Count >= 5)
                            break;
                    }
                }
                if (list.Count > 0)
                {
                    while (list.Count < 5 && fallbackPredictions.Length > 0)
                    {
                        string nextFallback = fallbackPredictions[list.Count % fallbackPredictions.Length];
                        if (!list.Contains(nextFallback, StringComparer.OrdinalIgnoreCase))
                            list.Add(nextFallback);
                        else
                            break;
                    }
                    parsedSuggestions = list.ToArray();
                }
            }

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
                IsAiRefined: true,
                SugestoesCompletar: parsedSuggestions);
        }
        catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Model-level timeout -> put model on brief 25s cooldown and try next candidate
            ModelCooldownUntilUtc[model] = DateTime.UtcNow.AddSeconds(25);
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

    /// <summary>
    /// Guarantees that all 6 variations are non-empty and genuinely distinct from one another.
    /// If any AI slot is empty or duplicates another slot, substitutes the corresponding distinct local style.
    /// </summary>
    private static RewriteVariations EnsureAllSixAreDistinct(
        RewriteVariations ai,
        RewriteVariations localSix,
        string[] fallbackSuggestions)
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
            IsAiRefined: true,
            SugestoesCompletar: ai.SugestoesCompletar ?? fallbackSuggestions);
    }

    private static string NormalizeForComparison(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return Regex.Replace(text.Trim().ToLowerInvariant(), @"[\s,.;:!?]+", " ").Trim();
    }

    /// <summary>
    /// Instant 0ms Local 6-Style Variation Engine in Portuguese (PT-BR).
    /// Produces 6 genuinely distinct variations aligned with the owner's workflow:
    /// 1. Técnica e Detalhada
    /// 2. Didática e Simples
    /// 3. Operacional / Entrega ("O que foi feito:")
    /// 4. Formal e Institucional
    /// 5. Passo a Passo / Checklist (1, 2, 3)
    /// 6. Direta e Limpa
    /// </summary>
    public static RewriteVariations BuildLocalSixVariations(
        string rawInput,
        string[]? suggestions = null,
        bool isAiRefined = true)
    {
        string raw = (rawInput ?? string.Empty).Trim();
        string[] preds = suggestions ?? PredictNextWordsLocally(raw);

        string diretaLimpa = CleanSpokenPortugueseLocally(raw);
        if (string.IsNullOrWhiteSpace(diretaLimpa))
        {
            return new RewriteVariations(raw, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, isAiRefined, preds);
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
            IsAiRefined: isAiRefined,
            SugestoesCompletar: preds);

        return EnsureAllSixAreDistinct(rawSet, rawSet, preds) with { IsAiRefined = isAiRefined };
    }

    private static string BuildTechnicalDetailedStyleLocally(string cleanText)
    {
        string s = cleanText;
        s = Regex.Replace(s, @"(?i)^(olá[,.!\s]*)?(está me vendo\?\s*)?(olá[,.!\s]*)?(está me ouvindo\?\s*)", string.Empty);
        s = Regex.Replace(s, @"(?i)\bnão está funcionando\b", "apresenta falha de execução no fluxo esperado");
        s = Regex.Replace(s, @"(?i)\bàs vezes demora muito\b", "apresenta latência elevada de processamento em determinados cenários");
        s = Regex.Replace(s, @"(?i)\bdemora muito\b", "apresenta tempo de resposta acima do esperado");
        s = Regex.Replace(s, @"(?i)\bficando lento\b", "apresentando degradação progressiva de desempenho");
        s = Regex.Replace(s, @"(?i)\bse tem como (nós|a gente) acelerar isso\b", "sendo necessário otimizar o tempo de resposta e reduzir a latência dessa operação");
        s = Regex.Replace(s, @"(?i)\bmexer aqui\b", "ajustar a implementação atual");
        s = Regex.Replace(s, @"(?i)\bnegócio de mouse\b", "módulo de controle e interceptação de cursor (CursorLoop)");
        s = Regex.Replace(s, @"(?i)\bflicando\b", "apresentando oscilação visual (flickering) de posição");

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
        s = Regex.Replace(s, @"(?i)\btem duas coisas que eu (quero|queria|gostaria de) ver\b[:.,\s]*", "Quero te explicar dois pontos de um jeito bem simples: ");
        s = Regex.Replace(s, @"(?i)\bse tem como (nós|a gente) acelerar isso\b", "assim deixamos tudo funcionando mais rápido e fácil no dia a dia");

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
        s = Regex.Replace(s, @"(?i)^(para esclarecer|gostaria de esclarecer que|é importante destacar que|tem duas coisas que eu quero ver|quero ver duas coisas)[:.,\s]*", string.Empty);

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

        // Replace conversational openings/constructions with executive Portuguese
        s = Regex.Replace(s, @"(?i)^olá[,.!\s]*está me vendo\?\s*(olá[,.!\s]*está me ouvindo\?\s*)?", "Prezados, gostaria de confirmar se o áudio e o vídeo estão funcionando adequadamente. ");
        s = Regex.Replace(s, @"(?i)^olá[,.!\s]*está me ouvindo\?\s*", "Prezados, gostaria de confirmar se o áudio está funcionando adequadamente. ");
        s = Regex.Replace(s, @"(?i)\btem duas coisas que eu (quero|queria|gostaria de) ver\b[:.,\s]*", "Gostaria de alinhar dois pontos específicos: ");
        s = Regex.Replace(s, @"(?i)\b(eu quero|eu queria) ver\b", "gostaria de avaliar");
        s = Regex.Replace(s, @"(?i)\bquero ver\b", "gostaria de verificar");
        s = Regex.Replace(s, @"(?i)\ba gente\b", "nós");
        s = Regex.Replace(s, @"(?i)\bnão está funcionando\b", "apresenta inconsistência operacional");
        s = Regex.Replace(s, @"(?i)\bàs vezes demora muito\b", "por vezes apresenta latência elevada");
        s = Regex.Replace(s, @"(?i)\bdemora muito\b", "apresenta tempo de resposta elevado");
        s = Regex.Replace(s, @"(?i)\bficando lento\b", "apresentando lentidão progressiva");
        s = Regex.Replace(s, @"(?i)\bse tem como (nós|a gente) acelerar isso\b", "solicito avaliarmos como otimizar o tempo de processamento");
        s = Regex.Replace(s, @"(?i)\bse tem como acelerar isso\b", "solicito avaliarmos como otimizar esse desempenho");
        s = Regex.Replace(s, @"(?i)\bo que pode ser\?", "Qual seria o diagnóstico técnico para esse comportamento?");

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

        if (Regex.IsMatch(s, @"(?i)\b(duas coisas|dois pontos)\b"))
        {
            s = Regex.Replace(s, @"(?i)\btem duas coisas que eu quero ver\b[:.,\s]*", "Passo a passo: ");
            s = Regex.Replace(s, @"(?i)\b(a primeira é que|uma é que)\b", "1) ");
            s = Regex.Replace(s, @"(?i)[,;.]?\s*\be (a segunda é que|dois é que)\b", "; 2) ");
            return EnsureSentenceCapitalizationAndPeriod(s);
        }

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

    private static readonly Dictionary<string, string[]> BuiltInNextWordMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = new[] { "acordo com", "verificar", "esclarecer que", "forma clara", "hoje" },
            ["para"] = new[] { "que você", "esclarecer", "verificar no sistema", "resolver isso", "concluir" },
            ["que"] = new[] { "você possa", "o sistema", "já foi", "está em andamento", "não haja dúvidas" },
            ["no"] = new[] { "sistema", "Moodle", "momento", "portal do aluno", "final da página" },
            ["na"] = new[] { "barra lateral", "plataforma", "matriz curricular", "disciplina", "tela" },
            ["com"] = new[] { "sucesso", "a equipe", "certeza", "base nisso", "o suporte" },
            ["por"] = new[] { "favor", "gentileza", "isso", "completo", "enquanto" },
            ["em"] = new[] { "relação a", "processo de", "detalhes", "andamento", "contato" },
            ["a"] = new[] { "partir de", "respeito de", "todos", "fim de", "informação" },
            ["à"] = new[] { "disposição", "sua disposição", "equipe responsável", "coordenação", "secretaria" },
            ["o"] = new[] { "sistema", "processo", "conteúdo", "mais breve possível", "que foi feito" },
            ["os"] = new[] { "dados no sistema", "alunos", "conteúdos", "ajustes necessários", "próximos passos" },
            ["as"] = new[] { "informações", "disciplinas", "opções ao vivo", "orientações", "alterações" },
            ["uma"] = new[] { "mensagem", "solução rápida", "nova opção", "verificação", "dificuldade" },
            ["um"] = new[] { "novo recurso", "ajuste rápido", "momento", "teste completo", "detalhe importante" },
            ["não"] = new[] { "precisa", "foi possível", "há necessidade", "interfere", "se preocupe" },
            ["já"] = new[] { "foi concluído", "está disponível", "está funcionando", "verifiquei no sistema", "foi ajustado" },
            ["estou"] = new[] { "à disposição", "verificando agora", "encaminhando", "acompanhando", "fazendo o ajuste" },
            ["estamos"] = new[] { "à disposição", "em processo de", "trabalhando nisso", "verificando", "finalizando" },
            ["gostaria"] = new[] { "de incluir", "de esclarecer", "de solicitar", "que você", "de confirmar" },
            ["quero"] = new[] { "deixar claro", "incluir um recurso", "que funcione", "verificar", "ajustar isso" },
            ["preciso"] = new[] { "que você", "verificar no sistema", "ajustar", "de ajuda com", "confirmar" },
            ["precisamos"] = new[] { "implementar", "verificar", "garantir que", "ajustar", "concluir" },
            ["qualquer"] = new[] { "dúvida", "dificuldade", "problema", "alteração", "coisa me avise" },
            ["caso"] = new[] { "encontre alguma dificuldade", "precise de ajuda", "tenha alguma dúvida", "seja necessário", "prefira" },
            ["conforme"] = new[] { "orientei anteriormente", "combinado", "solicitado", "conversamos", "o previsto" },
            ["esta"] = new[] { "mensagem", "sala", "disciplina", "configuração", "opção" },
            ["esse"] = new[] { "recurso", "processo", "ajuste", "comportamento", "ponto" },
            ["essa"] = new[] { "informação", "questão", "alteração", "disciplina", "versão" },
            ["como"] = new[] { "orientei anteriormente", "funciona no sistema", "podemos ver", "combinado", "solicitado" },
            ["mais"] = new[] { "detalhes", "rápido possível", "uma vez", "claro", "informações" },
            ["muito"] = new[] { "obrigado", "bom", "importante", "melhor agora", "rápido" },
            ["obrigado"] = new[] { "pela atenção", "pelo retorno", "pela compreensão", "desde já", "pela parceria" },
            ["olá"] = new[] { "tudo bem?", "bom dia", "boa tarde", "boa noite", "para esclarecer:" },
            ["bom"] = new[] { "dia, tudo bem?", "trabalho", "resultado", "saber disso", "andamento" },
            ["boa"] = new[] { "tarde, tudo bem?", "noite, tudo bem?", "semana", "ideia", "notícia" }
        };

    private static readonly string[] DefaultGeneralContinuations =
    {
        "para esclarecer",
        "no sistema",
        "por gentileza",
        "estou à disposição",
        "qualquer dúvida"
    };

    public static void LearnFromUserText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        MatchCollection matches = Regex.Matches(text, @"[A-Za-zÀ-ÿ0-9]+");
        if (matches.Count < 2)
            return;

        lock (BigramLock)
        {
            for (int i = 0; i < matches.Count - 1; i++)
            {
                string w1 = matches[i].Value.ToLowerInvariant();
                string w2 = matches[i + 1].Value;
                if (w1.Length < 2 || w2.Length < 2)
                    continue;

                string nextPhrase = (i + 2 < matches.Count && w2.Length <= 3)
                    ? w2 + " " + matches[i + 2].Value
                    : w2;

                if (!LearnedNextWords.TryGetValue(w1, out var list))
                {
                    list = new List<string>(6);
                    LearnedNextWords[w1] = list;
                }

                list.RemoveAll(x => string.Equals(x, nextPhrase, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, nextPhrase);
                if (list.Count > 5)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }
        }
    }

    /// <summary>
    /// 0ms local prediction of the next 5 words/short phrases based on the user's last spoken words.
    /// </summary>
    public static string[] PredictNextWordsLocally(string currentText)
    {
        if (string.IsNullOrWhiteSpace(currentText))
            return DefaultGeneralContinuations;

        MatchCollection words = Regex.Matches(currentText.Trim(), @"[A-Za-zÀ-ÿ0-9]+");
        if (words.Count == 0)
            return DefaultGeneralContinuations;

        string lastWord = words[^1].Value.ToLowerInvariant();
        var results = new List<string>(5);

        // 1. Check user's learned bigrams first
        lock (BigramLock)
        {
            if (LearnedNextWords.TryGetValue(lastWord, out var learned))
            {
                foreach (string item in learned)
                {
                    if (!results.Contains(item, StringComparer.OrdinalIgnoreCase))
                    {
                        results.Add(item);
                        if (results.Count >= 3)
                            break;
                    }
                }
            }
        }

        // 2. Check built-in Portuguese contextual map for the last word
        if (BuiltInNextWordMap.TryGetValue(lastWord, out string[]? builtIn))
        {
            foreach (string item in builtIn)
            {
                if (!results.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(item);
                    if (results.Count >= 5)
                        return results.ToArray();
                }
            }
        }

        // 3. Check morphological patterns on the last word
        if (lastWord.EndsWith("ar", StringComparison.Ordinal) ||
            lastWord.EndsWith("er", StringComparison.Ordinal) ||
            lastWord.EndsWith("ir", StringComparison.Ordinal))
        {
            string[] verbFollows = { "o sistema", "as informações", "isso agora", "com clareza", "rapidamente" };
            foreach (string vf in verbFollows)
            {
                if (results.Count < 5 && !results.Contains(vf, StringComparer.OrdinalIgnoreCase))
                    results.Add(vf);
            }
        }
        else if (lastWord.EndsWith("ndo", StringComparison.Ordinal))
        {
            string[] gerundFollows = { "ao vivo", "perfeitamente", "no sistema", "as opções", "agora mesmo" };
            foreach (string gf in gerundFollows)
            {
                if (results.Count < 5 && !results.Contains(gf, StringComparer.OrdinalIgnoreCase))
                    results.Add(gf);
            }
        }

        // 4. Fill remaining slots up to 5 with natural sentence connectors
        foreach (string def in DefaultGeneralContinuations)
        {
            if (results.Count >= 5)
                break;
            if (!results.Contains(def, StringComparer.OrdinalIgnoreCase))
                results.Add(def);
        }

        return results.ToArray();
    }

    private static readonly HashSet<string> ProperNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google", "Windows", "Gemini", "Brasil", "WhatsApp", "Chrome", "Edge",
        "ETEP", "Focus", "Futurus", "JACAD", "Moodle", "LXP", "SGR", "UniBTA",
        "Excel", "Word", "PowerPoint", "Office", "Antigravity", "Avalia", "SafeA"
    };

    /// <summary>
    /// Prefix-stable normalization for the active Windows cursor:
    /// removes isolated vocalized pauses ("eh", "hum"), filler "né", exact stutters ("sem sem"),
    /// fixes missing spaces after commas/periods, fixes PT-PT ASR constructions ("está-me a ouvir"),
    /// and applies context-aware capitalization at index 0 without mutating earlier words as the sentence grows.
    /// </summary>
    public static string NormalizeForLiveCursor(string rawChunk, char previousLastChar)
    {
        if (string.IsNullOrWhiteSpace(rawChunk))
            return string.Empty;

        string s = rawChunk.Trim();

        // 1. Remove isolated vocalized pauses ("eh", "éh", "hum", "hã", "ahn", "uhm")
        s = Regex.Replace(s, @"(?i)(^|[\s,;]+)(eh+|éh+|hum+|hã+|ahn+|uhm+)(?=([\s,;.!?]+|$))", " ");

        // 2. Remove conversational filler "né" unless preceded by meta-words ("como né", "palavra né", "escrever né")
        s = Regex.Replace(
            s,
            @"(?i)(?<!\b(como|palavra|palavras|termo|escrever|literal|dizer|falar)\s+),?\s*\bné\b\s*([?.!,;]*)",
            "$2");

        // 3. Collapse consecutive duplicate words ("sem sem", "para Para", "uma uma")
        s = Regex.Replace(s, @"(?i)\b([a-zà-ÿ]+)\s+\1\b", "$1");

        // 4. Convert PT-PT ASR constructions to natural PT-BR
        s = Regex.Replace(s, @"(?i)\bestá-me a ouvir\b", "está me ouvindo");
        s = Regex.Replace(s, @"(?i)\bestá-me a ver\b", "está me vendo");
        s = Regex.Replace(s, @"(?i)\bestou a (falar|dizer|escrever|ver|tentar|fazer)\b", "estou $1ndo");

        // 5. Fix common ASR merged words and missing space after punctuation ("ver.uma", "funcionando.e", "frasesvariadas")
        s = Regex.Replace(s, @"([,;:!?])(?=[A-Za-zÀ-ÿ])", "$1 ");
        s = Regex.Replace(s, @"([a-zà-ÿ]{2,})\.(?=[a-zà-ÿA-ZÀ-Ÿ]{2,})", "$1. ");
        s = Regex.Replace(s, @"(?i)\b(frases)(variadas|longas|curtas|diferentes|prontas)\b", "$1 $2");
        s = Regex.Replace(s, @"(?i)\b(uma|um)(coesa|coeso|opção|forma|ideia|coisa|entrega|aplicação|ferramenta|versão|situação)\b", "$1 $2");
        s = Regex.Replace(s, @"(?i)\b(usado|falando|fazendo|usando|clicando|escrevendo)(alguma|algum|aquilo|aquele|aquela|isso|essa|esse)\b", "$1 $2");

        // 6. Clean orphaned punctuation and extra spaces
        s = Regex.Replace(s, @"\s+([,;.!?])", "$1");
        s = Regex.Replace(s, @",\s*,+", ",");
        s = Regex.Replace(s, @"^[\s,;]+", string.Empty);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        if (string.IsNullOrEmpty(s))
            return string.Empty;

        // 7. Context-aware capitalization:
        // Only capitalize the first word if starting the field or after '.', '!', '?', '\n'.
        bool isSentenceStart =
            previousLastChar == '\0' ||
            previousLastChar == '.' ||
            previousLastChar == '!' ||
            previousLastChar == '?' ||
            previousLastChar == '\n';

        if (isSentenceStart)
        {
            if (char.IsLower(s[0]))
            {
                s = char.ToUpper(s[0]) + s[1..];
            }
        }
        else if (char.IsUpper(s[0]))
        {
            Match firstWordMatch = Regex.Match(s, @"^[A-Za-zÀ-ÿ]+");
            string firstWord = firstWordMatch.Success ? firstWordMatch.Value : string.Empty;
            bool isAllCapsAcronym = firstWord.Length > 1 && firstWord == firstWord.ToUpperInvariant();
            if (!isAllCapsAcronym && !ProperNouns.Contains(firstWord))
            {
                s = char.ToLower(s[0]) + s[1..];
            }
        }

        return s;
    }

    public static string CleanSpokenPortugueseLocally(string input)
    {
        string s = NormalizeForLiveCursor(input, '\0');
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        // Additional polish for tooltip variations
        s = Regex.Replace(s, @"(?i),?\s*\b(sabe assim|tipo assim)\b\s*,?", " ");
        s = Regex.Replace(s, @"(?i)\b(nessa|nesse|essa|esse|na|no)\s+(nessa|nesse|essa|esse|na|no)\b", "$2");
        s = Regex.Replace(s, @"(?i)\bver\.\s*uma\s+é\s+que\b", "ver: a primeira é que");
        s = Regex.Replace(s, @"(?i)\bfuncionando\.\s*e\s+dois\?\s*é\s+que\b", "funcionando, e a segunda é que");
        s = Regex.Replace(s, @"(?i)\be\s+dois\?\s*é\s+que\b", "e a segunda é que");
        s = Regex.Replace(s, @"\bTá\b", "Está");
        s = Regex.Replace(s, @"\btá\b", "está");
        s = Regex.Replace(s, @"\bTô\b", "Estou");
        s = Regex.Replace(s, @"\btô\b", "estou");
        s = Regex.Replace(s, @"\bTava\b", "Estava");
        s = Regex.Replace(s, @"\btava\b", "estava");
        s = Regex.Replace(s, @"\bPra\b", "Para");
        s = Regex.Replace(s, @"\bpra\b", "para");

        // Ensure uppercase after '. ', '! ', '? '
        s = Regex.Replace(s, @"([.!?]\s+)([a-zà-ÿ])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();

        if (s.Length > 0 && !s.EndsWith('.') && !s.EndsWith('!') && !s.EndsWith('?') && !s.EndsWith(':'))
        {
            s += ".";
        }
        return s;
    }

    private static int CountWords(string s) => Regex.Matches(s, @"\S+").Count;

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _pendingCts, null);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}
