using System.Diagnostics;
using System.Text;
using System.Text.Json;

using static Gradient.InterfaceText;

namespace Gradient;

internal sealed class CoreClient : IDisposable
{
    private readonly SemaphoreSlim callLock = new(1, 1);
    private readonly object replyGate = new();
    private Process? process;
    private TaskCompletionSource<JsonElement>? pendingReply;

    public event Action<JsonElement>? EventReceived;

    public async Task<T> Call<T>(object request)
    {
        await callLock.WaitAsync();
        try { return await CallCoreAsync<T>(request); }
        finally { callLock.Release(); }
    }

    private async Task<T> CallCoreAsync<T>(object request)
    {
        if (process?.HasExited == true)
            ResetProcess();

        if (process is null)
        {
#if DEBUG
            var executable = Environment.GetEnvironmentVariable("GRADIENT_CORE")
                ?? Path.Combine(AppContext.BaseDirectory, "gradient-core.exe");
#else
            var executable = Path.Combine(AppContext.BaseDirectory, "gradient-core.exe");
#endif
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            };
            if (!info.Environment.ContainsKey("THESEUS_DB_BACKUP_DIR"))
                info.Environment["THESEUS_DB_BACKUP_DIR"] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Gradient", "Backups", "app-db");
            var child = Process.Start(info) ?? throw new IOException(Tr("Не вдалося запустити ядро."));
            process = child;
            try
            {
                var line = await child.StandardOutput.ReadLineAsync()
                    ?? throw new IOException(Tr("Ядро завершило роботу. Перевірте каталог даних."));
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.GetProperty("protocol").GetInt32() != 1)
                    throw new IOException(Tr("Несумісна версія ядра."));
            }
            catch
            {
                ResetProcess();
                throw;
            }
            _ = Task.Run(() => ReadLoopAsync(child));
        }

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (replyGate)
            pendingReply = tcs;

        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        }
        catch
        {
            lock (replyGate)
                if (pendingReply == tcs) pendingReply = null;
            ResetProcess();
            throw;
        }

        var reply = await tcs.Task;
        if (reply.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());
        return reply.GetProperty("ok").Deserialize<T>()!;
    }

    private async Task ReadLoopAsync(Process child)
    {
        try
        {
            while (true)
            {
                var line = await child.StandardOutput.ReadLineAsync();
                if (line is null) break;
                JsonElement element;
                using (var document = JsonDocument.Parse(line))
                    element = document.RootElement.Clone();

                if (element.TryGetProperty("event", out _))
                {
                    try { EventReceived?.Invoke(element); }
                    catch { }
                }
                else
                {
                    TaskCompletionSource<JsonElement>? target;
                    lock (replyGate)
                    {
                        target = pendingReply;
                        pendingReply = null;
                    }
                    target?.TrySetResult(element);
                }
            }
        }
        catch (Exception error)
        {
            FailPendingReply(error);
            return;
        }
        FailPendingReply(new IOException(Tr("Ядро завершило роботу. Перевірте каталог даних.")));
    }

    private void FailPendingReply(Exception error)
    {
        TaskCompletionSource<JsonElement>? target;
        lock (replyGate)
        {
            target = pendingReply;
            pendingReply = null;
        }
        target?.TrySetException(error);
    }

    public async Task StopForUpdateAsync()
    {
        await callLock.WaitAsync();
        try
        {
            await CallCoreAsync<JsonElement>(new { method = "prepare_launcher_update" });
            var child = process!;
            try
            {
                child.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await child.WaitForExitAsync(timeout.Token);
            }
            finally { ResetProcess(); }
        }
        finally { callLock.Release(); }
    }

    private void ResetProcess()
    {
        FailPendingReply(new IOException(Tr("Ядро завершило роботу. Перевірте каталог даних.")));
        process?.StandardInput.Dispose();
        process?.Dispose();
        process = null;
    }

    public void Dispose() => ResetProcess();
}
