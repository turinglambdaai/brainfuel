using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace BrainFuel.Services;

public enum ManualUpdateStage
{
    Checking,
    Downloading,
    Restarting,
}

public enum ManualUpdateResult
{
    Restarting,
    UpToDate,
    NotInstalled,
    Failed,
}

/// <summary>
/// Checks GitHub Releases for Velopack updates. Installed Windows builds can
/// either stage updates quietly in the background or perform a user-requested
/// check/download/apply cycle that restarts directly into the new version.
/// Velopack prefers delta packages and falls back to the full package when
/// necessary. Portable builds intentionally remain manually replaceable.
/// </summary>
public static class UpdateService
{
    public const string RepositoryUrl = "https://github.com/turinglambdaai/brainfuel";
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// True only when this process is running from a Velopack-managed install.
    /// Development builds and portable ZIPs intentionally return false.
    /// </summary>
    public static bool IsInstalledBuild()
    {
        try
        {
            return CreateManager().IsInstalled;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RunAutomaticUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckAndStageUpdateAsync(cancellationToken);

            try
            {
                await Task.Delay(CheckInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static async Task<bool> CheckAndStageUpdateAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            // A background check should never queue behind a user-requested
            // update operation. It simply tries again on the next interval.
            entered = await Gate.WaitAsync(0, cancellationToken);
            if (!entered)
                return false;

            var manager = CreateManager();

            // Development builds and portable ZIPs are not Velopack installs.
            if (!manager.IsInstalled)
                return false;

            if (manager.UpdatePendingRestart is not null)
                return true;

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return false;

            await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            TryLogFailure(ex);
            return false;
        }
        finally
        {
            if (entered)
                Gate.Release();
        }
    }

    /// <summary>
    /// User-requested online update. Waits for any background check to finish,
    /// checks GitHub Releases, downloads the delta/full package, then asks
    /// Velopack to install it and restart BrainFuel immediately.
    /// </summary>
    public static async Task<ManualUpdateResult> CheckDownloadAndRestartAsync(
        Action<ManualUpdateStage>? reportStage = null,
        CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            await Gate.WaitAsync(cancellationToken);
            entered = true;

            var manager = CreateManager();
            if (!manager.IsInstalled)
                return ManualUpdateResult.NotInstalled;

            reportStage?.Invoke(ManualUpdateStage.Checking);
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return ManualUpdateResult.UpToDate;

            reportStage?.Invoke(ManualUpdateStage.Downloading);
            await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);

            reportStage?.Invoke(ManualUpdateStage.Restarting);
            manager.ApplyUpdatesAndRestart(update);

            // ApplyUpdatesAndRestart normally terminates this process. Keep a
            // result for testability and for defensive behavior if it ever returns.
            return ManualUpdateResult.Restarting;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ManualUpdateResult.Failed;
        }
        catch (Exception ex)
        {
            TryLogFailure(ex);
            return ManualUpdateResult.Failed;
        }
        finally
        {
            if (entered)
                Gate.Release();
        }
    }

    private static UpdateManager CreateManager()
        => new(new GithubSource(RepositoryUrl, null, false));

    private static void TryLogFailure(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDirectory);
            var path = Path.Combine(SettingsService.AppDirectory, "update.log");
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch
        {
            // Updating is best-effort and must never make the widget unusable.
        }
    }
}
