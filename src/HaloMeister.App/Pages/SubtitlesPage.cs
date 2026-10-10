using HaloMeister.App.Services;

namespace HaloMeister.App.Pages;

public sealed class SubtitlesPage : GameTextPage
{
    public SubtitlesPage()
        : base(GameTextService.Subtitles, "subtitles")
    {
    }
}
