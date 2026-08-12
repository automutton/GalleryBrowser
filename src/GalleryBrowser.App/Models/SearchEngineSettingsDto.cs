namespace GalleryBrowser.Models;

public sealed record SearchEngineSettingsDto(
    string Provider,
    string GoogleSearchUrlTemplate,
    string BraveApiKey,
    string GeminiApiKey,
    string YahooClientId);
