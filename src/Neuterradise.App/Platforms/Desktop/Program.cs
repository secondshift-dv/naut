using Neuterradise.App.Ui;
using Uno.UI.Hosting;

namespace Neuterradise.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        SkVideoView.PrewarmDecoder();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWin32()
            .Build();

        host.Run();
    }
}
