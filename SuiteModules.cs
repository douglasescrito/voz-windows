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
    public bool HeadRealtimeMouseEnabled { get; set; } = false; // Flag: Controla o mouse continuamente em tempo real com o rosto (começa desativado)
    public double HeadRealtimeMouseSpeed { get; set; } = 1.2; // Velocidade espacial do cursor no modo Realtime + Touch (0.2x a 4.0x, padrão 1.2x)
    public int HeadTouchLockMs { get; set; } = 260; // Janela de prioridade do Touchpad ao combinar com o rosto (100ms a 600ms, padrão 260ms)
    public bool SystemAudioLoopbackEnabled { get; set; } = false; // Flag: Ouvir o áudio do próprio computador (WASAPI Loopback, começa desativado)
    public double HeadSensitivity { get; set; } = 2.4; // Multiplicador de sensibilidade do giro (0.5x a 4.5x)
    public double HeadDistanceBoost { get; set; } = 2.0; // Compensação automática para trabalhar mais distante (1.0x a 3.5x)
    public double HeadEnterThreshold { get; set; } = 0.080; // Limiar de disparo p/ 2º monitor (menor = mais sensível)
    public double HeadReturnThreshold { get; set; } = 0.032; // Limiar de volta p/ 1º monitor
    public double HeadSmoothingAlpha { get; set; } = 0.65; // Velocidade de resposta do filtro EMA (0.20 a 0.95)
    public int HeadConfirmFrames { get; set; } = 1; // 1 frame = resposta imediata (~50ms)
    public bool HeadReturnToPrimary { get; set; } = true; // Volta o mouse p/ o centro do 1º monitor ao olhar reto
    public bool HeadLongDistanceHdMode { get; set; } = true; // Captura HD + rastreio de rosto pequeno/distante
    public int HeadConfigVersion { get; set; } = 2;
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

    public string GetHeadRealtimeMousePillText()
    {
        return HeadRealtimeMouseEnabled ? "🖱️ Mouse RT: ON" : "🖱️ Mouse RT: OFF";
    }

    public string GetSystemAudioLoopbackPillText()
    {
        return SystemAudioLoopbackEnabled ? "🎧 Ouvir PC: ON" : "🎧 Ouvir PC: OFF";
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
    /// Alterna o modo Função Realtime para controlar o cursor do mouse continuamente com o rosto (X e Y).
    /// </summary>
    public void ToggleHeadRealtimeMouseOnBar()
    {
        HeadRealtimeMouseEnabled = !HeadRealtimeMouseEnabled;
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
            // E o modo Função Realtime de controle do mouse e Ouvir PC sempre começam DESATIVADOS ao abrir o app
            loaded.Enabled = true;
            loaded.HorizontalLoop = true;
            loaded.VerticalLoop = true;
            loaded.HeadRealtimeMouseEnabled = false;
            loaded.SystemAudioLoopbackEnabled = false;
            if (loaded.HeadRealtimeMouseSpeed <= 0.1)
            {
                loaded.HeadRealtimeMouseSpeed = 1.2;
            }
            if (loaded.HeadTouchLockMs < 80 || loaded.HeadTouchLockMs > 800)
            {
                loaded.HeadTouchLockMs = 260;
            }
            if (loaded.HeadConfigVersion < 2)
            {
                loaded.HeadSensitivity = 2.4;
                loaded.HeadDistanceBoost = 2.0;
                loaded.HeadEnterThreshold = 0.080;
                loaded.HeadReturnThreshold = 0.032;
                loaded.HeadSmoothingAlpha = 0.65;
                loaded.HeadConfirmFrames = 1;
                loaded.HeadReturnToPrimary = true;
                loaded.HeadLongDistanceHdMode = true;
                loaded.HeadConfigVersion = 2;
            }
            if (loaded.HeadConfigVersion < 3)
            {
                loaded.HeadRealtimeMouseSpeed = 1.2;
                loaded.HeadTouchLockMs = 260;
                loaded.HeadConfigVersion = 3;
            }
            loaded.Save();
            return loaded;
        }
        var defaultSettings = new CursorLoopSettings
        {
            Enabled = true,
            SpeedLevel = 1,
            RealtimeRewriteEnabled = false,
            HeadTrackerEnabled = false,
            HeadRealtimeMouseEnabled = false,
            HeadRealtimeMouseSpeed = 1.2,
            HeadTouchLockMs = 260,
            SystemAudioLoopbackEnabled = false,
            HeadSensitivity = 2.4,
            HeadDistanceBoost = 2.0,
            HeadEnterThreshold = 0.080,
            HeadReturnThreshold = 0.032,
            HeadSmoothingAlpha = 0.65,
            HeadConfirmFrames = 1,
            HeadReturnToPrimary = true,
            HeadLongDistanceHdMode = true,
            HeadConfigVersion = 3,
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
    public bool LastTickHadPhysicalMove { get; private set; }

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
        LastTickHadPhysicalMove = false;
    }

    public void NotifyHeadRealtimeStep(Point newPoint, int stepX, int stepY)
    {
        _previous = newPoint;
        _hasPrevious = true;
        if (stepX != 0) _lastDx = stepX;
        if (stepY != 0) _lastDy = stepY;
        _remX = 0;
        _remY = 0;
        _lastWarp = Environment.TickCount64 - 80; // Evita multiplicar por SpeedMultiplier, mas permite atravessar bordas no Loop
        LastTickHadPhysicalMove = false;
    }

    public void Tick(bool loopEnabled, bool horizontal, bool vertical)
    {
        LastTickHadPhysicalMove = false;
        double multiplier = SpeedMultiplier;
        bool speedActive = multiplier > 1.001;

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

        // Movimento físico legítimo do Touchpad / Mouse detectado!
        LastTickHadPhysicalMove = true;

        if (!loopEnabled && !speedActive)
        {
            _previous = point;
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
// 3. HEAD MOTION MONITOR SWITCHER + TELA DE TESTE E CONFIGURAÇÃO EM TEMPO REAL
// ============================================================================

public sealed record HeadTrackerTelemetry(
    byte[]? GrayPixels,
    int FrameWidth,
    int FrameHeight,
    Rectangle FaceBox,
    double FaceCenterNormX,
    double BaselineX,
    double FaceCenterNormY,
    double BaselineY,
    double FeatureCentroidU,
    double BaselineU,
    double FeatureCentroidV,
    double BaselineV,
    double RawYawRight,
    double SmoothedYawRight,
    double SmoothedPitchDown,
    double EnterThreshold,
    double ReturnThreshold,
    double DistanceGainApplied,
    double TotalGainApplied,
    bool IsCalibrated,
    bool IsLookingRight,
    bool FaceDetected,
    bool IsFallbackLongRangeRoi,
    bool TouchRecalibratedRecently,
    int SpatialOffsetX,
    int SpatialOffsetY,
    string StatusMessage);

/// <summary>
/// Motor nativo em tempo real (Windows.Media.FaceAnalysis + MediaFrameReader + ROI Long-Range Tracker)
/// que opera dois modos combináveis no mesmo recurso:
/// 1) Monitor Switcher: move o cursor para o centro do 2º monitor ao virar à direita e retorna ao 1º monitor ao olhar reto.
/// 2) Modo Função Realtime: controla continuamente o cursor do mouse (X e Y) em tempo real com o movimento do rosto.
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
    private double _calibSumY;
    private double _calibSumU;
    private double _calibSumV;
    private double _calibSumAsym;
    private double _baselineX = 0.50;
    private double _baselineY = 0.50;
    private double _baselineU = 0.50;
    private double _baselineV = 0.50;
    private double _baselineAsym = 0.0;
    private bool _isCalibrated;

    // Última posição conhecida da face (com filtro sub-pixel p/ eliminar salto de caixa do FaceDetector)
    private Rectangle _lastValidFaceBox;
    private bool _hasSmoothBox;
    private double _smoothBoxX;
    private double _smoothBoxY;
    private double _smoothBoxW;
    private double _smoothBoxH;
    private long _lastValidFaceTick;

    // Filtro EMA + Histerese de transição + Mapeamento Espacial Híbrido (Touchpad + Rosto) para o Modo Realtime Mouse
    private double _smoothedYawRight;
    private double _smoothedPitchDown;
    private double _rtSmoothYaw;
    private double _rtSmoothPitch;
    private bool _hasRawFaceSample;
    private double _lastRawFaceX = 0.50;
    private double _lastRawFaceY = 0.50;
    private double _lastRawFeatureU = 0.50;
    private double _lastRawFeatureV = 0.50;
    private double _lastRawAsym = 0.0;
    private double _lastRawYawRight;
    private double _lastRawPitchDown;

    private bool _rtAnchorInitialized;
    private double _rtAnchorX;
    private double _rtAnchorY;
    private double _rtRefYaw;
    private double _rtRefPitch;
    private double _rtCurrentOffsetX;
    private double _rtCurrentOffsetY;
    private long _lastTouchpadRecalibTick;

    private bool _isLookingRight;
    private int _consecutiveRightFrames;
    private int _consecutiveStraightFrames;
    private long _lastWarpTick;
    private byte[]? _pixelBuffer;

    public bool IsRunning => _isRunning;
    public bool IsLookingRight => _isLookingRight;
    public double CurrentYawScore => _smoothedYawRight;
    public double CurrentPitchScore => _smoothedPitchDown;
    public CursorWrapEngine? WrapEngine { get; set; }
    public CursorLoopSettings? Settings { get; set; }
    public volatile bool IncludePreviewFrame;
    public volatile bool PauseMouseWarpForTest;

    public event Action<string, bool, double>? OnStateUpdated;
    public event Action<HeadTrackerTelemetry>? OnTelemetryFrame;

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
            // Reduz o tamanho mínimo detectável do rosto para permitir trabalhar mais distante da webcam
            try
            {
                _faceDetector.MinDetectableFaceSize = new Windows.Graphics.Imaging.BitmapSize { Width = 16, Height = 16 };
            }
            catch { }

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

            // No Modo Longa Distância HD, seleciona 640x480 / 960x540 / 1280x720 para enxergar o rosto mesmo afastado na cadeira
            try
            {
                bool hdMode = Settings?.HeadLongDistanceHdMode ?? true;
                int targetWidth = hdMode ? 640 : 320;
                int maxWidth = hdMode ? 1280 : 640;

                var preferredFormat = frameSource.SupportedFormats
                    .Where(f => f.VideoFormat.Width >= 320 && f.VideoFormat.Width <= maxWidth && f.FrameRate.Numerator >= 15)
                    .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - targetWidth))
                    .ThenByDescending(f => (double)f.FrameRate.Numerator / Math.Max(1u, f.FrameRate.Denominator))
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
        _calibSumY = 0;
        _calibSumU = 0;
        _calibSumV = 0;
        _calibSumAsym = 0;
        _isCalibrated = false;
        _hasSmoothBox = false;
        _smoothedYawRight = 0;
        _smoothedPitchDown = 0;
        _rtSmoothYaw = 0;
        _rtSmoothPitch = 0;
        _lastRawYawRight = 0;
        _lastRawPitchDown = 0;
        _rtAnchorInitialized = false;
        _rtCurrentOffsetX = 0;
        _rtCurrentOffsetY = 0;
        _rtRefYaw = 0;
        _rtRefPitch = 0;
        _isLookingRight = false;
        _consecutiveRightFrames = 0;
        _consecutiveStraightFrames = 0;
        _lastValidFaceTick = 0;
    }

    /// <summary>
    /// Reposiciona a âncora espacial do Modo Realtime (RT) e recalibra o centro espacial do rosto
    /// automaticamente sempre que o usuário ajusta o cursor usando o Touchpad / Mouse físico.
    /// </summary>
    public void ReanchorFromTouchpad(Point touchCursorPos)
    {
        _rtAnchorInitialized = true;
        _rtAnchorX = touchCursorPos.X;
        _rtAnchorY = touchCursorPos.Y;
        _rtCurrentOffsetX = 0;
        _rtCurrentOffsetY = 0;
        _lastTouchpadRecalibTick = Environment.TickCount64;

        if (!_hasRawFaceSample)
            return;

        bool monitorSwitchActive = Settings?.HeadTrackerEnabled ?? false;
        bool isOnPrimaryMonitor = true;
        try
        {
            isOnPrimaryMonitor = Screen.FromPoint(touchCursorPos).Primary;
        }
        catch { }

        if (!monitorSwitchActive || isOnPrimaryMonitor)
        {
            // Recalibra o ponto zero espacial (cruz amarela da câmera) na postura atual do rosto do usuário!
            _baselineX = _lastRawFaceX;
            _baselineY = _lastRawFaceY;
            _baselineU = _lastRawFeatureU;
            _baselineV = _lastRawFeatureV;
            _baselineAsym = _lastRawAsym;
            _lastRawYawRight = 0;
            _lastRawPitchDown = 0;
            _smoothedYawRight = 0;
            _smoothedPitchDown = 0;
            _rtSmoothYaw = 0;
            _rtSmoothPitch = 0;
            _rtRefYaw = 0;
            _rtRefPitch = 0;
            _isCalibrated = true;
            if (isOnPrimaryMonitor)
            {
                _isLookingRight = false;
                _consecutiveRightFrames = 0;
                _consecutiveStraightFrames = 0;
            }
        }
        else
        {
            // No 2º monitor com Head Switcher ativo: preserva o baseline do 1º monitor p/ retorno,
            // mas zera a referência espacial do Mouse RT exatamente na postura atual do 2º monitor!
            _smoothedYawRight = _lastRawYawRight;
            _smoothedPitchDown = _lastRawPitchDown;
            _rtRefYaw = _rtSmoothYaw;
            _rtRefPitch = _rtSmoothPitch;
            _isLookingRight = true;
            _consecutiveRightFrames = 0;
            _consecutiveStraightFrames = 0;
            _isCalibrated = true;
        }
    }

    /// <summary>
    /// Chamado a 120 Hz pelo timer principal:
    /// - Se o usuário moveu o Touchpad/Mouse físico neste ciclo (userMovedTouchpad = true), usa a nova posição do Touchpad
    ///   para reposicionar a âncora espacial do RT e recalibrar o centro espacial do rosto sem esforço.
    /// - Caso contrário, aplica deslocamento espacial suave e proporcional (desacoplado do ganho alto do 2º monitor),
    ///   com janela de prioridade total ao Touchpad (260ms) + rampa progressiva (350ms) para combinação suave.
    /// </summary>
    public void TickRealtimeCursor(bool userMovedTouchpad = false)
    {
        if (!_isRunning)
            return;

        if (userMovedTouchpad)
        {
            try
            {
                ReanchorFromTouchpad(Cursor.Position);
            }
            catch { }
            return;
        }

        if (!_isCalibrated || PauseMouseWarpForTest)
            return;

        if (!(Settings?.HeadRealtimeMouseEnabled ?? false))
        {
            _rtAnchorInitialized = false;
            _rtCurrentOffsetX = 0;
            _rtCurrentOffsetY = 0;
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastValidFaceTick > 750)
            return;

        if (!_rtAnchorInitialized)
        {
            try
            {
                ReanchorFromTouchpad(Cursor.Position);
            }
            catch { }
            return;
        }

        long elapsedSinceTouch = now - _lastTouchpadRecalibTick;
        int touchLockMs = Math.Clamp(Settings?.HeadTouchLockMs ?? 260, 80, 800);

        // Prioridade total ao Touchpad após cada toque: garante controle fino e cliques sem nenhuma briga com a câmera!
        if (elapsedSinceTouch < touchLockMs)
        {
            try
            {
                Point settlePos = Cursor.Position;
                _rtAnchorX = settlePos.X;
                _rtAnchorY = settlePos.Y;
                _rtRefYaw = _rtSmoothYaw;
                _rtRefPitch = _rtSmoothPitch;
                _rtCurrentOffsetX = 0;
                _rtCurrentOffsetY = 0;
            }
            catch { }
            return;
        }

        double deltaYaw = _rtSmoothYaw - _rtRefYaw;
        double deltaPitch = _rtSmoothPitch - _rtRefPitch;
        double mag = Math.Sqrt(deltaYaw * deltaYaw + deltaPitch * deltaPitch);

        // Zona morta central confortável (0.014): absorve respiração e micro-movimentos mantendo o cursor 100% parado onde o Touch deixou
        const double deadzone = 0.014;
        if (mag <= deadzone)
        {
            // Adapta suavemente a referência ao centro atual enquanto o rosto está parado na zona morta
            _rtRefYaw = (0.985 * _rtRefYaw) + (0.015 * _rtSmoothYaw);
            _rtRefPitch = (0.985 * _rtRefPitch) + (0.015 * _rtSmoothPitch);
            return;
        }

        // Rampa suave (350ms) após o término do uso do Touchpad para transição imperceptível entre Touch e Rosto
        double postTouchRamp = Math.Clamp((elapsedSinceTouch - touchLockMs) / 350.0, 0.0, 1.0);
        double speedSetting = Math.Clamp(Settings?.HeadRealtimeMouseSpeed ?? 1.2, 0.15, 6.0);

        // Escala espacial controlada e proporcional (desacoplada do ganho do 2º monitor):
        // Pequenos giros movem poucos pixels p/ ajuste fino; giros maiores movem 150~400px de forma fluida
        double absYaw = Math.Abs(deltaYaw);
        double effYaw = Math.Max(0.0, absYaw - deadzone * 0.85);
        double targetOffsetX = Math.Sign(deltaYaw) * (effYaw * 540.0 + Math.Pow(effYaw, 1.55) * 1250.0) * speedSetting * postTouchRamp;

        double absPitch = Math.Abs(deltaPitch);
        double effPitch = Math.Max(0.0, absPitch - deadzone * 0.85);
        double targetOffsetY = Math.Sign(deltaPitch) * (effPitch * 680.0 + Math.Pow(effPitch, 1.55) * 1450.0) * speedSetting * postTouchRamp;

        double diffX = targetOffsetX - _rtCurrentOffsetX;
        double diffY = targetOffsetY - _rtCurrentOffsetY;
        double dist = Math.Sqrt(diffX * diffX + diffY * diffY);

        // Estabilização de mira (< 1.50 px) para manter o cursor firme na hora do clique
        if (dist < 1.50)
            return;

        double lerp = dist < 12.0
            ? 0.11
            : Math.Clamp(0.14 + (dist / 220.0) * 0.14, 0.14, 0.28);

        _rtCurrentOffsetX += diffX * lerp;
        _rtCurrentOffsetY += diffY * lerp;

        int desiredX = (int)Math.Round(_rtAnchorX + _rtCurrentOffsetX);
        int desiredY = (int)Math.Round(_rtAnchorY + _rtCurrentOffsetY);

        try
        {
            Point cur = Cursor.Position;
            Point clamped = ClampToDesktopScreens(desiredX, desiredY, cur);

            // Ratchet espacial automático nas bordas da tela: evita "folga" ao virar o rosto de volta
            if (clamped.X != desiredX)
                _rtAnchorX = clamped.X - _rtCurrentOffsetX;
            if (clamped.Y != desiredY)
                _rtAnchorY = clamped.Y - _rtCurrentOffsetY;

            int moveX = clamped.X - cur.X;
            int moveY = clamped.Y - cur.Y;
            if (moveX == 0 && moveY == 0)
                return;

            Cursor.Position = clamped;
            WrapEngine?.NotifyHeadRealtimeStep(Cursor.Position, moveX, moveY);
        }
        catch { }
    }

    private static Point ClampToDesktopScreens(int desiredX, int desiredY, Point currentPos)
    {
        Screen[] screens = Screen.AllScreens;
        if (screens.Length == 0)
            return new Point(desiredX, desiredY);

        Point candidate = new(desiredX, desiredY);
        for (int i = 0; i < screens.Length; i++)
        {
            if (screens[i].Bounds.Contains(candidate))
                return candidate;
        }

        Screen activeScreen = Screen.FromPoint(candidate) ?? Screen.FromPoint(currentPos) ?? screens[0];
        Rectangle b = activeScreen.Bounds;
        int cx = Math.Clamp(desiredX, b.Left, b.Right - 1);
        int cy = Math.Clamp(desiredY, b.Top, b.Bottom - 1);
        return new Point(cx, cy);
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
        if (now - _lastFrameTick < 48) // ~20 FPS para resposta ultra ágil
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

                var faces = await _faceDetector.DetectFacesAsync(grayBitmap);

                Windows.Media.FaceAnalysis.DetectedFace? mainFace = null;
                uint maxArea = 0;
                if (faces != null)
                {
                    foreach (var f in faces)
                    {
                        uint area = f.FaceBox.Width * f.FaceBox.Height;
                        if (area > maxArea)
                        {
                            maxArea = area;
                            mainFace = f;
                        }
                    }
                }

                Rectangle activeBox;
                bool usedFallbackRoi = false;

                if (mainFace != null && mainFace.FaceBox.Width >= 12 && mainFace.FaceBox.Height >= 12)
                {
                    activeBox = new Rectangle(
                        (int)mainFace.FaceBox.X,
                        (int)mainFace.FaceBox.Y,
                        (int)mainFace.FaceBox.Width,
                        (int)mainFace.FaceBox.Height);
                    _lastValidFaceBox = activeBox;
                    _lastValidFaceTick = now;
                }
                else if (now - _lastValidFaceTick <= 2600 && _lastValidFaceBox.Width >= 12)
                {
                    // Rastreio contínuo de longa distância / perfil lateral quando o usuário vira muito o rosto ou se afasta
                    activeBox = TrackFaceRoiFallback(_pixelBuffer, frameW, frameH, _lastValidFaceBox);
                    _lastValidFaceBox = activeBox;
                    usedFallbackRoi = true;
                }
                else
                {
                    if (IncludePreviewFrame)
                    {
                        byte[]? previewCopy = new byte[byteCount];
                        Buffer.BlockCopy(_pixelBuffer, 0, previewCopy, 0, byteCount);
                        OnTelemetryFrame?.Invoke(new HeadTrackerTelemetry(
                            previewCopy,
                            frameW,
                            frameH,
                            Rectangle.Empty,
                            0.5,
                            _baselineX,
                            0.5,
                            _baselineY,
                            0.5,
                            _baselineU,
                            0.5,
                            _baselineV,
                            0,
                            _smoothedYawRight,
                            _smoothedPitchDown,
                            Settings?.HeadEnterThreshold ?? 0.080,
                            Settings?.HeadReturnThreshold ?? 0.032,
                            1.0,
                            Settings?.HeadSensitivity ?? 2.4,
                            _isCalibrated,
                            _isLookingRight,
                            false,
                            false,
                            false,
                            (int)Math.Round(_rtCurrentOffsetX),
                            (int)Math.Round(_rtCurrentOffsetY),
                            "Aguardando rosto na câmera (aproxime-se ou olhe para a câmera)..."));
                    }
                    return;
                }

                // Suavização sub-pixel da caixa da face para eliminar degraus inteiros do FaceDetector
                if (!_hasSmoothBox)
                {
                    _smoothBoxX = activeBox.X;
                    _smoothBoxY = activeBox.Y;
                    _smoothBoxW = activeBox.Width;
                    _smoothBoxH = activeBox.Height;
                    _hasSmoothBox = true;
                }
                else
                {
                    const double boxAlpha = 0.32;
                    _smoothBoxX = (boxAlpha * activeBox.X) + ((1.0 - boxAlpha) * _smoothBoxX);
                    _smoothBoxY = (boxAlpha * activeBox.Y) + ((1.0 - boxAlpha) * _smoothBoxY);
                    _smoothBoxW = (boxAlpha * activeBox.Width) + ((1.0 - boxAlpha) * _smoothBoxW);
                    _smoothBoxH = (boxAlpha * activeBox.Height) + ((1.0 - boxAlpha) * _smoothBoxH);
                }

                Rectangle smoothRect = new(
                    (int)Math.Round(_smoothBoxX),
                    (int)Math.Round(_smoothBoxY),
                    Math.Max(12, (int)Math.Round(_smoothBoxW)),
                    Math.Max(12, (int)Math.Round(_smoothBoxH)));

                double faceCenterNormX = (_smoothBoxX + 0.5 * _smoothBoxW) / frameW;
                double faceCenterNormY = (_smoothBoxY + 0.5 * _smoothBoxH) / frameH;
                double faceWidthNorm = Math.Max(0.08, _smoothBoxW / frameW);
                double faceHeightNorm = Math.Max(0.10, _smoothBoxH / frameH);

                var (featureCentroidU, featureCentroidV, cheekGradAsym) = ComputeInternalFaceYawFeatures(
                    _pixelBuffer,
                    frameW,
                    frameH,
                    smoothRect.X,
                    smoothRect.Y,
                    smoothRect.Width,
                    smoothRect.Height);

                _lastRawFaceX = faceCenterNormX;
                _lastRawFaceY = faceCenterNormY;
                _lastRawFeatureU = featureCentroidU;
                _lastRawFeatureV = featureCentroidV;
                _lastRawAsym = cheekGradAsym;
                _hasRawFaceSample = true;

                double userSensitivity = Math.Clamp(Settings?.HeadSensitivity ?? 2.4, 0.4, 5.0);
                double userDistBoost = Math.Clamp(Settings?.HeadDistanceBoost ?? 2.0, 0.8, 4.0);
                double enterThreshold = Math.Clamp(Settings?.HeadEnterThreshold ?? 0.080, 0.020, 0.350);
                double returnThreshold = Math.Clamp(Settings?.HeadReturnThreshold ?? 0.032, 0.005, enterThreshold - 0.010);
                double alpha = Math.Clamp(Settings?.HeadSmoothingAlpha ?? 0.65, 0.20, 0.95);
                int confirmFrames = Math.Clamp(Settings?.HeadConfirmFrames ?? 1, 1, 5);
                bool returnToPrimary = Settings?.HeadReturnToPrimary ?? true;

                // Ganho automático de longa distância: quanto menor o rosto na imagem (mais longe da câmera), maior a amplificação!
                double distanceRatio = Math.Clamp(0.24 / faceWidthNorm, 1.0, 3.2);
                double distanceGain = 1.0 + (distanceRatio - 1.0) * (userDistBoost * 0.65);
                double totalGain = userSensitivity * distanceGain;

                if (!_isCalibrated)
                {
                    if (!usedFallbackRoi)
                    {
                        _calibSumX += faceCenterNormX;
                        _calibSumY += faceCenterNormY;
                        _calibSumU += featureCentroidU;
                        _calibSumV += featureCentroidV;
                        _calibSumAsym += cheekGradAsym;
                        _calibrationSamples++;
                    }

                    if (_calibrationSamples >= 5)
                    {
                        _baselineX = _calibSumX / _calibrationSamples;
                        _baselineY = _calibSumY / _calibrationSamples;
                        _baselineU = _calibSumU / _calibrationSamples;
                        _baselineV = _calibSumV / _calibrationSamples;
                        _baselineAsym = _calibSumAsym / _calibrationSamples;
                        _isCalibrated = true;
                        OnStateUpdated?.Invoke("👀 HEAD TRACKER CALIBRADO • Pronto p/ Monitor Switcher e Modo Realtime", false, 0);
                    }

                    if (IncludePreviewFrame)
                    {
                        byte[]? previewCopy = new byte[byteCount];
                        Buffer.BlockCopy(_pixelBuffer, 0, previewCopy, 0, byteCount);
                        OnTelemetryFrame?.Invoke(new HeadTrackerTelemetry(
                            previewCopy,
                            frameW,
                            frameH,
                            activeBox,
                            faceCenterNormX,
                            _baselineX,
                            faceCenterNormY,
                            _baselineY,
                            featureCentroidU,
                            _baselineU,
                            featureCentroidV,
                            _baselineV,
                            0,
                            0,
                            0,
                            enterThreshold,
                            returnThreshold,
                            distanceGain,
                            totalGain,
                            false,
                            false,
                            true,
                            usedFallbackRoi,
                            false,
                            0,
                            0,
                            $"Calibrando centro ({_calibrationSamples}/5)... Mantenha o rosto voltado ao 1º monitor."));
                    }
                    return;
                }

                // No sensor nativo (não espelhado) da webcam frontal:
                // Virar o rosto para a DIREITA física do usuário desloca a face e a ponta do nariz para a ESQUERDA da imagem bruta (X e U menores).
                double posShiftRight = (_baselineX - faceCenterNormX) / Math.Max(0.10, faceWidthNorm);
                double featShiftRight = _baselineU - featureCentroidU;
                double asymShiftRight = cheekGradAsym - _baselineAsym;

                double baseYawSignal = (0.72 * posShiftRight) + (3.85 * featShiftRight) + (0.45 * asymShiftRight);
                double rawYawRight = baseYawSignal * (totalGain * 0.55);
                _lastRawYawRight = rawYawRight;
                _smoothedYawRight = (alpha * rawYawRight) + ((1.0 - alpha) * _smoothedYawRight);

                // Inclinação vertical (Pitch Down = positivo / Up = negativo)
                double posShiftDown = (faceCenterNormY - _baselineY) / Math.Max(0.10, faceHeightNorm);
                double featShiftDown = featureCentroidV - _baselineV;
                double basePitchSignal = (0.85 * posShiftDown) + (3.60 * featShiftDown);
                double rawPitchDown = basePitchSignal * (totalGain * 0.60);
                _lastRawPitchDown = rawPitchDown;
                _smoothedPitchDown = (alpha * rawPitchDown) + ((1.0 - alpha) * _smoothedPitchDown);

                // Sinal Espacial Limpo e Desacoplado para o Mouse RT (cancela o jitter da caixa do FaceDetector usando coordenada global da feição!)
                double globalFeatX = faceCenterNormX + (featureCentroidU - 0.5) * faceWidthNorm;
                double baseGlobalFeatX = _baselineX + (_baselineU - 0.5) * faceWidthNorm;
                double globalFeatY = faceCenterNormY + (featureCentroidV - 0.5) * faceHeightNorm;
                double baseGlobalFeatY = _baselineY + (_baselineV - 0.5) * faceHeightNorm;

                double rtRawYaw = ((baseGlobalFeatX - globalFeatX) / Math.Max(0.12, faceWidthNorm)) + (0.35 * featShiftRight);
                double rtRawPitch = ((globalFeatY - baseGlobalFeatY) / Math.Max(0.12, faceHeightNorm)) + (0.35 * featShiftDown);

                const double rtFilterAlpha = 0.24;
                _rtSmoothYaw = (rtFilterAlpha * rtRawYaw) + ((1.0 - rtFilterAlpha) * _rtSmoothYaw);
                _rtSmoothPitch = (rtFilterAlpha * rtRawPitch) + ((1.0 - rtFilterAlpha) * _rtSmoothPitch);

                bool touchRecalibratedRecently = (now - _lastTouchpadRecalibTick) <= 650;
                bool allowMonitorAutoWarp = (now - _lastTouchpadRecalibTick) > 450;

                // Adaptação lenta de postura neutra quando o usuário está olhando reto (apenas se o modo Realtime contínuo estiver desligado)
                if (!(Settings?.HeadRealtimeMouseEnabled ?? false) &&
                    !_isLookingRight &&
                    !usedFallbackRoi &&
                    Math.Abs(rawYawRight) < returnThreshold * 0.75)
                {
                    _baselineX = (0.988 * _baselineX) + (0.012 * faceCenterNormX);
                    _baselineY = (0.988 * _baselineY) + (0.012 * faceCenterNormY);
                    _baselineU = (0.988 * _baselineU) + (0.012 * featureCentroidU);
                    _baselineV = (0.988 * _baselineV) + (0.012 * featureCentroidV);
                    _baselineAsym = (0.988 * _baselineAsym) + (0.012 * cheekGradAsym);
                }

                bool monitorSwitchEnabled = Settings?.HeadTrackerEnabled ?? false;
                bool realtimeMouseEnabled = Settings?.HeadRealtimeMouseEnabled ?? false;

                string statusMsg;
                if (!_isLookingRight)
                {
                    if (_smoothedYawRight >= enterThreshold && allowMonitorAutoWarp)
                    {
                        _consecutiveRightFrames++;
                        if (_consecutiveRightFrames >= confirmFrames)
                        {
                            _isLookingRight = true;
                            _consecutiveRightFrames = 0;
                            _consecutiveStraightFrames = 0;
                            if (monitorSwitchEnabled && !PauseMouseWarpForTest)
                            {
                                WarpCursorToMonitorCenter(secondaryMonitor: true);
                            }
                            statusMsg = realtimeMouseEnabled
                                ? $"🖱️ MOUSE RT ESPACIAL (ΔX:{(int)_rtCurrentOffsetX:+0;-0;0}px ΔY:{(int)_rtCurrentOffsetY:+0;-0;0}px) • 2º Monitor"
                                : $"👀 ROSTO P/ DIREITA ({_smoothedYawRight:0.000}) ➔ Mouse no Centro do 2º Monitor";
                            OnStateUpdated?.Invoke(statusMsg, true, _smoothedYawRight);
                        }
                        else
                        {
                            statusMsg = $"👀 Detectando giro p/ direita ({_smoothedYawRight:0.000})...";
                        }
                    }
                    else
                    {
                        _consecutiveRightFrames = 0;
                        statusMsg = realtimeMouseEnabled
                            ? (touchRecalibratedRecently
                                ? "✨ CALIBRADO PELO TOUCH • Posição Espacial RT Ancorada"
                                : $"🖱️ MOUSE RT ESPACIAL (ΔX:{(int)_rtCurrentOffsetX:+0;-0;0}px ΔY:{(int)_rtCurrentOffsetY:+0;-0;0}px)")
                            : $"🟢 OLHANDO RETO (1º Monitor) • Sinal: {_smoothedYawRight:0.000} / Limiar: {enterThreshold:0.000}";
                    }
                }
                else
                {
                    if (_smoothedYawRight <= returnThreshold && allowMonitorAutoWarp)
                    {
                        _consecutiveStraightFrames++;
                        if (_consecutiveStraightFrames >= confirmFrames)
                        {
                            _isLookingRight = false;
                            _consecutiveStraightFrames = 0;
                            _consecutiveRightFrames = 0;
                            if (monitorSwitchEnabled && !PauseMouseWarpForTest && returnToPrimary)
                            {
                                WarpCursorToMonitorCenter(secondaryMonitor: false);
                            }
                            statusMsg = realtimeMouseEnabled
                                ? $"🖱️ MOUSE RT ESPACIAL (ΔX:{(int)_rtCurrentOffsetX:+0;-0;0}px ΔY:{(int)_rtCurrentOffsetY:+0;-0;0}px) • 1º Monitor"
                                : $"👀 ROSTO RETO ({_smoothedYawRight:0.000}) ➔ Mouse no Centro do 1º Monitor";
                            OnStateUpdated?.Invoke(statusMsg, false, _smoothedYawRight);
                        }
                        else
                        {
                            statusMsg = $"👀 Retornando ao 1º Monitor ({_smoothedYawRight:0.000})...";
                        }
                    }
                    else
                    {
                        _consecutiveStraightFrames = 0;
                        statusMsg = realtimeMouseEnabled
                            ? (touchRecalibratedRecently
                                ? "✨ CALIBRADO PELO TOUCH (2º Monitor) • RT Ancorado"
                                : $"🖱️ MOUSE RT ESPACIAL NO 2º MONITOR (ΔX:{(int)_rtCurrentOffsetX:+0;-0;0}px ΔY:{(int)_rtCurrentOffsetY:+0;-0;0}px)")
                            : $"🎯 ATIVO NO 2º MONITOR (Rosto à Direita) • Sinal: {_smoothedYawRight:0.000}";
                    }
                }

                if (IncludePreviewFrame)
                {
                    byte[]? previewCopy = new byte[byteCount];
                    Buffer.BlockCopy(_pixelBuffer, 0, previewCopy, 0, byteCount);
                    OnTelemetryFrame?.Invoke(new HeadTrackerTelemetry(
                        previewCopy,
                        frameW,
                        frameH,
                        activeBox,
                        faceCenterNormX,
                        _baselineX,
                        faceCenterNormY,
                        _baselineY,
                        featureCentroidU,
                        _baselineU,
                        featureCentroidV,
                        _baselineV,
                        rawYawRight,
                        _smoothedYawRight,
                        _smoothedPitchDown,
                        enterThreshold,
                        returnThreshold,
                        distanceGain,
                        totalGain,
                        _isCalibrated,
                        _isLookingRight,
                        true,
                        usedFallbackRoi,
                        touchRecalibratedRecently,
                        (int)Math.Round(_rtCurrentOffsetX),
                        (int)Math.Round(_rtCurrentOffsetY),
                        statusMsg));
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
    /// Rastreia o deslocamento horizontal da cabeça em uma janela expandida quando o usuário está muito distante
    /// ou gira o rosto em perfil acentuado (>40°), garantindo que o giro nunca perca o disparo.
    /// </summary>
    private static Rectangle TrackFaceRoiFallback(byte[] grayPixels, int frameW, int frameH, Rectangle lastBox)
    {
        int expandX = Math.Max(18, (int)(lastBox.Width * 0.45));
        int expandY = Math.Max(10, (int)(lastBox.Height * 0.20));

        int sx0 = Math.Clamp(lastBox.X - expandX, 2, frameW - 3);
        int sx1 = Math.Clamp(lastBox.Right + expandX, 2, frameW - 3);
        int sy0 = Math.Clamp(lastBox.Y - expandY, 2, frameH - 3);
        int sy1 = Math.Clamp(lastBox.Bottom + expandY, 2, frameH - 3);

        double weightedX = 0;
        double weightedY = 0;
        double totalWeight = 0;

        for (int y = sy0; y <= sy1; y += 2)
        {
            int row = y * frameW;
            int prevRow = (y - 1) * frameW;
            int nextRow = (y + 1) * frameW;
            for (int x = sx0; x <= sx1; x += 2)
            {
                int idx = row + x;
                int gx = Math.Abs(grayPixels[idx + 1] - grayPixels[idx - 1]);
                int gy = Math.Abs(grayPixels[nextRow + x] - grayPixels[prevRow + x]);
                double w = gx + gy;
                if (w > 14)
                {
                    weightedX += x * w;
                    weightedY += y * w;
                    totalWeight += w;
                }
            }
        }

        if (totalWeight <= 10)
            return lastBox;

        int newCenterX = (int)Math.Round(weightedX / totalWeight);
        int newCenterY = (int)Math.Round(weightedY / totalWeight);

        int newX = Math.Clamp(newCenterX - (lastBox.Width / 2), 0, Math.Max(0, frameW - lastBox.Width));
        int newY = Math.Clamp(newCenterY - (lastBox.Height / 2), 0, Math.Max(0, frameH - lastBox.Height));
        return new Rectangle(newX, newY, lastBox.Width, lastBox.Height);
    }

    /// <summary>
    /// Calcula o centroide horizontal (U) e vertical (V) das feições internas (olhos, nariz e boca)
    /// e a assimetria de gradiente entre as metades da face. Usa passo de 1px automaticamente quando o rosto está distante (boxW < 110).
    /// </summary>
    private static (double FeatureCentroidU, double FeatureCentroidV, double CheekGradAsym) ComputeInternalFaceYawFeatures(
        byte[] grayPixels,
        int frameW,
        int frameH,
        int boxX,
        int boxY,
        int boxW,
        int boxH)
    {
        int x0 = Math.Clamp(boxX + (int)(boxW * 0.08), 1, frameW - 2);
        int x1 = Math.Clamp(boxX + (int)(boxW * 0.92), 1, frameW - 2);
        int y0 = Math.Clamp(boxY + (int)(boxH * 0.20), 1, frameH - 2);
        int y1 = Math.Clamp(boxY + (int)(boxH * 0.80), 1, frameH - 2);

        if (x1 <= x0 + 3 || y1 <= y0 + 3)
            return (0.50, 0.50, 0.0);

        // Quando o usuário está mais distante da câmera (rosto menor), analisa pixel a pixel (step = 1) para máxima precisão!
        int step = boxW < 110 ? 1 : 2;

        long lumSum = 0;
        int count = 0;
        for (int y = y0; y <= y1; y += step)
        {
            int rowOffset = y * frameW;
            for (int x = x0; x <= x1; x += step)
            {
                lumSum += grayPixels[rowOffset + x];
                count++;
            }
        }

        double meanLum = count > 0 ? (double)lumSum / count : 128.0;
        double weightedUSum = 0.0;
        double weightedVSum = 0.0;
        double weightSum = 0.0;
        double leftHalfGrad = 0.0;
        double rightHalfGrad = 0.0;
        int midX = boxX + (boxW / 2);

        for (int y = y0; y <= y1; y += step)
        {
            int rowOffset = y * frameW;
            int prevRow = (y - 1) * frameW;
            int nextRow = (y + 1) * frameW;
            double v = (double)(y - boxY) / boxH;

            for (int x = x0; x <= x1; x += step)
            {
                int idx = rowOffset + x;
                int gx = Math.Abs(grayPixels[idx + 1] - grayPixels[idx - 1]);
                int gy = Math.Abs(grayPixels[nextRow + x] - grayPixels[prevRow + x]);
                double grad = gx + 0.55 * gy;
                double darkSalience = Math.Max(0.0, meanLum - grayPixels[idx]);

                double weight = grad + 0.95 * darkSalience;
                double u = (double)(x - boxX) / boxW;

                weightedUSum += u * weight;
                weightedVSum += v * weight;
                weightSum += weight;

                if (x < midX)
                    leftHalfGrad += grad;
                else
                    rightHalfGrad += grad;
            }
        }

        double centroidU = weightSum > 1e-3 ? (weightedUSum / weightSum) : 0.50;
        double centroidV = weightSum > 1e-3 ? (weightedVSum / weightSum) : 0.50;
        double asym = (leftHalfGrad - rightHalfGrad) / (leftHalfGrad + rightHalfGrad + 1.0);
        return (centroidU, centroidV, asym);
    }

    public void ForceTestWarp(bool secondaryMonitor)
    {
        WarpCursorToMonitorCenter(secondaryMonitor, bypassMouseCheck: true);
    }

    private void WarpCursorToMonitorCenter(bool secondaryMonitor, bool bypassMouseCheck = false)
    {
        try
        {
            if (!bypassMouseCheck && Control.MouseButtons != MouseButtons.None)
                return;

            long now = Environment.TickCount64;
            if (!bypassMouseCheck && now - _lastWarpTick < 150)
                return;

            Screen[] screens = Screen.AllScreens;
            if (screens.Length < 2)
                return;

            Screen? targetScreen;
            if (secondaryMonitor)
            {
                targetScreen = screens.FirstOrDefault(s => !s.Primary) ?? screens[^1];
            }
            else
            {
                targetScreen = Screen.PrimaryScreen ?? screens.FirstOrDefault(s => s.Primary) ?? screens[0];
            }

            Rectangle b = targetScreen.Bounds;
            Point center = new(b.Left + (b.Width / 2), b.Top + (b.Height / 2));

            Screen currentScreen = Screen.FromPoint(Cursor.Position);
            if (bypassMouseCheck || secondaryMonitor || !string.Equals(currentScreen.DeviceName, targetScreen.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                Cursor.Position = center;
                WrapEngine?.NotifyExternalWarp(center);
                _rtAnchorInitialized = true;
                _rtAnchorX = center.X;
                _rtAnchorY = center.Y;
                _rtRefYaw = _rtSmoothYaw;
                _rtRefPitch = _rtSmoothPitch;
                _rtCurrentOffsetX = 0;
                _rtCurrentOffsetY = 0;
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

/// <summary>
/// Tela dedicada de Teste ao Vivo e Configuração do Head Motion Monitor:
/// Exibe o visor da câmera espelhado com marcação do rosto/nariz, medidor visual do sinal de giro (Yaw),
/// ganho automático de longa distância e sliders em tempo real de sensibilidade e alcance.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public sealed class HeadTrackerConfigWindow : System.Windows.Window
{
    private readonly HeadMonitorTracker _tracker;
    private readonly CursorLoopSettings _settings;
    private readonly Func<Task> _onToggleEnabledChanged;
    private readonly Func<Task>? _onToggleRealtimeMouseChanged;

    private readonly System.Windows.Controls.Image _cameraImage;
    private readonly System.Windows.Controls.Canvas _overlayCanvas;
    private readonly System.Windows.Controls.TextBlock _stateBadgeText;
    private readonly System.Windows.Controls.Border _stateBadgeBorder;
    private readonly System.Windows.Controls.TextBlock _telemetryDetailText;
    private readonly System.Windows.Controls.TextBlock _distanceBadgeText;

    private readonly System.Windows.Controls.Border _gaugeFillBar;
    private readonly System.Windows.Controls.Border _gaugeThresholdMarker;
    private readonly System.Windows.Controls.Grid _gaugeTrackGrid;

    private readonly System.Windows.Controls.Slider _sensitivitySlider;
    private readonly System.Windows.Controls.TextBlock _sensitivityValueText;
    private readonly System.Windows.Controls.Slider _distanceBoostSlider;
    private readonly System.Windows.Controls.TextBlock _distanceBoostValueText;
    private readonly System.Windows.Controls.Slider _thresholdSlider;
    private readonly System.Windows.Controls.TextBlock _thresholdValueText;
    private readonly System.Windows.Controls.Slider _smoothingSlider;
    private readonly System.Windows.Controls.TextBlock _smoothingValueText;
    private readonly System.Windows.Controls.Slider _realtimeSpeedSlider;
    private readonly System.Windows.Controls.TextBlock _realtimeSpeedValueText;
    private readonly System.Windows.Controls.Slider _touchLockSlider;
    private readonly System.Windows.Controls.TextBlock _touchLockValueText;

    private readonly System.Windows.Controls.CheckBox _enableTrackerCheck;
    private readonly System.Windows.Controls.CheckBox _enableRealtimeMouseCheck;
    private readonly System.Windows.Controls.CheckBox _returnPrimaryCheck;
    private readonly System.Windows.Controls.CheckBox _hdLongDistanceCheck;
    private readonly System.Windows.Controls.CheckBox _pauseWarpCheck;

    private System.Windows.Media.Imaging.WriteableBitmap? _writeableBmp;
    private bool _suppressEvents;
    private bool _startedTempPreview;

    public HeadTrackerConfigWindow(
        HeadMonitorTracker tracker,
        CursorLoopSettings settings,
        Func<Task> onToggleEnabledChanged,
        Func<Task>? onToggleRealtimeMouseChanged = null)
    {
        _tracker = tracker;
        _settings = settings;
        _onToggleEnabledChanged = onToggleEnabledChanged;
        _onToggleRealtimeMouseChanged = onToggleRealtimeMouseChanged;

        Title = "🎯 Teste e Configuração — Head Motion Monitor + Controle Realtime do Mouse";
        Width = 800;
        Height = 710;
        MinWidth = 740;
        MinHeight = 640;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 11, 14));
        Foreground = System.Windows.Media.Brushes.White;
        Topmost = true;

        var rootScroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Padding = new System.Windows.Thickness(18)
        };

        var mainStack = new System.Windows.Controls.StackPanel();

        // 1. Cabeçalho
        var titleBlock = new System.Windows.Controls.TextBlock
        {
            Text = "🎯 Teste ao Vivo e Configuração — Mouse p/ 2º Monitor com o Rosto",
            FontSize = 18,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 244, 245)),
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var subBlock = new System.Windows.Controls.TextBlock
        {
            Text = "Vire levemente o rosto para a direita e acompanhe a barra verde ultrapassar a linha de disparo. Ajuste a sensibilidade e o alcance para trabalhar mais distante da câmera.",
            FontSize = 12.5,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14)
        };
        mainStack.Children.Add(titleBlock);
        mainStack.Children.Add(subBlock);

        // 2. Painel Superior: Visor da Câmera (Esquerda) + Medidor e Botões de Teste (Direita)
        var topGrid = new System.Windows.Controls.Grid
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 14)
        };
        topGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(320) });
        topGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(14) });
        topGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        // Coluna 0: Visor Espelhado da Câmera
        var camCard = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 18, 22)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(10),
            Padding = new System.Windows.Thickness(8)
        };

        var camStack = new System.Windows.Controls.StackPanel();
        var camViewportBorder = new System.Windows.Controls.Border
        {
            Width = 300,
            Height = 210,
            Background = System.Windows.Media.Brushes.Black,
            CornerRadius = new System.Windows.CornerRadius(6),
            ClipToBounds = true
        };

        var camLayerGrid = new System.Windows.Controls.Grid
        {
            Width = 300,
            Height = 210
        };

        _cameraImage = new System.Windows.Controls.Image
        {
            Width = 300,
            Height = 210,
            Stretch = System.Windows.Media.Stretch.Fill,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new System.Windows.Media.ScaleTransform(-1, 1) // Espelhado natural p/ o usuário
        };

        _overlayCanvas = new System.Windows.Controls.Canvas
        {
            Width = 300,
            Height = 210,
            ClipToBounds = true
        };

        camLayerGrid.Children.Add(_cameraImage);
        camLayerGrid.Children.Add(_overlayCanvas);
        camViewportBorder.Child = camLayerGrid;

        _distanceBadgeText = new System.Windows.Controls.TextBlock
        {
            Text = "📏 Alcance: Aguardando câmera...",
            FontSize = 11.5,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            Margin = new System.Windows.Thickness(2, 6, 2, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        };

        camStack.Children.Add(camViewportBorder);
        camStack.Children.Add(_distanceBadgeText);
        camCard.Child = camStack;
        System.Windows.Controls.Grid.SetColumn(camCard, 0);
        topGrid.Children.Add(camCard);

        // Coluna 2: Medidor de Giro em Tempo Real + Ações Rápidas
        var meterCard = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 18, 22)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(10),
            Padding = new System.Windows.Thickness(14)
        };

        var meterStack = new System.Windows.Controls.StackPanel();

        _stateBadgeBorder = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            CornerRadius = new System.Windows.CornerRadius(8),
            Padding = new System.Windows.Thickness(12, 8, 12, 8),
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };
        _stateBadgeText = new System.Windows.Controls.TextBlock
        {
            Text = "🟢 MONITOR 1 (OLHANDO RETO)",
            FontSize = 14,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White
        };
        _stateBadgeBorder.Child = _stateBadgeText;
        meterStack.Children.Add(_stateBadgeBorder);

        var gaugeTitle = new System.Windows.Controls.TextBlock
        {
            Text = "Medidor de Giro para a Direita (Barra Verde = Sinal Atual | Linha Amarela = Disparo p/ 2º Monitor):",
            FontSize = 11.5,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 212, 216)),
            Margin = new System.Windows.Thickness(0, 0, 0, 6)
        };
        meterStack.Children.Add(gaugeTitle);

        _gaugeTrackGrid = new System.Windows.Controls.Grid
        {
            Height = 26,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 9, 11)),
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
            ClipToBounds = true
        };

        _gaugeFillBar = new System.Windows.Controls.Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Width = 0,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94)),
            CornerRadius = new System.Windows.CornerRadius(4)
        };

        _gaugeThresholdMarker = new System.Windows.Controls.Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Width = 3,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21)),
            Margin = new System.Windows.Thickness(100, 0, 0, 0)
        };

        _gaugeTrackGrid.Children.Add(_gaugeFillBar);
        _gaugeTrackGrid.Children.Add(_gaugeThresholdMarker);
        meterStack.Children.Add(_gaugeTrackGrid);

        _telemetryDetailText = new System.Windows.Controls.TextBlock
        {
            Text = "Sinal: 0.000 | Limiar de Disparo: 0.080 | Sensibilidade: 2.4x",
            FontSize = 11.5,
            FontWeight = System.Windows.FontWeights.Medium,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)),
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };
        meterStack.Children.Add(_telemetryDetailText);

        // Botões de ação na tela de teste
        var btnRow1 = new System.Windows.Controls.WrapPanel { Margin = new System.Windows.Thickness(0, 0, 0, 6) };
        var recalibBtn = CreateActionButton("🎯 Recalibrar Centro Agora (Olhe p/ 1º Monitor)", true);
        recalibBtn.Click += (_, _) =>
        {
            _tracker.ResetCalibration();
            _telemetryDetailText.Text = "Recalibrando posição neutra... olhe para o 1º monitor.";
        };

        var presetBtn = CreateActionButton("⚡ Preset: Ultra Sensível + Longa Distância", false);
        presetBtn.Click += (_, _) => ApplyUltraSensitiveLongRangePreset();

        btnRow1.Children.Add(recalibBtn);
        btnRow1.Children.Add(presetBtn);
        meterStack.Children.Add(btnRow1);

        var btnRow2 = new System.Windows.Controls.WrapPanel();
        var testMon2Btn = CreateActionButton("↗️ Testar Centro do 2º Monitor", false);
        testMon2Btn.Click += (_, _) => _tracker.ForceTestWarp(secondaryMonitor: true);

        var testMon1Btn = CreateActionButton("↙️ Testar Centro do 1º Monitor", false);
        testMon1Btn.Click += (_, _) => _tracker.ForceTestWarp(secondaryMonitor: false);

        btnRow2.Children.Add(testMon2Btn);
        btnRow2.Children.Add(testMon1Btn);
        meterStack.Children.Add(btnRow2);

        meterCard.Child = meterStack;
        System.Windows.Controls.Grid.SetColumn(meterCard, 2);
        topGrid.Children.Add(meterCard);

        mainStack.Children.Add(topGrid);

        // 2B. Card Dedicado em Destaque: Controle Espacial Realtime (Mouse RT + Fusão com Touchpad)
        var rtCard = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 24, 38)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            BorderThickness = new System.Windows.Thickness(1.5),
            CornerRadius = new System.Windows.CornerRadius(10),
            Padding = new System.Windows.Thickness(16, 12, 16, 12),
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };

        var rtStack = new System.Windows.Controls.StackPanel();

        var rtTitleRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 6) };
        var rtHeaderText = new System.Windows.Controls.TextBlock
        {
            Text = "🖱️ Controle Espacial Realtime (Mouse RT + Fusão com Touchpad)",
            FontSize = 14,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        rtTitleRow.Children.Add(rtHeaderText);
        rtStack.Children.Add(rtTitleRow);

        _enableRealtimeMouseCheck = CreateCheckBox(
            "🖱️ Ativar Modo Função Realtime (Guiar o mouse com o rosto + reposicionar e calibrar com o Touchpad • Começa Desativado)",
            _settings.HeadRealtimeMouseEnabled);
        _enableRealtimeMouseCheck.FontWeight = System.Windows.FontWeights.SemiBold;
        _enableRealtimeMouseCheck.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 244, 245));
        _enableRealtimeMouseCheck.Margin = new System.Windows.Thickness(0, 0, 0, 8);
        _enableRealtimeMouseCheck.Checked += async (_, _) => await HandleRealtimeMouseCheckChangedAsync(true);
        _enableRealtimeMouseCheck.Unchecked += async (_, _) => await HandleRealtimeMouseCheckChangedAsync(false);
        rtStack.Children.Add(_enableRealtimeMouseCheck);

        (_realtimeSpeedSlider, _realtimeSpeedValueText) = AddSliderRow(
            rtStack,
            "🎚️ Velocidade / Sensibilidade Espacial do Mouse RT (Padrão Ideal: 1.2x • Menor = mais suave | Maior = alcance mais rápido):",
            0.2, 4.0, _settings.HeadRealtimeMouseSpeed, "0.0x",
            val =>
            {
                _settings.HeadRealtimeMouseSpeed = Math.Round(val, 1);
                _settings.Save();
            });

        var rtPresetRow = new System.Windows.Controls.WrapPanel { Margin = new System.Windows.Thickness(0, 0, 0, 8) };
        var btnSpeedSoft = CreateActionButton("🐢 0.8x Mais Suave", false);
        btnSpeedSoft.Click += (_, _) => SetRealtimeSpeedSliderValue(0.8);
        var btnSpeedIdeal = CreateActionButton("✨ 1.2x Velocidade Ideal (Padrão)", true);
        btnSpeedIdeal.Click += (_, _) => SetRealtimeSpeedSliderValue(1.2);
        var btnSpeedFast = CreateActionButton("🚀 1.8x Mais Rápido", false);
        btnSpeedFast.Click += (_, _) => SetRealtimeSpeedSliderValue(1.8);
        rtPresetRow.Children.Add(btnSpeedSoft);
        rtPresetRow.Children.Add(btnSpeedIdeal);
        rtPresetRow.Children.Add(btnSpeedFast);
        rtStack.Children.Add(rtPresetRow);

        (_touchLockSlider, _touchLockValueText) = AddSliderRow(
            rtStack,
            "🤝 Prioridade do Touchpad ao Combinar (Trava após tocar no Touch p/ clicar sem interferência da câmera):",
            100, 600, _settings.HeadTouchLockMs, "0ms",
            val =>
            {
                _settings.HeadTouchLockMs = (int)Math.Round(val);
                _settings.Save();
            });

        rtCard.Child = rtStack;
        mainStack.Children.Add(rtCard);

        // 3. Painel de Sliders de Sensibilidade e Distância (Salto p/ 2º Monitor)
        var slidersCard = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 18, 22)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(10),
            Padding = new System.Windows.Thickness(16),
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };

        var slidersStack = new System.Windows.Controls.StackPanel();

        (_sensitivitySlider, _sensitivityValueText) = AddSliderRow(
            slidersStack,
            "1. Sensibilidade do Giro de Cabeça p/ 2º Monitor (Mais alto = exige menos rotação para a direita):",
            0.5, 4.5, _settings.HeadSensitivity, "0.0x",
            val =>
            {
                _settings.HeadSensitivity = Math.Round(val, 2);
                _settings.Save();
            });

        (_distanceBoostSlider, _distanceBoostValueText) = AddSliderRow(
            slidersStack,
            "2. Alcance / Ganho para Trabalhar Mais Distante (Amplifica quando você se afasta da câmera):",
            0.8, 3.5, _settings.HeadDistanceBoost, "0.0x",
            val =>
            {
                _settings.HeadDistanceBoost = Math.Round(val, 2);
                _settings.Save();
            });

        (_thresholdSlider, _thresholdValueText) = AddSliderRow(
            slidersStack,
            "3. Limiar de Disparo p/ 2º Monitor (Mais baixo = dispara com movimento menor):",
            0.025, 0.220, _settings.HeadEnterThreshold, "0.000",
            val =>
            {
                _settings.HeadEnterThreshold = Math.Round(val, 3);
                _settings.HeadReturnThreshold = Math.Round(Math.Max(0.010, val * 0.42), 3);
                _settings.Save();
                UpdateThresholdMarkerPosition();
            });

        (_smoothingSlider, _smoothingValueText) = AddSliderRow(
            slidersStack,
            "4. Velocidade de Resposta / Reação Imediata (Mais alto = reação mais rápida):",
            0.25, 0.92, _settings.HeadSmoothingAlpha, "0.00",
            val =>
            {
                _settings.HeadSmoothingAlpha = Math.Round(val, 2);
                _settings.Save();
            });

        // Checkboxes de configuração
        _enableTrackerCheck = CreateCheckBox(
            "☑ Ativar modo Head Motion Monitor (Mover mouse p/ centro do 2º monitor ao virar o rosto à direita)",
            _settings.HeadTrackerEnabled);
        _enableTrackerCheck.Checked += async (_, _) => await HandleEnableCheckChangedAsync(true);
        _enableTrackerCheck.Unchecked += async (_, _) => await HandleEnableCheckChangedAsync(false);

        _returnPrimaryCheck = CreateCheckBox(
            "☑ Retornar automaticamente o mouse p/ o centro do 1º monitor quando voltar a olhar reto",
            _settings.HeadReturnToPrimary);
        _returnPrimaryCheck.Checked += (_, _) => { if (!_suppressEvents) { _settings.HeadReturnToPrimary = true; _settings.Save(); } };
        _returnPrimaryCheck.Unchecked += (_, _) => { if (!_suppressEvents) { _settings.HeadReturnToPrimary = false; _settings.Save(); } };

        _hdLongDistanceCheck = CreateCheckBox(
            "☑ Modo Longa Distância HD (Captura em maior definição + rastreio de rosto pequeno/afastado)",
            _settings.HeadLongDistanceHdMode);
        _hdLongDistanceCheck.Checked += async (_, _) => await HandleHdModeChangedAsync(true);
        _hdLongDistanceCheck.Unchecked += async (_, _) => await HandleHdModeChangedAsync(false);

        _pauseWarpCheck = CreateCheckBox(
            "⏸️ Pausar o movimento do mouse apenas enquanto testo os gráficos nesta janela",
            false);
        _pauseWarpCheck.Checked += (_, _) => _tracker.PauseMouseWarpForTest = true;
        _pauseWarpCheck.Unchecked += (_, _) => _tracker.PauseMouseWarpForTest = false;

        slidersStack.Children.Add(_enableTrackerCheck);
        slidersStack.Children.Add(_returnPrimaryCheck);
        slidersStack.Children.Add(_hdLongDistanceCheck);
        slidersStack.Children.Add(_pauseWarpCheck);

        slidersCard.Child = slidersStack;
        mainStack.Children.Add(slidersCard);

        rootScroll.Content = mainStack;
        Content = rootScroll;

        Loaded += async (_, _) =>
        {
            UpdateThresholdMarkerPosition();
            _tracker.IncludePreviewFrame = true;
            _tracker.OnTelemetryFrame += HandleTelemetryFrame;

            if (!_tracker.IsRunning)
            {
                _startedTempPreview = true;
                await _tracker.StartAsync();
            }
        };

        Closed += async (_, _) =>
        {
            _tracker.IncludePreviewFrame = false;
            _tracker.PauseMouseWarpForTest = false;
            _tracker.OnTelemetryFrame -= HandleTelemetryFrame;

            if (_startedTempPreview && !_settings.HeadTrackerEnabled && !_settings.HeadRealtimeMouseEnabled)
            {
                await _tracker.StopAsync();
            }
        };
    }

    public void SyncCheckboxesFromSettings()
    {
        _suppressEvents = true;
        try
        {
            _enableTrackerCheck.IsChecked = _settings.HeadTrackerEnabled;
            _enableRealtimeMouseCheck.IsChecked = _settings.HeadRealtimeMouseEnabled;
            _realtimeSpeedSlider.Value = _settings.HeadRealtimeMouseSpeed;
            _realtimeSpeedValueText.Text = _settings.HeadRealtimeMouseSpeed.ToString("0.0x", CultureInfo.InvariantCulture);
            _touchLockSlider.Value = _settings.HeadTouchLockMs;
            _touchLockValueText.Text = _settings.HeadTouchLockMs.ToString("0ms", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void SetRealtimeSpeedSliderValue(double speed)
    {
        _suppressEvents = true;
        try
        {
            double rounded = Math.Round(Math.Clamp(speed, 0.2, 4.0), 1);
            _settings.HeadRealtimeMouseSpeed = rounded;
            _settings.Save();
            _realtimeSpeedSlider.Value = rounded;
            _realtimeSpeedValueText.Text = rounded.ToString("0.0x", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private async Task HandleEnableCheckChangedAsync(bool enabled)
    {
        if (_suppressEvents)
            return;

        if (_settings.HeadTrackerEnabled != enabled)
        {
            _startedTempPreview = false;
            await _onToggleEnabledChanged();
            if (!_tracker.IsRunning)
            {
                _startedTempPreview = true;
                await _tracker.StartAsync();
            }
        }
    }

    private async Task HandleRealtimeMouseCheckChangedAsync(bool enabled)
    {
        if (_suppressEvents)
            return;

        if (_settings.HeadRealtimeMouseEnabled != enabled)
        {
            _startedTempPreview = false;
            if (_onToggleRealtimeMouseChanged != null)
            {
                await _onToggleRealtimeMouseChanged();
            }
            else
            {
                _settings.ToggleHeadRealtimeMouseOnBar();
            }

            if (!_tracker.IsRunning)
            {
                _startedTempPreview = true;
                await _tracker.StartAsync();
            }
        }
    }

    private async Task HandleHdModeChangedAsync(bool hdEnabled)
    {
        if (_suppressEvents)
            return;

        _settings.HeadLongDistanceHdMode = hdEnabled;
        _settings.Save();
        if (_tracker.IsRunning)
        {
            await _tracker.StopAsync();
            await _tracker.StartAsync();
        }
    }

    private void ApplyUltraSensitiveLongRangePreset()
    {
        _suppressEvents = true;
        try
        {
            _settings.HeadSensitivity = 3.1;
            _settings.HeadDistanceBoost = 2.5;
            _settings.HeadEnterThreshold = 0.065;
            _settings.HeadReturnThreshold = 0.026;
            _settings.HeadSmoothingAlpha = 0.72;
            _settings.HeadConfirmFrames = 1;
            _settings.HeadLongDistanceHdMode = true;
            _settings.Save();

            _sensitivitySlider.Value = _settings.HeadSensitivity;
            _sensitivityValueText.Text = $"{_settings.HeadSensitivity:0.0}x";

            _distanceBoostSlider.Value = _settings.HeadDistanceBoost;
            _distanceBoostValueText.Text = $"{_settings.HeadDistanceBoost:0.0}x";

            _thresholdSlider.Value = _settings.HeadEnterThreshold;
            _thresholdValueText.Text = $"{_settings.HeadEnterThreshold:0.000}";

            _smoothingSlider.Value = _settings.HeadSmoothingAlpha;
            _smoothingValueText.Text = $"{_settings.HeadSmoothingAlpha:0.00}";

            _hdLongDistanceCheck.IsChecked = true;
            UpdateThresholdMarkerPosition();
            _tracker.ResetCalibration();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void UpdateThresholdMarkerPosition()
    {
        double trackW = _gaugeTrackGrid.ActualWidth > 20 ? _gaugeTrackGrid.ActualWidth : 380;
        double maxScale = 0.26;
        double ratio = Math.Clamp(_settings.HeadEnterThreshold / maxScale, 0.05, 0.95);
        _gaugeThresholdMarker.Margin = new System.Windows.Thickness(ratio * trackW, 0, 0, 0);
    }

    private void HandleTelemetryFrame(HeadTrackerTelemetry t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (t.GrayPixels != null && t.FrameWidth > 0 && t.FrameHeight > 0)
                {
                    if (_writeableBmp == null || _writeableBmp.PixelWidth != t.FrameWidth || _writeableBmp.PixelHeight != t.FrameHeight)
                    {
                        _writeableBmp = new System.Windows.Media.Imaging.WriteableBitmap(
                            t.FrameWidth,
                            t.FrameHeight,
                            96,
                            96,
                            System.Windows.Media.PixelFormats.Gray8,
                            null);
                        _cameraImage.Source = _writeableBmp;
                    }

                    _writeableBmp.WritePixels(
                        new System.Windows.Int32Rect(0, 0, t.FrameWidth, t.FrameHeight),
                        t.GrayPixels,
                        t.FrameWidth,
                        0);

                    DrawCameraOverlay(t);
                }

                if (!t.FaceDetected)
                {
                    _stateBadgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 53, 15));
                    _stateBadgeText.Text = "⚠️ AGUARDANDO ROSTO NA CÂMERA...";
                    _distanceBadgeText.Text = "📏 Enquadre seu rosto na webcam para calibrar";
                }
                else if (!t.IsCalibrated)
                {
                    _stateBadgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 58, 138));
                    _stateBadgeText.Text = "⏳ CALIBRANDO CENTRO (OLHE P/ 1º MONITOR)...";
                }
                else if (t.TouchRecalibratedRecently)
                {
                    _stateBadgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 148, 136));
                    _stateBadgeText.Text = "✨ CALIBRADO PELO TOUCH • RT ESPACIAL REPOSICIONADO!";
                }
                else if (t.IsLookingRight)
                {
                    _stateBadgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61));
                    _stateBadgeText.Text = "🎯 ROSTO P/ DIREITA ➔ MOUSE NO 2º MONITOR!";
                }
                else
                {
                    _stateBadgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59));
                    _stateBadgeText.Text = "🟢 OLHANDO RETO (1º MONITOR PRINCIPAL)";
                }

                string distLabel = t.DistanceGainApplied >= 1.55
                    ? $"Longa Distância Ativa (Boost Automático {t.DistanceGainApplied:0.0}x)"
                    : $"Distância Normal (Boost {t.DistanceGainApplied:0.0}x)";
                if (t.IsFallbackLongRangeRoi)
                    distLabel += " • [Rastreio de Perfil/Longe]";
                _distanceBadgeText.Text = $"📏 {distLabel} • Ganho Total: {t.TotalGainApplied:0.1f}x";

                double trackW = _gaugeTrackGrid.ActualWidth > 20 ? _gaugeTrackGrid.ActualWidth : 380;
                double maxScale = 0.26;
                double fillRatio = Math.Clamp(Math.Max(0, t.SmoothedYawRight) / maxScale, 0.0, 1.0);
                _gaugeFillBar.Width = fillRatio * trackW;
                _gaugeFillBar.Background = t.SmoothedYawRight >= t.EnterThreshold
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));

                double threshRatio = Math.Clamp(t.EnterThreshold / maxScale, 0.05, 0.95);
                _gaugeThresholdMarker.Margin = new System.Windows.Thickness(threshRatio * trackW, 0, 0, 0);

                _telemetryDetailText.Text =
                    $"X: {t.SmoothedYawRight:+0.000;-0.000;0.000} | Y: {t.SmoothedPitchDown:+0.000;-0.000;0.000} | Offset RT: ({t.SpatialOffsetX:+0;-0;0}px, {t.SpatialOffsetY:+0;-0;0}px) | Mover o Touchpad recalibra o RT";
            }
            catch { }
        });
    }

    private void DrawCameraOverlay(HeadTrackerTelemetry t)
    {
        _overlayCanvas.Children.Clear();
        double cw = _overlayCanvas.Width;
        double ch = _overlayCanvas.Height;

        // Cruz de referência espacial calibrada (alinha exatamente na bolinha amarela do nariz ao calibrar ou mover o Touchpad)
        double scaleX = t.FrameWidth > 0 ? (cw / t.FrameWidth) : 1.0;
        double scaleY = t.FrameHeight > 0 ? (ch / t.FrameHeight) : 1.0;
        double calibBoxW = t.FaceDetected && t.FaceBox.Width > 0 ? (t.FaceBox.Width * scaleX) : 0;
        double calibBoxH = t.FaceDetected && t.FaceBox.Height > 0 ? (t.FaceBox.Height * scaleY) : 0;

        double mirroredBaseX = calibBoxW > 0
            ? Math.Clamp(cw - ((t.BaselineX * cw) + (t.BaselineU - 0.5) * calibBoxW), 0, cw)
            : (1.0 - t.BaselineX) * cw;
        double baseY = calibBoxH > 0
            ? Math.Clamp((t.BaselineY * ch) + (t.BaselineV - 0.5) * calibBoxH, 0, ch)
            : t.BaselineY * ch;

        var crossColor = t.TouchRecalibratedRecently
            ? System.Windows.Media.Color.FromArgb(230, 45, 212, 191)
            : System.Windows.Media.Color.FromArgb(170, 148, 163, 184);

        var baseLineX = new System.Windows.Shapes.Line
        {
            X1 = mirroredBaseX,
            Y1 = 0,
            X2 = mirroredBaseX,
            Y2 = ch,
            Stroke = new System.Windows.Media.SolidColorBrush(crossColor),
            StrokeThickness = t.TouchRecalibratedRecently ? 2.0 : 1.5,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 }
        };
        _overlayCanvas.Children.Add(baseLineX);

        var baseLineY = new System.Windows.Shapes.Line
        {
            X1 = 0,
            Y1 = baseY,
            X2 = cw,
            Y2 = baseY,
            Stroke = new System.Windows.Media.SolidColorBrush(crossColor),
            StrokeThickness = t.TouchRecalibratedRecently ? 2.0 : 1.2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 }
        };
        _overlayCanvas.Children.Add(baseLineY);

        if (!t.FaceDetected || t.FaceBox.Width <= 0 || t.FrameWidth <= 0 || t.FrameHeight <= 0)
            return;

        double rawLeft = t.FaceBox.X * scaleX;
        double boxW = t.FaceBox.Width * scaleX;
        double mirroredLeft = cw - (rawLeft + boxW);
        double boxTop = t.FaceBox.Y * scaleY;
        double boxH = t.FaceBox.Height * scaleY;

        var boxColor = t.IsLookingRight
            ? System.Windows.Media.Color.FromRgb(34, 197, 94)
            : (t.IsFallbackLongRangeRoi
                ? System.Windows.Media.Color.FromRgb(245, 158, 11)
                : System.Windows.Media.Color.FromRgb(56, 189, 248));

        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(8, boxW),
            Height = Math.Max(8, boxH),
            Stroke = new System.Windows.Media.SolidColorBrush(boxColor),
            StrokeThickness = 2.2,
            RadiusX = 5,
            RadiusY = 5
        };
        System.Windows.Controls.Canvas.SetLeft(rect, mirroredLeft);
        System.Windows.Controls.Canvas.SetTop(rect, boxTop);
        _overlayCanvas.Children.Add(rect);

        // Ponto 2D do centroide do nariz/feições espelhado (X e Y)
        double noseMirroredX = mirroredLeft + (1.0 - t.FeatureCentroidU) * boxW;
        double noseY = boxTop + (t.FeatureCentroidV * boxH);
        var noseDot = new System.Windows.Shapes.Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21)),
            Stroke = System.Windows.Media.Brushes.Black,
            StrokeThickness = 1.2
        };
        System.Windows.Controls.Canvas.SetLeft(noseDot, noseMirroredX - 4.5);
        System.Windows.Controls.Canvas.SetTop(noseDot, noseY - 4.5);
        _overlayCanvas.Children.Add(noseDot);
    }

    private (System.Windows.Controls.Slider Slider, System.Windows.Controls.TextBlock ValueLabel) AddSliderRow(
        System.Windows.Controls.StackPanel parent,
        string label,
        double min,
        double max,
        double initial,
        string format,
        Action<double> onChanged)
    {
        var rowHeader = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 3) };
        var valText = new System.Windows.Controls.TextBlock
        {
            Text = initial.ToString(format, CultureInfo.InvariantCulture),
            FontSize = 12.5,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248))
        };
        System.Windows.Controls.DockPanel.SetDock(valText, System.Windows.Controls.Dock.Right);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(228, 228, 231))
        };
        rowHeader.Children.Add(valText);
        rowHeader.Children.Add(titleText);

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(initial, min, max),
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        slider.ValueChanged += (_, e) =>
        {
            if (_suppressEvents)
                return;
            valText.Text = e.NewValue.ToString(format, CultureInfo.InvariantCulture);
            onChanged(e.NewValue);
        };

        parent.Children.Add(rowHeader);
        parent.Children.Add(slider);
        return (slider, valText);
    }

    private static System.Windows.Controls.CheckBox CreateCheckBox(string text, bool isChecked)
    {
        return new System.Windows.Controls.CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(228, 228, 231)),
            FontSize = 12,
            Margin = new System.Windows.Thickness(0, 4, 0, 4),
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    private static System.Windows.Controls.Button CreateActionButton(string text, bool primary)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new System.Windows.Thickness(0),
            FontSize = 11.5,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Padding = new System.Windows.Thickness(10, 6, 10, 6),
            Margin = new System.Windows.Thickness(0, 0, 6, 4)
        };
    }
}





