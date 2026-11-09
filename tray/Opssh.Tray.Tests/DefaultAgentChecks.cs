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
            bool alias = false;
            Task<bool> SetAlias(bool value, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                alias = value;
                return Task.FromResult(true);
            }

            Require(await agent.SetAsync(true, SetAlias, token) is null && alias &&
                service.State is { Start: 4, Running: false } && agent.Read() is { Enabled: true, Before: null },
                "system agent claims the pipe after stopping and disabling the automatic service");
            Require(service.Actions.SequenceEqual(["stop", "startup:4"]), "OpenSSH is stopped before being disabled");

            Require(await agent.SetAsync(false, SetAlias, token) is null && !alias &&
                service.State is { Start: 2, Running: true, Delayed: false } && agent.Read() is { Enabled: false, Before: null },
                "restoring the service sets automatic and starts it");

            service.State = service.State with { Delayed = true };
            Require(await agent.SetAsync(true, (enabled, _) => Task.FromResult(!enabled), token) == "Startup.DefaultChangeFailed" &&
                service.State is { Start: 2, Delayed: true, Running: true } && agent.Read() is { Enabled: false, Before: null },
                "a failed pipe reservation restores exact prior service settings");

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
            Require(await bounded.SetAsync(true, SetAlias, token) == "Startup.DefaultRecovery" &&
                bounded.Read() is { Enabled: false, Before.Running: true, MutatingService: true },
                "a pending service mutation is journaled before returning");
            Require(await bounded.SetAsync(false, SetAlias, token) == "Startup.DefaultRecovery" &&
                await new DefaultAgent(service, path).SetAsync(false, SetAlias, token) == "Startup.DefaultRecovery" &&
                service.Actions.SequenceEqual(["stop"]), "neither a retry nor a fresh process races a pending mutation");
            runningGate.SetResult();
            await service.LastRunningChange.WaitAsync(token);
            Require(await bounded.SetAsync(false, SetAlias, token) is null && !alias &&
                service.State is { Start: 2, Delayed: true, Running: true } &&
                bounded.Read() is { Enabled: false, Before: null, MutatingService: false },
                "a settled late mutation is rolled back before the next switch");
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
