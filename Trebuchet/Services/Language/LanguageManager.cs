using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using tot_lib;
using tot_gui_lib;

namespace Trebuchet.Services.Language;

public class LanguageManager : ILanguageManager
{
    private readonly LanguagesConfiguration _configuration;
    private readonly Lazy<Dictionary<string, LanguageModel>> _availableLanguages;

    public LanguageModel DefaultLanguage { get; }

    public LanguageModel CurrentLanguage => CreateLanguageModel(Thread.CurrentThread.CurrentUICulture);

    public IEnumerable<LanguageModel> AllLanguages => _availableLanguages.Value.Values;

    public LanguageManager(LanguagesConfiguration configuration)
    {
        _configuration = configuration;
        _availableLanguages = new Lazy<Dictionary<string, LanguageModel>>(GetAvailableLanguages);

        DefaultLanguage = CreateLanguageModel(CultureInfo.GetCultureInfo("en"));
    }

    public void SetLanguage(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode) || !_configuration.AvailableLocales.Contains(languageCode))
            languageCode = DefaultLanguage.Code;
        

        var culture = CultureInfo.GetCultureInfo(languageCode);
        Assets.Resources.Culture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        // Background threads (humanized durations, formatted logs) follow the selected language too.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public void SetLanguage(LanguageModel languageModel) => SetLanguage(languageModel.Code);

    private Dictionary<string, LanguageModel> GetAvailableLanguages()
    {
        return _configuration
            .AvailableLocales
            .Select(locale => CreateLanguageModel(new CultureInfo(locale)))
            .ToDictionary(lm => lm.Code, lm => lm);
    }
        

    private LanguageModel CreateLanguageModel(CultureInfo cultureInfo)
    {
        return new LanguageModel(
            cultureInfo.EnglishName, 
            cultureInfo.NativeName.ToTitleCase(),
            cultureInfo.TwoLetterISOLanguageName);
    }
}