using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Gradient;

internal sealed class CoreClient : IDisposable
{
    private Process? process;

    public async Task<T> Call<T>(object request)
    {
        if (process?.HasExited == true)
            Dispose();

        if (process is null)
        {
            var executable = Environment.GetEnvironmentVariable("GRADIENT_CORE")
                ?? Path.Combine(AppContext.BaseDirectory, "gradient-core.exe");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            };
            process = Process.Start(info) ?? throw new IOException("Не вдалося запустити ядро.");
            try
            {
                var hello = await Read();
                if (hello.GetProperty("protocol").GetInt32() != 1)
                    throw new IOException("Несумісна версія ядра.");
            }
            catch
            {
                Dispose();
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
            ?? throw new IOException("Ядро завершило роботу. Перевірте каталог даних.");
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        process?.StandardInput.Dispose();
        process?.Dispose();
        process = null;
    }
}
