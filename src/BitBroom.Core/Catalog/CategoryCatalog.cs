using BitBroom.Core.Engine;
using BitBroom.Core.Special;
using Microsoft.Win32;

namespace BitBroom.Core.Catalog;

/// <summary>
/// The complete set of cleaning categories BitBroom knows about.
/// Every path here is grounded in vendor documentation, Microsoft guidance, or the
/// community Winapp2/BleachBit databases — see docs/CATEGORIES.md for the per-category
/// rationale and sources, and docs/SAFETY.md for what BitBroom deliberately never touches.
/// </summary>
public static class CategoryCatalog
{
    public static IReadOnlyList<CleanCategory> Build()
    {
        var categories = new List<CleanCategory>();

        // =====================================================================
        // SYSTEM
        // =====================================================================

        categories.Add(new CleanCategory
        {
            Id = "user-temp",
            Name = "User temporary files",
            Description = "Your account's %TEMP% folder. Applications drop scratch files here and rarely clean up. Files newer than the minimum age are kept so running installers are never disturbed.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = "Temp" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "windows-temp",
            Name = "Windows temporary files",
            Description = @"The system-wide C:\Windows\Temp folder used by services and installers running as SYSTEM.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            RequiresAdmin = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "Temp" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "windows-update-cache",
            Name = "Windows Update download cache",
            Description = @"Downloaded update payloads in C:\Windows\SoftwareDistribution\Download. Safe once updates are installed; Windows re-downloads anything it still needs. Files in use by an active update are skipped automatically.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            RequiresAdmin = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = @"SoftwareDistribution\Download" }.Validate(),
            ],
        });

        categories.Add(DeliveryOptimizationCategory.Create());

        categories.Add(new CleanCategory
        {
            Id = "crash-dumps",
            Name = "Crash dumps & kernel reports",
            Description = @"Memory dumps from crashes and driver timeouts: %LocalAppData%\CrashDumps, C:\Windows\Minidump, C:\Windows\memory.dmp and C:\Windows\LiveKernelReports (WATCHDOG dumps here regularly reach many GB). Only needed if you are actively debugging a crash.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            RequiresAdmin = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = "CrashDumps" }.Validate(),
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "Minidump", FilePatterns = ["*.dmp"] }.Validate(),
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "LiveKernelReports", FilePatterns = ["*.dmp"] }.Validate(),
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "", Kind = RuleKind.FixedFiles, FilePatterns = ["memory.dmp"] }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "windows-error-reporting",
            Name = "Windows Error Reporting",
            Description = @"Queued and archived crash reports (WER) under ProgramData and your profile. Diagnostic leftovers Microsoft has usually already received.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            RequiresAdmin = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.ProgramData, RelativePattern = @"Microsoft\Windows\WER\ReportQueue" }.Validate(),
                new CleanRule { Base = KnownBase.ProgramData, RelativePattern = @"Microsoft\Windows\WER\ReportArchive" }.Validate(),
                new CleanRule { Base = KnownBase.ProgramData, RelativePattern = @"Microsoft\Windows\WER\Temp" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Windows\WER\ReportQueue" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Windows\WER\ReportArchive" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Windows\WER\Temp" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "windows-logs",
            Name = "Windows log files",
            Description = @"Setup, servicing and diagnostic logs: C:\Windows\Logs (CBS, DISM, MoSetup, waasmedic…), C:\Windows\Panther, C:\Windows\Debug and update-service logs. CBS logs alone can balloon to gigabytes. Logs from the last 7 days are kept.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            RequiresAdmin = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "Logs", MinAgeHoursOverride = 168, FilePatterns = ["*.log", "*.cab", "*.etl", "*.txt", "*.xml"] }.Validate(),
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "Panther", MinAgeHoursOverride = 168, FilePatterns = ["*.log", "*.xml", "*.etl"] }.Validate(),
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "Debug", MinAgeHoursOverride = 168, FilePatterns = ["*.log"] }.Validate(),
                new CleanRule { Base = KnownBase.ProgramData, RelativePattern = @"USOShared\Logs", MinAgeHoursOverride = 168 }.Validate(),
                new CleanRule { Base = KnownBase.SystemRoot, RelativePattern = "", Kind = RuleKind.FixedFiles, MinAgeHoursOverride = 168, FilePatterns = ["WindowsUpdate.log", "setupact.log", "setuperr.log", "PFRO.log"] }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "defender-logs",
            Name = "Microsoft Defender logs",
            Description = @"Defender's plain-text diagnostic logs (MPLog etc.) in ProgramData\Microsoft\Windows Defender\Support — a well-known multi-GB offender. The active log is locked by the service and skipped automatically. Detection history and quarantine are NOT touched.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            RequiresAdmin = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.ProgramData, RelativePattern = @"Microsoft\Windows Defender\Support", MinAgeHoursOverride = 168, FilePatterns = ["*.log"] }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "thumbnail-cache",
            Name = "Thumbnail & icon caches",
            Description = @"Explorer's thumbcache/iconcache databases. Windows rebuilds them; the first folder views afterwards regenerate thumbnails. Files locked by Explorer are skipped (use Tools → Restart Explorer to release them).",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Windows\Explorer", FilePatterns = ["thumbcache_*.db", "iconcache_*.db"], MinAgeHoursOverride = 0, Recurse = false }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "directx-shader-cache",
            Name = "DirectX shader cache",
            Description = @"Windows' own compiled-shader cache (%LocalAppData%\D3DSCache). Rebuilt automatically; clearing it is Microsoft's stock fix for post-driver-update stutter.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = "D3DSCache", MinAgeHoursOverride = 0 }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "inet-cache",
            Name = "Windows Internet cache (INetCache)",
            Description = @"The legacy WinINET cache still used by Office, Explorer and many desktop apps for downloads and web content.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Windows\INetCache\IE" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Windows\INetCache\Content.Outlook" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "store-apps-temp",
            Name = "Store apps temporary files",
            Description = @"TempState folders of Microsoft Store / packaged apps. The platform contract lets Windows purge these at any time, so apps must tolerate it. App settings and data (LocalState) are NOT touched.",
            Group = CategoryGroup.System,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\*\TempState" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\*\AC\Temp" }.Validate(),
            ],
        });

        categories.Add(RecycleBinCategory.Create());

        // =====================================================================
        // BROWSERS — caches only. Cookies, history, passwords, sessions and
        // IndexedDB/site data are never touched (see docs/SAFETY.md).
        // =====================================================================

        AddChromiumBrowser(categories, "chrome-cache", "Google Chrome cache", @"Google\Chrome\User Data");
        AddChromiumBrowser(categories, "edge-cache", "Microsoft Edge cache", @"Microsoft\Edge\User Data");
        AddChromiumBrowser(categories, "brave-cache", "Brave cache", @"BraveSoftware\Brave-Browser\User Data");
        AddChromiumBrowser(categories, "vivaldi-cache", "Vivaldi cache", @"Vivaldi\User Data");
        AddChromiumBrowser(categories, "opera-cache", "Opera cache", @"Opera Software\Opera Stable", profilesAreFlat: true);
        AddChromiumBrowser(categories, "opera-gx-cache", "Opera GX cache", @"Opera Software\Opera GX Stable", profilesAreFlat: true);
        AddChromiumBrowser(categories, "chromium-cache", "Chromium cache", @"Chromium\User Data");

        categories.Add(new CleanCategory
        {
            Id = "firefox-cache",
            Name = "Firefox cache",
            Description = "HTTP cache, startup cache and shader cache for all Firefox profiles. Bookmarks, history, cookies and sessions are untouched.",
            Group = CategoryGroup.Browsers,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Mozilla\Firefox\Profiles\*\cache2", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Mozilla\Firefox\Profiles\*\startupCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Mozilla\Firefox\Profiles\*\shader-cache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Mozilla\Firefox\Profiles\*\jumpListCache", MinAgeHoursOverride = 0 }.Validate(),
            ],
        });

        // =====================================================================
        // APPLICATIONS
        // =====================================================================

        AddElectronApp(categories, "discord-cache", "Discord cache", KnownBase.RoamingAppData, "discord",
            "Media, code and GPU caches. Routinely 1–10 GB for active users. Login and servers unaffected.");
        AddElectronApp(categories, "slack-cache", "Slack cache", KnownBase.RoamingAppData, "Slack",
            "Slack's Electron caches. Workspaces and sign-in unaffected.");

        categories.Add(new CleanCategory
        {
            Id = "teams-cache",
            Name = "Microsoft Teams cache",
            Description = "Cache folders of new Teams (packaged) and classic Teams. Chats re-sync from the cloud on next launch; sign-in is preserved.",
            Group = CategoryGroup.Applications,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                // New Teams (MSTeams package) — official cache location per Microsoft Learn.
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\EBWebView\Default\Code Cache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\EBWebView\Default\GPUCache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\Logs" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\PerfLogs" }.Validate(),
                // Classic Teams (EOL but still installed in many places).
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Microsoft\Teams\Cache" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Microsoft\Teams\Code Cache" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Microsoft\Teams\GPUCache" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Microsoft\Teams\Service Worker\CacheStorage" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Microsoft\Teams\tmp" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Microsoft\Teams\logs" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "spotify-cache",
            Name = "Spotify cache",
            Description = "Spotify's streaming cache (grows ~10 GB every couple of weeks for heavy listeners). Downloaded/offline songs (LocalState\\Spotify\\Storage) and login are NOT touched.",
            Group = CategoryGroup.Applications,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                // Store version — cache only; offline songs live under LocalState and are excluded.
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\SpotifyAB.SpotifyMusic_*\LocalCache\Spotify\Data" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\SpotifyAB.SpotifyMusic_*\LocalCache\Spotify\Browser" }.Validate(),
                // Desktop-installer version.
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Spotify\Data" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Spotify\Browser" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "adobe-media-cache",
            Name = "Adobe media cache",
            Description = "Premiere Pro / After Effects / Media Encoder conformed-audio and peak files (Media Cache, Media Cache Files, Peak Files). A notorious 10–100+ GB sink. Rebuilt on demand when projects reopen; close Adobe apps first for a full clean.",
            Group = CategoryGroup.Applications,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Adobe\Common\Media Cache Files" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Adobe\Common\Media Cache" }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"Adobe\Common\Peak Files" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "whatsapp-cache",
            Name = "WhatsApp Desktop cache",
            Description = "Packaged WhatsApp temporary/cache data. Chats live on your phone/cloud and re-sync.",
            Group = CategoryGroup.Applications,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\5319275A.WhatsAppDesktop_*\LocalCache" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "office-cache",
            Name = "Office web & document caches",
            Description = "Office's WEF (web add-in) caches and Outlook attachment temp. The Office Document Cache (unsynced cloud edits) is deliberately NOT touched.",
            Group = CategoryGroup.Applications,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Office\16.0\Wef" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\Office\16.0\WebServiceCache" }.Validate(),
            ],
        });

        // =====================================================================
        // GAMING & GPU
        // =====================================================================

        categories.Add(new CleanCategory
        {
            Id = "nvidia-caches",
            Name = "NVIDIA shader & installer caches",
            Description = @"DXCache/GLCache/OptixCache/ComputeCache, NV_Cache, driver downloader cache and C:\NVIDIA installer extractions. NVIDIA's own guidance: safe to delete; drivers rebuild caches on next launch.",
            Group = CategoryGroup.GamingAndGpu,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NVIDIA\DXCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NVIDIA\GLCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NVIDIA\OptixCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.RoamingAppData, RelativePattern = @"NVIDIA\ComputeCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NVIDIA Corporation\NV_Cache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.ProgramData, RelativePattern = @"NVIDIA Corporation\Downloader" }.Validate(),
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "NVIDIA" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "amd-caches",
            Name = "AMD shader & installer caches",
            Description = @"AMD's DxCache/DxcCache/GLCache/VkCache shader caches and C:\AMD installer leftovers.",
            Group = CategoryGroup.GamingAndGpu,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"AMD\DxCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"AMD\DxcCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"AMD\GLCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"AMD\VkCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "AMD" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "intel-shader-cache",
            Name = "Intel shader cache",
            Description = "Intel graphics shader cache folders.",
            Group = CategoryGroup.GamingAndGpu,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Intel\ShaderCache", MinAgeHoursOverride = 0 }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "steam-caches",
            Name = "Steam caches",
            Description = "Steam's per-game shader cache, aborted-download staging (steamapps\\downloading), temp and the client web cache. Installed games, saves and workshop content are NOT touched.",
            Group = CategoryGroup.GamingAndGpu,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Steam\htmlcache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule
                {
                    Base = KnownBase.Custom,
                    CustomBaseProvider = GetSteamPath,
                    RelativePattern = @"steamapps\shadercache",
                    MinAgeHoursOverride = 0,
                }.Validate(),
                new CleanRule
                {
                    Base = KnownBase.Custom,
                    CustomBaseProvider = GetSteamPath,
                    RelativePattern = @"steamapps\downloading",
                }.Validate(),
                new CleanRule
                {
                    Base = KnownBase.Custom,
                    CustomBaseProvider = GetSteamPath,
                    RelativePattern = @"steamapps\temp",
                }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "epic-cache",
            Name = "Epic Games Launcher cache",
            Description = "Epic launcher web cache and logs. Games and saves untouched.",
            Group = CategoryGroup.GamingAndGpu,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"EpicGamesLauncher\Saved\webcache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"EpicGamesLauncher\Saved\webcache_4430", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"EpicGamesLauncher\Saved\Logs", MinAgeHoursOverride = 168 }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "ea-cache",
            Name = "EA app cache",
            Description = "EA Desktop app cache and logs.",
            Group = CategoryGroup.GamingAndGpu,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Electronic Arts\EA Desktop\cache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Electronic Arts\EA Desktop\Logs", MinAgeHoursOverride = 168 }.Validate(),
            ],
        });

        // =====================================================================
        // DEVELOPMENT
        // =====================================================================

        categories.Add(new CleanCategory
        {
            Id = "npm-yarn-cache",
            Name = "npm / Yarn cache",
            Description = "Package download caches (%LocalAppData%\\npm-cache, Yarn\\Cache). Re-downloaded on demand; node_modules folders in your projects are NOT touched.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = "npm-cache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Yarn\Cache" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "pip-uv-cache",
            Name = "pip / uv cache",
            Description = "Python package caches (%LocalAppData%\\pip\\cache, %LocalAppData%\\uv\\cache). Installed environments are NOT touched.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"pip\cache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"uv\cache" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "nuget-http-cache",
            Name = "NuGet HTTP & temp cache",
            Description = "NuGet's HTTP response cache and plugin cache. The global-packages folder (restored packages) is a separate, off-by-default category.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NuGet\v3-cache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NuGet\http-cache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"NuGet\plugins-cache" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "nuget-global-packages",
            Name = "NuGet global packages",
            Description = "All restored NuGet packages (%UserProfile%\\.nuget\\packages). Safe but heavy: every project restores again on next build. Enable when you need serious space back.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Moderate,
            EnabledByDefault = false,
            Rules =
            [
                new CleanRule { Base = KnownBase.UserProfile, RelativePattern = @".nuget\packages" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "vs-code-cache",
            Name = "VS Code / Cursor caches",
            Description = "Editor caches (Cache, CachedData, Code Cache, GPUCache) and old logs for VS Code, VS Code Insiders, Cursor and VSCodium. Settings and extensions untouched.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules = BuildVsCodeRules(),
        });

        categories.Add(new CleanCategory
        {
            Id = "visual-studio-cache",
            Name = "Visual Studio caches",
            Description = "ComponentModelCache (MEF cache — VS rebuilds it on start, the classic fix for VS weirdness) and stale WebsiteCache.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\VisualStudio\1*\ComponentModelCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Microsoft\WebsiteCache" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "jetbrains-logs",
            Name = "JetBrains IDE logs",
            Description = "Log folders of IntelliJ-platform IDEs (%LocalAppData%\\JetBrains\\<product>\\log). IDE settings and caches for the current version are untouched.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"JetBrains\*\log", MinAgeHoursOverride = 168 }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "gradle-cache",
            Name = "Gradle caches",
            Description = "Gradle's dependency and build caches (%UserProfile%\\.gradle\\caches). Projects re-download dependencies on next build.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Moderate,
            EnabledByDefault = false,
            Rules =
            [
                new CleanRule { Base = KnownBase.UserProfile, RelativePattern = @".gradle\caches" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "maven-repository",
            Name = "Maven local repository",
            Description = "The Maven artifact store (%UserProfile%\\.m2\\repository). Re-downloaded on demand; enable only when reclaiming serious space.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Moderate,
            EnabledByDefault = false,
            Rules =
            [
                new CleanRule { Base = KnownBase.UserProfile, RelativePattern = @".m2\repository" }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "symbol-caches",
            Name = "Debugger symbol caches",
            Description = "Downloaded PDB symbol stores (SymbolCache / SymCache). Re-fetched from symbol servers when debugging.",
            Group = CategoryGroup.Development,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Temp\SymbolCache" }.Validate(),
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = "SymCache" }.Validate(),
            ],
        });

        // =====================================================================
        // ADVANCED (all off by default)
        // =====================================================================

        categories.Add(new CleanCategory
        {
            Id = "store-apps-localcache",
            Name = "Store apps LocalCache (aggressive)",
            Description = "LocalCache folders of ALL packaged apps. Platform-purgeable by contract, but some apps keep session tokens here and will ask you to sign in again. Prefer the per-app categories above.",
            Group = CategoryGroup.Advanced,
            Risk = RiskLevel.Moderate,
            EnabledByDefault = false,
            Warning = "Some Store apps may sign you out or lose window layouts. App data and settings (LocalState) are not touched.",
            Rules =
            [
                new CleanRule { Base = KnownBase.LocalAppData, RelativePattern = @"Packages\*\LocalCache", MinAgeHoursOverride = 168 }.Validate(),
            ],
        });

        categories.Add(new CleanCategory
        {
            Id = "upgrade-leftovers",
            Name = "Windows upgrade leftovers",
            Description = @"Staging folders from feature updates and resets: $Windows.~BT, $Windows.~WS, $GetCurrent, $SysReset and C:\ESD. Only present after upgrades; safe once you're not rolling back.",
            Group = CategoryGroup.Advanced,
            Risk = RiskLevel.Moderate,
            EnabledByDefault = false,
            RequiresAdmin = true,
            Warning = "Do not clean while a Windows feature update is downloading or pending a reboot.",
            Rules =
            [
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "$Windows.~BT" }.Validate(),
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "$Windows.~WS" }.Validate(),
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "$GetCurrent" }.Validate(),
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "$SysReset" }.Validate(),
                new CleanRule { Base = KnownBase.SystemDrive, RelativePattern = "ESD" }.Validate(),
            ],
        });

        categories.Add(WindowsOldCategory.Create());
        categories.Add(EventLogsCategory.Create());

        return categories;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Chromium cache dirs cleaned per profile. Cookies/History/Login Data are never listed here.</summary>
    private static readonly string[] ChromiumProfileCacheDirs =
    [
        @"Cache\Cache_Data",
        @"Code Cache\js",
        @"Code Cache\wasm",
        "GPUCache",
        "ShaderCache",
        "GrShaderCache",
        "GraphiteDawnCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "DawnCache",
        @"Service Worker\CacheStorage",
        @"Service Worker\ScriptCache",
        @"Media Cache",
    ];

    /// <summary>Chromium cache dirs at the User Data level (shared across profiles).</summary>
    private static readonly string[] ChromiumTopLevelCacheDirs =
    [
        "ShaderCache",
        "GrShaderCache",
        "GraphiteDawnCache",
        @"Crashpad\reports",
    ];

    private static void AddChromiumBrowser(
        List<CleanCategory> categories,
        string id,
        string name,
        string userDataRelative,
        bool profilesAreFlat = false)
    {
        var rules = new List<CleanRule>();

        if (profilesAreFlat)
        {
            // Opera keeps a single profile directly in the root.
            foreach (string cacheDir in ChromiumProfileCacheDirs)
            {
                rules.Add(new CleanRule
                {
                    Base = KnownBase.RoamingAppData,
                    RelativePattern = $@"{userDataRelative}\{cacheDir}",
                    MinAgeHoursOverride = 0,
                }.Validate());
            }

            // Opera's actual disk cache lives under Local AppData.
            string productName = userDataRelative[(userDataRelative.LastIndexOf('\\') + 1)..];
            rules.Add(new CleanRule
            {
                Base = KnownBase.LocalAppData,
                RelativePattern = $@"Opera Software\{productName}\Cache",
                MinAgeHoursOverride = 0,
            }.Validate());
        }
        else
        {
            foreach (string cacheDir in ChromiumProfileCacheDirs)
            {
                // "Default" and numbered profiles ("Profile 1", …). The wildcard also catches
                // Guest/System profiles; harmless because only cache subdirs are targeted.
                rules.Add(new CleanRule
                {
                    Base = KnownBase.LocalAppData,
                    RelativePattern = $@"{userDataRelative}\Default\{cacheDir}",
                    MinAgeHoursOverride = 0,
                }.Validate());
                rules.Add(new CleanRule
                {
                    Base = KnownBase.LocalAppData,
                    RelativePattern = $@"{userDataRelative}\Profile *\{cacheDir}",
                    MinAgeHoursOverride = 0,
                }.Validate());
            }

            foreach (string cacheDir in ChromiumTopLevelCacheDirs)
            {
                rules.Add(new CleanRule
                {
                    Base = KnownBase.LocalAppData,
                    RelativePattern = $@"{userDataRelative}\{cacheDir}",
                    MinAgeHoursOverride = 0,
                }.Validate());
            }
        }

        categories.Add(new CleanCategory
        {
            Id = id,
            Name = name,
            Description = "Disk cache, compiled-code cache, GPU/shader caches and service-worker cache storage. Cookies, history, passwords, sessions and site data are never touched. Locked files (browser running) are skipped.",
            Group = CategoryGroup.Browsers,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules = rules,
        });
    }

    private static void AddElectronApp(
        List<CleanCategory> categories,
        string id,
        string name,
        KnownBase @base,
        string appFolder,
        string description)
    {
        categories.Add(new CleanCategory
        {
            Id = id,
            Name = name,
            Description = description,
            Group = CategoryGroup.Applications,
            Risk = RiskLevel.Safe,
            EnabledByDefault = true,
            Rules =
            [
                new CleanRule { Base = @base, RelativePattern = $@"{appFolder}\Cache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = @base, RelativePattern = $@"{appFolder}\Code Cache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = @base, RelativePattern = $@"{appFolder}\GPUCache", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = @base, RelativePattern = $@"{appFolder}\blob_storage", MinAgeHoursOverride = 0 }.Validate(),
                new CleanRule { Base = @base, RelativePattern = $@"{appFolder}\Service Worker\CacheStorage", MinAgeHoursOverride = 0 }.Validate(),
            ],
        });
    }

    private static List<CleanRule> BuildVsCodeRules()
    {
        var rules = new List<CleanRule>();
        string[] editors = ["Code", "Code - Insiders", "Cursor", "VSCodium"];
        string[] cacheDirs = ["Cache", "CachedData", "Code Cache", "GPUCache", "CachedExtensionVSIXs", @"Service Worker\CacheStorage"];

        foreach (string editor in editors)
        {
            foreach (string cacheDir in cacheDirs)
            {
                rules.Add(new CleanRule
                {
                    Base = KnownBase.RoamingAppData,
                    RelativePattern = $@"{editor}\{cacheDir}",
                    MinAgeHoursOverride = 0,
                }.Validate());
            }

            rules.Add(new CleanRule
            {
                Base = KnownBase.RoamingAppData,
                RelativePattern = $@"{editor}\logs",
                MinAgeHoursOverride = 168,
            }.Validate());
        }

        return rules;
    }

    private static string? GetSteamPath()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? path = key?.GetValue("SteamPath") as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            path = path.Replace('/', '\\');
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
