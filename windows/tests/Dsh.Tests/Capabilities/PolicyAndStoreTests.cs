using Dsh.Cordis;
using Dsh.Credentials;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Session.Persistence;
using Dsh.Settings;
using Dsh.Tools;

namespace Dsh.Tests.Capabilities;

public sealed class SandboxPolicyTests
{
    private sealed class FixedPolicy : ISandboxPolicy
    {
        public FixedPolicy(SandboxMode mode, string? workspace = "/workspace")
            => State = new SandboxState(mode, ApprovalPolicy.Ask, workspace);

        public SandboxState State { get; }

        public string? RefuseWrite(string fullPath)
            => new PermissionService(
                    Context.CreateRoot(),
                    PermissionConfig.Default(State.Workspace) with { DefaultPreset = PresetFor(State.Sandbox) },
                    static () => null)
                .RefuseWrite(fullPath);

        public bool CommandNeedsApproval() => State.Sandbox != SandboxMode.DangerFullAccess;

        private static string PresetFor(SandboxMode mode) => mode switch
        {
            SandboxMode.ReadOnly => "read-only",
            SandboxMode.DangerFullAccess => "danger-full-access",
            _ => "workspace-write",
        };
    }

    private static JsonValue Args(params (string Key, object? Value)[] pairs)
        => JsonValue.From(pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value));

    [Fact]
    public void A_write_inside_the_workspace_is_allowed_under_workspace_write()
    {
        var policy = new FixedPolicy(SandboxMode.WorkspaceWrite);

        Assert.Null(policy.RefuseWrite("/workspace/src/a.txt"));
    }

    [Fact]
    public void A_write_outside_the_workspace_is_refused_under_workspace_write()
    {
        var policy = new FixedPolicy(SandboxMode.WorkspaceWrite);

        Assert.NotNull(policy.RefuseWrite("/etc/passwd"));
    }

    [Fact]
    public void Read_only_refuses_every_write()
    {
        var policy = new FixedPolicy(SandboxMode.ReadOnly);

        Assert.NotNull(policy.RefuseWrite("/workspace/src/a.txt"));
    }

    [Fact]
    public void Full_access_refuses_nothing()
    {
        var policy = new FixedPolicy(SandboxMode.DangerFullAccess);

        Assert.Null(policy.RefuseWrite("/etc/passwd"));
    }

    [Fact]
    public void A_refused_write_tells_the_model_how_to_ask()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.WorkspaceWrite),
            "write",
            Args(("file_path", "/etc/hosts")),
            static path => path);

        var denial = Assert.IsType<DenyDecision>(decision);
        Assert.Contains("sandbox_permissions", denial.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_escalated_call_puts_the_models_own_reason_to_a_person()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.WorkspaceWrite),
            "write",
            Args(
                ("file_path", "/etc/hosts"),
                ("sandbox_permissions", "danger-full-access"),
                ("justification", "the user asked me to update their hosts file")),
            static path => path);

        var ask = Assert.IsType<AskDecision>(decision);
        Assert.Equal("the user asked me to update their hosts file", ask.Reason);
    }

    [Fact]
    public void Escalation_without_a_justification_is_refused_rather_than_asked()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.WorkspaceWrite),
            "write",
            Args(("file_path", "/etc/hosts"), ("sandbox_permissions", "danger-full-access")),
            static path => path);

        Assert.IsType<DenyDecision>(decision);
    }

    [Fact]
    public void An_unknown_escalation_target_is_refused()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.WorkspaceWrite),
            "write",
            Args(
                ("file_path", "/etc/hosts"),
                ("sandbox_permissions", "root"),
                ("justification", "because")),
            static path => path);

        Assert.IsType<DenyDecision>(decision);
    }

    [Fact]
    public void Commands_are_refused_under_read_only()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.ReadOnly),
            "bash",
            Args(("command", "rm -rf /")),
            static path => path);

        Assert.IsType<DenyDecision>(decision);
    }

    [Fact]
    public void Commands_run_without_interruption_under_workspace_write()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.WorkspaceWrite),
            "bash",
            Args(("command", "ls")),
            static path => path);

        Assert.Null(decision);
    }

    [Fact]
    public void A_tool_the_policy_knows_nothing_about_is_left_to_the_rest_of_the_chain()
    {
        var decision = SandboxPolicyPlugin.Judge(
            new FixedPolicy(SandboxMode.ReadOnly),
            "read",
            Args(("file_path", "/etc/hosts")),
            static path => path);

        Assert.Null(decision);
    }
}

public sealed class CredentialTests : IDisposable
{
    private readonly string _home;
    private readonly Context _ctx = Context.CreateRoot();

    public CredentialTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "dsh-cred-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private LocalCredentials Provider(string? projectDirectory = null)
        => new(_ctx, Path.Combine(_home, LocalCredentials.FileName), projectDirectory);

    [Fact]
    public void A_stored_credential_round_trips()
    {
        var credentials = Provider();
        credentials.Set("TEST_API_KEY", "sk-example");

        var resolved = credentials.Resolve("TEST_API_KEY");

        Assert.Equal("sk-example", resolved?.Value);
        Assert.Equal("file", resolved?.Source);
    }

    [Fact]
    public void The_environment_wins_over_the_managed_document()
    {
        var credentials = Provider();
        credentials.Set("TEST_API_KEY", "from-file");
        var previous = Environment.GetEnvironmentVariable("TEST_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("TEST_API_KEY", "from-env");
            var resolved = credentials.Resolve("TEST_API_KEY");

            Assert.Equal("from-env", resolved?.Value);
            Assert.Equal("env", resolved?.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_API_KEY", previous);
        }
    }

    [Fact]
    public void A_project_env_file_is_a_fallback_below_the_managed_document()
    {
        var project = Path.Combine(_home, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".env"), "TEST_API_KEY=from-project\n");

        var resolved = Provider(project).Resolve("TEST_API_KEY");

        Assert.Equal("from-project", resolved?.Value);
        Assert.Equal("project-env", resolved?.Source);
    }

    [Fact]
    public void Unsetting_removes_the_credential()
    {
        var credentials = Provider();
        credentials.Set("TEST_API_KEY", "sk-example");
        credentials.Unset("TEST_API_KEY");

        Assert.Null(credentials.Resolve("TEST_API_KEY"));
    }

    [Fact]
    public void Describing_a_credential_never_reveals_it()
    {
        var credentials = Provider();
        credentials.Set("TEST_API_KEY", "sk-secret-value");

        var info = credentials.Describe("TEST_API_KEY");

        Assert.True(info.Configured);
        Assert.Equal("file", info.Source);
        Assert.DoesNotContain("sk-secret-value", System.Text.Json.JsonSerializer.Serialize(info), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_cannot_be_sent_in_a_header_is_refused_without_echoing_it()
    {
        var credentials = Provider();

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.Set("TEST_API_KEY", "has a space"));

        Assert.DoesNotContain("has a space", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_env_parsing_handles_quotes_export_and_comments()
    {
        var path = Path.Combine(_home, "sample.env");
        File.WriteAllText(path, "# comment\nexport A=\"one\"\nB='two'\nC=three\nnot a pair\n");

        var values = LocalCredentials.ReadDotEnv(path);

        Assert.Equal("one", values["A"]);
        Assert.Equal("two", values["B"]);
        Assert.Equal("three", values["C"]);
        Assert.Equal(3, values.Count);
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly string _path;
    private readonly Context _ctx = Context.CreateRoot();

    public SettingsTests()
        => _path = Path.Combine(Path.GetTempPath(), "dsh-settings-" + Guid.NewGuid().ToString("N")[..8] + ".yaml");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void An_unset_setting_falls_back_to_what_the_composition_would_use()
    {
        var settings = new SettingsService(_ctx, _path);

        Assert.Equal("workspace-write", settings.Get("permission", "defaultPreset", "workspace-write"));
    }

    [Fact]
    public void A_written_setting_survives_a_reload()
    {
        var settings = new SettingsService(_ctx, _path);
        settings.Update("permission", new Dictionary<string, object?> { ["defaultPreset"] = "read-only" });

        var reopened = new SettingsService(_ctx, _path);
        reopened.Reload();

        Assert.Equal("read-only", reopened.Get("permission", "defaultPreset", "workspace-write"));
    }

    [Fact]
    public void Updating_one_field_leaves_the_others_alone()
    {
        var settings = new SettingsService(_ctx, _path);
        settings.Update("ui", new Dictionary<string, object?> { ["theme"] = "dark", ["density"] = "compact" });
        settings.Update("ui", new Dictionary<string, object?> { ["theme"] = "light" });

        Assert.Equal("light", settings.Get("ui", "theme", "system"));
        Assert.Equal("compact", settings.Get("ui", "density", "comfortable"));
    }

    [Fact]
    public void Setting_a_field_to_null_removes_it()
    {
        var settings = new SettingsService(_ctx, _path);
        settings.Update("ui", new Dictionary<string, object?> { ["theme"] = "dark" });
        settings.Update("ui", new Dictionary<string, object?> { ["theme"] = null });

        Assert.Equal("system", settings.Get("ui", "theme", "system"));
    }

    [Fact]
    public void An_illegal_namespace_is_refused()
    {
        var settings = new SettingsService(_ctx, _path);

        Assert.Throws<InvalidOperationException>(() => settings.Section("Not Legal"));
    }

    [Fact]
    public void A_broken_document_fails_loudly_rather_than_reverting_the_user_to_defaults()
    {
        File.WriteAllText(_path, "this: [is not: valid: yaml\n");
        var settings = new SettingsService(_ctx, _path);

        Assert.Throws<InvalidOperationException>(settings.Reload);
    }
}

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly Context _ctx = Context.CreateRoot();

    public PersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dsh-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Dsh.Session.Session NewSession(string id = "session-1", string? cwd = "/workspace")
        => new(SessionStore.NewHeader(new SessionId(id), cwd));

    [Fact]
    public void A_workspace_becomes_a_readable_folder_name()
    {
        Assert.Equal("--home-user-project--", SessionPaths.ProjectFolder("/home/user/project"));
        Assert.Equal(SessionPaths.NoWorkspaceFolder, SessionPaths.ProjectFolder(null));
    }

    [Fact]
    public void A_session_id_encodes_reversibly_so_two_can_never_collide()
    {
        foreach (var id in new[] { "session-1", "..", ".", "a/b", "with space", "ünïcode" })
        {
            Assert.Equal(id, SessionPaths.DecodeSegment(SessionPaths.EncodeSegment(id)));
        }

        Assert.NotEqual(SessionPaths.EncodeSegment("."), SessionPaths.EncodeSegment(".."));
    }

    [Fact]
    public void A_session_round_trips_through_the_store()
    {
        var persistence = new JsonlPersistence(_ctx, _root);
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));
        session.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));

        foreach (var entry in session.Events) Queue(persistence, session, entry);
        persistence.Flush(session);

        var (header, events) = JsonlPersistence.Read(SessionPaths.LogPath(_root, "/workspace", session.Id));

        Assert.Equal(session.Id, header.Id);
        Assert.Equal(session.Events.Count, events.Count);
        Assert.Equal("hello", events[1].DataAs<Message>().Text);
    }

    [Fact]
    public void Derived_history_is_identical_after_a_round_trip()
    {
        var persistence = new JsonlPersistence(_ctx, _root);
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.UserMessage, Message.UserText("hello"), new SurfaceIntent(AppendOp.Instance));
        session.Append(
            SessionEvents.AssistantMessage,
            new AssistantMessageData(
                1,
                1,
                Message.Assistant([new TextBlock("hi")], new ModelMessageSource("p", "m")),
                new TokenUsage(10, 2)),
            new SurfaceIntent(AppendOp.Instance, []));
        session.Append(SessionEvents.TurnEnd, new TurnEndData(1, CompletedTurnEnd.Instance));

        foreach (var entry in session.Events) Queue(persistence, session, entry);
        persistence.Flush(session);

        var reopened = JsonlPersistence.Resume(SessionPaths.LogPath(_root, "/workspace", session.Id));

        Assert.Equal(
            session.DeriveMessages().Select(static message => message.Text),
            reopened.DeriveMessages().Select(static message => message.Text));
    }

    [Fact]
    public void Reopening_an_interrupted_log_closes_its_turn()
    {
        var persistence = new JsonlPersistence(_ctx, _root);
        var session = NewSession();
        session.Append(SessionEvents.TurnStart, new TurnStartData(1));
        session.Append(SessionEvents.StepStart, new StepStartData(1, 1));

        foreach (var entry in session.Events) Queue(persistence, session, entry);
        persistence.Flush(session);

        var reopened = JsonlPersistence.Resume(SessionPaths.LogPath(_root, "/workspace", session.Id));

        var closed = Assert.Single(reopened.Events, static entry => entry.Type == SessionEvents.TurnEnd.Name);
        Assert.IsType<InterruptedTurnEnd>(closed.DataAs<TurnEndData>().Reason);

        // The repair is part of the inherited history, so the seed boundary is marked
        // after it: nothing this lifecycle did comes before that point.
        Assert.Equal(SessionEvents.EndSeed.Name, reopened.Events[^1].Type);
        Assert.Empty(SessionRepair.InterruptedTurnClosers(reopened.Events));
    }

    [Fact]
    public void A_log_from_an_incompatible_build_says_to_upgrade_rather_than_reporting_damage()
    {
        var directory = SessionPaths.SessionDirectory(_root, "/workspace", new SessionId("session-future"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, SessionPaths.LogFileName),
            """{"type":"session","version":99,"id":"session-future","createdAt":1,"delegationDepth":0}""" + "\n");

        var error = Assert.Throws<SessionFormatException>(
            () => JsonlPersistence.Read(Path.Combine(directory, SessionPaths.LogFileName)));

        Assert.Contains("upgrade the harness", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_reads_only_each_logs_first_line()
    {
        var persistence = new JsonlPersistence(_ctx, _root);
        foreach (var id in new[] { "session-1", "session-2" })
        {
            var session = NewSession(id);
            session.Append(SessionEvents.TurnStart, new TurnStartData(1));
            foreach (var entry in session.Events) Queue(persistence, session, entry);
            persistence.Flush(session);
        }

        var stored = persistence.List();

        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, static entry => entry.Header.Id.Value == "session-1");
    }

    [Fact]
    public void Nothing_is_written_for_a_session_that_records_nothing()
    {
        var persistence = new JsonlPersistence(_ctx, _root);
        var session = NewSession();

        persistence.Flush(session);

        Assert.False(File.Exists(SessionPaths.LogPath(_root, "/workspace", session.Id)));
    }

    [Fact]
    public void An_unrecognized_required_event_refuses_the_log()
    {
        var directory = SessionPaths.SessionDirectory(_root, "/workspace", new SessionId("session-x"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, SessionPaths.LogFileName),
            """{"type":"session","version":0,"id":"session-x","createdAt":1,"delegationDepth":0}""" + "\n"
            + """{"type":"plugin/unknown","seq":0,"time":1,"data":{}}""" + "\n");

        Assert.ThrowsAny<Exception>(
            () => JsonlPersistence.Read(Path.Combine(directory, SessionPaths.LogFileName)));
    }

    private static void Queue(JsonlPersistence persistence, Dsh.Session.Session session, SessionEvent entry)
    {
        // The write path normally attaches through session/event; the test drives it
        // directly so it does not need a whole composition.
        var field = typeof(JsonlPersistence).GetField(
            "_pending",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var pending = (Dictionary<SessionId, List<SessionEvent>>)field.GetValue(persistence)!;
        if (!pending.TryGetValue(session.Id, out var queue))
        {
            queue = [];
            pending[session.Id] = queue;
        }

        queue.Add(entry);
    }
}
