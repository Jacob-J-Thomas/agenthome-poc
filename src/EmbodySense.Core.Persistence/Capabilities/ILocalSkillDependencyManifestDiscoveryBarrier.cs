namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Provides an optional coordination point after a skill directory has been physically bound and before its sidecars are read.</summary>
public interface ILocalSkillDependencyManifestDiscoveryBarrier
{
    /// <summary>Coordinates one bounded discovery read for the supplied canonical directory path.</summary>
    void BeforeSkillRead(string directoryPath);
}
