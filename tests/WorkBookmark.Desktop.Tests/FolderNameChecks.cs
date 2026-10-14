using System.Reflection;
using WorkBookmark.App;

namespace WorkBookmark.Desktop.Tests;

internal static class FolderNameChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<bool, string> assert)
    {
        var type = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.FolderNameForm")!;
        var constructor = type.GetConstructors(Private).Single();
        string? saved = null;
        int calls = 0;
        bool reject = true;
        var gate = new TaskCompletionSource();
        using var form = (Form)constructor.Invoke(["새 폴더", false, (Func<string, Task>)(async text =>
        {
            calls++;
            if (reject) throw new IOException("Injected failure");
            await gate.Task;
            saved = text;
        })]);
        var name = (TextBox)type.GetField("_name", Private)!.GetValue(form)!;
        var status = (Label)type.GetField("_status", Private)!.GetValue(form)!;
        var save = type.GetMethod("SaveAsync", Private)!;
        name.Text = "   ";
        ((Task)save.Invoke(form, null)!).GetAwaiter().GetResult();
        assert(calls == 0 && !form.IsDisposed && status.Text.Contains("1~100"), "FN01 blank folder name is rejected before persistence");
        name.Text = "  분기별 기획  ";
        ((Task)save.Invoke(form, null)!).GetAwaiter().GetResult();
        assert(calls == 1 && name.Text == "  분기별 기획  " && !name.ReadOnly && status.Text.Contains("다시 시도"),
            "FN02 failed folder create retains the typed name and allows retry");
        reject = false;
        var pending = (Task)save.Invoke(form, null)!;
        assert(name.ReadOnly && !pending.IsCompleted, "FN03 a pending folder save protects the input from conflicting edits");
        ((Task)save.Invoke(form, null)!).GetAwaiter().GetResult();
        assert(calls == 2, "FN04 repeated save cannot create duplicate folders while the first commit is pending");
        gate.SetResult();
        while (!pending.IsCompleted) { Application.DoEvents(); Thread.Sleep(1); }
        pending.GetAwaiter().GetResult();
        assert(saved == "분기별 기획", "FN05 successful retry sends one trimmed folder name");
        using var rename = (Form)constructor.Invoke(["자료", true, (Func<string, Task>)(_ => Task.CompletedTask)]);
        assert(rename.Text == "폴더 이름 변경" && ((TextBox)type.GetField("_name", Private)!.GetValue(rename)!).MaxLength == 100,
            "FN06 rename dialog keeps the stored name and folder-specific length limit");
    }
}
