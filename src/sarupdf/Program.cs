using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SaruPdf.Instances;
using SaruPdf.WindowsIntegration;

namespace SaruPdf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (AssociationCommand.Handle(args) is int associationResult) return associationResult;
        var request = OpenRequest.FromArguments(args, Environment.CurrentDirectory);
        string identity = $"{WindowsIdentity.GetCurrent().User?.Value}:{Process.GetCurrentProcess().SessionId}";
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        string name = $"sarupdf.v1.{suffix}"; // Keep IPC compatibility with the initial release.
        try
        {
            using var mutex = new Mutex(false, $"Local\\{name}");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                bool owns;
                try { owns = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owns = true; }
                if (owns)
                {
                    try
                    {
                        using var context = new PdfApplicationContext();
                        using var pipe = new InstancePipe(name, context.DispatchAsync);
                        pipe.Failed += context.ReportPipeFailure;
                        context.OpenInitial(request);
                        Application.Run(context);
                        return 0;
                    }
                    finally { mutex.ReleaseMutex(); }
                }

                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    if (InstancePipe.ForwardAsync(name, request, attempt.Token).GetAwaiter().GetResult()) return 0;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
                // The owner may still be starting or releasing its mutex on exit.
                Thread.Sleep(80);
            }
            MessageBox.Show("起動済みの SaruPDF に要求を引き継げませんでした。\n応答を待ってから再度開いてください。管理者権限の異なる起動は避けてください。",
                "SaruPDF — 引き継ぎエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SaruPDF を起動できませんでした。\n{ex.Message}", "SaruPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
