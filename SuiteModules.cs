using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VozWindows;

// ============================================================================
// 1. MODO CURSORLOOP & TOUCHPAD (Motor embutido na barra única, sem outra janela)
// ============================================================================

public sealed class CursorLoopSettings
{
    public bool Enabled { get; set; } = false;
    public int SpeedLevel { get; set; } = 0; // 0 = 1.0x (OFF), 1 = 1.8x, 2 = 2.8x, 3 = 4.5x
    public bool RealtimeRewriteEnabled { get; set; } = false; // false = Modo Nativo Puro (sem correção/sem 6 opções)
    public bool HeadTrackerEnabled { get; set; } = false; // Flag: Move mouse to center of 2nd monitor when face turns right
    public double Level1Multiplier { get; set; } = 1.8;
    public double Level2Multiplier { get; set; } = 2.8;
    public double Level3Multiplier { get; set; } = 4.5;
    public double CustomMultiplier { get; set; } = 1.8;
    public bool UseCustomMultiplier { get; set; } = false;
    public bool SmartPrecision { get; set; } = true;
    public bool HorizontalLoop { get; set; } = true;
    public bool VerticalLoop { get; set; } = true;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorLoop", "settings.json");

    public static bool IsCursorLoopSupported
    {
        get
        {
#if PORTABLE_VOICE_ONLY
            return false;
#else
            return IsOwnerWorkspaceMachine();
#endif
        }
    }

    private static bool IsOwnerWorkspaceMachine()
    {
        string? exe = Environment.ProcessPath;
        return !string.IsNullOrEmpty(exe) && exe.Contains("PROJETOS", StringComparison.OrdinalIgnoreCase);
    }

    public double GetEffectiveMultiplier()
    {
        if (UseCustomMultiplier)
            return Math.Clamp(CustomMultiplier, 1.0, 10.0);

        return SpeedLevel switch
        {
            1 => Level1Multiplier,
            2 => Level2Multiplier,
            3 => Level3Multiplier,
            _ => 1.0
        };
    }

    public string GetLoopPillText()
    {
        return Enabled ? "🟢 Loop ON" : "⚪ Loop OFF";
    }

    public string GetSpeedPillText()
    {
        double mult = GetEffectiveMultiplier();
        if (SpeedLevel <= 0 && !UseCustomMultiplier)
            return "⚪ 1.0x";
        return $"⚡ {mult.ToString("0.0", CultureInfo.InvariantCulture)}x";
    }

    public string GetRewriteModePillText()
    {
        return RealtimeRewriteEnabled ? "✨ 6 Opções: ON" : "⚡ Só Nativo";
    }

    public string GetHeadTrackerPillText()
    {
        return HeadTrackerEnabled ? "👀 Head ON" : "👀 Head OFF";
    }

    public string GetModePillText()
    {
        return $"{GetLoopPillText()} • {GetSpeedPillText()}";
    }

    /// <summary>
    /// Alterna o modo Head Motion Detection (move o mouse para o centro do 2º monitor ao virar o rosto para a direita).
    /// </summary>
    public void ToggleHeadTrackerOnBar()
    {
        HeadTrackerEnabled = !HeadTrackerEnabled;
        Save();
    }

    /// <summary>
    /// Alterna entre o Modo Nativo Puro (sem reescrita / sem 6 opções) e o Modo com 6 Opções de Escrita.
    /// </summary>
    public void ToggleRealtimeRewriteOnBar()
    {
        RealtimeRewriteEnabled = !RealtimeRewriteEnabled;
        Save();
    }

    /// <summary>
    /// Alterna exclusivamente a estrutura de repetição de borda (Loop ON/OFF), sem alterar a rotina de velocidade.
    /// </summary>
    public void ToggleLoopOnBar()
    {
        Enabled = !Enabled;
        Save();
    }

    /// <summary>
    /// Alterna exclusivamente a rotina de velocidade (1.8x -> 2.8x -> 4.5x -> 1.0x), sem alterar o estado do Loop.
    /// </summary>
    public void CycleSpeedOnBar()
    {
        UseCustomMultiplier = false;
        SpeedLevel = SpeedLevel switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            _ => 0
        };
        Save();
    }

    public void CycleModeOnBar()
    {
        ToggleLoopOnBar();
    }

    /// <summary>
    /// Define diretamente o nível de velocidade (0 = 1.0x, 1 = 1.8x, 2 = 2.8x, 3 = 4.5x) de forma independente do Loop.
    /// </summary>
    public void SetLevelDirect(int level)
    {
        UseCustomMultiplier = false;
        SpeedLevel = Math.Clamp(level, 0, 3);
        Save();
    }

    public static CursorLoopSettings Load()
    {
        CursorLoopSettings? loaded = null;
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                loaded = JsonSerializer.Deserialize<CursorLoopSettings>(json);
            }
        }
        catch { }

        // Em execucao portatil (computadores de terceiros), sempre inicia com o CursorLoop DESATIVADO
        // para nunca interferir no mouse fisico nem teleportar o cursor na borda da tela.
        if (!IsOwnerWorkspaceMachine())
        {
            return new CursorLoopSettings
            {
                Enabled = false,
                SpeedLevel = 0,
                RealtimeRewriteEnabled = loaded?.RealtimeRewriteEnabled ?? false,
                HorizontalLoop = false,
                VerticalLoop = false
            };
        }

        if (loaded is not null)
        {
            // Sempre iniciar com o teletransporte de borda (Loop ON) ativo ao abrir o aplicativo na maquina do dono
            loaded.Enabled = true;
            loaded.HorizontalLoop = true;
            loaded.VerticalLoop = true;
            loaded.Save();
            return loaded;
        }
        var defaultSettings = new CursorLoopSettings
        {
            Enabled = true,
            SpeedLevel = 1,
            RealtimeRewriteEnabled = false,
            HorizontalLoop = true,
            VerticalLoop = true
        };
        defaultSettings.Save();
        return defaultSettings;
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VozWindowsSuite";

    public static void EnsureUnifiedStartup(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true);
            if (key is null) return;
            key.DeleteValue("CursorLoop", false);
            key.DeleteValue(RunValueName, false);
            key.DeleteValue("VozWindows", false);
        }
        catch { }
    }
}

public sealed class CursorWrapEngine
{
    private Screen[] _screens = Screen.AllScreens;
    private long _lastRefresh;
    private long _lastWarp;
    private long _lastMoveTick;
    private Point _previous;
    private int _lastDx;
    private int _lastDy;
    private double _remX;
    private double _remY;
    private bool _hasPrevious;

    public double SpeedMultiplier { get; set; } = 1.8;
    public bool SmartPrecision { get; set; } = true;
    public long WrapCount { get; private set; }

    public void Refresh()
    {
        _screens = Screen.AllScreens;
        _lastRefresh = Environment.TickCount64;
    }

    public void NotifyExternalWarp(Point newPoint)
    {
        _previous = newPoint;
        _hasPrevious = true;
        _lastDx = 0;
        _lastDy = 0;
        _remX = 0;
        _remY = 0;
        _lastWarp = Environment.TickCount64;
    }

    public void Tick(bool loopEnabled, bool horizontal, bool vertical)
    {
        double multiplier = SpeedMultiplier;
        bool speedActive = multiplier > 1.001;

        if (!loopEnabled && !speedActive)
        {
            _hasPrevious = false;
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastRefresh >= 1000) Refresh();
        if (_screens.Length == 0) return;

        Point point = Cursor.Position;
        if (!_hasPrevious)
        {
            _previous = point;
            _hasPrevious = true;
            return;
        }

        Point previous = _previous;
        int dx = point.X - previous.X;
        int dy = point.Y - previous.Y;

        if (dx == 0 && dy == 0)
        {
            if (now - _lastMoveTick > 60)
            {
                _remX = 0;
                _remY = 0;
            }
            return;
        }

        _lastMoveTick = now;

        // Ignora saltos externos (ex: teletransporte do HeadMonitorTracker entre monitores) para não aplicar boost
        if (Math.Abs(dx) > 240 || Math.Abs(dy) > 240)
        {
            _previous = point;
            _remX = 0;
            _remY = 0;
            return;
        }

        if (speedActive && now - _lastWarp >= 40)
        {
            double effectiveMult = multiplier;
            if (SmartPrecision)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist <= 1.5)
                    effectiveMult = 1.0 + (multiplier - 1.0) * 0.35;
                else if (dist < 3.5)
                    effectiveMult = 1.0 + (multiplier - 1.0) * 0.70;
            }

            double extraX = dx * (effectiveMult - 1.0) + _remX;
            double extraY = dy * (effectiveMult - 1.0) + _remY;
            int boostX = (int)Math.Truncate(extraX);
            int boostY = (int)Math.Truncate(extraY);
            _remX = extraX - boostX;
            _remY = extraY - boostY;

            if (boostX != 0 || boostY != 0)
            {
                int targetX = point.X + boostX;
                int targetY = point.Y + boostY;

                if (loopEnabled)
                {
                    Screen? sourceScreen = _screens.FirstOrDefault(s => s.Bounds.Contains(point))
                                        ?? _screens.FirstOrDefault(s => s.Bounds.Contains(previous));
                    if (sourceScreen is not null)
                    {
                        if (horizontal)
                            targetX = Math.Clamp(targetX, sourceScreen.Bounds.Left, sourceScreen.Bounds.Right - 1);
                        if (vertical)
                            targetY = Math.Clamp(targetY, sourceScreen.Bounds.Top, sourceScreen.Bounds.Bottom - 1);
                    }
                }

                Point boosted = new(targetX, targetY);
                if (boosted != point)
                {
                    Cursor.Position = boosted;
                    point = Cursor.Position;
                    dx = point.X - previous.X;
                    dy = point.Y - previous.Y;
                }
            }
        }
        else
        {
            _remX = 0;
            _remY = 0;
        }

        if (dx != 0) _lastDx = dx;
        if (dy != 0) _lastDy = dy;
        _previous = point;

        if (!loopEnabled || now - _lastWarp < 110)
            return;

        Screen? current = _screens.FirstOrDefault(s => s.Bounds.Contains(point));
        if (current is null) return;

        Rectangle bounds = current.Bounds;
        var candidates = new List<Direction>(4);
        if (horizontal)
        {
            if (point.X <= bounds.Left && _lastDx < 0) candidates.Add(Direction.Left);
            if (point.X >= bounds.Right - 1 && _lastDx > 0) candidates.Add(Direction.Right);
        }
        if (vertical)
        {
            if (point.Y <= bounds.Top && _lastDy < 0) candidates.Add(Direction.Up);
            if (point.Y >= bounds.Bottom - 1 && _lastDy > 0) candidates.Add(Direction.Down);
        }
        if (candidates.Count == 0) return;

        Direction direction = candidates.Count == 1 ? candidates[0] :
            candidates.OrderByDescending(d => d is Direction.Left or Direction.Right
                ? Math.Abs(point.X - previous.X)
                : Math.Abs(point.Y - previous.Y)).First();

        Screen destination = ChooseDestination(current, direction, point);
        Point newPoint = MapPoint(bounds, destination.Bounds, direction, point);
        if (newPoint == point) return;

        Cursor.Position = newPoint;
        _previous = newPoint;
        _lastDx = 0;
        _lastDy = 0;
        _remX = 0;
        _remY = 0;
        _lastWarp = now;
        WrapCount++;
    }

    private Screen ChooseDestination(Screen current, Direction direction, Point point)
    {
        Rectangle source = current.Bounds;
        Screen[] sameBand = _screens.Where(s => direction is Direction.Left or Direction.Right
            ? s.Bounds.Top <= point.Y && point.Y < s.Bounds.Bottom
            : s.Bounds.Left <= point.X && point.X < s.Bounds.Right).ToArray();

        var next = sameBand
            .Where(s => !ReferenceEquals(s, current))
            .Select(s => (screen: s, distance: ForwardDistance(source, s.Bounds, direction)))
            .Where(x => x.distance >= 0)
            .OrderBy(x => x.distance)
            .FirstOrDefault();

        if (next.screen is not null)
            return next.screen;

        if (_screens.Length > 1)
        {
            Screen[] ordered = _screens.OrderBy(s => s.Bounds.Top).ThenBy(s => s.Bounds.Left).ToArray();
            int index = Array.FindIndex(ordered, s => ReferenceEquals(s, current));
            int step = direction is Direction.Right or Direction.Down ? 1 : -1;
            return ordered[(index + step + ordered.Length) % ordered.Length];
        }

        return current;
    }

    private static int ForwardDistance(Rectangle source, Rectangle other, Direction direction) => direction switch
    {
        Direction.Right when other.Left >= source.Right => other.Left - source.Right,
        Direction.Left when other.Right <= source.Left => source.Left - other.Right,
        Direction.Down when other.Top >= source.Bottom => other.Top - source.Bottom,
        Direction.Up when other.Bottom <= source.Top => source.Top - other.Bottom,
        _ => -1
    };

    private static Point MapPoint(Rectangle source, Rectangle target, Direction direction, Point point)
    {
        const int inset = 12;
        if (direction is Direction.Left or Direction.Right)
        {
            double fraction = source.Height <= 1 ? 0.5 : (double)(point.Y - source.Top) / (source.Height - 1);
            int y = target.Top + (int)Math.Round(fraction * (target.Height - 1));
            int x = direction == Direction.Right
                ? target.Left + Math.Min(inset, target.Width - 1)
                : target.Right - 1 - Math.Min(inset, target.Width - 1);
            return new Point(x, y);
        }
        else
        {
            double fraction = source.Width <= 1 ? 0.5 : (double)(point.X - source.Left) / (source.Width - 1);
            int x = target.Left + (int)Math.Round(fraction * (target.Width - 1));
            int y = direction == Direction.Down
                ? target.Top + Math.Min(inset, target.Height - 1)
                : target.Bottom - 1 - Math.Min(inset, target.Height - 1);
            return new Point(x, y);
        }
    }

    private enum Direction { Left, Right, Up, Down }
}

// ============================================================================
// 2. AUTO-UPDATE PADRONIZADO VIA GITHUB RELEASES (OTA 1-CLIQUE)
// ============================================================================

public sealed record UpdateReleaseInfo(
    string TagName,
    Version Version,
    string DownloadUrl,
    long SizeBytes,
    string? Notes = null,
    int PendingCount = 1);

public static class AutoUpdateService
{
    public static readonly Version CurrentVersion = GetAssemblyThreePartVersion();
    public static readonly string CurrentVersionString = $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";
    public const string GitHubRepo = "douglasescrito/voz-windows";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/douglasescrito/voz-windows/releases/latest";
    public const string ReleasesListApiUrl = "https://api.github.com/repos/douglasescrito/voz-windows/releases?per_page=30";

    private static Version GetAssemblyThreePartVersion()
    {
        var v = typeof(AutoUpdateService).Assembly.GetName().Version;
        if (v == null)
            return new Version(1, 3, 0);
        return new Version(Math.Max(1, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }

    private static readonly HttpClient UpdateHttp = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"VozWindows-AutoUpdater/{CurrentVersionString}");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Remove qualquer executável .old deixado pela troca atômica da atualização anterior.
    /// </summary>
    public static void CleanupOldExecutableOnStartup()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            string oldPath = exePath + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            string partialUpdate = exePath + ".update";
            if (File.Exists(partialUpdate))
            {
                File.Delete(partialUpdate);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Consulta a lista de Releases publicadas no GitHub (douglasescrito/voz-windows),
    /// conta quantas versões acima de CurrentVersion estão acumuladas e retorna o executável mais recente
    /// junto com o resumo acumulado de todas as versões pendentes.
    /// </summary>
    public static async Task<UpdateReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesListApiUrl);
            using var resp = await UpdateHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var newerReleases = new System.Collections.Generic.List<(string Tag, Version Ver, string Url, long Size, string? Body)>();

            foreach (JsonElement rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True)
                    continue;

                if (!rel.TryGetProperty("tag_name", out JsonElement tagElem))
                    continue;

                string tagName = (tagElem.GetString() ?? string.Empty).Trim();
                string cleanVer = tagName.TrimStart('v', 'V').Split('-')[0].Trim();
                if (!Version.TryParse(cleanVer, out Version? remoteVersion) || remoteVersion == null)
                    continue;

                if (remoteVersion <= CurrentVersion)
                    continue;

                if (!rel.TryGetProperty("assets", out JsonElement assetsElem) || assetsElem.ValueKind != JsonValueKind.Array)
                    continue;

                string? exeUrl = null;
                long exeSize = 0;
                foreach (JsonElement asset in assetsElem.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement n) ? (n.GetString() ?? string.Empty) : string.Empty;
                    string url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? (u.GetString() ?? string.Empty) : string.Empty;
                    long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long sz) ? sz : 0;

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    {
                        exeUrl = url;
                        exeSize = size;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(exeUrl))
                {
                    string? body = rel.TryGetProperty("body", out JsonElement bodyElem) ? bodyElem.GetString() : null;
                    newerReleases.Add((tagName, remoteVersion, exeUrl, exeSize, body));
                }
            }

            if (newerReleases.Count == 0)
                return null;

            newerReleases.Sort((a, b) => b.Ver.CompareTo(a.Ver));
            var latest = newerReleases[0];
            int pendingCount = newerReleases.Count;

            string combinedNotes;
            if (pendingCount == 1)
            {
                combinedNotes = latest.Body ?? string.Empty;
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in newerReleases)
                {
                    if (sb.Length > 0)
                        sb.AppendLine().AppendLine();
                    sb.AppendLine($"[{item.Tag}]");
                    sb.Append(string.IsNullOrWhiteSpace(item.Body) ? "• Melhorias e correções." : item.Body.Trim());
                }
                combinedNotes = sb.ToString();
            }

            return new UpdateReleaseInfo(latest.Tag, latest.Ver, latest.Url, latest.Size, combinedNotes, pendingCount);
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Baixa o novo VozWindows.exe, valida o cabeçalho PE ('MZ'), faz a troca atômica do próprio executável em disco
    /// e inicia a nova versão imediatamente.
    /// </summary>
    public static async Task<(bool Ok, string Message)> DownloadAndApplyUpdateAsync(
        UpdateReleaseInfo release,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return (false, "Caminho do executável atual não localizado.");

        string tempUpdatePath = currentExe + ".update";
        string backupOldPath = currentExe + ".old";

        try
        {
            if (File.Exists(tempUpdatePath))
                File.Delete(tempUpdatePath);

            onProgress?.Invoke($"⏳ Baixando {release.TagName} (0%)...");

            using (var resp = await UpdateHttp.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return (false, $"Erro HTTP {(int)resp.StatusCode} ao baixar atualização.");

                long totalBytes = resp.Content.Headers.ContentLength ?? release.SizeBytes;
                await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempUpdatePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int lastPercent = -1;
                int read;

                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    if (totalBytes > 0)
                    {
                        int pct = (int)Math.Clamp((downloaded * 100L) / totalBytes, 0, 100);
                        if (pct != lastPercent && (pct % 5 == 0 || pct == 100))
                        {
                            lastPercent = pct;
                            onProgress?.Invoke($"⏳ Baixando {release.TagName} ({pct}%)...");
                        }
                    }
                }
            }

            // Verifica integridade básica do executável Windows PE ('MZ')
            var info = new FileInfo(tempUpdatePath);
            if (!info.Exists || info.Length < 100_000)
                return (false, "Arquivo baixado incompleto.");

            byte[] header = new byte[2];
            await using (var checkFs = new FileStream(tempUpdatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (await checkFs.ReadAsync(header, ct) < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    return (false, "Arquivo baixado não é um executável Windows válido.");
            }

            onProgress?.Invoke($"✨ Aplicando {release.TagName} e reiniciando...");

            // Troca atômica nativa do Windows: renomeia o .exe em execução para .old e move o .update para o nome original
            if (File.Exists(backupOldPath))
            {
                try { File.Delete(backupOldPath); } catch { }
            }

            File.Move(currentExe, backupOldPath, overwrite: true);
            File.Move(tempUpdatePath, currentExe, overwrite: true);

            // Inicia a nova versão
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(currentExe) ?? Environment.CurrentDirectory
            });

            return (true, $"Atualizado para {release.TagName}!");
        }
        catch (Exception ex)
        {
            // Se falhou após renomear para .old, restaura o executável original
            try
            {
                if (!File.Exists(currentExe) && File.Exists(backupOldPath))
                {
                    File.Move(backupOldPath, currentExe, overwrite: true);
                }
            }
            catch { }

            return (false, $"Falha na atualização: {ex.Message}");
        }
    }
}

// ============================================================================
// 3. HEAD MOTION MONITOR SWITCHER (Mouse p/ Centro do 2º Monitor via Rosto)
// ============================================================================

/// <summary>
/// Motor nativo em tempo real (Windows.Media.FaceAnalysis + MediaFrameReader) que detecta a rotação
/// horizontal do rosto (Yaw para a direita) e move o cursor automaticamente para o centro do segundo monitor
/// quando o usuário vira o rosto para a direita, retornando ao centro do monitor principal quando volta a olhar reto.
/// Opera somente na transição de estado (com histerese), nunca brigando com o mouse físico.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public sealed class HeadMonitorTracker : IDisposable
{
    private Windows.Media.Capture.MediaCapture? _mediaCapture;
    private Windows.Media.Capture.Frames.MediaFrameReader? _frameReader;
    private Windows.Media.FaceAnalysis.FaceDetector? _faceDetector;

    private volatile bool _isRunning;
    private int _isProcessingFrame;
    private long _lastFrameTick;

    // Auto-calibração neutra (olhando para o monitor principal ao ativar)
    private int _calibrationSamples;
    private double _calibSumX;
    private double _calibSumU;
    private double _calibSumAsym;
    private double _baselineX = 0.50;
    private double _baselineU = 0.50;
    private double _baselineAsym = 0.0;
    private bool _isCalibrated;

    // Filtro EMA + Histerese de transição entre Monitor 1 (Reto) e Monitor 2 (Rosto p/ Direita)
    private double _smoothedYawRight;
    private bool _isLookingRight;
    private int _consecutiveRightFrames;
    private int _consecutiveStraightFrames;
    private long _lastWarpTick;
    private byte[]? _pixelBuffer;

    // Limiares com histerese (evita oscilação na fronteira entre monitores)
    private const double EnterRightMonitorThreshold = 0.135;
    private const double ReturnPrimaryMonitorThreshold = 0.055;
    private const int ConfirmFramesRequired = 2;

    public bool IsRunning => _isRunning;
    public bool IsLookingRight => _isLookingRight;
    public double CurrentYawScore => _smoothedYawRight;
    public CursorWrapEngine? WrapEngine { get; set; }

    public event Action<string, bool, double>? OnStateUpdated;

    public async Task StartAsync()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        ResetCalibration();

        try
        {
            if (!Windows.Media.FaceAnalysis.FaceDetector.IsSupported)
            {
                _isRunning = false;
                OnStateUpdated?.Invoke("HEAD TRACKER: FaceDetector não suportado neste Windows", false, 0);
                return;
            }

            _faceDetector = await Windows.Media.FaceAnalysis.FaceDetector.CreateAsync();

            var sourceGroups = await Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync();
            Windows.Media.Capture.Frames.MediaFrameSourceGroup? selectedGroup = null;
            Windows.Media.Capture.Frames.MediaFrameSourceInfo? selectedSourceInfo = null;

            foreach (var group in sourceGroups)
            {
                foreach (var si in group.SourceInfos)
                {
                    if (si.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color &&
                        (si.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview ||
                         si.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoRecord))
                    {
                        selectedGroup = group;
                        selectedSourceInfo = si;
                        break;
                    }
                }
                if (selectedGroup != null)
                    break;
            }

            if (selectedGroup == null || selectedSourceInfo == null)
            {
                _isRunning = false;
                OnStateUpdated?.Invoke("HEAD TRACKER: Nenhuma webcam encontrada", false, 0);
                return;
            }

            _mediaCapture = new Windows.Media.Capture.MediaCapture();
            var settings = new Windows.Media.Capture.MediaCaptureInitializationSettings
            {
                SourceGroup = selectedGroup,
                SharingMode = Windows.Media.Capture.MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = Windows.Media.Capture.MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Video
            };

            try
            {
                await _mediaCapture.InitializeAsync(settings);
            }
            catch
            {
                // Fallback para SharedReadOnly caso outro app já esteja usando a webcam
                _mediaCapture.Dispose();
                _mediaCapture = new Windows.Media.Capture.MediaCapture();
                settings.SharingMode = Windows.Media.Capture.MediaCaptureSharingMode.SharedReadOnly;
                await _mediaCapture.InitializeAsync(settings);
            }

            if (!_mediaCapture.FrameSources.TryGetValue(selectedSourceInfo.Id, out var frameSource))
            {
                frameSource = _mediaCapture.FrameSources.Values.FirstOrDefault(
                    s => s.Info.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color);
            }

            if (frameSource == null)
            {
                await StopAsync();
                OnStateUpdated?.Invoke("HEAD TRACKER: Fonte de vídeo indisponível", false, 0);
                return;
            }

            // Seleciona resolução leve (ex: 320x240 ou 640x360/480) para mínima latência (~2ms) e <1% CPU
            try
            {
                var preferredFormat = frameSource.SupportedFormats
                    .Where(f => f.VideoFormat.Width >= 320 && f.VideoFormat.Width <= 640 && f.FrameRate.Numerator >= 15)
                    .OrderBy(f => f.VideoFormat.Width * f.VideoFormat.Height)
                    .FirstOrDefault();

                if (preferredFormat != null)
                {
                    await frameSource.SetFormatAsync(preferredFormat);
                }
            }
            catch { }

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(frameSource);
            _frameReader.AcquisitionMode = Windows.Media.Capture.Frames.MediaFrameReaderAcquisitionMode.Realtime;
            _frameReader.FrameArrived += OnFrameArrived;

            var status = await _frameReader.StartAsync();
            if (status != Windows.Media.Capture.Frames.MediaFrameReaderStartStatus.Success)
            {
                await StopAsync();
                OnStateUpdated?.Invoke($"HEAD TRACKER: Câmera ocupada ({status})", false, 0);
                return;
            }

            OnStateUpdated?.Invoke("👀 HEAD TRACKER ATIVO • Calibrando centro (olhe p/ o monitor principal)...", false, 0);
        }
        catch (Exception ex)
        {
            await StopAsync();
            OnStateUpdated?.Invoke($"HEAD TRACKER ERRO: {ex.Message}", false, 0);
        }
    }

    public void ResetCalibration()
    {
        _calibrationSamples = 0;
        _calibSumX = 0;
        _calibSumU = 0;
        _calibSumAsym = 0;
        _isCalibrated = false;
        _smoothedYawRight = 0;
        _isLookingRight = false;
        _consecutiveRightFrames = 0;
        _consecutiveStraightFrames = 0;
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        try
        {
            if (_frameReader != null)
            {
                _frameReader.FrameArrived -= OnFrameArrived;
                await _frameReader.StopAsync();
                _frameReader.Dispose();
                _frameReader = null;
            }
        }
        catch { }

        try
        {
            if (_mediaCapture != null)
            {
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }
        }
        catch { }
    }

    private async void OnFrameArrived(
        Windows.Media.Capture.Frames.MediaFrameReader sender,
        Windows.Media.Capture.Frames.MediaFrameArrivedEventArgs args)
    {
        if (!_isRunning || _faceDetector == null)
            return;

        long now = Environment.TickCount64;
        if (now - _lastFrameTick < 55) // ~18 FPS é ideal para resposta imediata com uso mínimo de CPU
            return;

        if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) != 0)
            return;

        _lastFrameTick = now;

        try
        {
            Windows.Graphics.Imaging.SoftwareBitmap? grayBitmap = null;
            using (var frameRef = sender.TryAcquireLatestFrame())
            {
                var sourceBitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
                if (sourceBitmap == null)
                    return;

                grayBitmap = sourceBitmap.BitmapPixelFormat == Windows.Graphics.Imaging.BitmapPixelFormat.Gray8
                    ? Windows.Graphics.Imaging.SoftwareBitmap.Copy(sourceBitmap)
                    : Windows.Graphics.Imaging.SoftwareBitmap.Convert(sourceBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Gray8);
            }

            using (grayBitmap)
            {
                var faces = await _faceDetector.DetectFacesAsync(grayBitmap);
                if (faces == null || faces.Count == 0)
                    return;

                // Seleciona o rosto principal (maior área na câmera)
                Windows.Media.FaceAnalysis.DetectedFace? mainFace = null;
                uint maxArea = 0;
                foreach (var f in faces)
                {
                    uint area = f.FaceBox.Width * f.FaceBox.Height;
                    if (area > maxArea)
                    {
                        maxArea = area;
                        mainFace = f;
                    }
                }

                if (mainFace == null || mainFace.FaceBox.Width < 24 || mainFace.FaceBox.Height < 24)
                    return;

                int frameW = grayBitmap.PixelWidth;
                int frameH = grayBitmap.PixelHeight;
                int byteCount = frameW * frameH;
                if (_pixelBuffer == null || _pixelBuffer.Length != byteCount)
                {
                    _pixelBuffer = new byte[byteCount];
                }

                var winrtBuffer = new Windows.Storage.Streams.Buffer((uint)byteCount);
                grayBitmap.CopyToBuffer(winrtBuffer);
                using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(winrtBuffer))
                {
                    reader.ReadBytes(_pixelBuffer);
                }

                var box = mainFace.FaceBox;
                double faceCenterNormX = (box.X + 0.5 * box.Width) / frameW;
                double faceWidthNorm = Math.Max(0.12, (double)box.Width / frameW);

                var (featureCentroidU, cheekGradAsym) = ComputeInternalFaceYawFeatures(
                    _pixelBuffer,
                    frameW,
                    frameH,
                    (int)box.X,
                    (int)box.Y,
                    (int)box.Width,
                    (int)box.Height);

                if (!_isCalibrated)
                {
                    _calibSumX += faceCenterNormX;
                    _calibSumU += featureCentroidU;
                    _calibSumAsym += cheekGradAsym;
                    _calibrationSamples++;

                    if (_calibrationSamples >= 6)
                    {
                        _baselineX = _calibSumX / _calibrationSamples;
                        _baselineU = _calibSumU / _calibrationSamples;
                        _baselineAsym = _calibSumAsym / _calibrationSamples;
                        _isCalibrated = true;
                        OnStateUpdated?.Invoke("👀 HEAD TRACKER CALIBRADO • Olhe p/ direita para centralizar no 2º monitor", false, 0);
                    }
                    return;
                }

                // No sensor nativo (não espelhado) da webcam frontal:
                // Virar o rosto para a DIREITA física do usuário desloca a face e a ponta do nariz para a ESQUERDA da imagem bruta (X e U menores).
                double posShiftRight = (_baselineX - faceCenterNormX) / faceWidthNorm;
                double featShiftRight = _baselineU - featureCentroidU;
                double asymShiftRight = cheekGradAsym - _baselineAsym;

                double rawYawRight = (0.68 * posShiftRight) + (3.60 * featShiftRight) + (0.40 * asymShiftRight);
                _smoothedYawRight = (0.48 * rawYawRight) + (0.52 * _smoothedYawRight);

                // Adaptação lenta de postura neutra quando o usuário está olhando reto para o monitor principal
                if (!_isLookingRight && Math.Abs(rawYawRight) < 0.055)
                {
                    _baselineX = (0.985 * _baselineX) + (0.015 * faceCenterNormX);
                    _baselineU = (0.985 * _baselineU) + (0.015 * featureCentroidU);
                    _baselineAsym = (0.985 * _baselineAsym) + (0.015 * cheekGradAsym);
                }

                if (!_isLookingRight)
                {
                    if (_smoothedYawRight >= EnterRightMonitorThreshold)
                    {
                        _consecutiveRightFrames++;
                        if (_consecutiveRightFrames >= ConfirmFramesRequired)
                        {
                            _isLookingRight = true;
                            _consecutiveRightFrames = 0;
                            _consecutiveStraightFrames = 0;
                            WarpCursorToMonitorCenter(secondaryMonitor: true);
                            OnStateUpdated?.Invoke($"👀 ROSTO P/ DIREITA ({_smoothedYawRight:0.00}) ➔ Mouse no centro do 2º Monitor", true, _smoothedYawRight);
                        }
                    }
                    else
                    {
                        _consecutiveRightFrames = 0;
                    }
                }
                else
                {
                    if (_smoothedYawRight <= ReturnPrimaryMonitorThreshold)
                    {
                        _consecutiveStraightFrames++;
                        if (_consecutiveStraightFrames >= ConfirmFramesRequired)
                        {
                            _isLookingRight = false;
                            _consecutiveStraightFrames = 0;
                            _consecutiveRightFrames = 0;
                            WarpCursorToMonitorCenter(secondaryMonitor: false);
                            OnStateUpdated?.Invoke($"👀 ROSTO RETO ({_smoothedYawRight:0.00}) ➔ Mouse no centro do 1º Monitor", false, _smoothedYawRight);
                        }
                    }
                    else
                    {
                        _consecutiveStraightFrames = 0;
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessingFrame, 0);
        }
    }

    /// <summary>
    /// Calcula o centroide horizontal das feições internas (olhos, nariz e boca) e a assimetria de gradiente
    /// entre as metades da face para estimar o ângulo de Yaw (rotação horizontal da cabeça).
    /// </summary>
    private static (double FeatureCentroidU, double CheekGradAsym) ComputeInternalFaceYawFeatures(
        byte[] grayPixels,
        int frameW,
        int frameH,
        int boxX,
        int boxY,
        int boxW,
        int boxH)
    {
        int x0 = Math.Clamp(boxX + (int)(boxW * 0.10), 1, frameW - 2);
        int x1 = Math.Clamp(boxX + (int)(boxW * 0.90), 1, frameW - 2);
        int y0 = Math.Clamp(boxY + (int)(boxH * 0.22), 1, frameH - 2);
        int y1 = Math.Clamp(boxY + (int)(boxH * 0.78), 1, frameH - 2);

        if (x1 <= x0 + 4 || y1 <= y0 + 4)
            return (0.50, 0.0);

        long lumSum = 0;
        int count = 0;
        for (int y = y0; y <= y1; y += 2)
        {
            int rowOffset = y * frameW;
            for (int x = x0; x <= x1; x += 2)
            {
                lumSum += grayPixels[rowOffset + x];
                count++;
            }
        }

        double meanLum = count > 0 ? (double)lumSum / count : 128.0;
        double weightedUSum = 0.0;
        double weightSum = 0.0;
        double leftHalfGrad = 0.0;
        double rightHalfGrad = 0.0;
        int midX = boxX + (boxW / 2);

        for (int y = y0; y <= y1; y += 2)
        {
            int rowOffset = y * frameW;
            int prevRow = (y - 1) * frameW;
            int nextRow = (y + 1) * frameW;

            for (int x = x0; x <= x1; x += 2)
            {
                int idx = rowOffset + x;
                int gx = Math.Abs(grayPixels[idx + 1] - grayPixels[idx - 1]);
                int gy = Math.Abs(grayPixels[nextRow + x] - grayPixels[prevRow + x]);
                double grad = gx + 0.5 * gy;
                double darkSalience = Math.Max(0.0, meanLum - grayPixels[idx]);

                double weight = grad + 0.85 * darkSalience;
                double u = (double)(x - boxX) / boxW;

                weightedUSum += u * weight;
                weightSum += weight;

                if (x < midX)
                    leftHalfGrad += grad;
                else
                    rightHalfGrad += grad;
            }
        }

        double centroidU = weightSum > 1e-3 ? (weightedUSum / weightSum) : 0.50;
        double asym = (leftHalfGrad - rightHalfGrad) / (leftHalfGrad + rightHalfGrad + 1.0);
        return (centroidU, asym);
    }

    private void WarpCursorToMonitorCenter(bool secondaryMonitor)
    {
        try
        {
            // Nunca interrompe um clique/arraste ativo do mouse
            if (Control.MouseButtons != MouseButtons.None)
                return;

            long now = Environment.TickCount64;
            if (now - _lastWarpTick < 180)
                return;

            Screen[] screens = Screen.AllScreens;
            if (screens.Length < 2)
                return;

            Screen? targetScreen;
            if (secondaryMonitor)
            {
                // Seleciona o segundo monitor (!Primary)
                targetScreen = screens.FirstOrDefault(s => !s.Primary) ?? screens[^1];
            }
            else
            {
                // Seleciona o monitor principal (Primary)
                targetScreen = Screen.PrimaryScreen ?? screens.FirstOrDefault(s => s.Primary) ?? screens[0];
            }

            Rectangle b = targetScreen.Bounds;
            Point center = new(b.Left + (b.Width / 2), b.Top + (b.Height / 2));

            // Na transição para o 2º monitor (rosto p/ direita), sempre centraliza no 2º monitor.
            // Na volta para o 1º monitor (rosto reto), centraliza no 1º monitor se o cursor estava no 2º monitor.
            Screen currentScreen = Screen.FromPoint(Cursor.Position);
            if (secondaryMonitor || !string.Equals(currentScreen.DeviceName, targetScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                Cursor.Position = center;
                WrapEngine?.NotifyExternalWarp(center);
                _lastWarpTick = now;
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}



