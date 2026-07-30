using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class ArtifactScanAccumulator
{
    private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);
    private long _liveTraceBytes;
    private long _tombstoneBytes;
    private long _accountedBytes;
    private int _activeReservations;
    private int _liveTraceCount;
    private int _tombstoneCount;

    public void Add(RunArtifact artifact)
    {
        var runId = artifact.Run?.Id ?? artifact.Tombstone?.RunId ?? throw new FormatException($"Custom loop trace `{artifact.Location.Path}` contains an unsupported artifact.");
        var admissionOperationId = artifact.Run?.AdmissionOperationId ?? artifact.Tombstone!.AdmissionOperationId;
        if (!_runIds.Add(runId))
        {
            throw new FormatException($"Custom loop run id `{runId}` is duplicated. The persisted state requires review.");
        }

        if (!_operationIds.Add(admissionOperationId))
        {
            throw new FormatException($"Admission operation id `{admissionOperationId}` is duplicated. The persisted state requires review.");
        }

        if (artifact.Tombstone is not null)
        {
            _tombstoneCount++;
            if (_tombstoneCount > CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
            {
                throw new FormatException($"Custom loop run storage contains more than {CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace} terminal-trace tombstones.");
            }

            _tombstoneBytes = checked(_tombstoneBytes + artifact.PersistedUtf8Bytes);
            _accountedBytes = checked(_accountedBytes + artifact.PersistedUtf8Bytes);
            return;
        }

        var run = artifact.Run ?? throw new FormatException($"Custom loop trace `{artifact.Location.Path}` contains an unsupported artifact.");
        _liveTraceCount++;
        if (_liveTraceCount > CustomLoopLimits.MaxRunTracesPerWorkspace)
        {
            throw new FormatException($"Custom loop run storage contains more than {CustomLoopLimits.MaxRunTracesPerWorkspace} live traces. No trace was pruned automatically.");
        }

        _liveTraceBytes = checked(_liveTraceBytes + artifact.PersistedUtf8Bytes);
        if (run.IsTerminal)
        {
            var warningReservation = CustomLoopRunStore.HasTerminalIntegrityWarning(run) ? 0 : CustomLoopLimits.MaxTraceControlEventUtf8Bytes;
            _accountedBytes = checked(_accountedBytes + artifact.PersistedUtf8Bytes + warningReservation);
            if (warningReservation > 0)
            {
                _activeReservations++;
            }
        }
        else
        {
            _activeReservations++;
            _accountedBytes = checked(_accountedBytes + CustomLoopLimits.MaxRunTraceUtf8Bytes);
        }
    }

    public ArtifactScanResult Complete()
    {
        return new ArtifactScanResult(new CustomLoopTraceQuota(
            _liveTraceCount,
            _liveTraceBytes,
            _accountedBytes,
            _activeReservations,
            CustomLoopLimits.MaxRunTracesPerWorkspace,
            CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes,
            CustomLoopLimits.MaxRunTraceUtf8Bytes,
            _tombstoneCount,
            _tombstoneBytes,
            CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace,
            0,
            CustomLoopLimits.MaxRunTraceDeletionOperationsPerWorkspace));
    }
}
