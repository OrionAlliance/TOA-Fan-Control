// Asks once about newly spinning fan headers.
using System.Windows;
using System.Windows.Threading;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class App
{
    private static void AskAboutNewFans()
    {
        IReadOnlyList<(string Name, float? Rpm)> candidates = Controller.CandidateFans;

        // First run of this feature: existing setup counts as decided, so a pump left unchecked is never nagged.
        if (Controller.Settings.AskedFans == null)
        {
            Controller.UpdateSettings(s => NewFans.RecordDecisions(s, candidates), reresolve: false);
            DebugLog.Write($"New-fan check set up: {Controller.Settings.AskedFans?.Count ?? 0} unchecked spinning header(s) count as already decided.");
            return;
        }

        foreach (string name in NewFans.Find(candidates, Controller.Settings))
        {
            string shown = FanName.Display(name);
            DebugLog.Write($"New fan detected on '{name}' - asking.");
            bool drive = MessageWindow.ConfirmRisky(null, "New fan detected",
                $"A fan is spinning on {shown}, which the app isn't driving yet.\n\n" +
                "Liquid-cooled? Make sure this isn't your pump - slowing a pump can " +
                "overheat your CPU. Not sure what it is? Choose No: it simply stays on " +
                "your BIOS curve, exactly as it is now.\n\n" +
                "Want the app to drive it along with your other case fans?",
                "Yes, drive it", "No, leave it on the BIOS");

            Controller.UpdateSettings(s =>
            {
                s.AskedFans!.Add(name);
                if (drive) s.SelectedFans?.Add(name);
            }, reresolve: drive);
            DebugLog.Write($"New fan '{name}': {(drive ? "user chose to drive it" : "left on the BIOS")}.");
        }
    }
}
