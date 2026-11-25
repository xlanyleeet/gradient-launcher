using System.Diagnostics;
using System.Text;
using System.Text.Json;

using static Gradient.InterfaceText;

namespace Gradient;

internal sealed class CoreClient : IDisposable
{
    private readonly SemaphoreSlim callLock = new(1, 1);
    private Process? process;

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
            process = Process.Start(info) ?? throw new IOException(Tr("Не вдалося запустити ядро."));
            try
            {
                var hello = await Read();
                if (hello.GetProperty("protocol").GetInt32() != 1)
                    throw new IOException(Tr("Несумісна версія ядра."));
            }
            catch
            {
                ResetProcess();
                throw;
            }
        }

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        var reply = await Read();
        if (reply.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());
        return reply.GetProperty("ok").Deserialize<T>()!;
    }

    private async Task<JsonElement> Read()
    {
        var line = await process!.StandardOutput.ReadLineAsync()
            ?? throw new IOException(Tr("Ядро завершило роботу. Перевірте каталог даних."));
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
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
        process?.StandardInput.Dispose();
        process?.Dispose();
        process = null;
    }

    public void Dispose() => ResetProcess();
}
