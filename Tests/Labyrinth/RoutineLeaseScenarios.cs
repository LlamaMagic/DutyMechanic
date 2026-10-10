using DutyMechanic.Dungeons;

// Verify ownership across normal completion, an initially disabled preference and a
// user override. These detached checks cannot establish live rotation scheduling.
internal static class RoutineLeaseScenarios
{
    internal sealed class Settings
    {
        private bool value;
        internal int Writes;
        public bool BioMultipleTargets { get=>value; set { this.value=value; Writes++; } }
    }
    internal static void Verify(Action<bool,string> check)
    {
        var property=typeof(Settings).GetProperty(nameof(Settings.BioMultipleTargets));
        var settings=new Settings {BioMultipleTargets=true};
        var lease=new LabyrinthRoutineLease(settings,property);
        check(!settings.BioMultipleTargets,"Multi-dot is suppressed during owned duty");
        lease.Dispose(); lease.Dispose();
        check(settings.BioMultipleTargets && settings.Writes==3,"Original true restored exactly once");
        settings=new Settings();
        lease=new LabyrinthRoutineLease(settings,property); lease.Dispose();
        check(!settings.BioMultipleTargets && settings.Writes==0,"Existing disabled setting stays untouched");
        settings=new Settings {BioMultipleTargets=true};
        lease=new LabyrinthRoutineLease(settings,property);
        settings.BioMultipleTargets=true;
        int writes=settings.Writes; lease.Dispose();
        check(settings.Writes==writes,"Explicit re-enable is not overwritten during release");
    }
}
