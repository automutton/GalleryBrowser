using GalleryBrowser.Models;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GalleryBrowser.Services;

internal sealed class PCloudBackupService
{
    public const string OAuthRedirectUri = "http://127.0.0.1:43817/pcloud/";
    private const string BackupFilePrefix = "GalleryBrowser_gallery_api_";
    private const string BackupFileSuffix = ".sqlite";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly GalleryDatabase _database;
    private readonly StorageSettingsStore _settingsStore;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public PCloudBackupService(GalleryDatabase database, StorageSettingsStore settingsStore, string dataDirectory)
    {
        _database = database;
        _settingsStore = settingsStore;
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public PCloudBackupSettingsDto GetSettings() => _settingsStore.GetPCloudSettings();

    public void SaveSettings(
        string apiHost,
        string targetFolder,
        string clientId,
        string? accessToken,
        bool autoBackupEnabled,
        int checkIntervalMinutes,
        int backupIntervalDays,
        int maximumSnapshots,
        int idleThresholdMinutes) =>
        _settingsStore.SavePCloudConfiguration(
            apiHost,
            targetFolder,
            clientId,
            accessToken,
            autoBackupEnabled,
            checkIntervalMinutes,
            backupIntervalDays,
            maximumSnapshots,
            idleThresholdMinutes);

    public void Disconnect() => _settingsStore.DisconnectPCloud();

    public bool IsAutomaticBackupDue()
    {
        var settings = _settingsStore.GetPCloudSettings();
        if (!settings.AutoBackupEnabled || !settings.HasAccessToken)
        {
            return false;
        }

        return settings.LastBackupAt is null ||
               settings.LastBackupAt.Value.ToLocalTime().Date
                   .AddDays(settings.BackupIntervalDays) <= DateTimeOffset.Now.Date;
    }

    public async Task<PCloudConnectionResult> ConnectWithOAuthAsync(
        string apiHost,
        string targetFolder,
        string clientId,
        CancellationToken cancellationToken)
    {
        _settingsStore.SavePCloudConfiguration(apiHost, targetFolder, clientId);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("pCloud Developersで作成したアプリのClient IDを入力してください。", nameof(clientId));
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var listener = new TcpListener(IPAddress.Loopback, new Uri(OAuthRedirectUri).Port);
        listener.Start();
        try
        {
            var authorizationUri = new UriBuilder("https://my.pcloud.com/oauth2/authorize")
            {
                Query = string.Join('&', new Dictionary<string, string>
                {
                    ["client_id"] = clientId.Trim(),
                    ["response_type"] = "token",
                    ["redirect_uri"] = OAuthRedirectUri,
                    ["state"] = state
                }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
            }.Uri;

            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUri.AbsoluteUri,
                UseShellExecute = true
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            OAuthTokenResult tokenResult;
            try
            {
                tokenResult = await WaitForOAuthTokenAsync(listener, state, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("pCloudのOAuth連携が3分以内に完了しませんでした。もう一度実行してください。");
            }

            var connection = await GetUserInfoAsync(tokenResult.ApiHost, tokenResult.AccessToken, cancellationToken);
            _settingsStore.SavePCloudConnection(tokenResult.ApiHost, tokenResult.AccessToken, connection);
            return connection;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<PCloudConnectionResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetPCloudSettings();
        var token = _settingsStore.GetPCloudAccessToken();
        var connection = await GetUserInfoAsync(settings.ApiHost, token, cancellationToken);
        _settingsStore.SavePCloudConnectionStatus(connection);
        return connection;
    }

    public async Task<PCloudBackupResult> BackupAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await BackupCoreAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<PCloudBackupResult?> BackupIfAutomaticDueAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            // Recheck after acquiring the lock so a just-completed manual backup does not
            // create an automatic snapshot before the configured interval has elapsed.
            if (!IsAutomaticBackupDue())
            {
                return null;
            }
            return await BackupCoreAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<PCloudBackupResult> BackupCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetPCloudSettings();
        if (!settings.AutoBackupEnabled)
        {
            throw new InvalidOperationException("pCloudバックアップ機能が無効です。設定で有効にしてから実行してください。");
        }
        var token = _settingsStore.GetPCloudAccessToken();
        var snapshotDirectory = Path.Combine(
            Path.GetTempPath(),
            "GalleryBrowser",
            "DatabaseBackups",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(snapshotDirectory);
        var now = DateTimeOffset.Now;
        var fileName = $"GalleryBrowser_gallery_api_{now:yyyyMMdd_HHmmss}.sqlite";
        var snapshotPath = Path.Combine(snapshotDirectory, fileName);

        try
        {
            await Task.Run(() => _database.CreateGalleryDatabaseSnapshot(snapshotPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var localSize = new FileInfo(snapshotPath).Length;
            var folderId = await EnsureTargetFolderAsync(settings.ApiHost, token, settings.TargetFolder, cancellationToken);
            var uploaded = await UploadFileAsync(
                settings.ApiHost,
                token,
                folderId,
                snapshotPath,
                fileName,
                cancellationToken);
            if (uploaded.SizeBytes != localSize)
            {
                throw new InvalidDataException(
                    $"pCloud上のファイルサイズがローカルスナップショットと一致しません。ローカル: {localSize:N0} bytes / pCloud: {uploaded.SizeBytes:N0} bytes");
            }

            var removedOldSnapshots = 0;
            var retentionWarning = string.Empty;
            try
            {
                removedOldSnapshots = await PruneOldSnapshotsAsync(
                    settings.ApiHost,
                    token,
                    folderId,
                    settings.MaximumSnapshots,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException)
            {
                retentionWarning = $"古いスナップショットの整理に失敗しました: {exception.Message}";
            }

            var result = new PCloudBackupResult(
                uploaded.FileName,
                uploaded.SizeBytes,
                now,
                settings.TargetFolder,
                removedOldSnapshots,
                retentionWarning);
            _settingsStore.SavePCloudBackupResult(result);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(snapshotDirectory))
                {
                    Directory.Delete(snapshotDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // The temporary snapshot is safe to leave for OS cleanup when a stream is still being released.
            }
            catch (UnauthorizedAccessException)
            {
                // Backup completion should not be reported as failed solely because temporary cleanup failed.
            }
        }
    }

    public async Task<IReadOnlyList<PCloudSnapshotDto>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.GetPCloudSettings();
        var token = _settingsStore.GetPCloudAccessToken();
        var folderId = await EnsureTargetFolderAsync(settings.ApiHost, token, settings.TargetFolder, cancellationToken);
        return await ListSnapshotsAsync(settings.ApiHost, token, folderId, cancellationToken);
    }

    public async Task<PCloudRestorePreparationResult> PrepareRestoreAsync(
        long fileId,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.GetPCloudSettings();
            var token = _settingsStore.GetPCloudAccessToken();
            var folderId = await EnsureTargetFolderAsync(settings.ApiHost, token, settings.TargetFolder, cancellationToken);
            var snapshots = await ListSnapshotsAsync(settings.ApiHost, token, folderId, cancellationToken);
            var selected = snapshots.SingleOrDefault(snapshot => snapshot.FileId == fileId)
                ?? throw new InvalidOperationException("復元対象のスナップショットがpCloudに見つかりません。");

            var pendingDirectory = GetPendingRestoreDirectory();
            Directory.CreateDirectory(pendingDirectory);
            var downloadedPath = Path.Combine(
                pendingDirectory,
                $"{Guid.NewGuid():N}_{selected.FileName}");
            var backupDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GalleryBrowser",
                "DatabaseRestoreBackups");
            Directory.CreateDirectory(backupDirectory);
            var currentBackupPath = Path.Combine(
                backupDirectory,
                $"gallery_api_before_pcloud_restore_{DateTime.Now:yyyyMMdd-HHmmss}.sqlite");

            try
            {
                await DownloadSnapshotAsync(
                    settings.ApiHost,
                    token,
                    selected,
                    downloadedPath,
                    cancellationToken);
                ValidateSqliteSnapshot(downloadedPath, "pCloudから取得したスナップショット");
                await Task.Run(() => _database.CreateGalleryDatabaseSnapshot(currentBackupPath), cancellationToken);

                var manifest = new PendingRestoreManifest(
                    selected.FileId,
                    selected.FileName,
                    downloadedPath,
                    _database.GetGalleryDatabasePath(),
                    currentBackupPath,
                    DateTimeOffset.Now);
                WritePendingRestoreManifest(_dataDirectory, manifest);
                return new PCloudRestorePreparationResult(selected.FileName, currentBackupPath);
            }
            catch
            {
                TryDeleteFile(downloadedPath);
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public static PCloudStartupRestoreResult? ApplyPendingRestore(string dataDirectory)
    {
        var markerPath = GetPendingRestoreManifestPath(dataDirectory);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<PendingRestoreManifest>(File.ReadAllText(markerPath), JsonOptions)
                ?? throw new InvalidDataException("pCloud復元情報を読み取れませんでした。");
            var snapshotPath = Path.GetFullPath(manifest.DownloadedSnapshotPath);
            var targetPath = Path.GetFullPath(manifest.TargetDatabasePath);
            if (!File.Exists(snapshotPath))
            {
                throw new FileNotFoundException("復元用スナップショットが見つかりません。", snapshotPath);
            }

            ValidateSqliteSnapshot(snapshotPath, "復元用スナップショット");
            var targetDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new InvalidOperationException("本体DBの保存先ディレクトリを確認できません。");
            Directory.CreateDirectory(targetDirectory);
            var stagedPath = Path.Combine(
                targetDirectory,
                $".{Path.GetFileName(targetPath)}.pcloud-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(snapshotPath, stagedPath, overwrite: false);
                ValidateSqliteSnapshot(stagedPath, "復元直前のスナップショット");
                SqliteConnection.ClearAllPools();
                TryDeleteFile(targetPath + "-wal");
                TryDeleteFile(targetPath + "-shm");

                IOException? replaceError = null;
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            File.Replace(stagedPath, targetPath, null, ignoreMetadataErrors: true);
                        }
                        else
                        {
                            File.Move(stagedPath, targetPath);
                        }
                        replaceError = null;
                        break;
                    }
                    catch (IOException exception) when (attempt < 5)
                    {
                        replaceError = exception;
                        Thread.Sleep(250);
                    }
                    catch (IOException exception)
                    {
                        replaceError = exception;
                    }
                }

                if (replaceError is not null)
                {
                    throw new IOException("本体DBが使用中のため、pCloudスナップショットへ切り替えられませんでした。", replaceError);
                }
            }
            finally
            {
                TryDeleteFile(stagedPath);
            }

            TryDeleteFile(snapshotPath);
            TryDeleteFile(markerPath);
            return new PCloudStartupRestoreResult(
                true,
                $"pCloudスナップショット {manifest.FileName} を復元しました。",
                manifest.CurrentDatabaseBackupPath);
        }
        catch (Exception exception)
        {
            try
            {
                var failedMarkerPath = markerPath + $".failed-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(markerPath, failedMarkerPath, overwrite: true);
            }
            catch
            {
                // Keep the original marker when the failure record cannot be renamed.
            }
            return new PCloudStartupRestoreResult(false, $"pCloudスナップショットを復元できませんでした: {exception.Message}");
        }
    }

    private static async Task<PCloudConnectionResult> GetUserInfoAsync(
        string apiHost,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, apiHost, "userinfo", accessToken);
        using var document = await SendApiRequestAsync(request, cancellationToken);
        var root = document.RootElement;
        return new PCloudConnectionResult(
            ReadString(root, "email"),
            ReadInt64(root, "usedquota"),
            ReadInt64(root, "quota"));
    }

    private static async Task<long> EnsureTargetFolderAsync(
        string apiHost,
        string accessToken,
        string targetFolder,
        CancellationToken cancellationToken)
    {
        var normalized = StorageSettingsStore.NormalizeTargetFolder(targetFolder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("pCloud内の保存先を入力してください。");
        }
        if (normalized == "/")
        {
            return 0;
        }

        long folderId = 0;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, apiHost, "createfolderifnotexists", accessToken);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["folderid"] = folderId.ToString(CultureInfo.InvariantCulture),
                ["name"] = segment
            });
            using var document = await SendApiRequestAsync(request, cancellationToken);
            if (!document.RootElement.TryGetProperty("metadata", out var metadata))
            {
                throw new InvalidDataException("pCloudから作成先フォルダの情報が返されませんでした。");
            }
            folderId = ReadInt64(metadata, "folderid");
        }
        return folderId;
    }

    private static async Task<IReadOnlyList<PCloudSnapshotDto>> ListSnapshotsAsync(
        string apiHost,
        string accessToken,
        long folderId,
        CancellationToken cancellationToken)
    {
        var query = $"folderid={folderId.ToString(CultureInfo.InvariantCulture)}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, apiHost, $"listfolder?{query}", accessToken);
        using var document = await SendApiRequestAsync(request, cancellationToken);
        if (!document.RootElement.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("pCloudからスナップショット一覧を取得できませんでした。");
        }

        var snapshots = new List<PCloudSnapshotDto>();
        foreach (var item in contents.EnumerateArray())
        {
            var fileName = ReadString(item, "name");
            if (!IsManagedSnapshotName(fileName) ||
                !item.TryGetProperty("fileid", out var fileIdElement) ||
                !fileIdElement.TryGetInt64(out var fileId))
            {
                continue;
            }

            snapshots.Add(new PCloudSnapshotDto(
                fileId,
                fileName,
                ReadInt64(item, "size"),
                ReadSnapshotTimestamp(item, fileName)));
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ThenByDescending(snapshot => snapshot.FileId)
            .ToArray();
    }

    private static async Task<int> PruneOldSnapshotsAsync(
        string apiHost,
        string accessToken,
        long folderId,
        int maximumSnapshots,
        CancellationToken cancellationToken)
    {
        var snapshots = await ListSnapshotsAsync(apiHost, accessToken, folderId, cancellationToken);
        var removed = 0;
        foreach (var snapshot in snapshots.Skip(maximumSnapshots))
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, apiHost, "deletefile", accessToken);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fileid"] = snapshot.FileId.ToString(CultureInfo.InvariantCulture)
            });
            using var document = await SendApiRequestAsync(request, cancellationToken);
            removed++;
        }
        return removed;
    }

    private static async Task DownloadSnapshotAsync(
        string apiHost,
        string accessToken,
        PCloudSnapshotDto snapshot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var query = $"fileid={snapshot.FileId.ToString(CultureInfo.InvariantCulture)}&forcedownload=1";
        using var linkRequest = CreateAuthorizedRequest(HttpMethod.Get, apiHost, $"getfilelink?{query}", accessToken);
        using var linkDocument = await SendApiRequestAsync(linkRequest, cancellationToken);
        var root = linkDocument.RootElement;
        var path = ReadString(root, "path");
        if (string.IsNullOrWhiteSpace(path) ||
            !root.TryGetProperty("hosts", out var hosts) ||
            hosts.ValueKind != JsonValueKind.Array ||
            hosts.GetArrayLength() == 0)
        {
            throw new InvalidDataException("pCloudからスナップショットのダウンロードURLを取得できませんでした。");
        }
        var host = hosts[0].GetString();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidDataException("pCloudのダウンロード先ホストを取得できませんでした。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var response = await HttpClient.GetAsync(
            new Uri($"https://{host}{path}", UriKind.Absolute),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1024 * 1024,
                         useAsync: true))
        {
            await response.Content.CopyToAsync(destination, cancellationToken);
        }

        var downloadedSize = new FileInfo(destinationPath).Length;
        if (downloadedSize != snapshot.SizeBytes)
        {
            throw new InvalidDataException(
                $"復元用スナップショットのサイズが一致しません。pCloud: {snapshot.SizeBytes:N0} bytes / ローカル: {downloadedSize:N0} bytes");
        }
    }

    private static bool IsManagedSnapshotName(string fileName) =>
        fileName.StartsWith(BackupFilePrefix, StringComparison.Ordinal) &&
        fileName.EndsWith(BackupFileSuffix, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ReadSnapshotTimestamp(JsonElement metadata, string fileName)
    {
        var timestampText = fileName[BackupFilePrefix.Length..^BackupFileSuffix.Length];
        if (DateTime.TryParseExact(
                timestampText,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var fileTimestamp))
        {
            return new DateTimeOffset(fileTimestamp);
        }

        foreach (var propertyName in new[] { "modified", "created" })
        {
            var value = ReadString(metadata, propertyName);
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
            {
                return parsed;
            }
        }
        return DateTimeOffset.MinValue;
    }

    private static void ValidateSqliteSnapshot(string databasePath, string label)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using (var quickCheck = connection.CreateCommand())
            {
                quickCheck.CommandText = "PRAGMA quick_check;";
                var result = Convert.ToString(quickCheck.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{label}の整合性チェックに失敗しました: {result}");
                }
            }
            using var schemaCheck = connection.CreateCommand();
            schemaCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'items';";
            if (Convert.ToInt64(schemaCheck.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException($"{label}はGalleryBrowser本体DBではありません。");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException($"{label}をSQLiteDBとして検証できませんでした。", exception);
        }
    }

    private static string GetPendingRestoreDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GalleryBrowser",
        "PendingDatabaseRestore");

    private static string GetPendingRestoreManifestPath(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), "pcloud-restore.json");

    private static void WritePendingRestoreManifest(string dataDirectory, PendingRestoreManifest manifest)
    {
        var markerPath = GetPendingRestoreManifestPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        var temporaryPath = markerPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temporaryPath, markerPath, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary and SQLite sidecar files can be cleaned up on the next run.
        }
        catch (UnauthorizedAccessException)
        {
            // Restore diagnostics are more useful than failing solely on cleanup.
        }
    }

    private static async Task<UploadedFile> UploadFileAsync(
        string apiHost,
        string accessToken,
        long folderId,
        string sourcePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["folderid"] = folderId.ToString(CultureInfo.InvariantCulture),
            ["filename"] = fileName,
            ["nopartial"] = "1",
            ["renameifexists"] = "1"
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = CreateAuthorizedRequest(HttpMethod.Put, apiHost, $"uploadfile?{query}", accessToken);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);
        using var fileContent = new StreamContent(source, 1024 * 1024);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.sqlite3");
        fileContent.Headers.ContentLength = source.Length;
        request.Content = fileContent;

        using var document = await SendApiRequestAsync(request, cancellationToken);
        if (!document.RootElement.TryGetProperty("metadata", out var metadataArray) ||
            metadataArray.ValueKind != JsonValueKind.Array ||
            metadataArray.GetArrayLength() == 0)
        {
            throw new InvalidDataException("pCloudからアップロード済みファイルの情報が返されませんでした。");
        }

        var metadata = metadataArray[0];
        return new UploadedFile(ReadString(metadata, "name"), ReadInt64(metadata, "size"));
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string apiHost,
        string methodName,
        string accessToken)
    {
        var host = StorageSettingsStore.NormalizeApiHost(apiHost);
        var request = new HttpRequestMessage(method, $"https://{host}/{methodName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<JsonDocument> SendApiRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"pCloud APIとの通信に失敗しました（HTTP {(int)response.StatusCode}）。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("pCloud APIから不正な応答を受信しました。", exception);
        }

        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Number)
        {
            document.Dispose();
            throw new InvalidDataException("pCloud API応答のresultを確認できませんでした。");
        }
        var resultCode = result.GetInt32();
        if (resultCode != 0)
        {
            var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : "不明なエラー";
            document.Dispose();
            throw new InvalidOperationException($"pCloud APIエラー {resultCode}: {error}");
        }
        return document;
    }

    private static async Task<OAuthTokenResult> WaitForOAuthTokenAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = requestParts.ElementAtOrDefault(0) ?? string.Empty;
            var path = requestParts.ElementAtOrDefault(1) ?? string.Empty;
            var contentLength = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(header["Content-Length:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                }
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/pcloud/", StringComparison.Ordinal))
            {
                await WriteHttpResponseAsync(stream, HttpStatusCode.OK, "text/html; charset=utf-8", OAuthCallbackHtml, cancellationToken);
                continue;
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/pcloud/token", StringComparison.Ordinal))
            {
                var bodyBuffer = new char[Math.Clamp(contentLength, 0, 16_384)];
                var read = 0;
                while (read < bodyBuffer.Length)
                {
                    var count = await reader.ReadAsync(bodyBuffer.AsMemory(read, bodyBuffer.Length - read), cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }
                    read += count;
                }

                var values = ParseFormBody(new string(bodyBuffer, 0, read));
                if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(expectedState),
                        Encoding.UTF8.GetBytes(state)))
                {
                    await WriteHttpResponseAsync(stream, HttpStatusCode.BadRequest, "text/plain; charset=utf-8", "OAuth state mismatch", cancellationToken);
                    continue;
                }
                if (values.TryGetValue("error", out var error))
                {
                    await WriteHttpResponseAsync(stream, HttpStatusCode.BadRequest, "text/plain; charset=utf-8", error, cancellationToken);
                    throw new InvalidOperationException($"pCloud OAuth連携が拒否されました: {error}");
                }
                if (!values.TryGetValue("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken) ||
                    !values.TryGetValue("hostname", out var apiHost))
                {
                    await WriteHttpResponseAsync(stream, HttpStatusCode.BadRequest, "text/plain; charset=utf-8", "Missing OAuth token", cancellationToken);
                    continue;
                }

                apiHost = StorageSettingsStore.NormalizeApiHost(apiHost);
                await WriteHttpResponseAsync(stream, HttpStatusCode.OK, "text/plain; charset=utf-8", "OK", cancellationToken);
                return new OAuthTokenResult(apiHost, accessToken);
            }

            await WriteHttpResponseAsync(stream, HttpStatusCode.NotFound, "text/plain; charset=utf-8", "Not found", cancellationToken);
        }
    }

    private static async Task WriteHttpResponseAsync(
        Stream stream,
        HttpStatusCode status,
        string contentType,
        string content,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return values;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new InvalidDataException($"pCloud API応答の{propertyName}を読み取れませんでした。");
        }
        return number;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GalleryBrowser/1.0");
        return client;
    }

    private const string OAuthCallbackHtml = """
        <!doctype html>
        <html lang="ja">
        <head><meta charset="utf-8"><title>GalleryBrowser pCloud OAuth</title></head>
        <body style="margin:0;min-height:100vh;display:grid;place-items:center;background:#111827;color:#f8fafc;font-family:Segoe UI,sans-serif">
          <main style="max-width:560px;padding:32px;border:1px solid #334155;border-radius:12px;background:#1e293b">
            <h1 style="margin-top:0">pCloud連携</h1><p id="status">連携情報をGalleryBrowserへ渡しています...</p>
          </main>
          <script>
            const status = document.getElementById('status');
            const payload = location.hash.startsWith('#') ? location.hash.slice(1) : '';
            if (!payload) {
              status.textContent = 'pCloudから認証情報が返されませんでした。このタブを閉じてもう一度実行してください。';
            } else {
              fetch('token', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: payload })
                .then(response => { if (!response.ok) throw new Error('連携情報を検証できませんでした。'); return response.text(); })
                .then(() => { status.textContent = 'pCloudとの連携が完了しました。このタブを閉じてGalleryBrowserへ戻ってください。'; })
                .catch(error => { status.textContent = error.message; });
            }
          </script>
        </body>
        </html>
        """;

    private sealed record OAuthTokenResult(string ApiHost, string AccessToken);
    private sealed record UploadedFile(string FileName, long SizeBytes);
    private sealed record PendingRestoreManifest(
        long FileId,
        string FileName,
        string DownloadedSnapshotPath,
        string TargetDatabasePath,
        string CurrentDatabaseBackupPath,
        DateTimeOffset PreparedAt);
}
