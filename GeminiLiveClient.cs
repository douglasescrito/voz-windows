using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VozWindows;

public sealed class GeminiLiveClient : IAsyncDisposable
{
    private const string EndpointTemplate =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=";

    public const string ModelName = "models/gemini-3.5-transcribe-live";

    // Keep up to 75 frames (3.0 seconds of 16kHz PCM) buffered during brief network jitter
    private const int MaxQueueFrames = 75;

    // Hot-swap to a pre-warmed fresh WebSocket after 2.5 minutes of active use and 2.5s of silence so context never grows heavy
    private static readonly TimeSpan ActiveSessionHotSwapAge = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan IdleSessionHotSwapAge = TimeSpan.FromMinutes(4);

    private sealed class LiveConnection : IDisposable
    {
        public readonly ClientWebSocket Socket = new();
        public readonly CancellationTokenSource Cts = new();
        public readonly TaskCompletionSource<bool> SetupTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> DrainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SetupComplete;
        public volatile bool IsClosing;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public bool HasTranscribedInSession;

        public void Dispose()
        {
            IsClosing = true;
            SetupComplete = false;
            try { Cts.Cancel(); } catch { }
            try { Socket.Dispose(); } catch { }
            try { Cts.Dispose(); } catch { }
        }
    }

    private volatile LiveConnection? _activeConn;
    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private string _apiKey = string.Empty;
    private string _languageCode = "pt-BR";
    private volatile bool _streamActive;
    private volatile bool _reconnecting;
    private volatile bool _hotSwapping;

    private DateTime _lastTranscriptActivityUtc = DateTime.UtcNow;
    private DateTime _lastSendSuccessUtc = DateTime.UtcNow;
    private string _lastInterim = string.Empty;

    private readonly object _turnPrefixLock = new();
    private string _rawServerTurnInterim = string.Empty;
    private string _consumedServerTurnPrefix = string.Empty;

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnInterimTranscript;
    public event Action<string>? OnFinalTranscript;
    public event Action<string>? OnError;

    public bool IsConnected
    {
        get
        {
            var conn = _activeConn;
            return conn != null &&
                   conn.Socket.State == WebSocketState.Open &&
                   conn.SetupComplete &&
                   (DateTime.UtcNow - _lastSendSuccessUtc).TotalSeconds < 8;
        }
    }

    public bool IsStandby => false;

    public void ClearInterimState()
    {
        lock (_turnPrefixLock)
        {
            if (!string.IsNullOrWhiteSpace(_rawServerTurnInterim))
            {
                _consumedServerTurnPrefix = _rawServerTurnInterim;
            }
            _lastInterim = string.Empty;
        }
    }

    /// <summary>
    /// Marks any open server turn prefix as consumed without dropping queued audio or resetting the WebSocket.
    /// </summary>
    public void CutOffServerTurn()
    {
        ClearInterimState();
    }

    public async Task StartSessionAsync(string apiKey, string languageCode = "pt-BR")
    {
        _apiKey = apiKey.Trim();
        _languageCode = languageCode;
        _streamActive = true;

        while (_pcmQueue.TryDequeue(out _)) { }
        lock (_turnPrefixLock)
        {
            _lastInterim = string.Empty;
            _rawServerTurnInterim = string.Empty;
            _consumedServerTurnPrefix = string.Empty;
        }

        await _lifecycleLock.WaitAsync();
        try
        {
            OnStatusChanged?.Invoke("Conectando ao Gemini 3.5 Live...");
            var fresh = await CreateAndInitializeConnectionAsync();
            var old = Interlocked.Exchange(ref _activeConn, fresh);
            old?.Dispose();

            _lastSendSuccessUtc = DateTime.UtcNow;
            OnStatusChanged?.Invoke("OUVINDO AO VIVO • Qualidade máxima sem reescrita (gemini-3.5-transcribe-live)");
            _ = FlushQueuedAudioAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<LiveConnection> CreateAndInitializeConnectionAsync()
    {
        var conn = new LiveConnection();
        conn.Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        try
        {
            string uri = EndpointTemplate + Uri.EscapeDataString(_apiKey);
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
            using (var linkedConnect = CancellationTokenSource.CreateLinkedTokenSource(conn.Cts.Token, connectTimeout.Token))
            {
                await conn.Socket.ConnectAsync(new Uri(uri), linkedConnect.Token);
            }

            _ = Task.Run(() => ReceiveLoopAsync(conn));

            var setupPayload = new
            {
                setup = new
                {
                    model = ModelName,
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT" }
                    },
                    inputAudioTranscription = new
                    {
                        languageCodes = string.IsNullOrWhiteSpace(_languageCode)
                            ? Array.Empty<string>()
                            : new[] { _languageCode }
                    }
                }
            };

            byte[] setupBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(setupPayload));
            using (var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            using (var linkedSend = CancellationTokenSource.CreateLinkedTokenSource(conn.Cts.Token, sendTimeout.Token))
            {
                await conn.Socket.SendAsync(new ArraySegment<byte>(setupBytes), WebSocketMessageType.Text, true, linkedSend.Token);
            }

            using (var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
            using (var linkedSetup = CancellationTokenSource.CreateLinkedTokenSource(conn.Cts.Token, setupTimeout.Token))
            {
                await conn.SetupTcs.Task.WaitAsync(linkedSetup.Token);
            }

            conn.CreatedUtc = DateTime.UtcNow;
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public void EnqueueAudioPcm16k(byte[] pcmBytes, double rms = 0.02)
    {
        if (!_streamActive || pcmBytes.Length == 0)
            return;

        _pcmQueue.Enqueue(pcmBytes);
        while (_pcmQueue.Count > MaxQueueFrames && _pcmQueue.TryDequeue(out _)) { }

        var conn = _activeConn;
        DateTime now = DateTime.UtcNow;

        if (conn != null && conn.SetupComplete && conn.Socket.State == WebSocketState.Open)
        {
            _ = FlushQueuedAudioAsync();

            // Check if this session should be hot-swapped in the background so context never grows heavy
            TimeSpan age = now - conn.CreatedUtc;
            double quietSecs = (now - _lastTranscriptActivityUtc).TotalSeconds;
            bool shouldHotSwap =
                !_hotSwapping &&
                !_reconnecting &&
                quietSecs >= 2.5 &&
                string.IsNullOrEmpty(_lastInterim) &&
                ((conn.HasTranscribedInSession && age >= ActiveSessionHotSwapAge) || age >= IdleSessionHotSwapAge);

            if (shouldHotSwap)
            {
                _ = PerformZeroDowntimeHotSwapAsync();
            }
        }
        else if (!_reconnecting)
        {
            _ = TriggerReconnectAsync("Reconexão automática");
        }
    }

    /// <summary>
    /// Opens and initializes a brand-new WebSocket session in the background while the current session
    /// continues streaming, then atomically swaps them in 0ms ONLY if the user is still silent.
    /// </summary>
    private async Task PerformZeroDowntimeHotSwapAsync(bool forceForCleanTurn = false)
    {
        if (!_streamActive || _hotSwapping || _reconnecting)
            return;

        _hotSwapping = true;
        try
        {
            if (!await _lifecycleLock.WaitAsync(0))
                return;

            try
            {
                if (!_streamActive)
                    return;

                var fresh = await CreateAndInitializeConnectionAsync();
                double minQuietSecs = forceForCleanTurn ? 0.5 : 2.2;
                if (!_streamActive ||
                    !string.IsNullOrEmpty(_lastInterim) ||
                    (DateTime.UtcNow - _lastTranscriptActivityUtc).TotalSeconds < minQuietSecs)
                {
                    // User started speaking during the background handshake -> keep current socket untouched!
                    fresh.Dispose();
                    return;
                }

                var old = Interlocked.Exchange(ref _activeConn, fresh);
                _lastSendSuccessUtc = DateTime.UtcNow;

                lock (_turnPrefixLock)
                {
                    _rawServerTurnInterim = string.Empty;
                    _consumedServerTurnPrefix = string.Empty;
                }

                if (old != null)
                {
                    old.IsClosing = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(500);
                        }
                        catch { }
                        old.Dispose();
                    });
                }
            }
            catch
            {
                // Ignore background pre-warm failure; active socket remains untouched
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        finally
        {
            _hotSwapping = false;
        }
    }

    public async Task TriggerReconnectAsync(string reason = "Reconectando...")
    {
        if (!_streamActive || _reconnecting)
            return;

        _reconnecting = true;
        try
        {
            FlushRemainingInterimIfNeeded();
            int delayMs = 150;
            while (_streamActive)
            {
                await _lifecycleLock.WaitAsync();
                try
                {
                    if (!_streamActive)
                        return;

                    // If another path already restored a healthy connection, exit
                    if (_activeConn is { SetupComplete: true } c && c.Socket.State == WebSocketState.Open &&
                        (DateTime.UtcNow - _lastSendSuccessUtc).TotalSeconds < 4)
                    {
                        return;
                    }

                    var fresh = await CreateAndInitializeConnectionAsync();
                    var old = Interlocked.Exchange(ref _activeConn, fresh);
                    old?.Dispose();

                    _lastSendSuccessUtc = DateTime.UtcNow;
                    OnStatusChanged?.Invoke("OUVINDO AO VIVO • Baixa latência contínua (gemini-3.5-transcribe-live)");
                    _ = FlushQueuedAudioAsync();
                    return;
                }
                catch (Exception ex)
                {
                    if (!_streamActive)
                        return;
                    OnStatusChanged?.Invoke($"Reconectando automaticamente... ({ex.Message})");
                }
                finally
                {
                    _lifecycleLock.Release();
                }

                await Task.Delay(delayMs);
                delayMs = Math.Min(2500, (int)(delayMs * 1.6));
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private async Task FlushQueuedAudioAsync()
    {
        var conn = _activeConn;
        if (conn == null || !conn.SetupComplete || conn.Socket.State != WebSocketState.Open || conn.IsClosing)
            return;

        if (!await _sendLock.WaitAsync(0))
            return;

        try
        {
            while (!_pcmQueue.IsEmpty)
            {
                conn = _activeConn;
                if (conn == null || !conn.SetupComplete || conn.Socket.State != WebSocketState.Open || conn.Cts.IsCancellationRequested)
                    break;

                // Coalesce all currently queued PCM chunks into a SINGLE WebSocket frame!
                // This prevents any send-queue lag from ever building up over time.
                using var batchStream = new MemoryStream(2560);
                int framesMerged = 0;
                while (framesMerged < 12 && _pcmQueue.TryDequeue(out byte[]? frame))
                {
                    batchStream.Write(frame, 0, frame.Length);
                    framesMerged++;
                }

                if (batchStream.Length == 0)
                    break;

                string b64 = Convert.ToBase64String(batchStream.GetBuffer(), 0, (int)batchStream.Length);
                string json = "{\"realtimeInput\":{\"audio\":{\"mimeType\":\"audio/pcm;rate=16000\",\"data\":\"" + b64 + "\"}}}";
                byte[] utf8 = Encoding.UTF8.GetBytes(json);

                await conn.Socket.SendAsync(new ArraySegment<byte>(utf8), WebSocketMessageType.Text, true, conn.Cts.Token)
                    .WaitAsync(TimeSpan.FromMilliseconds(2200), conn.Cts.Token);
                _lastSendSuccessUtc = DateTime.UtcNow;
            }
        }
        catch
        {
            if (_streamActive && !_hotSwapping)
            {
                _ = TriggerReconnectAsync("Recuperando transporte de áudio");
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task StopAndFlushAsync()
    {
        _streamActive = false;
        var conn = Interlocked.Exchange(ref _activeConn, null);

        if (conn == null || conn.Socket.State != WebSocketState.Open)
        {
            FlushRemainingInterimIfNeeded();
            conn?.Dispose();
            return;
        }

        try
        {
            await _sendLock.WaitAsync(TimeSpan.FromSeconds(1.5));
            try
            {
                using var batchStream = new MemoryStream();
                while (_pcmQueue.TryDequeue(out byte[]? frame))
                {
                    batchStream.Write(frame, 0, frame.Length);
                }

                if (batchStream.Length > 0)
                {
                    string b64 = Convert.ToBase64String(batchStream.GetBuffer(), 0, (int)batchStream.Length);
                    string json = "{\"realtimeInput\":{\"audio\":{\"mimeType\":\"audio/pcm;rate=16000\",\"data\":\"" + b64 + "\"}}}";
                    byte[] utf8 = Encoding.UTF8.GetBytes(json);
                    await conn.Socket.SendAsync(new ArraySegment<byte>(utf8), WebSocketMessageType.Text, true, conn.Cts.Token);
                }

                byte[] endBytes = Encoding.UTF8.GetBytes("{\"realtimeInput\":{\"audioStreamEnd\":true}}");
                await conn.Socket.SendAsync(new ArraySegment<byte>(endBytes), WebSocketMessageType.Text, true, conn.Cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            await Task.WhenAny(conn.DrainTcs.Task, Task.Delay(750));
        }
        catch
        {
        }
        finally
        {
            FlushRemainingInterimIfNeeded();
            conn.Dispose();
        }
    }

    private void FlushRemainingInterimIfNeeded()
    {
        string leftover = Interlocked.Exchange(ref _lastInterim, string.Empty).Trim();
        if (!string.IsNullOrEmpty(leftover))
        {
            OnFinalTranscript?.Invoke(leftover);
        }
    }

    private async Task ReceiveLoopAsync(LiveConnection conn)
    {
        byte[] buffer = new byte[32768];
        using var ms = new MemoryStream();
        bool shouldReconnect = false;

        try
        {
            while (conn.Socket.State == WebSocketState.Open && !conn.Cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), conn.Cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        conn.DrainTcs.TrySetResult(true);
                        shouldReconnect = _streamActive && !conn.IsClosing;
                        break;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (shouldReconnect || result.MessageType == WebSocketMessageType.Close)
                    break;

                string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                if (ProcessServerMessage(conn, json))
                {
                    shouldReconnect = _streamActive && !conn.IsClosing;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            shouldReconnect = _streamActive && !conn.IsClosing;
        }
        finally
        {
            conn.DrainTcs.TrySetResult(true);
            if (shouldReconnect && _streamActive && ReferenceEquals(_activeConn, conn))
            {
                _ = TriggerReconnectAsync("Sessão renovada automaticamente");
            }
        }
    }

    /// <returns>true if server requested goAway / rotation</returns>
    private bool ProcessServerMessage(LiveConnection conn, string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement errElem))
            {
                string msg = errElem.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "Erro na API" : "Erro na API";
                OnError?.Invoke(msg);
                conn.SetupTcs.TrySetException(new InvalidOperationException(msg));
                return true;
            }

            if (root.TryGetProperty("setupComplete", out _))
            {
                conn.SetupComplete = true;
                conn.SetupTcs.TrySetResult(true);
            }

            if (root.TryGetProperty("serverContent", out JsonElement content) && content.ValueKind == JsonValueKind.Object)
            {
                if (content.TryGetProperty("interimInputTranscription", out JsonElement interimObj) &&
                    interimObj.ValueKind == JsonValueKind.Object &&
                    interimObj.TryGetProperty("text", out JsonElement interimTextElem))
                {
                    string rawInterim = (interimTextElem.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(rawInterim))
                    {
                        conn.HasTranscribedInSession = true;
                        _lastTranscriptActivityUtc = DateTime.UtcNow;

                        string strippedInterim = StripConsumedTurnPrefix(rawInterim, isFinalFromServer: false);
                        if (!string.IsNullOrEmpty(strippedInterim))
                        {
                            _lastInterim = strippedInterim;
                            OnInterimTranscript?.Invoke(strippedInterim);
                        }
                    }
                }

                if (content.TryGetProperty("inputTranscription", out JsonElement finalObj) &&
                    finalObj.ValueKind == JsonValueKind.Object &&
                    finalObj.TryGetProperty("text", out JsonElement finalTextElem))
                {
                    string rawFinal = (finalTextElem.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(rawFinal))
                    {
                        conn.HasTranscribedInSession = true;
                        _lastTranscriptActivityUtc = DateTime.UtcNow;

                        string strippedFinal = StripConsumedTurnPrefix(rawFinal, isFinalFromServer: true);
                        _lastInterim = string.Empty;
                        if (!string.IsNullOrEmpty(strippedFinal))
                        {
                            OnFinalTranscript?.Invoke(strippedFinal);
                        }
                    }
                }

                bool turnDone =
                    (content.TryGetProperty("turnComplete", out JsonElement tc) && tc.ValueKind == JsonValueKind.True) ||
                    (content.TryGetProperty("generationComplete", out JsonElement gc) && gc.ValueKind == JsonValueKind.True);

                if (turnDone)
                {
                    lock (_turnPrefixLock)
                    {
                        _rawServerTurnInterim = string.Empty;
                        _consumedServerTurnPrefix = string.Empty;
                    }

                    if (!_streamActive)
                    {
                        FlushRemainingInterimIfNeeded();
                        conn.DrainTcs.TrySetResult(true);
                    }
                }
            }

            if (root.TryGetProperty("goAway", out _))
            {
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// Strips any prefix of the server's cumulative audio turn that was already sealed, applied, or cleared locally,
    /// eliminating any speech residue when the user resumes speaking after a pause or auto-clear.
    /// </summary>
    private string StripConsumedTurnPrefix(string rawServerText, bool isFinalFromServer)
    {
        lock (_turnPrefixLock)
        {
            if (!isFinalFromServer)
            {
                _rawServerTurnInterim = rawServerText;
            }

            if (string.IsNullOrWhiteSpace(_consumedServerTurnPrefix))
            {
                if (isFinalFromServer)
                {
                    _rawServerTurnInterim = string.Empty;
                }
                return rawServerText;
            }

            MatchCollection consumedWords = Regex.Matches(_consumedServerTurnPrefix, @"[A-Za-zÀ-ÿ0-9]+");
            MatchCollection rawWords = Regex.Matches(rawServerText, @"[A-Za-zÀ-ÿ0-9]+");

            if (consumedWords.Count == 0 || rawWords.Count == 0)
            {
                if (isFinalFromServer)
                {
                    _rawServerTurnInterim = string.Empty;
                    _consumedServerTurnPrefix = string.Empty;
                }
                return rawServerText;
            }

            // 1. If the server emitted a shorter phrase than our consumed prefix (< k - 1 words when k >= 3),
            // the server has started a brand-new turn on its own!
            if (consumedWords.Count >= 3 && rawWords.Count < consumedWords.Count - 1)
            {
                _consumedServerTurnPrefix = string.Empty;
                if (isFinalFromServer)
                {
                    _rawServerTurnInterim = string.Empty;
                }
                return rawServerText;
            }

            // 2. Check word-level match between the start of rawServerText and _consumedServerTurnPrefix
            int compareLen = Math.Min(consumedWords.Count, rawWords.Count);
            int matches = 0;
            for (int i = 0; i < compareLen; i++)
            {
                if (string.Equals(consumedWords[i].Value, rawWords[i].Value, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                }
            }

            double matchRatio = (double)matches / compareLen;
            int cutAfterWordIdx = -1;

            if (matchRatio >= 0.65)
            {
                cutAfterWordIdx = compareLen - 1;
            }
            else if (consumedWords.Count >= 2)
            {
                // Tail-anchor fallback: find the last 2 words of _consumedServerTurnPrefix near the expected index
                string lastW1 = consumedWords[^2].Value;
                string lastW2 = consumedWords[^1].Value;
                int searchMax = Math.Min(rawWords.Count - 1, consumedWords.Count + 2);
                for (int j = Math.Max(1, consumedWords.Count - 3); j <= searchMax; j++)
                {
                    if (string.Equals(rawWords[j - 1].Value, lastW1, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(rawWords[j].Value, lastW2, StringComparison.OrdinalIgnoreCase))
                    {
                        cutAfterWordIdx = j;
                        break;
                    }
                }
            }

            if (cutAfterWordIdx < 0)
            {
                // Did not match the consumed prefix -> server started a brand-new turn!
                _consumedServerTurnPrefix = string.Empty;
                if (isFinalFromServer)
                {
                    _rawServerTurnInterim = string.Empty;
                }
                return rawServerText;
            }

            if (isFinalFromServer)
            {
                _rawServerTurnInterim = string.Empty;
                _consumedServerTurnPrefix = string.Empty;
            }

            // If all words in rawServerText belong to the already-consumed prefix, return empty!
            if (cutAfterWordIdx >= rawWords.Count - 1)
            {
                return string.Empty;
            }

            Match lastConsumedMatch = rawWords[cutAfterWordIdx];
            int slicePos = lastConsumedMatch.Index + lastConsumedMatch.Length;

            // Skip any trailing punctuation or whitespace right after the last consumed word
            while (slicePos < rawServerText.Length &&
                   (char.IsWhiteSpace(rawServerText[slicePos]) ||
                    rawServerText[slicePos] is '.' or ',' or ';' or ':' or '!' or '?' or '-'))
            {
                slicePos++;
            }

            return slicePos < rawServerText.Length ? rawServerText[slicePos..].Trim() : string.Empty;
        }
    }

    public ValueTask DisposeAsync()
    {
        _streamActive = false;
        var conn = Interlocked.Exchange(ref _activeConn, null);
        conn?.Dispose();
        return ValueTask.CompletedTask;
    }
}
