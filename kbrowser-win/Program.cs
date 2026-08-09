using System;
using System.IO;
using System.Windows.Forms;

namespace KBrowser
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppPaths.Initialize();
            Application.Run(new MainForm(false));
        }
    }

    internal static class AppPaths
    {
        public static string ProgramDirectory { get; private set; }
        public static string DataDirectory { get; private set; }
        public static string UserDataDirectory { get; private set; }
        public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public static string BookmarksFile => Path.Combine(DataDirectory, "bookmarks.json");

        public static void Initialize()
        {
            ProgramDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            DataDirectory = Path.Combine(ProgramDirectory, ".KBrowserData");
            UserDataDirectory = Path.Combine(DataDirectory, "WebView2");
            Directory.CreateDirectory(UserDataDirectory);
            try
            {
                var info = new DirectoryInfo(DataDirectory);
                info.Attributes |= FileAttributes.Hidden;
            }
            catch { }
        }

        public static string CreateSessionDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "KBrowser-Session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
