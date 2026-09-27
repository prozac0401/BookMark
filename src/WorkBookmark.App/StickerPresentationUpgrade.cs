namespace WorkBookmark.App;

// The caller holds the settings gate until the completed settings are published.
// Layout writes are repeatable; no completion marker is written before all succeed.
internal static class StickerPresentationUpgrade
{
    internal static async Task<UserSettings> ApplyAsync(UserSettings settings,
        Func<Task> resetLayouts, Func<UserSettings, Task> saveSettings)
    {
        if (settings.StickerPresentationVersion >= UserSettings.CurrentStickerPresentationVersion) return settings;
        await resetLayouts();
        var updated = settings with
        {
            // The first presentation upgrade selects stickers. Later visual upgrades
            // preserve the display mode explicitly chosen since that first startup.
            DisplayMode = settings.StickerPresentationVersion == 0 ? BookmarkDisplayMode.Stickers : settings.DisplayMode,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        };
        await saveSettings(updated);
        return updated;
    }
}
