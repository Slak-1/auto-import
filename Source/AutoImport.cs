using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Text.RegularExpressions;

namespace AutoImportPlugin
{
    public class AutoImport : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private AutoImportSettingsViewModel settings { get; set; }

        private const string UriSource = "autoimport";
        // Shared by the normal library scan and the URI-triggered scan so they
        // cannot open two selection windows at the same time.
        private int scanInProgress;

        public override Guid Id { get; } = Guid.Parse("92c96e54-069b-4fc4-bbaa-35ac3064f85a");
        public override string Name => "AutoImport";

        public AutoImport(IPlayniteAPI api) : base(api)
        {
            settings = new AutoImportSettingsViewModel(this);
            Properties = new LibraryPluginProperties { HasSettings = true };

            // Allows an external script to request a scan with:
            // playnite://autoimport/scan
            PlayniteApi.UriHandler.RegisterSource(UriSource, HandleUri);
        }

        public override void Dispose()
        {
            try
            {
                PlayniteApi.UriHandler.RemoveSource(UriSource);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to unregister AutoImport URI handler");
            }

            base.Dispose();
        }

        public override ISettings GetSettings(bool firstRunSettings) => settings;
        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings) => new AutoImportSettingsView();

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            return RunScanWithGate();
        }

        private List<GameMetadata> RunScanWithGate()
        {
            if (Interlocked.CompareExchange(ref scanInProgress, 1, 0) != 0)
            {
                logger.Info("AutoImport scan skipped because another scan is already running.");
                return new List<GameMetadata>();
            }

            try
            {
                // This is the normal library-plugin path: Playnite imports the returned
                // GameMetadata objects after GetGames completes.
                return ScanAndSelectGames();
            }
            finally
            {
                Interlocked.Exchange(ref scanInProgress, 0);
            }
        }

        private void HandleUri(PlayniteUriEventArgs args)
        {
            // Only accept playnite://autoimport/scan.
            if (args?.Arguments == null ||
                args.Arguments.Length == 0 ||
                !string.Equals(args.Arguments[0], "scan", StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn("Ignored an AutoImport URI that did not request a scan.");
                return;
            }

            if (Interlocked.CompareExchange(ref scanInProgress, 1, 0) != 0)
            {
                // A scan or its selection window is already active. Dropping the
                // overlapping request avoids spawning a second selection window.
                logger.Info("AutoImport URI scan skipped because another scan is already running.");
                return;
            }

            try
            {
                var selectedGames = ScanAndSelectGames();
                if (selectedGames.Count == 0)
                {
                    logger.Info("AutoImport URI scan finished; no games were selected for import.");
                    return;
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    logger.Error("Cannot import AutoImport results because the WPF dispatcher is unavailable.");
                    return;
                }

                int importedCount = 0;
                dispatcher.Invoke(() =>
                {
                    foreach (var game in selectedGames)
                    {
                        try
                        {
                            PlayniteApi.Database.ImportGame(game, this);
                            importedCount++;
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, $"Failed to import '{game?.Name ?? "(unnamed game)"}' from AutoImport URI scan.");
                        }
                    }
                });

                logger.Info($"AutoImport URI scan finished. Imported {importedCount} game(s).");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "AutoImport URI scan failed.");
            }
            finally
            {
                Interlocked.Exchange(ref scanInProgress, 0);
            }
        }

        private HashSet<string> BuildExistingGamesSet()
        {
            var existingSet = new HashSet<string>();
            try
            {
                foreach (var game in PlayniteApi.Database.Games)
                {
                    if (game.IsInstalled)
                    {
                        if (!string.IsNullOrEmpty(game.InstallDirectory))
                            existingSet.Add(NormalizePath(game.InstallDirectory));
                        if (game.GameActions != null)
                        {
                            foreach (var action in game.GameActions)
                            {
                                if (action.Type == GameActionType.File && !string.IsNullOrEmpty(action.Path))
                                {
                                    existingSet.Add(NormalizePath(action.Path));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to build existing games set");
                return new HashSet<string>();
            }
            return existingSet;
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.ToLowerInvariant().Trim().Replace("/", "\\");
        }

        private List<GameMetadata> ScanAndSelectGames()
        {
            var allFoundGames = new List<ScannedGameWrapper>();

            if (settings.Settings.ScanFolders == null) return new List<GameMetadata>();
            var blockedSet = new HashSet<string>();
            if (settings.Settings.BlockedPaths != null)
            {
                foreach (var path in settings.Settings.BlockedPaths)
                {
                    blockedSet.Add(NormalizePath(path));
                }
            }

            var existingSet = BuildExistingGamesSet();
            foreach (var folder in settings.Settings.ScanFolders)
            {
                if (Directory.Exists(folder))
                {
                    allFoundGames.AddRange(ScanFolderLimited(folder, blockedSet, existingSet));
                }
            }

            // A game can be found more than once if configured scan folders overlap.
            // Count unique executable paths so a single game never looks like a batch.
            allFoundGames = allFoundGames
                .GroupBy(game => NormalizePath(game.ExecutablePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (allFoundGames.Count == 0) return new List<GameMetadata>();

            // If exactly one eligible, new game remains after filtering, import it
            // without showing the selection window. This works for both Playnite's
            // normal library scan (returned metadata) and the URI-triggered scan
            // (which imports the returned metadata through the database API).
            // Multiple candidates still use the existing selection window.
            if (allFoundGames.Count == 1)
            {
                logger.Info($"AutoImport found one eligible game ('{allFoundGames[0].GameData?.Name ?? "(unnamed game)"}'); importing without showing the selection window.");
                return new List<GameMetadata> { allFoundGames[0].GameData };
            }

            List<GameMetadata> finalSelection = new List<GameMetadata>();
            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = new GameSelectionWindow(allFoundGames);
                if (Application.Current.MainWindow != null) window.Owner = Application.Current.MainWindow;

                if (window.ShowDialog() == true)
                {
                    finalSelection = window.SelectedGames.Select(game => game.GameData).ToList();
                    var newlyIgnored = allFoundGames
                        .Where(game => game.IsIgnored)
                        .Select(game => game.ExecutablePath)
                        .ToList();
                    if (newlyIgnored.Count > 0)
                    {
                        foreach (var path in newlyIgnored)
                        {
                            if (!settings.BlockedPathsUI.Contains(path))
                            {
                                settings.BlockedPathsUI.Add(path);
                            }
                        }
                        settings.EndEdit();
                    }
                }
            });
            return finalSelection;
        }

        private IEnumerable<ScannedGameWrapper> ScanFolderLimited(string rootPath, HashSet<string> blockedSet, HashSet<string> existingSet)
        {
            var results = new List<ScannedGameWrapper>();
            results.AddRange(GetExecutablesInDir(rootPath, blockedSet, existingSet));
            try
            {
                foreach (var subDir in Directory.GetDirectories(rootPath))
                {
                    if (IsExcludedFolder(subDir)) continue;
                    results.AddRange(GetExecutablesInDir(subDir, blockedSet, existingSet));
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Failed to scan subdirectories in: {rootPath}");
            }
            return results;
        }

        private IEnumerable<ScannedGameWrapper> GetExecutablesInDir(string dirPath, HashSet<string> blockedSet, HashSet<string> existingSet)
        {
            var list = new List<ScannedGameWrapper>();
            try
            {
                var files = Directory.GetFiles(dirPath, "*.exe");
                foreach (var file in files)
                {
                    string normalizedFile = NormalizePath(file);
                    string normalizedDir = NormalizePath(dirPath);
                    bool isIgnored = blockedSet.Contains(normalizedFile) || blockedSet.Contains(normalizedDir);
                    if (isIgnored) continue;

                    bool alreadyExists = existingSet.Contains(normalizedFile) || existingSet.Contains(normalizedDir);
                    if (alreadyExists) continue;
                    if (IsGameExecutable(file))
                    {
                        var fileInfo = new FileInfo(file);
                        string gameName = GetGameNameFromFolderOrExe(dirPath, fileInfo);
                        var metadata = new GameMetadata
                        {
                            Name = gameName,
                            GameId = fileInfo.FullName,
                            InstallDirectory = fileInfo.DirectoryName,
                            IsInstalled = true,
                            Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                            Source = new MetadataNameProperty("AutoImport"),
                            GameActions = new List<GameAction>
                            {
                                new GameAction
                                {
                                    Type = GameActionType.File,
                                    Path = fileInfo.FullName,
                                    WorkingDir = fileInfo.DirectoryName,
                                    Name = "Play",
                                    IsPlayAction = true
                                }
                            }
                        };
                        list.Add(new ScannedGameWrapper { GameData = metadata });
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"Failed to scan directory for executables: {dirPath}");
            }
            return list;
        }

        private string GetGameNameFromFolderOrExe(string dirPath, FileInfo fileInfo)
        {
            string folderName = Path.GetFileName(dirPath);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                string cleanFolderName = CleanGameName(folderName);
                if (IsValidGameName(cleanFolderName))
                {
                    return cleanFolderName;
                }
            }
            string rawExeName = Path.GetFileNameWithoutExtension(fileInfo.Name);
            return CleanGameName(rawExeName);
        }

        private bool IsValidGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (name.Length < 2) return false;
            string lowerName = name.ToLowerInvariant();
            string[] genericNames = { "bin", "game", "games", "exe", "exes", "program", "programs",
                                      "application", "applications", "software", "tools", "util",
                                      "utils", "temp", "tmp", "download", "downloads" };

            return !genericNames.Contains(lowerName);
        }

        private string CleanGameName(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return filename;

            string clean = filename.Replace('.', ' ').Replace('_', ' ');
            clean = Regex.Replace(clean, @"\[.*?\]|\(.*?\)", "");

            string junkPattern = @"\bv?(\d+(\.\d+)+)\b|repack|goty|edition|remastered|x64|x86|build|setup|installer";
            clean = Regex.Replace(clean, junkPattern, "", RegexOptions.IgnoreCase);
            return Regex.Replace(clean, @"\s+", " ").Trim();
        }

        // Parts of file names that mark an executable as NOT being a game.
        // Add your own entries here (lowercase).
        private static readonly string[] ExcludedFileNameParts =
        {
            "uninstall", "setup", "config", "crash",
            "redist", "oalinst", "dotnet", "physx", "dgvoodoo"
        };

        private bool IsGameExecutable(string path)
        {
            string fileName = Path.GetFileName(path).ToLowerInvariant();

            // Inno Setup uninstallers: unins000.exe, unins001.exe, ...
            if (fileName.StartsWith("unins")) return false;

            return !ExcludedFileNameParts.Any(part => fileName.Contains(part));
        }

        // Skips whole folders that only hold helper installers, e.g. _CommonRedist
        private bool IsExcludedFolder(string dirPath)
        {
            string name = Path.GetFileName(dirPath).ToLowerInvariant();
            return name.StartsWith("_") || name.Contains("redist");
        }
    }
}
