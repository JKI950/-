using System;
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
        public string BookmarkDisplayMode { get; set; } = "IconShort";
        public bool PopupBlockEnabled { get; set; } = true;
        public List<string> PopupAllowedHosts { get; set; } = new List<string>();
    }

    internal sealed class BookmarkItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string FaviconUrl { get; set; }
    }

    internal sealed class HiddenRule
    {
        public string Host { get; set; }
        public string Selector { get; set; }
        public string PageUrl { get; set; }
        public string CreatedAt { get; set; }
    }

    internal sealed class EffectCapture
    {
        public string Html { get; set; }
        public string Css { get; set; }
        public string ComputedCss { get; set; }
        public string Keyframes { get; set; }
        public string[] MediaUrls { get; set; }
        public string PageUrl { get; set; }
        public string Selector { get; set; }
        public string TagName { get; set; }
        public string Title { get; set; }
    }

    internal sealed class HideCapture
    {
        public string Host { get; set; }
        public string Selector { get; set; }
        public string PageUrl { get; set; }
    }

    internal static class Store
    {
        private static JavaScriptSerializer NewJson()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        public static BrowserSettings LoadSettings()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    return NewJson().Deserialize<BrowserSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new BrowserSettings();
            }
            catch { }
            return new BrowserSettings();
        }

        public static void SaveSettings(BrowserSettings settings)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(AppPaths.SettingsFile, NewJson().Serialize(settings));
            }
            catch { }
        }

        public static List<BookmarkItem> LoadBookmarks()
        {
            try
            {
                if (File.Exists(AppPaths.BookmarksFile))
                {
                    var saved = NewJson().Deserialize<List<BookmarkItem>>(File.ReadAllText(AppPaths.BookmarksFile));
                    if (saved != null) return saved;
                }
            }
            catch { }

            return new List<BookmarkItem>
            {
                new BookmarkItem { Name = "네이버", Url = "https://www.naver.com/", FaviconUrl = "https://www.naver.com/favicon.ico" },
                new BookmarkItem { Name = "다음", Url = "https://www.daum.net/", FaviconUrl = "https://www.daum.net/favicon.ico" },
                new BookmarkItem { Name = "구글", Url = "https://www.google.co.kr/", FaviconUrl = "https://www.google.co.kr/favicon.ico" },
                new BookmarkItem { Name = "구글번역", Url = "https://translate.google.co.kr/", FaviconUrl = "https://translate.google.co.kr/favicon.ico" },
                new BookmarkItem { Name = "파파고", Url = "https://papago.naver.com/", FaviconUrl = "https://papago.naver.com/favicon.ico" },
                new BookmarkItem { Name = "윈도우포럼", Url = "https://windowsforum.kr/", FaviconUrl = "https://windowsforum.kr/favicon.ico" },
                new BookmarkItem { Name = "홍차의꿈", Url = "https://jsb000.tistory.com/", FaviconUrl = "https://jsb000.tistory.com/favicon.ico" },
                new BookmarkItem { Name = "YouTube", Url = "https://www.youtube.com/", FaviconUrl = "https://www.youtube.com/favicon.ico" }
            };
        }

        public static void SaveBookmarks(List<BookmarkItem> bookmarks)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(AppPaths.BookmarksFile, NewJson().Serialize(bookmarks));
            }
            catch { }
        }

        public static List<HiddenRule> LoadHiddenRules()
        {
            try
            {
                if (File.Exists(AppPaths.HiddenRulesFile))
                {
                    var saved = NewJson().Deserialize<List<HiddenRule>>(File.ReadAllText(AppPaths.HiddenRulesFile));
                    if (saved != null) return saved;
                }
            }
            catch { }
            return new List<HiddenRule>();
        }

        public static void SaveHiddenRules(List<HiddenRule> rules)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(AppPaths.HiddenRulesFile, NewJson().Serialize(rules));
            }
            catch { }
        }
    }
}
