using Opssh.Tray;

internal static class DefaultAgentChecks
{
    internal static async Task RunAsync(CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("opssh-agent-checks-");
        var path = Path.Combine(directory.FullName, "system-agent.json");
        try
        {
            var service = new FakeOpenSsh();
            var agent = new DefaultAgent(service, path);
            var stages = new List<DefaultAgentStage>();
            bool alias = false;
            Task<bool> SetAlias(bool value, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                alias = value;
                return Task.FromResult(true);
            }

            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.RunningGate = stop.Task;
            var enable = agent.SetAsync(true, SetAlias, token, stages.Add);
            var stopping = !enable.IsCompleted && stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.StoppingOpenSsh]) &&
                agent.Read() is { Enabled: false, MutatingService: true };
            stop.SetResult();
            var enableResult = await enable;
            Require(stopping, "the stop stage is visible while the service operation waits");
            Require(enableResult is null && alias &&
                service.State is { Start: 4, Running: false } && agent.Read() is { Enabled: true, Before: null },
                "system agent claims the pipe after stopping and disabling the automatic service");
            Require(service.Actions.SequenceEqual(["stop", "startup:4"]), "OpenSSH is stopped before being disabled");
            Require(stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.StoppingOpenSsh,
                DefaultAgentStage.DisablingOpenSsh, DefaultAgentStage.ReservingPipe, DefaultAgentStage.Saving]),
                "enabling reports service changes before pipe reservation and durable completion");

            stages.Clear();
            Require(await agent.SetAsync(false, SetAlias, token, stages.Add) is null && !alias &&
                service.State is { Start: 2, Running: true, Delayed: false } && agent.Read() is { Enabled: false, Before: null },
                "restoring the service sets automatic and starts it");
            Require(stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.ReleasingPipe,
                DefaultAgentStage.ConfiguringOpenSsh, DefaultAgentStage.StartingOpenSsh, DefaultAgentStage.Saving]),
                "disabling reports pipe release before restoring the service");

            stages.Clear();
            service.State = service.State with { Delayed = true };
            Require(await agent.SetAsync(true, (enabled, _) => Task.FromResult(!enabled), token, stages.Add) == "Startup.DefaultChangeFailed" &&
                service.State is { Start: 2, Delayed: true, Running: true } && agent.Read() is { Enabled: false, Before: null },
                "a failed pipe reservation restores exact prior service settings");
            Require(stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.StoppingOpenSsh,
                DefaultAgentStage.DisablingOpenSsh, DefaultAgentStage.ReservingPipe, DefaultAgentStage.RollingBackAfterFailure]),
                "a failed reservation reports rollback and no final save");

            service.State = service.State with { Start = 3 };
            service.Actions.Clear();
            Require(await agent.SetAsync(true, SetAlias, token) == "Error.PipeUnavailable" && service.Actions.Count == 0,
                "a manually started service is left alone");

            service.State = new(true, 2, false, false, 0);
            Require(await agent.SetAsync(true, SetAlias, token) == "Startup.OpenSshStopped" && service.Actions.Count == 0,
                "an automatic stopped service is left unchanged and is not scheduled to race opssh");

            service.State = new(true, 2, true, true, 41);
            var bounded = new DefaultAgent(service, path, TimeSpan.Zero);
            var runningGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.RunningGate = runningGate.Task;
            stages.Clear();
            Require(await bounded.SetAsync(true, SetAlias, token, stages.Add) == "Startup.DefaultRecovery" &&
                bounded.Read() is { Enabled: false, Before.Running: true, MutatingService: true },
                "a pending service mutation is journaled before returning");
            Require(stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.StoppingOpenSsh]),
                "a pending mutation does not report later steps or rollback");
            Require(await bounded.SetAsync(false, SetAlias, token) == "Startup.DefaultRecovery" &&
                await new DefaultAgent(service, path).SetAsync(false, SetAlias, token) == "Startup.DefaultRecovery" &&
                service.Actions.SequenceEqual(["stop"]), "neither a retry nor a fresh process races a pending mutation");
            runningGate.SetResult();
            await service.LastRunningChange.WaitAsync(token);
            stages.Clear();
            Require(await bounded.SetAsync(false, SetAlias, token, stages.Add) is null && !alias &&
                service.State is { Start: 2, Delayed: true, Running: true } &&
                bounded.Read() is { Enabled: false, Before: null, MutatingService: false },
                "a settled late mutation is rolled back before the next switch");
            Require(stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.RollingBack,
                DefaultAgentStage.ReleasingPipe]), "recovery reports rollback before the requested pipe action");

            stages.Clear();
            service.State = new(false, 0, false, false, 0);
            Require(await agent.SetAsync(true, SetAlias, token, stages.Add) is null && alias &&
                stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.OpenSshAbsent,
                    DefaultAgentStage.ReservingPipe, DefaultAgentStage.Saving]),
                "enabling without OpenSSH reports the skipped service steps before reserving the pipe");
            stages.Clear();
            Require(await agent.SetAsync(false, SetAlias, token, stages.Add) is null && !alias &&
                stages.SequenceEqual([DefaultAgentStage.Checking, DefaultAgentStage.ReleasingPipe,
                    DefaultAgentStage.OpenSshAbsent, DefaultAgentStage.Saving]),
                "disabling without OpenSSH releases the pipe before reporting skipped service steps");

            FileStream? journalReader = null;
            try
            {
                var outcome = await agent.SetAsync(true, (enabled, _) =>
                {
                    if (enabled)
                        return Task.FromResult(false);
                    journalReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Task.FromResult(true);
                }, token);
                Require(outcome == "Startup.DefaultRecovery" && agent.Read() is { Enabled: false, Before.Exists: false },
                    "rollback still needs recovery when the journal cannot be cleared");
            }
            finally
            {
                journalReader?.Dispose();
                journalReader = null;
            }

            try
            {
                var outcome = await agent.SetAsync(false, (enabled, _) =>
                {
                    journalReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Task.FromResult(!enabled);
                }, token);
                Require(outcome == "Startup.DefaultRecovery" && agent.Read() is { Enabled: false, Before.Exists: false },
                    "successful recovery still needs confirmation when its journal cannot be cleared");
            }
            finally { journalReader?.Dispose(); }
            Require(await agent.SetAsync(false, SetAlias, token) is null && agent.Read() is { Before: null },
                "the same recovery can clear the journal after the storage failure ends");
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Default agent check failed: " + scenario);
    }

    private sealed class FakeOpenSsh : IOpenSshService
    {
        internal OpenSshState State { get; set; } = new(true, 2, false, true, 41);
        internal List<string> Actions { get; } = [];
        internal Task RunningGate { get; set; } = Task.CompletedTask;
        internal Task LastRunningChange { get; private set; } = Task.CompletedTask;

        public OpenSshState Read() => State;
        public bool PipeAvailable(uint expectedOwner = 0) =>
            State.Running ? expectedOwner == State.Pid : expectedOwner == 0;
        public Task ChangeStartupAsync(int start, bool delayed)
        {
            Actions.Add("startup:" + start);
            State = State with { Start = start, Delayed = delayed };
            return Task.CompletedTask;
        }
        public Task ChangeRunningAsync(bool running)
        {
            Actions.Add(running ? "start" : "stop");
            return LastRunningChange = CompleteRunningChangeAsync(running);
        }

        private async Task CompleteRunningChangeAsync(bool running)
        {
            await RunningGate;
            State = State with { Running = running };
        }
    }
}
