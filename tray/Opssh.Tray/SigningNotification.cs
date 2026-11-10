using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Opssh.Tray;

internal sealed class SigningNotification(AppNotifications notifications)
{
    private readonly ApplicationIcon icons = new();
    private readonly SemaphoreSlim imageWriter = new(1);

    internal async Task<IAsyncDisposable?> ShowAsync(SigningPrompt prompt, CancellationToken token)
    {
        if (!notifications.IsAvailable())
            return null;

        var image = await CreateImageAsync(prompt.Process, token);
        try
        {
            token.ThrowIfCancellationRequested();
            var id = notifications.Show(() =>
            {
                var builder = new AppNotificationBuilder()
                    .SetTag("signing")
                    .SetDuration(AppNotificationDuration.Long)
                    .AddText(Strings.Get("Approval.Application", prompt.Application, prompt.Process.Pid),
                        new AppNotificationTextProperties().SetMaxLines(2))
                    .AddText(Strings.Get("Approval.Key", prompt.KeyName), new AppNotificationTextProperties().SetMaxLines(2))
                    .AddText(prompt.Fingerprint, new AppNotificationTextProperties().SetMaxLines(2));
                if (image is not null)
                    builder.SetAppLogoOverride(new Uri(image), AppNotificationImageCrop.Default);

                foreach (var scope in prompt.Scopes)
                {
                    var label = scope switch
                    {
                        "app" => "Approval.AllowApp",
                        "once" => "Approval.AllowOnce",
                        _ => "Approval.Deny"
                    };
                    builder.AddButton(new AppNotificationButton(Strings.Get(label))
                        .AddArgument(AppNotifications.Signing, prompt.Nonce).AddArgument("choice", scope));
                }

                var notification = builder.BuildNotification();
                notification.Expiration = prompt.Approval.Deadline;
                notification.ExpiresOnReboot = true;
                return notification;
            });
            if (id == 0)
                return null;
            var shown = new Shown(notifications, id, image);
            image = null;
            return shown;
        }
        finally
        {
            await DeleteImageAsync(image);
        }
    }

    private async Task<string?> CreateImageAsync(ProcessIdentity process, CancellationToken token)
    {
        try
        {
            var pixels = await icons.ReadAsync(process, token);
            if (pixels is null)
                return null;

            using var buffer = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer).AsTask(token).WaitAsync(token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, ApplicationIcon.Size,
                ApplicationIcon.Size, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(token).WaitAsync(token);
            if (buffer.Size is 0 or > 32768)
                return null;
            buffer.Seek(0);
            var png = new byte[(int)buffer.Size];
            using var input = buffer.AsStreamForRead();
            await input.ReadExactlyAsync(png, token);
            return await WriteImageAsync(png, token);
        }
        catch (Exception e) when (AppNotifications.Unavailable(e) || e is IOException) { return null; }
    }

    private async Task<string?> WriteImageAsync(byte[] png, CancellationToken token)
    {
        if (!imageWriter.Wait(0))
            return null;
        var write = Task.Run(() =>
        {
            string? incomplete = null;
            try
            {
                var directory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
                if (!ApplicationIcon.IsLocalPath(directory))
                    return null;
                var path = Path.Combine(directory, "opssh-toast-" + Guid.NewGuid().ToString("N") + ".png");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    incomplete = path;
                    file.Write(png);
                }
                incomplete = null;
                return path;
            }
            catch (Exception e) when (AppNotifications.Unavailable(e) || e is IOException) { return null; }
            finally
            {
                DeleteImage(incomplete);
                imageWriter.Release();
            }
        });
        try { return await write.WaitAsync(TimeSpan.FromMilliseconds(250), token); }
        catch (TimeoutException)
        {
            _ = DeleteLateImageAsync(write);
            return null;
        }
        catch (OperationCanceledException)
        {
            _ = DeleteLateImageAsync(write);
            throw;
        }
    }

    private static async Task DeleteLateImageAsync(Task<string?> image) => await DeleteImageAsync(await image);

    private static async Task DeleteImageAsync(string? image)
    {
        if (image is null)
            return;
        try { await Task.Run(() => DeleteImage(image)).WaitAsync(TimeSpan.FromMilliseconds(250)); }
        catch (TimeoutException) { }
    }

    private static void DeleteImage(string? image)
    {
        if (image is null)
            return;
        try { File.Delete(image); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Shown(AppNotifications notifications, uint id, string? image) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await notifications.RemoveAsync(id); }
            finally { await DeleteImageAsync(image); }
        }
    }
}
