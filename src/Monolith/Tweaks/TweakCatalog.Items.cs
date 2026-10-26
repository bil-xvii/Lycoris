using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Tweaks;

public static partial class TweakCatalog
{
    // Placeholder catalog; the full list is written by the Optimize module.
    private static partial IEnumerable<Tweak> Build()
    {
        yield return new RegistryTweak
        {
            Id = "game-mode",
            Title = "Игровой режим Windows",
            Description = "Windows отдаёт приоритет запущенной игре и не ставит обновления драйверов во время игры.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Recommended,
            Values = [RegSet.Dword(RegHive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)],
        };
    }
}
