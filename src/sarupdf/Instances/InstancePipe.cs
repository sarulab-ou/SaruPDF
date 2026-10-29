using System.IO.Pipes;
using System.Text.Json;

namespace SaruPdf.Instances;

internal sealed record PipeHello(int Version, int ProcessId);
internal sealed record PipeAck(Guid Id, bool Accepted);

internal sealed class InstancePipe : IDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<OpenRequest, Task<bool>> dispatch;
    private readonly Task listener;
    public event Action<Exception>? Failed;

    public InstancePipe(string name, Func<OpenRequest, Task<bool>> dispatch)
    {
        this.dispatch = dispatch;
        server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        listener = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await PipeProtocol.WriteAsync(server, new PipeHello(1, Environment.ProcessId), timeout.Token);
                    var request = await PipeProtocol.ReadAsync<OpenRequest>(server, timeout.Token);
                    if (request.Paths is null || request.Errors is null || request.Id == Guid.Empty ||
                        request.Paths.Length > 256 || request.Errors.Length > 256 ||
                        request.Paths.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
                        throw new InvalidDataException("引き継ぎデータの形式が不正です。");
                    bool accepted;
                    try { accepted = await dispatch(request).WaitAsync(timeout.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Failed?.Invoke(ex);
                        accepted = false;
                    }
                    // Acknowledge only after UI-thread registration, never merely on receipt.
                    await PipeProtocol.WriteAsync(server, new PipeAck(request.Id, accepted), timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
                { /* A disconnected launcher can retry the same request id. */ }
                // IsConnected becomes false on a broken pipe, but Disconnect is still
                // required to reset the server instance before accepting a new client.
                finally { if (!stop.IsCancellationRequested) server.Disconnect(); }
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }

    public static async Task<bool> ForwardAsync(string name, OpenRequest request, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(token);
        var hello = await PipeProtocol.ReadAsync<PipeHello>(pipe, token);
        if (hello.Version != 1 || hello.ProcessId <= 0) throw new InvalidDataException("互換性のない SaruPDF が起動しています。");
        NativeWindows.AllowSetForegroundWindow((uint)hello.ProcessId);
        await PipeProtocol.WriteAsync(pipe, request, token);
        var ack = await PipeProtocol.ReadAsync<PipeAck>(pipe, token);
        return ack.Id == request.Id && ack.Accepted;
    }

    public void Dispose()
    {
        stop.Cancel();
        server.Dispose();
        // Never wait on the UI thread: a dispatch may be queued there during shutdown.
        _ = listener.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
