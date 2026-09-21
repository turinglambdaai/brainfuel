using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace BrainFuel.Services;

/// <summary>
/// Checks GitHub Releases for Velopack updates and downloads them in the
/// background. Velopack prefers delta packages and automatically falls back to
/// the full package when a delta cannot be used. A downloaded update is applied
/// on the next normal application launch.
/// </summary>
public static class UpdateService
{
    private const string RepositoryUrl = "https://github.com/turinglambdaai/brainfuel";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
            entered = await Gate.WaitAsync(0, cancellationToken);
            if (!entered)
                return false;

            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));

            // Development builds and the legacy portable ZIP are not Velopack
            // installations. Update checks must be a no-op in those cases.
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
