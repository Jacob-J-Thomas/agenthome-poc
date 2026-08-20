using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Composes one canonical single-host background runtime over the real durable workspace stores.</summary>
public static class GovernedLoopLocalBackgroundRuntimeFactory
{
    /// <summary>Creates an inert runtime whose lifetime owns schedule, queue, sleep, wake, and coordinator persistence.</summary>
    /// <remarks>
    /// Schedule and trigger policy ports remain composition-owned trusted adapters. The three sleep ports are the #314
    /// executable Wait/frontier seams: this factory does not implement or duplicate frontier mutation, event sensing, or
    /// authority decisions.
    /// </remarks>
    public static GovernedLoopLocalBackgroundRuntime Create(
        WorkspacePaths paths,
        IScheduleCurrentEvidencePort scheduleCurrentEvidence,
        IScheduleOverlapPort scheduleOverlap,
        IScheduleTimeZonePort scheduleTimeZone,
        ITriggerDispatchAuthorizer triggerAuthorizer,
        ITriggerWorkerDispatcher triggerDispatcher,
        IGovernedLoopSleepCurrentPosturePort sleepCurrentPosture,
        IGovernedLoopWakeContinuationPort wakeContinuation,
        IGovernedLoopAuthenticatedWakeVerificationPort authenticatedWakeVerification,
        GovernedLoopLocalWorkRunnerOptions workOptions,
        GovernedLoopLocalCoordinatorOptions coordinatorOptions,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scheduleCurrentEvidence);
        ArgumentNullException.ThrowIfNull(scheduleOverlap);
        ArgumentNullException.ThrowIfNull(scheduleTimeZone);
        ArgumentNullException.ThrowIfNull(triggerAuthorizer);
        ArgumentNullException.ThrowIfNull(triggerDispatcher);
        ArgumentNullException.ThrowIfNull(sleepCurrentPosture);
        ArgumentNullException.ThrowIfNull(wakeContinuation);
        ArgumentNullException.ThrowIfNull(authenticatedWakeVerification);
        ArgumentNullException.ThrowIfNull(workOptions);
        ArgumentNullException.ThrowIfNull(coordinatorOptions);

        var clock = timeProvider ?? TimeProvider.System;
        var scheduleStore = new ScheduleStore(paths);
        var schedules = ScheduleRuntimeFactory.Create(
            paths,
            scheduleStore,
            scheduleCurrentEvidence,
            scheduleOverlap,
            scheduleTimeZone,
            clock);
        CustomLoopRunStore? runStore = null;
        try
        {
            runStore = new CustomLoopRunStore(paths, clock);
            var triggerStore = new TriggerQueueStore(paths, TriggerQueueQuota.Runtime, timeProvider: clock);
            var triggerWorker = new TriggerWorkerService(
                triggerStore,
                triggerAuthorizer,
                triggerDispatcher,
                new ScheduleTriggerDispatchReadinessService(scheduleStore),
                clock);
            var sleepStore = new GovernedLoopSleepStore(paths);
            var sleep = new GovernedLoopSleepService(
                sleepStore,
                sleepCurrentPosture,
                wakeContinuation,
                authenticatedWakeVerification,
                clock);
            var background = new GovernedLoopBackgroundWorkSource(scheduleStore, sleepStore);
            var work = new GovernedLoopLocalWorkRunner(
                background,
                schedules,
                runStore,
                triggerStore,
                triggerWorker,
                sleep,
                workOptions,
                clock);
            var evidence = new GovernedLoopCoordinatorEvidenceStore(paths);
            var coordinator = new GovernedLoopLocalCoordinator(
                evidence,
                work,
                coordinatorOptions,
                clock);
            return new GovernedLoopLocalBackgroundRuntime(coordinator, schedules, runStore, work);
        }
        catch
        {
            runStore?.Dispose();
            schedules.Dispose();
            throw;
        }
    }
}
