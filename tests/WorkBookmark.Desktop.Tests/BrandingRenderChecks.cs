using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

// Renders the actual WinForms controls with synthetic content at 100% scaling.
// No application context, user database, external document or automation session is opened.
internal static class BrandingRenderChecks
{
    internal static void Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Assembly assembly = typeof(BookmarkApplicationContext).Assembly;
        var renders = new List<object>();

        void Render(Form form, string filename)
        {
            using (form)
            {
                // Native child controls need a displayed parent before WM_PRINT can
                // paint them; a hidden Form handle alone renders only its chrome.
                form.Show();
                Application.DoEvents();
                form.PerformLayout();
                form.Refresh();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                form.Hide();
                string path = Path.Combine(directory, filename);
                bitmap.Save(path, ImageFormat.Png);
                var labels = form.Controls.OfType<Label>().Select(label => new
                {
                    label.Text,
                    label.UseMnemonic,
                    x = label.Left,
                    y = label.Top,
                    width = label.Width,
                    height = label.Height,
                    preferredHeight = label.GetPreferredSize(new Size(label.Width, 0)).Height
                }).ToArray();
                renders.Add(new { filename, width = form.Width, height = form.Height, dpi = form.DeviceDpi, labels });
                Console.WriteLine($"RENDER: {filename} {form.Width}x{form.Height} at {form.DeviceDpi} DPI");
            }
        }

        Type toastType = assembly.GetType("WorkBookmark.App.UI.ToastForm")!;
        string intro = $"업무 책갈피\n{Hotkey.CaptureDefault} 저장 · {Hotkey.RecentDefault} 책갈피 보기\n탐색기 · Excel · Word · PowerPoint · 메모장\nEdge·Chrome: 확장 없이 저장 · 트레이에서 URL 입력 가능";
        Render((Form)Activator.CreateInstance(toastType, intro, "설정", (Action)(() => { }), 10000)!, "toast-introduction.png");
        string capture = "책갈피를 남겼습니다 · 연구개발 R&D & 품질관리\n" +
            @"C:\합성 검증 자료\고객사 공동 연구 및 제품 개선 프로젝트\2026년 하반기 제품 검증 결과와 후속 조치 계획.xlsx" +
            " · 검토 현황!$D$127\n14:32 · 이전 메모 유지\n지웠던 책갈피를 복원했습니다.";
        Render((Form)Activator.CreateInstance(toastType, capture, "메모", (Action)(() => { }), 10000)!, "toast-long-korean-path.png");

        var now = DateTimeOffset.UtcNow;
        Bookmark Sample(CapturedTarget target, string title, string note, int minutes) =>
            new(Guid.NewGuid(), target, target.Path, title, note, now.AddMinutes(-minutes), now.AddMinutes(-minutes), 1, null, null, null, null);
        Bookmark[] bookmarks =
        [
            Sample(new(TargetKind.ExcelCell, @"C:\합성 검증 자료\제품 출시 일정.xlsx", "진행 현황", "$D$127", false), "제품 출시 일정.xlsx", "품질 확인 후 담당자에게 공유", 2),
            Sample(new(TargetKind.WordPosition, @"C:\합성 검증 자료\서비스 이용 안내.docx", WordStart: 208), "서비스 이용 안내.docx", "다음 수정은 시작하기 항목부터", 35),
            Sample(new(TargetKind.WebPage, "https://example.test/research?q=R%26D&year=2026", PageTitle: "연구 자료 모음"), "연구 자료 모음", "검토할 참고 자료 세 가지", 90),
            Sample(new(TargetKind.Folder, @"C:\합성 검증 자료\다음 주 회의"), "다음 주 회의", "발표 자료와 회의 기록", 1440)
        ];
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(bookmarks, false));
        Type recentType = assembly.GetType("WorkBookmark.App.UI.RecentForm")!;
        var recent = (Form)Activator.CreateInstance(recentType, load)!;
        ((Task)recentType.GetMethod("ReloadAsync")!.Invoke(recent, [false])!).GetAwaiter().GetResult();
        Render(recent, "recent-bookmarks.png");

        Type settingsType = assembly.GetType("WorkBookmark.App.UI.SettingsForm")!;
        Func<UserSettings, Task<string?>> apply = _ => Task.FromResult<string?>(null);
        Render((Form)Activator.CreateInstance(settingsType, UserSettings.Default, true, true,
            @"C:\합성 검증 자료\WorkBookmark", apply, (Action)(() => { }), (Action)(() => { }))!, "settings.png");
        File.WriteAllText(Path.Combine(directory, "render-manifest.json"), JsonSerializer.Serialize(
            new { suite = "Branding render verification", scale = "100%", renders }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
