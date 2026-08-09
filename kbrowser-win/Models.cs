using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace KBrowser
{
    internal sealed class BrowserSettings
    {
        public string HomeUrl { get; set; } = "https://www.google.com/";
        public string DownloadFolder { get; set; } = "";
        public bool ShowBookmarksBar { get; set; } = true;
    }

    internal sealed class BookmarkItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    internal static class Store
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static BrowserSettings LoadSettings()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    return Json.Deserialize<BrowserSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new BrowserSettings();
            }
            catch { }
            return new BrowserSettings();
        }

        public static void SaveSettings(BrowserSettings settings)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(AppPaths.SettingsFile, Json.Serialize(settings));
            }
            catch { }
        }

        public static List<BookmarkItem> LoadBookmarks()
        {
            try
            {
                if (File.Exists(AppPaths.BookmarksFile))
                {
                    var saved = Json.Deserialize<List<BookmarkItem>>(File.ReadAllText(AppPaths.BookmarksFile));
                    if (saved != null) return saved;
                }
            }
            catch { }

            return new List<BookmarkItem>
            {
                new BookmarkItem { Name = "네이버", Url = "https://www.naver.com/" },
                new BookmarkItem { Name = "다음", Url = "https://www.daum.net/" },
                new BookmarkItem { Name = "구글", Url = "https://www.google.co.kr/" },
                new BookmarkItem { Name = "구글번역", Url = "https://translate.google.co.kr/" },
                new BookmarkItem { Name = "파파고", Url = "https://papago.naver.com/" },
                new BookmarkItem { Name = "윈도우포럼", Url = "https://windowsforum.kr/" },
                new BookmarkItem { Name = "홍차의꿈", Url = "https://jsb000.tistory.com/" },
                new BookmarkItem { Name = "YouTube", Url = "https://www.youtube.com/" }
            };
        }

        public static void SaveBookmarks(List<BookmarkItem> bookmarks)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(AppPaths.BookmarksFile, Json.Serialize(bookmarks));
            }
            catch { }
        }
    }
}
