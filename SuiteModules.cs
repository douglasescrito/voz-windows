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
        if (!Enabled)
            return 1.0;

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

    public string GetModePillText()
    {
        if (!Enabled)
            return "⚪ Loop OFF";

        double mult = GetEffectiveMultiplier();
        return $"🟢 Loop {mult.ToString("0.0", CultureInfo.InvariantCulture)}x";
    }

    public void CycleModeOnBar()
    {
        // Cycles: Enabled 1.8x (Level 1) -> Enabled 2.8x (Level 2) -> Enabled 4.5x (Level 3) -> OFF -> Enabled 1.8x
        UseCustomMultiplier = false;
        if (!Enabled)
        {
            Enabled = true;
            SpeedLevel = 1;
        }
        else if (SpeedLevel == 1)
        {
            SpeedLevel = 2;
        }
        else if (SpeedLevel == 2)
        {
            SpeedLevel = 3;
        }
        else
        {
            Enabled = false;
            SpeedLevel = 0;
        }
        Save();
    }

    public void SetLevelDirect(int level)
    {
        UseCustomMultiplier = false;
        if (level <= 0)
        {
            Enabled = false;
            SpeedLevel = 0;
        }
        else
        {
            Enabled = true;
            SpeedLevel = Math.Clamp(level, 1, 3);
        }
        Save();
    }

    public static CursorLoopSettings Load()
    {
        // Em execucao portatil (computadores de terceiros), sempre inicia com o CursorLoop DESATIVADO
        // para nunca interferir no mouse fisico nem teleportar o cursor na borda da tela.
        if (!IsOwnerWorkspaceMachine())
        {
            return new CursorLoopSettings
            {
                Enabled = false,
                SpeedLevel = 0,
                HorizontalLoop = false,
                VerticalLoop = false
            };
        }

        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<CursorLoopSettings>(json);
                if (loaded is not null)
                {
                    if (loaded.SpeedLevel == 0 && !loaded.UseCustomMultiplier)
                        loaded.SpeedLevel = 1;
                    return loaded;
                }
            }
        }
        catch { }
        return new CursorLoopSettings { Enabled = true, SpeedLevel = 1 };
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
            if (!IsOwnerWorkspaceMachine())
            {
                key.DeleteValue(RunValueName, false);
                return;
            }
            key.SetValue(RunValueName, $"\"{exePath}\"");
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

    public void Tick(bool enabled, bool horizontal, bool vertical)
    {
        if (!enabled)
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

        double multiplier = SpeedMultiplier;
        if (multiplier > 1.001 && now - _lastWarp >= 40)
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
                Screen? sourceScreen = _screens.FirstOrDefault(s => s.Bounds.Contains(point))
                                    ?? _screens.FirstOrDefault(s => s.Bounds.Contains(previous));

                int targetX = point.X + boostX;
                int targetY = point.Y + boostY;

                if (sourceScreen is not null)
                {
                    if (horizontal)
                        targetX = Math.Clamp(targetX, sourceScreen.Bounds.Left, sourceScreen.Bounds.Right - 1);
                    if (vertical)
                        targetY = Math.Clamp(targetY, sourceScreen.Bounds.Top, sourceScreen.Bounds.Bottom - 1);
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
        if (now - _lastWarp < 110) return;

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


