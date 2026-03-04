using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace AnilistListConverter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check if App got launched with the argument
        if (e.Args.Length > 0 && e.Args[0].StartsWith("AnilistConverter://", StringComparison.OrdinalIgnoreCase))
        {
            // SECOND INSTANCE
            SendTokenToMainInstance(e.Args[0]);

            // Shut down immediately so no window appears
            Shutdown();
            return;
        }

        // FIRST INSTANCE
        MainWindow mainWindow = new MainWindow();
        mainWindow.Show();
    }

    // Pass token to other Instance.
    private void SendTokenToMainInstance(string url)
    {
        try
        {
            // Connect to the pipe created by the first instance
            using var client = new NamedPipeClientStream(".", "AnilistAuthPipe", PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(url);
            writer.Flush();
        }
        catch
        {
            // User closed first instance while requesting a Token... Not my Problem.
        }
    }
}