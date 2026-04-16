using CS2ChatTranslator.UI;

namespace CS2ChatTranslator;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
